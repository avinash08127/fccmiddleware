using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FccMiddleware.Api.AgentControl;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Api.Portal;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.DiagnosticLogs;
using FccMiddleware.Application.Observability;
using FccMiddleware.Application.Registration;
using FccMiddleware.Application.Telemetry;
using FccMiddleware.Contracts.Agent;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Config;
using FccMiddleware.Contracts.DiagnosticLogs;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Contracts.Registration;
using FccMiddleware.Contracts.Telemetry;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models;
using FccMiddleware.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
    private const int MaxDiagnosticLogBatches = 100;

    private readonly IMediator _mediator;
    private readonly ILogger<AgentController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IObservabilityMetrics _metrics;
    private readonly FccMiddlewareDbContext _dbContext;
    private readonly PortalAccessResolver _accessResolver;
    private readonly IRegistrationThrottleService _registrationThrottle;
    private readonly IOptions<AgentCommandsOptions> _agentCommandsOptions;
    private readonly IOptions<BootstrapTokensOptions> _bootstrapTokensOptions;
    private readonly IAgentPushHintDispatcher _pushHintDispatcher;

    public AgentController(
        IMediator mediator,
        ILogger<AgentController> logger,
        IConfiguration configuration,
        IObservabilityMetrics metrics,
        FccMiddlewareDbContext dbContext,
        PortalAccessResolver accessResolver,
        IRegistrationThrottleService registrationThrottle,
        IOptions<AgentCommandsOptions> agentCommandsOptions,
        IOptions<BootstrapTokensOptions> bootstrapTokensOptions,
        IAgentPushHintDispatcher pushHintDispatcher)
    {
        _mediator = mediator;
        _logger = logger;
        _configuration = configuration;
        _metrics = metrics;
        _dbContext = dbContext;
        _accessResolver = accessResolver;
        _registrationThrottle = registrationThrottle;
        _agentCommandsOptions = agentCommandsOptions;
        _bootstrapTokensOptions = bootstrapTokensOptions;
        _pushHintDispatcher = pushHintDispatcher;
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
            CreatedBy = _accessResolver.ResolveUserDisplay(User) ?? _accessResolver.ResolveUserId(User) ?? "system",
            CreatedByActorId = _accessResolver.ResolveUserId(User),
            CreatedByActorDisplay = _accessResolver.ResolveUserDisplay(User),
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
            RevokedBy = _accessResolver.ResolveUserDisplay(User) ?? _accessResolver.ResolveUserId(User) ?? "system",
            RevokedByActorId = _accessResolver.ResolveUserId(User),
            RevokedByActorDisplay = _accessResolver.ResolveUserDisplay(User)
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

    [HttpGet("api/v1/admin/bootstrap-tokens")]
    [Authorize(Policy = "PortalUser")]
    [ProducesResponseType(typeof(PortalPagedResult<BootstrapTokenHistoryRow>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBootstrapTokenHistory(
        [FromQuery] Guid legalEntityId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? siteCode = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        if (!_bootstrapTokensOptions.Value.HistoryApiEnabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Bootstrap token history API is disabled."));
        }

        if (legalEntityId == Guid.Empty)
        {
            return BadRequest(BuildError("VALIDATION.LEGAL_ENTITY_REQUIRED", "legalEntityId is required."));
        }

        if (pageSize is < 1 or > 100)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 100."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        if (!TryParseBootstrapStatus(status, out var statusFilter))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_STATUS", $"Unknown bootstrap token status '{status}'."));
        }

        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var query = _dbContext.BootstrapTokens
            .ForPortal(access, legalEntityId);

        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            query = query.Where(item => item.SiteCode == siteCode);
        }

        if (from.HasValue)
        {
            query = query.Where(item => item.CreatedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(item => item.CreatedAt <= to.Value);
        }

        if (statusFilter.HasValue)
        {
            query = statusFilter.Value switch
            {
                ProvisioningTokenStatus.ACTIVE => query.Where(item =>
                    item.Status == ProvisioningTokenStatus.ACTIVE && item.ExpiresAt > now),
                ProvisioningTokenStatus.EXPIRED => query.Where(item =>
                    item.Status == ProvisioningTokenStatus.ACTIVE && item.ExpiresAt <= now),
                ProvisioningTokenStatus.USED => query.Where(item =>
                    item.Status == ProvisioningTokenStatus.USED),
                ProvisioningTokenStatus.REVOKED => query.Where(item =>
                    item.Status == ProvisioningTokenStatus.REVOKED),
                _ => query
            };
        }

        var totalCount = await query.CountAsync(cancellationToken);

        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            query = query.Where(item =>
                item.CreatedAt > cursorTimestamp
                || (item.CreatedAt == cursorTimestamp && item.Id.CompareTo(cursorId) > 0));
        }

        var rows = await query
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var last = rows[^1];
            nextCursor = PortalCursor.Encode(last.CreatedAt, last.Id);
        }

        stopwatch.Stop();
        _metrics.RecordBootstrapTokenHistoryApiLatency(legalEntityId, stopwatch.Elapsed.TotalMilliseconds);

        return Ok(new PortalPagedResult<BootstrapTokenHistoryRow>
        {
            Data = rows.Select(item => ToBootstrapTokenHistoryRow(item, now)).ToList(),
            Meta = new PortalPageMeta
            {
                PageSize = rows.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                TotalCount = totalCount
            }
        });
    }

    [HttpGet("api/v1/admin/bootstrap-tokens/{tokenId:guid}")]
    [Authorize(Policy = "PortalUser")]
    [ProducesResponseType(typeof(BootstrapTokenHistoryRow), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBootstrapTokenById(
        Guid tokenId,
        CancellationToken cancellationToken = default)
    {
        if (!_bootstrapTokensOptions.Value.HistoryApiEnabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Bootstrap token history API is disabled."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var token = await _dbContext.BootstrapTokens
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == tokenId, cancellationToken);

        if (token is null)
        {
            return NotFound(BuildError("TOKEN_NOT_FOUND", $"Bootstrap token '{tokenId}' not found."));
        }

        if (!access.CanAccess(token.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(ToBootstrapTokenHistoryRow(token, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Registers a new Edge Agent device using a single-use bootstrap token.
    /// The bootstrap token is passed in the X-Provisioning-Token header.
    /// </summary>
    [HttpPost("api/v1/agent/register")]
    [AllowAnonymous] // Authenticated via bootstrap token in header, not JWT
    [RequestSizeLimit(65_536)] // S-3: 64 KB max for registration payloads
    [EnableRateLimiting("registration")]
    [ProducesResponseType(typeof(DeviceRegistrationApiResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(
        [FromBody] DeviceRegistrationApiRequest request,
        CancellationToken cancellationToken)
    {
        // FM-S02: Block IPs with too many failed registration attempts
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (await _registrationThrottle.IsBlockedAsync(clientIp, cancellationToken))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests,
                BuildError("REGISTRATION_BLOCKED",
                    "Too many failed registration attempts. Please try again later."));
        }

        // Extract bootstrap token from JSON body; fall back to X-Provisioning-Token header (deprecated)
        var provisioningToken = request.ProvisioningToken;
        if (string.IsNullOrWhiteSpace(provisioningToken)
            && Request.Headers.TryGetValue("X-Provisioning-Token", out var tokenHeader))
        {
            provisioningToken = tokenHeader.ToString();

            // OB-S03: Header-channel tokens are at higher risk of appearing in infrastructure logs
            // (reverse proxies, CDNs, API gateways). Both Android and desktop agents send the token
            // in the request body. Log a deprecation warning when the header path is used.
            _logger.LogWarning(
                "Registration used deprecated X-Provisioning-Token header instead of request body. " +
                "ClientIp={ClientIp}. Migrate to sending provisioningToken in the JSON body.",
                clientIp);
        }

        if (string.IsNullOrWhiteSpace(provisioningToken))
        {
            return Unauthorized(BuildError("BOOTSTRAP_TOKEN_MISSING",
                "Provisioning token is required in the request body."));
        }

        var command = new RegisterDeviceCommand
        {
            ProvisioningToken = provisioningToken,
            SiteCode = request.SiteCode,
            DeviceSerialNumber = request.DeviceSerialNumber,
            DeviceModel = request.DeviceModel,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            DeviceClass = request.DeviceClass,
            RoleCapability = request.RoleCapability,
            SiteHaPriority = request.SiteHaPriority,
            Capabilities = request.Capabilities,
            PeerApiBaseUrl = request.PeerApi?.BaseUrl,
            PeerApiAdvertisedHost = request.PeerApi?.AdvertisedHost,
            PeerApiPort = request.PeerApi?.Port,
            PeerApiTlsEnabled = request.PeerApi?.TlsEnabled ?? false,
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
            // FM-S02: Track failed attempts for token-auth failures (brute-force indicator)
            if (result.Error!.Code is "BOOTSTRAP_TOKEN_INVALID"
                or "BOOTSTRAP_TOKEN_EXPIRED"
                or "BOOTSTRAP_TOKEN_REVOKED"
                or "BOOTSTRAP_TOKEN_ALREADY_USED")
            {
                await _registrationThrottle.RecordFailedAttemptAsync(clientIp, cancellationToken);
            }

            if (result.Error.Code is "DEVICE_PENDING_APPROVAL" or "DEVICE_QUARANTINED")
            {
                return await CommitSuspiciousRegistrationConflictAsync(transaction, result.Error, cancellationToken);
            }

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

        // FM-S02: Clear failed-attempt counter on successful registration
        await _registrationThrottle.ResetAsync(clientIp, cancellationToken);

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
    /// FM-S03: Requires the current (even expired) device JWT to bind the refresh
    /// to the original device identity.
    /// </summary>
    [HttpPost("api/v1/agent/token/refresh")]
    [AllowAnonymous] // Authenticated via refresh token + expired JWT in body
    [RequestSizeLimit(16_384)] // S-3: 16 KB max for token refresh payloads
    [EnableRateLimiting("token-refresh")]
    [ProducesResponseType(typeof(RefreshTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RefreshToken(
        [FromBody] RefreshTokenRequest request,
        [FromServices] IDeviceTokenService tokenService,
        CancellationToken cancellationToken)
    {
        // FM-S03: Validate the expired device JWT (signature only) and extract device identity
        if (string.IsNullOrWhiteSpace(request.DeviceToken))
        {
            return Unauthorized(BuildError("DEVICE_TOKEN_REQUIRED",
                "The current device JWT must be provided alongside the refresh token."));
        }

        var deviceId = tokenService.ExtractDeviceIdFromExpiredToken(request.DeviceToken);
        if (deviceId is null)
        {
            return Unauthorized(BuildError("DEVICE_TOKEN_INVALID",
                "The provided device JWT has an invalid signature or is malformed."));
        }

        var command = new RefreshDeviceTokenCommand
        {
            RefreshToken = request.RefreshToken,
            ExpectedDeviceId = deviceId.Value
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_DECOMMISSIONED" or "DEVICE_PENDING_APPROVAL" or "DEVICE_QUARANTINED" =>
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
    /// FM-S04: Requires a reason for audit traceability.
    /// </summary>
    [HttpPost("api/v1/admin/agent/{deviceId}/decommission")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(DecommissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decommission(
        Guid deviceId,
        [FromBody] DecommissionRequest request,
        CancellationToken cancellationToken)
    {
        // FM-S04: Validate reason is provided for this irreversible action
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
        {
            return BadRequest(BuildError("REASON_REQUIRED",
                "A reason (at least 10 characters) is required for decommissioning a device."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var device = await _dbContext.AgentRegistrations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Id == deviceId)
            .Select(a => new { a.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        // OB-S04: Return 404 for both not-found and cross-tenant to avoid leaking device existence
        if (device is null || !access.CanAccess(device.LegalEntityId))
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));

        var command = new DecommissionDeviceCommand
        {
            DeviceId = deviceId,
            DecommissionedBy = _accessResolver.ResolveUserId(User) ?? "system",
            Reason = request.Reason.Trim()
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

        await CancelPendingCommandsAsync(
            deviceId,
            _accessResolver.ResolveUserId(User),
            _accessResolver.ResolveUserDisplay(User),
            request.Reason.Trim(),
            cancellationToken);

        return Ok(new DecommissionResponse
        {
            DeviceId = result.Value!.DeviceId,
            DeactivatedAt = result.Value.DeactivatedAt
        });
    }

    [HttpPost("api/v1/admin/agent/{deviceId}/approve")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(SuspiciousRegistrationReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveSuspiciousRegistration(
        Guid deviceId,
        [FromBody] SuspiciousRegistrationReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
        {
            return BadRequest(BuildError("REASON_REQUIRED",
                "A reason (at least 10 characters) is required for approving a suspicious registration."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var device = await _dbContext.AgentRegistrations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Id == deviceId)
            .Select(a => new { a.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null || !access.CanAccess(device.LegalEntityId))
        {
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));
        }

        var result = await _mediator.Send(new ApproveSuspiciousDeviceCommand
        {
            DeviceId = deviceId,
            ApprovedByActorId = _accessResolver.ResolveUserId(User) ?? "system",
            ApprovedByActorDisplay = _accessResolver.ResolveUserDisplay(User),
            Reason = request.Reason.Trim()
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "DEVICE_NOT_SUSPENDED" => Conflict(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        return Ok(new SuspiciousRegistrationReviewResponse
        {
            DeviceId = result.Value!.DeviceId,
            Status = result.Value.Status,
            UpdatedAt = result.Value.UpdatedAt
        });
    }

    [HttpPost("api/v1/admin/agent/{deviceId}/reject")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(SuspiciousRegistrationReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectSuspiciousRegistration(
        Guid deviceId,
        [FromBody] SuspiciousRegistrationReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
        {
            return BadRequest(BuildError("REASON_REQUIRED",
                "A reason (at least 10 characters) is required for rejecting a suspicious registration."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var device = await _dbContext.AgentRegistrations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.Id == deviceId)
            .Select(a => new { a.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null || !access.CanAccess(device.LegalEntityId))
        {
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));
        }

        var result = await _mediator.Send(new RejectSuspiciousDeviceCommand
        {
            DeviceId = deviceId,
            RejectedByActorId = _accessResolver.ResolveUserId(User) ?? "system",
            RejectedByActorDisplay = _accessResolver.ResolveUserDisplay(User),
            Reason = request.Reason.Trim()
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Code switch
            {
                "DEVICE_NOT_FOUND" => NotFound(BuildError(result.Error.Code, result.Error.Message)),
                "DEVICE_NOT_SUSPENDED" => Conflict(BuildError(result.Error.Code, result.Error.Message)),
                _ => LogInternalError(result.Error.Code, result.Error.Message)
            };
        }

        return Ok(new SuspiciousRegistrationReviewResponse
        {
            DeviceId = result.Value!.DeviceId,
            Status = result.Value.Status,
            UpdatedAt = result.Value.UpdatedAt
        });
    }

    [HttpPost("api/v1/admin/agents/{deviceId:guid}/commands")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(CreateAgentCommandResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAgentCommand(
        Guid deviceId,
        [FromBody] CreateAgentCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_agentCommandsOptions.Value.Enabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Agent command APIs are disabled."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var device = await _dbContext.AgentRegistrations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == deviceId, cancellationToken);

        if (device is null || !access.CanAccess(device.LegalEntityId))
        {
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));
        }

        if (device.Status != AgentRegistrationStatus.ACTIVE || !device.IsActive)
        {
            return Conflict(BuildError(
                device.Status.ToDeviceAccessErrorCode(),
                device.Status == AgentRegistrationStatus.ACTIVE
                    ? "Device is not eligible to accept commands."
                    : device.Status.ToDeviceAccessErrorMessage()));
        }

        if (!TryValidateNonSensitiveJson(request.Payload, out var offendingPath))
        {
            return BadRequest(BuildError(
                "VALIDATION.SENSITIVE_PAYLOAD",
                $"Command payload contains a sensitive field at '{offendingPath}'."));
        }

        var trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length < 3)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_REASON", "reason must contain at least 3 non-whitespace characters."));
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = request.ExpiresAt ?? now.AddHours(Math.Max(1, _agentCommandsOptions.Value.DefaultCommandTtlHours));
        if (expiresAt <= now)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_EXPIRY", "expiresAt must be in the future."));
        }

        var actorId = _accessResolver.ResolveUserId(User);
        var actorDisplay = _accessResolver.ResolveUserDisplay(User) ?? actorId ?? "system";

        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            LegalEntityId = device.LegalEntityId,
            SiteCode = device.SiteCode,
            CommandType = request.CommandType,
            Reason = trimmedReason,
            PayloadJson = request.Payload.HasValue ? request.Payload.Value.GetRawText() : null,
            Status = AgentCommandStatus.PENDING,
            CreatedByActorId = actorId,
            CreatedByActorDisplay = actorDisplay,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            UpdatedAt = now
        };

        _dbContext.AgentCommands.Add(command);
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = device.LegalEntityId,
            EventType = AgentControlAuditEventTypes.AgentCommandCreated,
            CorrelationId = Guid.NewGuid(),
            SiteCode = device.SiteCode,
            Source = nameof(AgentController),
            EntityId = device.Id,
            Payload = JsonSerializer.Serialize(new
            {
                CommandId = command.Id,
                DeviceId = device.Id,
                CommandType = request.CommandType,
                command.Reason,
                command.ExpiresAt,
                CreatedByActorId = actorId,
                CreatedByActorDisplay = actorDisplay,
                Payload = request.Payload
            })
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metrics.RecordAgentCommandCreated(device.LegalEntityId, device.SiteCode, device.Id, request.CommandType.ToString());

        await _pushHintDispatcher.SendCommandPendingHintAsync(command, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToCreateAgentCommandResponse(command));
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
        if (!TryGetAuthenticatedDeviceContext(out var deviceId, out var siteCode, out var legalEntityId))
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

    [HttpGet("api/v1/agent/commands")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(EdgeCommandPollResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PollAgentCommands(CancellationToken cancellationToken = default)
    {
        if (!_agentCommandsOptions.Value.Enabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Agent command APIs are disabled."));
        }

        if (!TryGetAuthenticatedDeviceContext(out var deviceId, out var siteCode, out var legalEntityId))
        {
            return Unauthorized(BuildError("INVALID_TOKEN_CLAIMS",
                "Device JWT is missing required claims (sub, site, lei)."));
        }

        await ExpirePendingCommandsAsync(deviceId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var commands = await _dbContext.AgentCommands
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item =>
                item.DeviceId == deviceId
                && item.LegalEntityId == legalEntityId
                && item.SiteCode == siteCode
                && item.ExpiresAt > now
                && (item.Status == AgentCommandStatus.PENDING || item.Status == AgentCommandStatus.DELIVERY_HINT_SENT))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        return Ok(new EdgeCommandPollResponse
        {
            ServerTimeUtc = now,
            Commands = commands.Select(item => new EdgeCommandPollItem
            {
                CommandId = item.Id,
                CommandType = item.CommandType,
                Status = item.Status,
                Reason = item.Reason,
                Payload = ParseJsonOrNull(item.PayloadJson),
                CreatedAt = item.CreatedAt,
                ExpiresAt = item.ExpiresAt
            }).ToList()
        });
    }

    [HttpPost("api/v1/agent/commands/{commandId:guid}/ack")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(CommandAckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AckAgentCommand(
        Guid commandId,
        [FromBody] CommandAckRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_agentCommandsOptions.Value.Enabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Agent command APIs are disabled."));
        }

        if (!TryGetAuthenticatedDeviceContext(out var deviceId, out var siteCode, out var legalEntityId))
        {
            return Unauthorized(BuildError("INVALID_TOKEN_CLAIMS",
                "Device JWT is missing required claims (sub, site, lei)."));
        }

        if (!TryValidateNonSensitiveJson(request.Result, out var offendingPath))
        {
            return BadRequest(BuildError(
                "VALIDATION.SENSITIVE_PAYLOAD",
                $"Command result contains a sensitive field at '{offendingPath}'."));
        }

        if (request.CompletionStatus == AgentCommandCompletionStatus.ACKED
            && (!string.IsNullOrWhiteSpace(request.FailureCode) || !string.IsNullOrWhiteSpace(request.FailureMessage)))
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_ACK_PAYLOAD",
                "failureCode and failureMessage are only allowed when completionStatus is FAILED."));
        }

        await ExpirePendingCommandsAsync(deviceId, cancellationToken);

        var command = await _dbContext.AgentCommands
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item =>
                item.Id == commandId
                && item.DeviceId == deviceId
                && item.LegalEntityId == legalEntityId
                && item.SiteCode == siteCode,
                cancellationToken);

        if (command is null)
        {
            return NotFound(BuildError("COMMAND_NOT_FOUND", $"Command '{commandId}' was not found."));
        }

        var requestedStatus = request.CompletionStatus == AgentCommandCompletionStatus.ACKED
            ? AgentCommandStatus.ACKED
            : AgentCommandStatus.FAILED;

        if (command.Status is AgentCommandStatus.ACKED or AgentCommandStatus.FAILED)
        {
            var requestedResultJson = request.Result.HasValue ? request.Result.Value.GetRawText() : null;
            var duplicate =
                command.Status == requestedStatus
                && string.Equals(command.FailureCode, request.FailureCode, StringComparison.Ordinal)
                && string.Equals(command.FailureMessage, request.FailureMessage, StringComparison.Ordinal)
                && string.Equals(command.ResultJson, requestedResultJson, StringComparison.Ordinal);

            if (duplicate)
            {
                return Ok(new CommandAckResponse
                {
                    CommandId = command.Id,
                    Status = command.Status,
                    AcknowledgedAt = command.AcknowledgedAt ?? command.UpdatedAt,
                    Duplicate = true
                });
            }

            return Conflict(BuildError(
                "COMMAND_ACK_CONFLICT",
                "Command has already been acknowledged with a different terminal outcome."));
        }

        if (command.Status is AgentCommandStatus.EXPIRED or AgentCommandStatus.CANCELLED)
        {
            return Conflict(BuildError(
                "COMMAND_NOT_ACTIONABLE",
                $"Command is already {command.Status} and cannot be acknowledged."));
        }

        var now = DateTimeOffset.UtcNow;
        command.Status = requestedStatus;
        command.AcknowledgedAt = now;
        command.HandledAtUtc = request.HandledAtUtc ?? now;
        command.ResultJson = request.Result.HasValue ? request.Result.Value.GetRawText() : null;
        command.FailureCode = request.CompletionStatus == AgentCommandCompletionStatus.FAILED
            ? request.FailureCode?.Trim()
            : null;
        command.FailureMessage = request.CompletionStatus == AgentCommandCompletionStatus.FAILED
            ? request.FailureMessage?.Trim()
            : null;
        command.LastError = requestedStatus == AgentCommandStatus.FAILED
            ? string.Join(": ", new[] { command.FailureCode, command.FailureMessage }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : null;
        command.UpdatedAt = now;

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = legalEntityId,
            EventType = requestedStatus == AgentCommandStatus.ACKED
                ? AgentControlAuditEventTypes.AgentCommandAcked
                : AgentControlAuditEventTypes.AgentCommandFailed,
            CorrelationId = Guid.NewGuid(),
            SiteCode = siteCode,
            Source = nameof(AgentController),
            EntityId = deviceId,
            Payload = JsonSerializer.Serialize(new
            {
                CommandId = command.Id,
                DeviceId = deviceId,
                command.CommandType,
                Status = requestedStatus,
                command.HandledAtUtc,
                command.FailureCode,
                command.FailureMessage,
                Result = request.Result
            })
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (requestedStatus == AgentCommandStatus.ACKED)
        {
            _metrics.RecordAgentCommandAcked(legalEntityId, siteCode, deviceId, command.CommandType.ToString());
        }
        else
        {
            _metrics.RecordAgentCommandFailed(legalEntityId, siteCode, deviceId, command.CommandType.ToString());
        }

        return Ok(new CommandAckResponse
        {
            CommandId = command.Id,
            Status = command.Status,
            AcknowledgedAt = now,
            Duplicate = false
        });
    }

    [HttpPost("api/v1/agent/installations/android")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpsertAndroidInstallation(
        [FromBody] AndroidInstallationUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_agentCommandsOptions.Value.Enabled || !_agentCommandsOptions.Value.FcmHintsEnabled)
        {
            return NotFound(BuildError("FEATURE_DISABLED", "Android installation registration is disabled."));
        }

        if (!TryGetAuthenticatedDeviceContext(out var deviceId, out var siteCode, out var legalEntityId))
        {
            return Unauthorized(BuildError("INVALID_TOKEN_CLAIMS",
                "Device JWT is missing required claims (sub, site, lei)."));
        }

        var now = DateTimeOffset.UtcNow;
        var tokenHash = ComputeSha256Hex(request.RegistrationToken);
        var installation = await _dbContext.AgentInstallations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.InstallationId, cancellationToken);

        if (installation is not null && installation.DeviceId != deviceId)
        {
            return Conflict(BuildError(
                "INSTALLATION_OWNERSHIP_CONFLICT",
                "Installation ID is already associated with another device."));
        }

        if (installation is null)
        {
            installation = new AgentInstallation
            {
                Id = request.InstallationId,
                DeviceId = deviceId,
                LegalEntityId = legalEntityId,
                SiteCode = siteCode,
                Platform = "ANDROID",
                PushProvider = "FCM",
                RegistrationToken = request.RegistrationToken,
                TokenHash = tokenHash,
                AppVersion = request.AppVersion.Trim(),
                OsVersion = request.OsVersion.Trim(),
                DeviceModel = request.DeviceModel.Trim(),
                LastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            _dbContext.AgentInstallations.Add(installation);
        }
        else
        {
            installation.LegalEntityId = legalEntityId;
            installation.SiteCode = siteCode;
            installation.RegistrationToken = request.RegistrationToken;
            installation.TokenHash = tokenHash;
            installation.AppVersion = request.AppVersion.Trim();
            installation.OsVersion = request.OsVersion.Trim();
            installation.DeviceModel = request.DeviceModel.Trim();
            installation.LastSeenAt = now;
            installation.UpdatedAt = now;
        }

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = legalEntityId,
            EventType = AgentControlAuditEventTypes.AgentInstallationUpdated,
            CorrelationId = Guid.NewGuid(),
            SiteCode = siteCode,
            Source = nameof(AgentController),
            EntityId = deviceId,
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = deviceId,
                InstallationId = installation.Id,
                Platform = installation.Platform,
                PushProvider = installation.PushProvider,
                installation.AppVersion,
                installation.OsVersion,
                installation.DeviceModel,
                UpdatedAt = now
            })
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDiagnosticLogs(
        Guid deviceId,
        [FromQuery] int maxBatches = 10,
        CancellationToken cancellationToken = default)
    {
        if (maxBatches is < 1 or > MaxDiagnosticLogBatches)
        {
            return BadRequest(BuildError(
                "VALIDATION.INVALID_MAX_BATCHES",
                $"maxBatches must be between 1 and {MaxDiagnosticLogBatches}."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
            return Unauthorized();

        var device = await _dbContext.AgentRegistrations
            .ForPortal(access)
            .Where(a => a.Id == deviceId)
            .Select(a => new { a.LegalEntityId })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
            return NotFound(BuildError("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found."));

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

    private bool TryGetAuthenticatedDeviceContext(
        out Guid deviceId,
        out string siteCode,
        out Guid legalEntityId)
    {
        deviceId = Guid.Empty;
        legalEntityId = Guid.Empty;
        siteCode = string.Empty;

        var deviceIdClaim = User.FindFirst("sub")?.Value
                         ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var rawSiteCode = User.FindFirst("site")?.Value;
        var leiClaim = User.FindFirst("lei")?.Value;

        if (deviceIdClaim is null
            || rawSiteCode is null
            || leiClaim is null
            || !Guid.TryParse(deviceIdClaim, out deviceId)
            || !Guid.TryParse(leiClaim, out legalEntityId))
        {
            return false;
        }

        siteCode = rawSiteCode;
        return true;
    }

    private static BootstrapTokenHistoryRow ToBootstrapTokenHistoryRow(BootstrapToken token, DateTimeOffset now) =>
        new()
        {
            TokenId = token.Id,
            LegalEntityId = token.LegalEntityId,
            SiteCode = token.SiteCode,
            StoredStatus = token.Status,
            EffectiveStatus = ComputeEffectiveBootstrapTokenStatus(token, now),
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt,
            UsedAt = token.UsedAt,
            UsedByDeviceId = token.UsedByDeviceId,
            RevokedAt = token.RevokedAt,
            CreatedByActorId = token.CreatedByActorId,
            CreatedByActorDisplay = token.CreatedByActorDisplay ?? token.CreatedBy,
            RevokedByActorId = token.RevokedByActorId,
            RevokedByActorDisplay = token.RevokedByActorDisplay
        };

    private static ProvisioningTokenStatus ComputeEffectiveBootstrapTokenStatus(
        BootstrapToken token,
        DateTimeOffset now) =>
        token.Status switch
        {
            ProvisioningTokenStatus.ACTIVE when token.ExpiresAt <= now => ProvisioningTokenStatus.EXPIRED,
            _ => token.Status
        };

    private static bool TryParseBootstrapStatus(string? value, out ProvisioningTokenStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<ProvisioningTokenStatus>(value, true, out var parsed))
        {
            status = parsed;
            return true;
        }

        return false;
    }

    private static CreateAgentCommandResponse ToCreateAgentCommandResponse(AgentCommand command) =>
        new()
        {
            CommandId = command.Id,
            DeviceId = command.DeviceId,
            LegalEntityId = command.LegalEntityId,
            SiteCode = command.SiteCode,
            CommandType = command.CommandType,
            Status = command.Status,
            Reason = command.Reason,
            Payload = ParseJsonOrNull(command.PayloadJson),
            CreatedAt = command.CreatedAt,
            ExpiresAt = command.ExpiresAt,
            CreatedByActorId = command.CreatedByActorId,
            CreatedByActorDisplay = command.CreatedByActorDisplay
        };

    private static JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<int> ExpirePendingCommandsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var commands = await _dbContext.AgentCommands
            .IgnoreQueryFilters()
            .Where(item =>
                item.DeviceId == deviceId
                && item.ExpiresAt <= now
                && (item.Status == AgentCommandStatus.PENDING || item.Status == AgentCommandStatus.DELIVERY_HINT_SENT))
            .ToListAsync(cancellationToken);

        if (commands.Count == 0)
        {
            return 0;
        }

        foreach (var command in commands)
        {
            command.Status = AgentCommandStatus.EXPIRED;
            command.LastError = "Command expired before acknowledgement.";
            command.UpdatedAt = now;

            _dbContext.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = command.LegalEntityId,
                EventType = AgentControlAuditEventTypes.AgentCommandExpired,
                CorrelationId = Guid.NewGuid(),
                SiteCode = command.SiteCode,
                Source = nameof(AgentController),
                EntityId = command.DeviceId,
                Payload = JsonSerializer.Serialize(new
                {
                    CommandId = command.Id,
                    DeviceId = command.DeviceId,
                    command.CommandType,
                    ExpiredAt = now
                })
            });

            _metrics.RecordAgentCommandExpired(
                command.LegalEntityId,
                command.SiteCode,
                command.DeviceId,
                command.CommandType.ToString());
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return commands.Count;
    }

    private async Task<int> CancelPendingCommandsAsync(
        Guid deviceId,
        string? actorId,
        string? actorDisplay,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var commands = await _dbContext.AgentCommands
            .IgnoreQueryFilters()
            .Where(item =>
                item.DeviceId == deviceId
                && (item.Status == AgentCommandStatus.PENDING || item.Status == AgentCommandStatus.DELIVERY_HINT_SENT))
            .ToListAsync(cancellationToken);

        if (commands.Count == 0)
        {
            return 0;
        }

        foreach (var command in commands)
        {
            command.Status = AgentCommandStatus.CANCELLED;
            command.LastError = "Command cancelled because the device was decommissioned.";
            command.UpdatedAt = now;

            _dbContext.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = command.LegalEntityId,
                EventType = AgentControlAuditEventTypes.AgentCommandCancelled,
                CorrelationId = Guid.NewGuid(),
                SiteCode = command.SiteCode,
                Source = nameof(AgentController),
                EntityId = command.DeviceId,
                Payload = JsonSerializer.Serialize(new
                {
                    CommandId = command.Id,
                    DeviceId = command.DeviceId,
                    command.CommandType,
                    CancelledAt = now,
                    CancelledByActorId = actorId,
                    CancelledByActorDisplay = actorDisplay,
                    Reason = reason
                })
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return commands.Count;
    }

    private static bool TryValidateNonSensitiveJson(JsonElement? value, out string? offendingPath)
    {
        offendingPath = null;
        if (!value.HasValue)
        {
            return true;
        }

        return TryValidateNonSensitiveJson(value.Value, "$", ref offendingPath);
    }

    private static bool TryValidateNonSensitiveJson(JsonElement value, string path, ref string? offendingPath)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    if (ContainsSensitivePropertyName(property.Name))
                    {
                        offendingPath = childPath;
                        return false;
                    }

                    if (!TryValidateNonSensitiveJson(property.Value, childPath, ref offendingPath))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryValidateNonSensitiveJson(item, $"{path}[{index}]", ref offendingPath))
                    {
                        return false;
                    }

                    index++;
                }

                return true;

            default:
                return true;
        }
    }

    private static bool ContainsSensitivePropertyName(string propertyName)
    {
        var normalized = propertyName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized is "bootstraptoken"
            or "registrationtoken"
            or "refreshtoken"
            or "devicetoken"
            or "apikey"
            or "password"
            or "sharedsecret"
            or "clientsecret"
            or "webhooksecret"
            or "customertaxid"
            or "taxpayerid"
            || normalized.EndsWith("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal);
    }

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

    private async Task<IActionResult> CommitSuspiciousRegistrationConflictAsync(
        IDbContextTransaction transaction,
        Application.Common.Error error,
        CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return Conflict(BuildError(error.Code, error.Message));
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
