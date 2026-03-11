using System.Diagnostics;
using System.Globalization;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.Registration;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Config;
using FccMiddleware.Contracts.Registration;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// Handles Edge Agent device registration, config distribution, token refresh, and decommission.
/// GET  /api/v1/agent/config                  — pull site configuration (ETag-based)
/// POST /api/v1/agent/register               — register a new device using a bootstrap token
/// POST /api/v1/agent/token/refresh           — refresh device JWT (token rotation)
/// POST /api/v1/admin/bootstrap-tokens        — generate a bootstrap token for a site
/// POST /api/v1/admin/agent/{deviceId}/decommission — decommission a device
/// </summary>
[ApiController]
public sealed class AgentController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<AgentController> _logger;

    public AgentController(IMediator mediator, ILogger<AgentController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Generates a single-use bootstrap token for Edge Agent provisioning.
    /// </summary>
    [HttpPost("api/v1/admin/bootstrap-tokens")]
    [AllowAnonymous] // TODO: Replace with [Authorize(Policy="PortalAdmin")] when Azure Entra auth is implemented
    [ProducesResponseType(typeof(GenerateBootstrapTokenResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateBootstrapToken(
        [FromBody] GenerateBootstrapTokenRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GenerateBootstrapTokenCommand
        {
            SiteCode = request.SiteCode,
            LegalEntityId = request.LegalEntityId,
            CreatedBy = User.Identity?.Name ?? "system"
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
        // Extract bootstrap token from X-Provisioning-Token header
        if (!Request.Headers.TryGetValue("X-Provisioning-Token", out var tokenHeader)
            || string.IsNullOrWhiteSpace(tokenHeader.FirstOrDefault()))
        {
            return Unauthorized(BuildError("BOOTSTRAP_TOKEN_MISSING",
                "X-Provisioning-Token header is required."));
        }

        var command = new RegisterDeviceCommand
        {
            ProvisioningToken = tokenHeader.ToString(),
            SiteCode = request.SiteCode,
            DeviceSerialNumber = request.DeviceSerialNumber,
            DeviceModel = request.DeviceModel,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            ReplacePreviousAgent = request.ReplacePreviousAgent
        };

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
                "SITE_NOT_FOUND" or "SITE_MISMATCH" =>
                    BadRequest(BuildError(result.Error.Code, result.Error.Message)),
                _ => StatusCode(StatusCodes.Status500InternalServerError,
                    BuildError("INTERNAL.UNEXPECTED", result.Error.Message, retryable: true))
            };
        }

        var value = result.Value!;
        return StatusCode(StatusCodes.Status201Created, new DeviceRegistrationApiResponse
        {
            DeviceId = value.DeviceId,
            DeviceToken = value.DeviceToken,
            RefreshToken = value.RefreshToken,
            TokenExpiresAt = value.TokenExpiresAt,
            SiteCode = value.SiteCode,
            LegalEntityId = value.LegalEntityId,
            RegisteredAt = value.RegisteredAt,
            SiteConfig = new { } // Placeholder — full site config will be populated when config distribution is implemented
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
    [AllowAnonymous] // TODO: Replace with [Authorize(Policy="PortalAdmin")] when Azure Entra auth is implemented
    [ProducesResponseType(typeof(DecommissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decommission(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
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
                _ => StatusCode(StatusCodes.Status500InternalServerError,
                    BuildError("INTERNAL.UNEXPECTED", result.Error.Message, retryable: true))
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
                _ => StatusCode(StatusCodes.Status500InternalServerError,
                    BuildError("INTERNAL.UNEXPECTED", result.Error.Message, retryable: true))
            };
        }

        var value = result.Value!;

        // Set ETag header with current config version
        Response.Headers["ETag"] = $"\"{value.ConfigVersion}\"";

        if (value.NotModified)
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(value.Config);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
