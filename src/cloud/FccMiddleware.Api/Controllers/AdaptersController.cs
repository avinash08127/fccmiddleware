using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/adapters")]
[Authorize(Policy = "PortalUser")]
public sealed class AdaptersController : PortalControllerBase
{
    private readonly AdapterCatalogService _catalog;
    private readonly AdapterConfigPortalService _service;
    private readonly PortalAccessResolver _accessResolver;

    public AdaptersController(
        AdapterCatalogService catalog,
        AdapterConfigPortalService service,
        PortalAccessResolver accessResolver)
    {
        _catalog = catalog;
        _service = service;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdapterSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAdapters(
        [FromQuery] Guid legalEntityId,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        return Ok(await _service.GetAdapterSummariesAsync(legalEntityId, cancellationToken));
    }

    [HttpGet("{adapterKey}")]
    [ProducesResponseType(typeof(AdapterDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdapterDetail(
        string adapterKey,
        [FromQuery] Guid legalEntityId,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        var entry = _catalog.Find(adapterKey);
        if (entry is null)
        {
            return NotFound(BuildError("NOT_FOUND.ADAPTER", $"Adapter '{adapterKey}' was not found."));
        }

        return Ok(await _service.GetAdapterDetailAsync(legalEntityId, entry, cancellationToken));
    }

    [HttpGet("{adapterKey}/defaults")]
    [ProducesResponseType(typeof(AdapterConfigDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdapterDefaults(
        string adapterKey,
        [FromQuery] Guid legalEntityId,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        var entry = _catalog.Find(adapterKey);
        if (entry is null)
        {
            return NotFound(BuildError("NOT_FOUND.ADAPTER", $"Adapter '{adapterKey}' was not found."));
        }

        return Ok(await _service.GetDefaultConfigAsync(legalEntityId, entry, cancellationToken));
    }

    [HttpPut("{adapterKey}/defaults")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(AdapterConfigDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAdapterDefaults(
        string adapterKey,
        [FromBody] UpdateAdapterDefaultConfigRequestDto request,
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

        if (!access.CanAccess(request.LegalEntityId))
        {
            return Forbid();
        }

        var entry = _catalog.Find(adapterKey);
        if (entry is null)
        {
            return NotFound(BuildError("NOT_FOUND.ADAPTER", $"Adapter '{adapterKey}' was not found."));
        }

        try
        {
            return Ok(await _service.UpsertDefaultConfigAsync(
                request.LegalEntityId,
                entry,
                request.Values,
                _accessResolver.ResolveUserId(User) ?? "unknown",
                request.Reason.Trim(),
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_ADAPTER_CONFIG", ex.Message));
        }
    }
}
