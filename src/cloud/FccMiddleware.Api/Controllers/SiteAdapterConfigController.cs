using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/sites/{siteId:guid}/adapter-config")]
[Authorize(Policy = "PortalUser")]
public sealed class SiteAdapterConfigController : PortalControllerBase
{
    private readonly AdapterConfigPortalService _service;
    private readonly PortalAccessResolver _accessResolver;

    public SiteAdapterConfigController(
        AdapterConfigPortalService service,
        PortalAccessResolver accessResolver)
    {
        _service = service;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(SiteAdapterConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSiteAdapterConfig(Guid siteId, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var config = await _service.GetSiteAdapterConfigAsync(siteId, cancellationToken);
        if (config is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE_ADAPTER_CONFIG", "Site adapter config was not found."));
        }

        if (!access.CanAccess(config.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(config);
    }

    [HttpPut("overrides")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(SiteAdapterConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSiteAdapterConfig(
        Guid siteId,
        [FromBody] UpdateSiteAdapterConfigRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(BuildError("VALIDATION.REASON_REQUIRED", "A change reason is required."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var current = await _service.GetSiteAdapterConfigAsync(siteId, cancellationToken);
        if (current is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE_ADAPTER_CONFIG", "Site adapter config was not found."));
        }

        if (!access.CanAccess(current.LegalEntityId))
        {
            return Forbid();
        }

        try
        {
            var updated = await _service.UpsertSiteAdapterConfigAsync(
                siteId,
                request.EffectiveValues,
                _accessResolver.ResolveUserId(User) ?? "unknown",
                request.Reason.Trim(),
                cancellationToken);

            return updated is null
                ? NotFound(BuildError("NOT_FOUND.SITE_ADAPTER_CONFIG", "Site adapter config was not found."))
                : Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_ADAPTER_CONFIG", ex.Message));
        }
    }

    [HttpPost("reset")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(SiteAdapterConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetSiteAdapterConfig(
        Guid siteId,
        [FromBody] ResetSiteAdapterConfigRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(BuildError("VALIDATION.REASON_REQUIRED", "A change reason is required."));
        }

        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var current = await _service.GetSiteAdapterConfigAsync(siteId, cancellationToken);
        if (current is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE_ADAPTER_CONFIG", "Site adapter config was not found."));
        }

        if (!access.CanAccess(current.LegalEntityId))
        {
            return Forbid();
        }

        var updated = await _service.ResetSiteAdapterConfigAsync(
            siteId,
            _accessResolver.ResolveUserId(User) ?? "unknown",
            request.Reason.Trim(),
            cancellationToken);

        return updated is null
            ? NotFound(BuildError("NOT_FOUND.SITE_ADAPTER_CONFIG", "Site adapter config was not found."))
            : Ok(updated);
    }
}
