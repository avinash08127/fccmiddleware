using System.Diagnostics;
using FccMiddleware.Api.Auth;
using FccMiddleware.Application.MasterData;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.MasterData;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// Master data synchronisation endpoints — Databricks pushes reference data to the cloud.
///
/// PUT /api/v1/master-data/legal-entities  — upsert legal entities (max 500)
/// PUT /api/v1/master-data/sites           — upsert sites (max 500)
/// PUT /api/v1/master-data/pumps           — upsert pumps + nozzles (max 1000)
/// PUT /api/v1/master-data/products        — upsert fuel products (max 200)
/// PUT /api/v1/master-data/operators       — upsert operators (max 500)
///
/// All endpoints require the Databricks API key scheme (X-Api-Key header, role=master-data-sync).
/// </summary>
[ApiController]
[Route("api/v1/master-data")]
[Authorize(Policy = DatabricksApiKeyAuthOptions.PolicyName)]
public sealed class MasterDataController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<MasterDataController> _logger;

    public MasterDataController(IMediator mediator, ILogger<MasterDataController> logger)
    {
        _mediator = mediator;
        _logger   = logger;
    }

    /// <summary>
    /// Upserts a batch of legal entity records. Records absent from the payload are only soft-deactivated
    /// when isFullSnapshot=true.
    /// </summary>
    [HttpPut("legal-entities")]
    [ProducesResponseType(typeof(MasterDataSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncLegalEntities(
        [FromBody] LegalEntitySyncRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LegalEntities is { Count: 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one legal entity is required."));

        if (request.LegalEntities.Count > 500)
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE", $"Batch size {request.LegalEntities.Count} exceeds maximum of 500."));

        var command = new SyncLegalEntitiesCommand
        {
            IsFullSnapshot = request.IsFullSnapshot,
            Items = request.LegalEntities.Select(r => new LegalEntitySyncItem
            {
                Id                      = r.Id,
                Code                    = r.Code,
                Name                    = r.Name,
                CountryCode             = r.CountryCode,
                CountryName             = r.CountryName,
                CurrencyCode            = r.CurrencyCode,
                TaxAuthorityCode        = r.TaxAuthorityCode,
                DefaultFiscalizationMode = r.DefaultFiscalizationMode,
                FiscalizationProvider   = r.FiscalizationProvider,
                DefaultTimezone         = r.DefaultTimezone,
                OdooCompanyId           = r.OdooCompanyId,
                IsActive                = r.IsActive
            }).ToList()
        };

        var result = await _mediator.Send(command, cancellationToken);
        return Ok(ToResponse(result));
    }

    /// <summary>
    /// Upserts a batch of site records. Records absent from the payload are only soft-deactivated
    /// when isFullSnapshot=true.
    /// </summary>
    [HttpPut("sites")]
    [ProducesResponseType(typeof(MasterDataSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncSites(
        [FromBody] SiteSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Sites is { Count: 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one site is required."));

        if (request.Sites.Count > 500)
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE", $"Batch size {request.Sites.Count} exceeds maximum of 500."));

        var command = new SyncSitesCommand
        {
            IsFullSnapshot = request.IsFullSnapshot,
            Items = request.Sites.Select(r => new SiteSyncItem
            {
                Id              = r.Id,
                SiteCode        = r.SiteCode,
                LegalEntityId   = r.LegalEntityId,
                SiteName        = r.SiteName,
                OperatingModel  = r.OperatingModel,
                ConnectivityMode = r.ConnectivityMode,
                CompanyTaxPayerId = r.CompanyTaxPayerId,
                OperatorName = r.OperatorName,
                OperatorTaxPayerId = r.OperatorTaxPayerId,
                SiteUsesPreAuth = r.SiteUsesPreAuth,
                FiscalizationMode = r.FiscalizationMode,
                TaxAuthorityEndpoint = r.TaxAuthorityEndpoint,
                RequireCustomerTaxId = r.RequireCustomerTaxId,
                FiscalReceiptRequired = r.FiscalReceiptRequired,
                OdooSiteId = r.OdooSiteId,
                IsActive        = r.IsActive
            }).ToList()
        };

        var result = await _mediator.Send(command, cancellationToken);
        return Ok(ToResponse(result));
    }

    /// <summary>
    /// Upserts a batch of pump records (including nested nozzles). Pumps absent from the payload are only
    /// soft-deactivated when isFullSnapshot=true.
    /// </summary>
    [HttpPut("pumps")]
    [ProducesResponseType(typeof(MasterDataSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncPumps(
        [FromBody] PumpSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Pumps is { Count: 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one pump is required."));

        if (request.Pumps.Count > 1000)
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE", $"Batch size {request.Pumps.Count} exceeds maximum of 1000."));

        var command = new SyncPumpsCommand
        {
            IsFullSnapshot = request.IsFullSnapshot,
            Items = request.Pumps.Select(r => new PumpSyncItem
            {
                Id         = r.Id,
                SiteCode   = r.SiteCode,
                PumpNumber = r.PumpNumber,
                FccPumpNumber = r.FccPumpNumber,
                IsActive   = r.IsActive,
                Nozzles    = r.Nozzles.Select(n => new NozzleSyncItem
                {
                    NozzleNumber         = n.NozzleNumber,
                    FccNozzleNumber      = n.FccNozzleNumber,
                    CanonicalProductCode = n.CanonicalProductCode
                }).ToList()
            }).ToList()
        };

        var result = await _mediator.Send(command, cancellationToken);
        return Ok(ToResponse(result));
    }

    /// <summary>
    /// Upserts a batch of fuel product records. Records absent from the payload are only soft-deactivated
    /// when isFullSnapshot=true.
    /// </summary>
    [HttpPut("products")]
    [ProducesResponseType(typeof(MasterDataSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncProducts(
        [FromBody] ProductSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Products is { Count: 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one product is required."));

        if (request.Products.Count > 200)
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE", $"Batch size {request.Products.Count} exceeds maximum of 200."));

        var command = new SyncProductsCommand
        {
            IsFullSnapshot = request.IsFullSnapshot,
            Items = request.Products.Select(r => new ProductSyncItem
            {
                Id            = r.Id,
                LegalEntityId = r.LegalEntityId,
                CanonicalCode = r.CanonicalCode,
                DisplayName   = r.DisplayName,
                IsActive      = r.IsActive
            }).ToList()
        };

        var result = await _mediator.Send(command, cancellationToken);
        return Ok(ToResponse(result));
    }

    /// <summary>
    /// Upserts a batch of operator records. Records absent from the payload are only soft-deactivated
    /// when isFullSnapshot=true.
    /// </summary>
    [HttpPut("operators")]
    [ProducesResponseType(typeof(MasterDataSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SyncOperators(
        [FromBody] OperatorSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Operators is { Count: 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one operator is required."));

        if (request.Operators.Count > 500)
            return BadRequest(BuildError("VALIDATION.BATCH_TOO_LARGE", $"Batch size {request.Operators.Count} exceeds maximum of 500."));

        var command = new SyncOperatorsCommand
        {
            IsFullSnapshot = request.IsFullSnapshot,
            Items = request.Operators.Select(r => new OperatorSyncItem
            {
                Id            = r.Id,
                LegalEntityId = r.LegalEntityId,
                Name          = r.Name,
                TaxPayerId    = r.TaxPayerId,
                IsActive      = r.IsActive
            }).ToList()
        };

        var result = await _mediator.Send(command, cancellationToken);
        return Ok(ToResponse(result));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MasterDataSyncResponse ToResponse(MasterDataSyncResult result) =>
        new()
        {
            UpsertedCount    = result.UpsertedCount,
            UnchangedCount   = result.UnchangedCount,
            DeactivatedCount = result.DeactivatedCount,
            ErrorCount       = result.ErrorCount,
            Errors           = result.Errors.Count > 0 ? result.Errors : null
        };

    private ErrorResponse BuildError(
        string errorCode,
        string message,
        bool retryable = false) =>
        new()
        {
            ErrorCode = errorCode,
            Message   = message,
            TraceId   = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Retryable = retryable
        };
}
