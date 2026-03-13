using System.Text.Json;
using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Adapters;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/sites")]
[Authorize(Policy = "PortalUser")]
public sealed class SitesController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public SitesController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PortalPagedResult<SiteDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSites(
        [FromQuery] Guid legalEntityId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? isActive = null,
        [FromQuery] string? operatingModel = null,
        [FromQuery] string? connectivityMode = null,
        [FromQuery] string? ingestionMode = null,
        [FromQuery] string? fccVendor = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 200)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_PAGE_SIZE", "pageSize must be between 1 and 200."));
        }

        // F09-04: Reject Guid.Empty — avoids a silent empty-result response for an unset parameter.
        if (legalEntityId == Guid.Empty)
        {
            return BadRequest(BuildError("VALIDATION.INVALID_LEGAL_ENTITY", "legalEntityId must not be empty."));
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

        // Parse enum filters early to surface 400s before hitting the database.
        SiteOperatingModel? parsedOperatingModel = null;
        if (!string.IsNullOrWhiteSpace(operatingModel))
        {
            if (!Enum.TryParse<SiteOperatingModel>(operatingModel, true, out var om))
            {
                return BadRequest(BuildError("VALIDATION.INVALID_OPERATING_MODEL", $"Unknown operatingModel '{operatingModel}'."));
            }
            parsedOperatingModel = om;
        }

        IngestionMode? parsedIngestionMode = null;
        if (!string.IsNullOrWhiteSpace(ingestionMode))
        {
            if (!Enum.TryParse<IngestionMode>(ingestionMode, true, out var im))
            {
                return BadRequest(BuildError("VALIDATION.INVALID_INGESTION_MODE", $"Unknown ingestionMode '{ingestionMode}'."));
            }
            parsedIngestionMode = im;
        }

        FccVendor? parsedFccVendor = null;
        if (!string.IsNullOrWhiteSpace(fccVendor))
        {
            if (!Enum.TryParse<FccVendor>(fccVendor, true, out var fv))
            {
                return BadRequest(BuildError("VALIDATION.INVALID_FCC_VENDOR", $"Unknown fccVendor '{fccVendor}'."));
            }
            parsedFccVendor = fv;
        }

        // F09-01 + F09-09: Push all filtering to the database via IQueryable.
        // ForPortal replaces raw IgnoreQueryFilters and enforces explicit tenant scoping,
        // reducing the risk of data leakage if a future endpoint omits the manual check.
        IQueryable<Site> query = _db.Sites
            .ForPortal(access, legalEntityId)
            .Include(site => site.LegalEntity)
            .Include(site => site.FccConfigs.Where(config => config.IsActive));

        if (isActive.HasValue)
        {
            query = query.Where(s => s.IsActive == isActive.Value);
        }

        if (parsedOperatingModel.HasValue)
        {
            query = query.Where(s => s.OperatingModel == parsedOperatingModel.Value);
        }

        if (!string.IsNullOrWhiteSpace(connectivityMode))
        {
            var normalizedMode = connectivityMode.ToUpperInvariant();
            query = query.Where(s => s.ConnectivityMode == normalizedMode);
        }

        if (parsedIngestionMode.HasValue)
        {
            query = query.Where(s => s.FccConfigs.Any(c => c.IsActive && c.IngestionMode == parsedIngestionMode.Value));
        }

        if (parsedFccVendor.HasValue)
        {
            query = query.Where(s => s.FccConfigs.Any(c => c.IsActive && c.FccVendor == parsedFccVendor.Value));
        }

        // F09-02: Count before cursor pagination to avoid double-enumeration of a lazy LINQ chain.
        var totalCount = await query.CountAsync(cancellationToken);

        // F09-03: Cursor applied at DB level. UpdatedAt is non-nullable on Site, so no null-skip risk.
        if (PortalCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
        {
            query = query.Where(s =>
                s.UpdatedAt > cursorTimestamp ||
                (s.UpdatedAt == cursorTimestamp && s.Id > cursorId));
        }

        // F09-01: Only fetch pageSize+1 rows from the database — no full table scan.
        var page = await query
            .OrderBy(s => s.UpdatedAt)
            .ThenBy(s => s.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > pageSize;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = PortalCursor.Encode(last.UpdatedAt, last.Id);
        }

        return Ok(new PortalPagedResult<SiteDto>
        {
            Data = page.Select(MapSite).ToList(),
            Meta = new PortalPageMeta
            {
                PageSize = page.Count,
                HasMore = hasMore,
                NextCursor = nextCursor,
                TotalCount = totalCount
            }
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SiteDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSite(Guid id, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var site = await LoadSiteDetailAsync(id, cancellationToken);
        if (site is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE", "Site was not found."));
        }

        if (!access.CanAccess(site.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(MapSiteDetail(site));
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(SiteDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSite(Guid id, [FromBody] UpdateSiteRequestDto request, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var site = await _db.Sites
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (site is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE", "Site was not found."));
        }

        if (!access.CanAccess(site.LegalEntityId))
        {
            return Forbid();
        }

        var operatingModel = default(SiteOperatingModel);
        if (!string.IsNullOrWhiteSpace(request.OperatingModel)
            && !Enum.TryParse<SiteOperatingModel>(request.OperatingModel, true, out operatingModel))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_OPERATING_MODEL", $"Unknown operatingModel '{request.OperatingModel}'."));
        }

        var fiscalizationMode = default(FiscalizationMode);
        if (!string.IsNullOrWhiteSpace(request.Fiscalization?.Mode)
            && !Enum.TryParse<FiscalizationMode>(request.Fiscalization.Mode, true, out fiscalizationMode))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_FISCALIZATION_MODE", $"Unknown fiscalization mode '{request.Fiscalization.Mode}'."));
        }

        if (!string.IsNullOrWhiteSpace(request.OperatingModel))
        {
            site.OperatingModel = operatingModel;
        }

        if (!string.IsNullOrWhiteSpace(request.ConnectivityMode))
        {
            var normalizedMode = request.ConnectivityMode.ToUpperInvariant();
            if (normalizedMode is not ("CONNECTED" or "DISCONNECTED"))
            {
                return BadRequest(BuildError("VALIDATION.INVALID_CONNECTIVITY_MODE", $"Unknown connectivityMode '{request.ConnectivityMode}'. Must be CONNECTED or DISCONNECTED."));
            }
            site.ConnectivityMode = normalizedMode;
        }

        if (request.SiteUsesPreAuth.HasValue)
        {
            // Block enabling pre-auth on sites missing required master data fields
            if (request.SiteUsesPreAuth.Value && !site.SiteUsesPreAuth)
            {
                if (string.IsNullOrWhiteSpace(site.CompanyTaxPayerId))
                {
                    return BadRequest(BuildError(
                        "VALIDATION.INCOMPLETE_MASTER_DATA",
                        "Cannot enable SiteUsesPreAuth: site is missing companyTaxPayerId. Sync master data from Databricks first."));
                }
            }

            site.SiteUsesPreAuth = request.SiteUsesPreAuth.Value;
        }

        if (request.Tolerance is not null)
        {
            if (request.Tolerance.AmountTolerancePct.HasValue)
            {
                site.AmountTolerancePercent = request.Tolerance.AmountTolerancePct.Value;
            }

            if (request.Tolerance.AmountToleranceAbsoluteMinorUnits.HasValue)
            {
                site.AmountToleranceAbsolute = request.Tolerance.AmountToleranceAbsoluteMinorUnits.Value;
            }

            if (request.Tolerance.TimeWindowMinutes.HasValue)
            {
                site.TimeWindowMinutes = request.Tolerance.TimeWindowMinutes.Value;
            }
        }

        if (request.Fiscalization is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.Fiscalization.Mode))
            {
                site.FiscalizationMode = fiscalizationMode;
            }

            if (request.Fiscalization.TaxAuthorityEndpoint is not null)
            {
                site.TaxAuthorityEndpoint = request.Fiscalization.TaxAuthorityEndpoint;
            }

            if (request.Fiscalization.RequireCustomerTaxId.HasValue)
            {
                site.RequireCustomerTaxId = request.Fiscalization.RequireCustomerTaxId.Value;
            }

            if (request.Fiscalization.FiscalReceiptRequired.HasValue)
            {
                site.FiscalReceiptRequired = request.Fiscalization.FiscalReceiptRequired.Value;
            }
        }

        site.UpdatedAt = DateTimeOffset.UtcNow;

        // F10-11: Audit event for site config change
        var siteUserId = User.FindFirst("sub")?.Value ?? "unknown";
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            LegalEntityId = site.LegalEntityId,
            EventType = "SITE_CONFIG_UPDATED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = site.SiteCode,
            Source = "SitesController",
            Payload = JsonSerializer.Serialize(new
            {
                SiteId = id,
                site.SiteCode,
                UpdatedBy = siteUserId,
                Fields = new
                {
                    request.OperatingModel,
                    request.ConnectivityMode,
                    request.SiteUsesPreAuth,
                    HasTolerance = request.Tolerance is not null,
                    HasFiscalization = request.Fiscalization is not null,
                }
            }),
            EntityId = id
        });

        await _db.SaveChangesAsync(cancellationToken);

        var detail = await LoadSiteDetailAsync(id, cancellationToken);
        return Ok(MapSiteDetail(detail!));
    }

    [HttpPut("{siteId:guid}/fcc-config")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(FccConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateFccConfig(
        Guid siteId,
        [FromBody] UpdateFccConfigRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var site = await _db.Sites
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(item => item.LegalEntity)
            .FirstOrDefaultAsync(item => item.Id == siteId, cancellationToken);

        if (site is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE", "Site was not found."));
        }

        if (!access.CanAccess(site.LegalEntityId))
        {
            return Forbid();
        }

        if (!TryParseFccVendor(request.Vendor, out var vendor))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_FCC_VENDOR", $"Unknown FCC vendor '{request.Vendor}'."));
        }

        if (vendor.HasValue && !CloudFccAdapterFactoryRegistration.IsSupported(vendor.Value))
        {
            return BadRequest(BuildError(
                "VALIDATION.UNSUPPORTED_FCC_VENDOR",
                $"FCC vendor '{vendor.Value}' is not supported by the current cloud runtimes."));
        }

        if (!TryParseConnectionProtocol(request.ConnectionProtocol, out var protocol))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_CONNECTION_PROTOCOL", $"Unknown connection protocol '{request.ConnectionProtocol}'."));
        }

        if (!TryParseTransactionMode(request.TransactionMode, out var transactionMode))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_TRANSACTION_MODE", $"Unknown transaction mode '{request.TransactionMode}'."));
        }

        if (!TryParseIngestionMode(request.IngestionMode, out var parsedIngestionMode))
        {
            return BadRequest(BuildError("VALIDATION.INVALID_INGESTION_MODE", $"Unknown ingestion mode '{request.IngestionMode}'."));
        }

        var config = await _db.FccConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.SiteId == siteId && item.IsActive, cancellationToken);

        if (config is null)
        {
            config = new FccConfig
            {
                SiteId = site.Id,
                LegalEntityId = site.LegalEntityId,
                FccVendor = vendor ?? FccVendor.DOMS,
                ConnectionProtocol = protocol ?? ConnectionProtocol.REST,
                HostAddress = request.HostAddress ?? "127.0.0.1",
                Port = request.Port ?? 8080,
                CredentialRef = $"portal-managed://{site.SiteCode}",
                IngestionMethod = transactionMode ?? IngestionMethod.PUSH,
                IngestionMode = parsedIngestionMode ?? IngestionMode.CLOUD_DIRECT,
                PullIntervalSeconds = request.PullIntervalSeconds,
                CatchUpPullIntervalSeconds = request.CatchUpPullIntervalSeconds,
                HybridCatchUpIntervalSeconds = request.HybridCatchUpIntervalSeconds,
                HeartbeatIntervalSeconds = request.HeartbeatIntervalSeconds ?? 60,
                HeartbeatTimeoutSeconds = request.HeartbeatTimeoutSeconds ?? 180,
                IsActive = request.Enabled ?? true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _db.FccConfigs.Add(config);
        }
        else
        {
            if (vendor.HasValue)
            {
                config.FccVendor = vendor.Value;
            }

            if (protocol.HasValue)
            {
                config.ConnectionProtocol = protocol.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.HostAddress))
            {
                config.HostAddress = request.HostAddress;
            }

            if (request.Port.HasValue)
            {
                config.Port = request.Port.Value;
            }

            if (transactionMode.HasValue)
            {
                config.IngestionMethod = transactionMode.Value;
            }

            if (parsedIngestionMode.HasValue)
            {
                config.IngestionMode = parsedIngestionMode.Value;
            }

            if (request.PullIntervalSeconds.HasValue)
            {
                config.PullIntervalSeconds = request.PullIntervalSeconds.Value;
            }

            if (request.CatchUpPullIntervalSeconds.HasValue)
            {
                config.CatchUpPullIntervalSeconds = request.CatchUpPullIntervalSeconds.Value;
            }

            if (request.HybridCatchUpIntervalSeconds.HasValue)
            {
                config.HybridCatchUpIntervalSeconds = request.HybridCatchUpIntervalSeconds.Value;
            }

            if (request.HeartbeatIntervalSeconds.HasValue)
            {
                config.HeartbeatIntervalSeconds = request.HeartbeatIntervalSeconds.Value;
            }

            if (request.HeartbeatTimeoutSeconds.HasValue)
            {
                config.HeartbeatTimeoutSeconds = request.HeartbeatTimeoutSeconds.Value;
            }

            if (request.Enabled.HasValue)
            {
                config.IsActive = request.Enabled.Value;
            }

            config.ConfigVersion += 1;
            config.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // F10-01: Apply vendor-specific fields
        ApplyVendorSpecificFields(config, request);

        // F10-11: Audit event for FCC config change
        var userId = User.FindFirst("sub")?.Value ?? "unknown";
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            LegalEntityId = site.LegalEntityId,
            EventType = "FCC_CONFIG_UPDATED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = site.SiteCode,
            Source = "SitesController",
            Payload = JsonSerializer.Serialize(new
            {
                SiteId = siteId,
                site.SiteCode,
                Vendor = request.Vendor,
                UpdatedBy = userId,
            }),
            EntityId = config.Id
        });

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(MapFccConfig(config));
    }

    [HttpGet("{siteId:guid}/pumps")]
    [ProducesResponseType(typeof(IReadOnlyList<PumpDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPumps(Guid siteId, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var site = await LoadSiteDetailAsync(siteId, cancellationToken);
        if (site is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE", "Site was not found."));
        }

        if (!access.CanAccess(site.LegalEntityId))
        {
            return Forbid();
        }

        return Ok(site.Pumps.Where(pump => pump.IsActive).OrderBy(pump => pump.PumpNumber).Select(pump => MapPump(pump, site.SiteCode)).ToList());
    }

    [HttpPost("{siteId:guid}/pumps")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(PumpDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddPump(
        Guid siteId,
        [FromBody] AddPumpRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var site = await _db.Sites
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == siteId, cancellationToken);

        if (site is null)
        {
            return NotFound(BuildError("NOT_FOUND.SITE", "Site was not found."));
        }

        if (!access.CanAccess(site.LegalEntityId))
        {
            return Forbid();
        }

        var existing = await _db.Pumps
            .IgnoreQueryFilters()
            .AnyAsync(item => item.SiteId == siteId && item.PumpNumber == request.PumpNumber && item.IsActive, cancellationToken);

        if (existing)
        {
            return BadRequest(BuildError("VALIDATION.DUPLICATE_PUMP", $"Pump number {request.PumpNumber} already exists for this site."));
        }

        var productCodes = request.Nozzles.Select(item => item.CanonicalProductCode).Distinct().ToList();
        var products = await _db.Products
            .IgnoreQueryFilters()
            .Where(item => item.LegalEntityId == site.LegalEntityId && productCodes.Contains(item.ProductCode))
            .ToDictionaryAsync(item => item.ProductCode, cancellationToken);

        if (products.Count != productCodes.Count)
        {
            return BadRequest(BuildError("VALIDATION.PRODUCT_NOT_FOUND", "One or more nozzle product mappings are invalid."));
        }

        var now = DateTimeOffset.UtcNow;
        var pump = new Pump
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            LegalEntityId = site.LegalEntityId,
            PumpNumber = request.PumpNumber,
            FccPumpNumber = request.FccPumpNumber,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        foreach (var nozzleRequest in request.Nozzles)
        {
            pump.Nozzles.Add(new Nozzle
            {
                Id = Guid.NewGuid(),
                PumpId = pump.Id,
                SiteId = site.Id,
                LegalEntityId = site.LegalEntityId,
                OdooNozzleNumber = nozzleRequest.NozzleNumber,
                FccNozzleNumber = nozzleRequest.FccNozzleNumber,
                ProductId = products[nozzleRequest.CanonicalProductCode].Id,
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        _db.Pumps.Add(pump);

        // F10-11: Audit event for pump added
        var addPumpUserId = User.FindFirst("sub")?.Value ?? "unknown";
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = site.LegalEntityId,
            EventType = "PUMP_ADDED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = site.SiteCode,
            Source = "SitesController",
            Payload = JsonSerializer.Serialize(new
            {
                SiteId = siteId,
                site.SiteCode,
                pump.PumpNumber,
                NozzleCount = pump.Nozzles.Count,
                AddedBy = addPumpUserId,
            }),
            EntityId = pump.Id
        });

        await _db.SaveChangesAsync(cancellationToken);

        pump = await _db.Pumps
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(item => item.Site)
            .Include(item => item.Nozzles.Where(nozzle => nozzle.IsActive))
                .ThenInclude(nozzle => nozzle.Product)
            .FirstAsync(item => item.Id == pump.Id, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, MapPump(pump, site.SiteCode));
    }

    [HttpDelete("{siteId:guid}/pumps/{pumpId:guid}")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemovePump(Guid siteId, Guid pumpId, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var pump = await _db.Pumps
            .IgnoreQueryFilters()
            .Include(item => item.Nozzles)
            .FirstOrDefaultAsync(item => item.Id == pumpId && item.SiteId == siteId, cancellationToken);

        if (pump is null)
        {
            return NotFound(BuildError("NOT_FOUND.PUMP", "Pump was not found."));
        }

        if (!access.CanAccess(pump.LegalEntityId))
        {
            return Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        pump.IsActive = false;
        pump.UpdatedAt = now;
        foreach (var nozzle in pump.Nozzles)
        {
            nozzle.IsActive = false;
            nozzle.UpdatedAt = now;
        }

        // F10-11: Audit event for pump removed
        var removePumpUserId = User.FindFirst("sub")?.Value ?? "unknown";
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = pump.LegalEntityId,
            EventType = "PUMP_REMOVED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = null,
            Source = "SitesController",
            Payload = JsonSerializer.Serialize(new
            {
                SiteId = siteId,
                PumpId = pumpId,
                pump.PumpNumber,
                RemovedBy = removePumpUserId,
            }),
            EntityId = pumpId
        });

        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("{siteId:guid}/pumps/{pumpId:guid}/nozzles/{nozzleNumber:int}")]
    [Authorize(Policy = "PortalAdminWrite")]
    [ProducesResponseType(typeof(NozzleDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateNozzle(
        Guid siteId,
        Guid pumpId,
        int nozzleNumber,
        [FromBody] UpdateNozzleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var nozzle = await _db.Nozzles
            .IgnoreQueryFilters()
            .Include(item => item.Product)
            .FirstOrDefaultAsync(
                item => item.SiteId == siteId && item.PumpId == pumpId && item.OdooNozzleNumber == nozzleNumber,
                cancellationToken);

        if (nozzle is null)
        {
            return NotFound(BuildError("NOT_FOUND.NOZZLE", "Nozzle was not found."));
        }

        if (!access.CanAccess(nozzle.LegalEntityId))
        {
            return Forbid();
        }

        var product = await _db.Products
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                item => item.LegalEntityId == nozzle.LegalEntityId && item.ProductCode == request.CanonicalProductCode,
                cancellationToken);

        if (product is null)
        {
            return BadRequest(BuildError("VALIDATION.PRODUCT_NOT_FOUND", $"Product '{request.CanonicalProductCode}' was not found."));
        }

        nozzle.ProductId = product.Id;
        nozzle.Product = product;
        nozzle.UpdatedAt = DateTimeOffset.UtcNow;

        // F10-11: Audit event for nozzle updated
        var nozzleUserId = User.FindFirst("sub")?.Value ?? "unknown";
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            LegalEntityId = nozzle.LegalEntityId,
            EventType = "NOZZLE_UPDATED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = null,
            Source = "SitesController",
            Payload = JsonSerializer.Serialize(new
            {
                SiteId = siteId,
                PumpId = pumpId,
                NozzleNumber = nozzleNumber,
                NewProductCode = request.CanonicalProductCode,
                UpdatedBy = nozzleUserId,
            }),
            EntityId = nozzle.Id
        });

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(MapNozzle(nozzle));
    }

    private async Task<Site?> LoadSiteDetailAsync(Guid siteId, CancellationToken cancellationToken) =>
        await _db.Sites
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(site => site.LegalEntity)
            .Include(site => site.FccConfigs.Where(config => config.IsActive))
            .Include(site => site.Pumps.Where(pump => pump.IsActive))
                .ThenInclude(pump => pump.Nozzles.Where(nozzle => nozzle.IsActive))
                    .ThenInclude(nozzle => nozzle.Product)
            .FirstOrDefaultAsync(site => site.Id == siteId, cancellationToken);

    private static SiteDto MapSite(Site site)
    {
        var config = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        return new SiteDto
        {
            Id = site.Id,
            SiteCode = site.SiteCode,
            LegalEntityId = site.LegalEntityId,
            SiteName = site.SiteName,
            OperatingModel = site.OperatingModel.ToString(),
            SiteUsesPreAuth = site.SiteUsesPreAuth,
            ConnectivityMode = site.ConnectivityMode,
            IngestionMode = config?.IngestionMode.ToString(),
            FccVendor = config?.FccVendor.ToString(),
            Timezone = site.LegalEntity.DefaultTimezone,
            IsActive = site.IsActive,
            UpdatedAt = site.UpdatedAt
        };
    }

    private static SiteDetailDto MapSiteDetail(Site site) =>
        new()
        {
            Id = site.Id,
            SiteCode = site.SiteCode,
            LegalEntityId = site.LegalEntityId,
            SiteName = site.SiteName,
            OperatingModel = site.OperatingModel.ToString(),
            SiteUsesPreAuth = site.SiteUsesPreAuth,
            ConnectivityMode = site.ConnectivityMode,
            IngestionMode = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).FirstOrDefault()?.IngestionMode.ToString(),
            FccVendor = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).FirstOrDefault()?.FccVendor.ToString(),
            Timezone = site.LegalEntity.DefaultTimezone,
            IsActive = site.IsActive,
            UpdatedAt = site.UpdatedAt,
            OperatorName = site.OperatorName,
            Fcc = site.FccConfigs.OrderByDescending(item => item.UpdatedAt).Select(MapFccConfig).FirstOrDefault(),
            Fiscalization = new SiteFiscalizationDto
            {
                Mode = site.FiscalizationMode.ToString(),
                TaxAuthorityEndpoint = site.TaxAuthorityEndpoint,
                RequireCustomerTaxId = site.RequireCustomerTaxId,
                FiscalReceiptRequired = site.FiscalReceiptRequired,
            },
            Tolerance = new SiteToleranceDto
            {
                AmountTolerancePct = site.AmountTolerancePercent ?? site.LegalEntity.AmountTolerancePercent ?? 0,
                AmountToleranceAbsoluteMinorUnits = site.AmountToleranceAbsolute ?? site.LegalEntity.AmountToleranceAbsolute ?? 0,
                TimeWindowMinutes = site.TimeWindowMinutes ?? site.LegalEntity.TimeWindowMinutes ?? 60
            },
            Pumps = site.Pumps.OrderBy(pump => pump.PumpNumber).Select(pump => MapPump(pump, site.SiteCode)).ToList()
        };

    private static PumpDto MapPump(Pump pump, string? siteCode = null) =>
        new()
        {
            Id = pump.Id,
            SiteCode = siteCode ?? pump.Site?.SiteCode ?? string.Empty,
            PumpNumber = pump.PumpNumber,
            Nozzles = pump.Nozzles
                .Where(nozzle => nozzle.IsActive)
                .OrderBy(nozzle => nozzle.OdooNozzleNumber)
                .Select(MapNozzle)
                .ToList(),
            IsActive = pump.IsActive,
            UpdatedAt = pump.UpdatedAt
        };

    private static NozzleDto MapNozzle(Nozzle nozzle) =>
        new()
        {
            NozzleNumber = nozzle.OdooNozzleNumber,
            CanonicalProductCode = nozzle.Product.ProductCode,
            OdooPumpId = null
        };

    private static FccConfigurationDto MapFccConfig(FccConfig config) =>
        new()
        {
            Enabled = config.IsActive,
            FccId = config.Id.ToString(),
            Vendor = config.FccVendor.ToString(),
            Model = config.FccModel,
            Version = null,
            ConnectionProtocol = config.ConnectionProtocol.ToString(),
            HostAddress = config.HostAddress,
            Port = config.Port,
            CredentialRef = config.CredentialRef,
            CredentialRevision = config.ConfigVersion,
            SecretEnvelope = new SecretEnvelopeDto
            {
                Format = "NONE",
                Payload = null
            },
            TransactionMode = config.IngestionMethod.ToString(),
            IngestionMode = config.IngestionMode.ToString(),
            PullIntervalSeconds = config.PullIntervalSeconds,
            CatchUpPullIntervalSeconds = config.CatchUpPullIntervalSeconds,
            HybridCatchUpIntervalSeconds = config.HybridCatchUpIntervalSeconds,
            HeartbeatIntervalSeconds = config.HeartbeatIntervalSeconds,
            HeartbeatTimeoutSeconds = config.HeartbeatTimeoutSeconds,
            PushSourceIpAllowList = Array.Empty<string>(),

            // F10-01: DOMS TCP/JPL vendor-specific fields
            JplPort = config.JplPort,
            FcAccessCode = config.FcAccessCode,
            DomsCountryCode = config.DomsCountryCode,
            PosVersionId = config.PosVersionId,
            ConfiguredPumps = config.ConfiguredPumps,

            // F10-01: Radix vendor-specific fields
            SharedSecret = config.SharedSecret,
            UsnCode = config.UsnCode,
            AuthPort = config.AuthPort,
            FccPumpAddressMap = config.FccPumpAddressMap,

            // F10-01: Petronite OAuth2 vendor-specific fields
            ClientId = config.ClientId,
            ClientSecret = config.ClientSecret,
            WebhookSecret = config.WebhookSecret,
            OAuthTokenEndpoint = config.OAuthTokenEndpoint,

            // F10-01: Advatec EFD vendor-specific fields
            AdvatecDevicePort = config.AdvatecDevicePort,
            AdvatecWebhookToken = config.AdvatecWebhookToken,
            AdvatecEfdSerialNumber = config.AdvatecEfdSerialNumber,
            AdvatecCustIdType = config.AdvatecCustIdType,
            AdvatecPumpMap = config.AdvatecPumpMap,
        };

    private static bool TryParseFccVendor(string? value, out FccVendor? vendor)
    {
        vendor = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<FccVendor>(value, true, out var parsed))
        {
            vendor = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseConnectionProtocol(string? value, out ConnectionProtocol? protocol)
    {
        protocol = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<ConnectionProtocol>(value, true, out var parsed))
        {
            protocol = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseTransactionMode(string? value, out IngestionMethod? transactionMode)
    {
        transactionMode = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<IngestionMethod>(value, true, out var parsed))
        {
            transactionMode = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseIngestionMode(string? value, out IngestionMode? ingestionMode)
    {
        ingestionMode = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<IngestionMode>(value, true, out var parsed))
        {
            ingestionMode = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// F10-01: Applies vendor-specific fields from the request to the FCC config entity.
    /// </summary>
    private static void ApplyVendorSpecificFields(FccConfig config, UpdateFccConfigRequestDto request)
    {
        // DOMS TCP/JPL fields
        if (request.JplPort.HasValue) config.JplPort = request.JplPort.Value;
        if (request.FcAccessCode is not null) config.FcAccessCode = request.FcAccessCode;
        if (request.DomsCountryCode is not null) config.DomsCountryCode = request.DomsCountryCode;
        if (request.PosVersionId is not null) config.PosVersionId = request.PosVersionId;
        if (request.ConfiguredPumps is not null) config.ConfiguredPumps = request.ConfiguredPumps;

        // Radix fields
        if (request.SharedSecret is not null) config.SharedSecret = request.SharedSecret;
        if (request.UsnCode.HasValue) config.UsnCode = request.UsnCode.Value;
        if (request.AuthPort.HasValue) config.AuthPort = request.AuthPort.Value;
        if (request.FccPumpAddressMap is not null) config.FccPumpAddressMap = request.FccPumpAddressMap;

        // Petronite OAuth2 fields
        if (request.ClientId is not null) config.ClientId = request.ClientId;
        if (request.ClientSecret is not null) config.ClientSecret = request.ClientSecret;
        if (request.WebhookSecret is not null) config.WebhookSecret = request.WebhookSecret;
        if (request.OAuthTokenEndpoint is not null) config.OAuthTokenEndpoint = request.OAuthTokenEndpoint;

        // Advatec EFD fields
        if (request.AdvatecDevicePort.HasValue) config.AdvatecDevicePort = request.AdvatecDevicePort.Value;
        if (request.AdvatecWebhookToken is not null) config.AdvatecWebhookToken = request.AdvatecWebhookToken;
        if (request.AdvatecEfdSerialNumber is not null) config.AdvatecEfdSerialNumber = request.AdvatecEfdSerialNumber;
        if (request.AdvatecCustIdType.HasValue) config.AdvatecCustIdType = request.AdvatecCustIdType.Value;
        if (request.AdvatecPumpMap is not null) config.AdvatecPumpMap = request.AdvatecPumpMap;
    }
}
