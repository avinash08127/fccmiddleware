using System.Diagnostics;
using System.Globalization;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Api.Portal;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.DiagnosticLogs;
using FccMiddleware.Application.Observability;
using FccMiddleware.Application.Registration;
using FccMiddleware.Application.Telemetry;
using FccMiddleware.Contracts.Agent;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Config;
using FccMiddleware.Contracts.DiagnosticLogs;
using FccMiddleware.Contracts.Registration;
using FccMiddleware.Contracts.Telemetry;
using FccMiddleware.Domain.Models;
using FccMiddleware.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// Handles Edge Agent device registration, config distribution, token refresh, and decommission.
/// GET  /api/v1/agent/config                  — pull site configuration (ETag-based)
/// POST /api/v1/agent/register               — register a new device using a bootstrap token
/// POST /api/v1/agent/token/refresh           — refresh device JWT (token rotation)
/// POST /api/v1/admin/bootstrap-tokens        — generate a bootstrap token for a site
/// DELETE /api/v1/admin/bootstrap-tokens/{tokenId} — revoke a bootstrap token before expiry
/// POST /api/v1/admin/agent/{deviceId}/decommission — decommission a device
/// </summary>
[ApiController]
public sealed class AgentController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<AgentController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IObservabilityMetrics _metrics;
    private readonly FccMiddlewareDbContext _dbContext;
    private readonly PortalAccessResolver _accessResolver;

    public AgentController(
        IMediator mediator,
        ILogger<AgentController> logger,
        IConfiguration configuration,
        IObservabilityMetrics metrics,
        FccMiddlewareDbContext dbContext,
        PortalAccessResolver accessResolver)
    {
        _mediator = mediator;
        _logger = logger;
        _configuration = configuration;
        _metrics = metrics;
        _dbContext = dbContext;
        _accessResolver = accessResolver;
    }

    /// <summary>
    /// Generates a single-use bootstrap token for Edge Agent provisioning.
    /// </summary>
    [HttpPost("api/v1/admin/bootstrap-tokens")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(GenerateBootstrapTokenResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateBootstrapToken(
        [FromBody] GenerateBootstrapTokenRequest request,
        CancellationToken cancellationToken)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();
        if (!access.CanAccess(request.LegalEntityId))
            return Forbid();

        var command = new GenerateBootstrapTokenCommand
        {
            SiteCode = request.SiteCode,
            LegalEntityId = request.LegalEntityId,
            CreatedBy = User.Identity?.Name ?? "system",
            Environment = request.Environment
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "SITE_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                _ => BadRequest(BuildError(result.Error.Code, result.Error.Message))
            };
        }

        return StatusCode(StatusCodes.Status201Created, new GenerateBootstrapTokenResponse
        {
            TokenId = result.Value!.TokenId,
            RawToken = result.Value.RawToken,
            ExpiresAt = result.Value.ExpiresAt,
            SiteCode = request.SiteCode
        });
    }

    /// <summary>
    /// Revokes an active bootstrap token so it can no longer be used for registration.
    /// </summary>
    [HttpDelete("api/v1/admin/bootstrap-tokens/{tokenId}")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(RevokeBootstrapTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeBootstrapToken(
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var token = await _dbContext.BootstrapTokens
            .AsNoTracking()
            .Where(t => t.Id == tokenId)
            .Select(t => new { t.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        if (token is null)
            return NotFound(BuildError("TOKEN_NOT_FOUND", $"Bootstrap token '{tokenId}' not found."));

        if (!access.CanAccess(token.LegalEntityId))
            return Forbid();

        var command = new RevokeBootstrapTokenCommand
        {
            TokenId = tokenId,
            RevokedBy = User.Identity?.Name ?? "system"
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "TOKEN_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                _ => Conflict(BuildError(result.Error.Code, result.Error.Message))
            };
        }

        return Ok(new RevokeBootstrapTokenResponse
        {
            TokenId = result.Value!.TokenId,
            RevokedAt = result.Value.RevokedAt
        });
    }

    /// <summary>
    /// Registers a new Edge Agent device using a single-use bootstrap token.
    /// The bootstrap token is passed in the X-Provisioning-Token header.
    /// </summary>
    [HttpPost("api/v1/agent/register")]
    [AllowAnonymous] // Authenticated via bootstrap token in header, not JWT
    [ProducesResponseType(typeof(DeviceRegistrationApiResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(
        [FromBody] DeviceRegistrationApiRequest request,
        CancellationToken cancellationToken)
    {
        // Extract bootstrap token from JSON body; fall back to X-Provisioning-Token header
        var provisioningToken = request.ProvisioningToken;
        if (string.IsNullOrWhiteSpace(provisioningToken)
            && Request.Headers.TryGetValue("X-Provisioning-Token", out var tokenHeader))
        {
            provisioningToken = tokenHeader.ToString();
        }

        if (string.IsNullOrWhiteSpace(provisioningToken))
        {
            return Unauthorized(BuildError("BOOTSTRAP_TOKEN_MISSING",
                "Provisioning token is required (in request body or X-Provisioning-Token header)."));
        }

        var command = new RegisterDeviceCommand
        {
            ProvisioningToken = provisioningToken,
            SiteCode = request.SiteCode,
            DeviceSerialNumber = request.DeviceSerialNumber,
            DeviceModel = request.DeviceModel,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            ReplacePreviousAgent = request.ReplacePreviousAgent
        };

        // Wrap registration + config assembly in an explicit EF Core transaction so the
        // bootstrap token is not consumed if config assembly fails. Uses Database.BeginTransactionAsync
        // instead of System.Transactions.TransactionScope because Npgsql 7+ does not enlist in
        // ambient transactions by default (requires Enlist=true in connection string).
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "BOOTSTRAP_TOKEN_INVALID" or "BOOTSTRAP_TOKEN_EXPIRED" or "BOOTSTRAP_TOKEN_REVOKED" =>
                    Unauthorized(BuildError(result.Error.Code, result.Error.Message)),
                "BOOTSTRAP_TOKEN_ALREADY_USED" =>
                    Conflict(BuildError(result.Error.Code, result.Error.Message)),
                "ACTIVE_AGENT_EXISTS" =>
                    Conflict(BuildError(result.Error.Code, result.Error.Message)),
                "CONFIG_NOT_FOUND" =>
                    Conflict(BuildError(result.Error.Code, result.Error.Message)),
                "SITE_NOT_FOUND" or "SITE_MISMATCH" =>
                    BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        var value = result.Value!;
        var siteConfigResult = await _mediator.Send(new GetAgentConfigQuery
        {
            DeviceId = value.DeviceId,
            SiteCode = value.SiteCode,
            LegalEntityId = value.LegalEntityId
        }, cancellationToken);

        if (siteConfigResult.IsFailure || siteConfigResult.Value?.Config is null)
        {
            _logger.LogError(
                "Device {DeviceId} registered for site {SiteCode} but initial site config could not be loaded. ErrorCode={ErrorCode}",
                value.DeviceId,
                value.SiteCode,
                siteConfigResult.Error?.Code ?? "CONFIG_UNAVAILABLE");

            // Transaction rolls back on dispose — registration and token consumption are reverted.
            // The device can retry with the same bootstrap token.
            return StatusCode(StatusCodes.Status500InternalServerError,
                BuildError(
                    "INITIAL_SITE_CONFIG_UNAVAILABLE",
                    "Device registration failed because the initial site configuration could not be loaded. The bootstrap token has not been consumed — retry is safe.",
                    retryable: true));
        }

        // Both registration and config assembly succeeded — commit the transaction.
        await transaction.CommitAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, new DeviceRegistrationApiResponse
        {
            DeviceId = value.DeviceId,
            DeviceToken = value.DeviceToken,
            RefreshToken = value.RefreshToken,
            TokenExpiresAt = value.TokenExpiresAt,
            SiteCode = value.SiteCode,
            LegalEntityId = value.LegalEntityId,
            RegisteredAt = value.RegisteredAt,
            SiteConfig = siteConfigResult.Value.Config
        });
    }

    /// <summary>
    /// Refreshes a device JWT using the opaque refresh token. Implements token rotation.
    /// </summary>
    [HttpPost("api/v1/agent/token/refresh")]
    [AllowAnonymous] // Authenticated via refresh token in body, not JWT
    [ProducesResponseType(typeof(RefreshTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RefreshToken(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RefreshDeviceTokenCommand
        {
            RefreshToken = request.RefreshToken
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_DECOMMISSIONED" =>
                    StatusCode(StatusCodes.Status403Forbidden,
                        BuildError(result.Error.Code, result.Error.Message)),
                _ => Unauthorized(BuildError(result.Error.Code, result.Error.Message))
            };
        }

        return Ok(new RefreshTokenResponse
        {
            DeviceToken = result.Value!.DeviceToken,
            RefreshToken = result.Value.RefreshToken,
            TokenExpiresAt = result.Value.TokenExpiresAt
        });
    }

    /// <summary>
    /// Decommissions a device — sets status to DECOMMISSIONED and revokes all refresh tokens.
    /// </summary>
    [HttpPost("api/v1/admin/agent/{deviceId}/decommission")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(DecommissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decommission(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var device = await _dbContext.AgentRegistrations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Id == deviceId)
            .Select(a => new { a.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));

        if (!access.CanAccess(device.LegalEntityId))
            return Forbid();

        var command = new DecommissionDeviceCommand
        {
            DeviceId = deviceId
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "DEVICE_ALREADY_DECOMMISSIONED" => Conflict(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        return Ok(new DecommissionResponse
        {
            DeviceId = result.Value!.DeviceId,
            DeactivatedAt = result.Value.DeactivatedAt
        });
    }

    /// <summary>
    /// Returns the current SiteConfig for the registered device.
    /// Supports ETag-based caching: pass the config version in If-None-Match to receive
    /// 304 Not Modified when the config has not changed.
    /// </summary>
    [HttpGet("api/v1/agent/config")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(SiteConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        // Extract device identity from JWT claims
        var deviceIdClaim = User.FindFirst("sub")?.Value
                         ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var siteCode      = User.FindFirst("site")?.Value;
        var leiClaim      = User.FindFirst("lei")?.Value;

        if (deviceIdClaim is null || siteCode is null || leiClaim is null
            || !Guid.TryParse(deviceIdClaim, out var deviceId)
            || !Guid.TryParse(leiClaim, out var legalEntityId))
        {
            return Unauthorized(BuildError("INVALID_TOKEN_CLAIMS",
                "Device JWT is missing required claims (sub, site, lei)."));
        }

        // Parse If-None-Match header → client config version
        int? clientConfigVersion = null;
        if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
        {
            var etag = ifNoneMatch.ToString().Trim().Trim('"');
            if (int.TryParse(etag, CultureInfo.InvariantCulture, out var parsed))
                clientConfigVersion = parsed;
        }

        var query = new GetAgentConfigQuery
        {
            DeviceId = deviceId,
            SiteCode = siteCode,
            LegalEntityId = legalEntityId,
            ClientConfigVersion = clientConfigVersion
        };

        var result = await _mediator.Send(query, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_NOT_FOUND" or "CONFIG_NOT_FOUND" =>
                    NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "SITE_MISMATCH" =>
                    Unauthorized(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        var value = result.Value!;

        // Set ETag header with current config version
        Response.Headers["ETag"] = $"\"{value.ConfigVersion}\"";

        if (value.NotModified)
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(value.Config);
    }

    /// <summary>
    /// Returns Edge Agent version compatibility state for the reported APK version.
    /// </summary>
    [HttpGet("api/v1/agent/version-check")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(VersionCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CheckVersion(
        [FromQuery] VersionCheckRequest request,
        CancellationToken cancellationToken)
    {
        var appVersion = request.AppVersion;
        var agentVersion = request.AgentVersion;

        if (string.IsNullOrWhiteSpace(appVersion) && string.IsNullOrWhiteSpace(agentVersion))
        {
            return BadRequest(BuildError("INVALID_AGENT_VERSION",
                "Query parameter appVersion is required."));
        }

        if (!string.IsNullOrWhiteSpace(appVersion)
            && !string.IsNullOrWhiteSpace(agentVersion)
            && !string.Equals(appVersion, agentVersion, StringComparison.Ordinal))
        {
            return BadRequest(BuildError("INVALID_AGENT_VERSION",
                "appVersion and agentVersion must match when both are provided."));
        }

        var result = await _mediator.Send(new CheckAgentVersionQuery
        {
            AgentVersion = appVersion ?? agentVersion!
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "INVALID_AGENT_VERSION" => BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                "VERSION_CONFIG_INVALID" => StatusCode(StatusCodes.Status500InternalServerError,
                    BuildError(result.Error.Code, result.Error.Message, retryable: true)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        var value = result.Value!;
        return Ok(new VersionCheckResponse
        {
            Compatible = value.Compatible,
            MinimumVersion = value.MinimumVersion,
            LatestVersion = value.LatestVersion,
            UpdateRequired = value.UpdateRequired,
            UpdateUrl = value.UpdateUrl,
            AgentVersion = value.AgentVersion,
            UpdateAvailable = value.UpdateAvailable,
            ReleaseNotes = value.ReleaseNotes,
        });
    }

    /// <summary>
    /// Accepts a telemetry snapshot from a registered Edge Agent.
    /// </summary>
    [HttpPost("api/v1/agent/telemetry")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitTelemetry(
        [FromBody] SubmitTelemetryRequest request,
        CancellationToken cancellationToken)
    {
        var deviceIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? User.FindFirstValue("sub");
        var siteCodeClaim = User.FindFirstValue("site");
        var leiClaim = User.FindFirstValue("lei");

        if (deviceIdClaim is null || siteCodeClaim is null || leiClaim is null
            || !Guid.TryParse(deviceIdClaim, out var deviceId)
            || !Guid.TryParse(leiClaim, out var legalEntityId))
        {
            return Unauthorized(BuildError("INVALID_TOKEN_CLAIMS",
                "Device JWT is missing required claims (sub, site, lei)."));
        }

        if (request.DeviceId != deviceId
            || !string.Equals(request.SiteCode, siteCodeClaim, StringComparison.Ordinal)
            || request.LegalEntityId != legalEntityId)
        {
            return Unauthorized(BuildError("SITE_MISMATCH",
                "Telemetry payload identity does not match the authenticated device token."));
        }

        var result = await _mediator.Send(new SubmitTelemetryCommand
        {
            DeviceId = deviceId,
            SiteCode = siteCodeClaim,
            LegalEntityId = legalEntityId,
            Payload = MapToTelemetryPayload(request)
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "SITE_MISMATCH" => Unauthorized(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        LogTelemetryWarnings(request);
        RecordTelemetryMetrics(request);

        return NoContent();
    }

    /// <summary>
    /// Accepts diagnostic log entries (WARN/ERROR) from a registered Edge Agent.
    /// </summary>
    [HttpPost("api/v1/agent/diagnostic-logs")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitDiagnosticLogs(
        [FromBody] DiagnosticLogUploadRequest request,
        CancellationToken cancellationToken)
    {
        var deviceIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var siteCodeClaim = User.FindFirstValue("site");
        var leiClaim = User.FindFirstValue("lei");

        if (deviceIdClaim is null || siteCodeClaim is null || leiClaim is null
            || !Guid.TryParse(deviceIdClaim, out var deviceId)
            || !Guid.TryParse(leiClaim, out var legalEntityId))
        {
            return Unauthorized(BuildError("INVALID_TOKEN_CLAIMS",
                "Device JWT is missing required claims (sub, site, lei)."));
        }

        if (request.DeviceId != deviceId
            || !string.Equals(request.SiteCode, siteCodeClaim, StringComparison.Ordinal)
            || request.LegalEntityId != legalEntityId)
        {
            return Unauthorized(BuildError("SITE_MISMATCH",
                "Diagnostic log payload identity does not match the authenticated device token."));
        }

        var result = await _mediator.Send(new SubmitDiagnosticLogsCommand
        {
            DeviceId = deviceId,
            SiteCode = siteCodeClaim,
            LegalEntityId = legalEntityId,
            UploadedAtUtc = request.UploadedAtUtc,
            LogEntries = request.LogEntries,
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "SITE_MISMATCH" => Unauthorized(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        return NoContent();
    }

    /// <summary>
    /// Returns recent diagnostic log batches for a device. Used by the portal.
    /// </summary>
    [HttpGet("api/v1/agents/{deviceId}/diagnostic-logs")]
    [Authorize(Policy = "PortalUser")]
    [ProducesResponseType(typeof(GetDiagnosticLogsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDiagnosticLogs(
        Guid deviceId,
        [FromQuery] int maxBatches = 10,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var device = await _dbContext.AgentRegistrations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Id == deviceId)
            .Select(a => new { a.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));

        if (!access.CanAccess(device.LegalEntityId))
            return Forbid();

        var result = await _mediator.Send(new GetDiagnosticLogsQuery
        {
            DeviceId = deviceId,
            MaxBatches = maxBatches,
        }, cancellationToken);

        if (result.IsFailure)
        {
            return NotFound(BuildError(result.Error!.Code, result.Error.Message));
        }

        var value = result.Value!;
        return Ok(new GetDiagnosticLogsResponse
        {
            DeviceId = value.DeviceId,
            Batches = value.Batches.Select(b => new DiagnosticLogBatch
            {
                Id = b.Id,
                UploadedAtUtc = b.UploadedAtUtc,
                LogEntries = b.LogEntries,
            }).ToList()
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TelemetryPayload MapToTelemetryPayload(SubmitTelemetryRequest request) =>
        new()
        {
            SchemaVersion = request.SchemaVersion,
            DeviceId = request.DeviceId,
            SiteCode = request.SiteCode,
            LegalEntityId = request.LegalEntityId,
            ReportedAtUtc = request.ReportedAtUtc,
            SequenceNumber = request.SequenceNumber,
            ConnectivityState = request.ConnectivityState,
            Device = new DeviceStatus
            {
                BatteryPercent = request.Device.BatteryPercent,
                IsCharging = request.Device.IsCharging,
                StorageFreeMb = request.Device.StorageFreeMb,
                StorageTotalMb = request.Device.StorageTotalMb,
                MemoryFreeMb = request.Device.MemoryFreeMb,
                MemoryTotalMb = request.Device.MemoryTotalMb,
                AppVersion = request.Device.AppVersion,
                AppUptimeSeconds = request.Device.AppUptimeSeconds,
                OsVersion = request.Device.OsVersion,
                DeviceModel = request.Device.DeviceModel
            },
            FccHealth = new FccHealthStatus
            {
                IsReachable = request.FccHealth.IsReachable,
                LastHeartbeatAtUtc = request.FccHealth.LastHeartbeatAtUtc,
                HeartbeatAgeSeconds = request.FccHealth.HeartbeatAgeSeconds,
                FccVendor = request.FccHealth.FccVendor,
                FccHost = request.FccHealth.FccHost,
                FccPort = request.FccHealth.FccPort,
                ConsecutiveHeartbeatFailures = request.FccHealth.ConsecutiveHeartbeatFailures
            },
            Buffer = new BufferStatus
            {
                TotalRecords = request.Buffer.TotalRecords,
                PendingUploadCount = request.Buffer.PendingUploadCount,
                SyncedCount = request.Buffer.SyncedCount,
                SyncedToOdooCount = request.Buffer.SyncedToOdooCount,
                FailedCount = request.Buffer.FailedCount,
                OldestPendingAtUtc = request.Buffer.OldestPendingAtUtc,
                BufferSizeMb = request.Buffer.BufferSizeMb
            },
            Sync = new SyncStatus
            {
                LastSyncAttemptUtc = request.Sync.LastSyncAttemptUtc,
                LastSuccessfulSyncUtc = request.Sync.LastSuccessfulSyncUtc,
                SyncLagSeconds = request.Sync.SyncLagSeconds,
                LastStatusPollUtc = request.Sync.LastStatusPollUtc,
                LastConfigPullUtc = request.Sync.LastConfigPullUtc,
                ConfigVersion = request.Sync.ConfigVersion,
                UploadBatchSize = request.Sync.UploadBatchSize
            },
            ErrorCounts = new ErrorCounts
            {
                FccConnectionErrors = request.ErrorCounts.FccConnectionErrors,
                CloudUploadErrors = request.ErrorCounts.CloudUploadErrors,
                CloudAuthErrors = request.ErrorCounts.CloudAuthErrors,
                LocalApiErrors = request.ErrorCounts.LocalApiErrors,
                BufferWriteErrors = request.ErrorCounts.BufferWriteErrors,
                AdapterNormalizationErrors = request.ErrorCounts.AdapterNormalizationErrors,
                PreAuthErrors = request.ErrorCounts.PreAuthErrors
            }
        };

    private void LogTelemetryWarnings(SubmitTelemetryRequest request)
    {
        var telemetryConfig = _configuration.GetSection("EdgeAgentDefaults:Telemetry");
        var bufferDepthThreshold = telemetryConfig.GetValue("BufferDepthWarningThreshold", 5000);
        var syncLagThresholdSeconds = telemetryConfig.GetValue("SyncLagWarningThresholdSeconds", 7200);

        if (request.Buffer.PendingUploadCount > bufferDepthThreshold)
        {
            _logger.LogWarning(
                "Edge Agent buffer depth threshold exceeded. DeviceId={DeviceId} SiteCode={SiteCode} PendingUploadCount={PendingUploadCount} Threshold={Threshold}",
                request.DeviceId, request.SiteCode, request.Buffer.PendingUploadCount, bufferDepthThreshold);
        }

        if (request.Sync.SyncLagSeconds is int syncLagSeconds && syncLagSeconds > syncLagThresholdSeconds)
        {
            _logger.LogWarning(
                "Edge Agent sync lag threshold exceeded. DeviceId={DeviceId} SiteCode={SiteCode} SyncLagSeconds={SyncLagSeconds} Threshold={Threshold}",
                request.DeviceId, request.SiteCode, syncLagSeconds, syncLagThresholdSeconds);
        }
    }

    private void RecordTelemetryMetrics(SubmitTelemetryRequest request)
    {
        _metrics.RecordEdgeBufferDepth(
            request.LegalEntityId,
            request.SiteCode,
            request.DeviceId,
            request.Buffer.PendingUploadCount);

        if (request.Sync.SyncLagSeconds is int syncLagSeconds)
        {
            _metrics.RecordEdgeSyncLag(
                request.LegalEntityId,
                request.SiteCode,
                request.DeviceId,
                syncLagSeconds / 3600d);
        }

        if (request.FccHealth.HeartbeatAgeSeconds is int heartbeatAgeSeconds)
        {
            _metrics.RecordFccHeartbeatAge(
                request.LegalEntityId,
                request.SiteCode,
                request.DeviceId,
                heartbeatAgeSeconds / 60d);
        }

        if (request.ErrorCounts.CloudUploadErrors > 0)
        {
            _metrics.RecordApplicationError("EDGE.CLOUD_UPLOAD", "/api/v1/agent/telemetry", request.ErrorCounts.CloudUploadErrors);
        }

        if (request.ErrorCounts.CloudAuthErrors > 0)
        {
            _metrics.RecordApplicationError("EDGE.CLOUD_AUTH", "/api/v1/agent/telemetry", request.ErrorCounts.CloudAuthErrors);
        }
    }

    /// <summary>
    /// M-13: Logs internal error details server-side and returns a safe generic 500 response.
    /// Prevents leaking database column names, constraint names, or stack traces to clients.
    /// </summary>
    private IActionResult LogInternalError(string errorCode, string errorMessage)
    {
        _logger.LogError("Unhandled application error: {ErrorCode} — {ErrorMessage}", errorCode, errorMessage);
        return StatusCode(StatusCodes.Status500InternalServerError,
            BuildError("INTERNAL.UNEXPECTED", "An unexpected error occurred. Please retry or contact support.", retryable: true));
    }

    private ErrorResponse BuildError(
        string errorCode,
        string message,
        bool retryable = false) =>
        new()
        {
            ErrorCode = errorCode,
            Message = message,
            TraceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Retryable = retryable
        };
}
