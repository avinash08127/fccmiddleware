using System.Diagnostics;
using System.Security.Claims;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.SiteData;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Api.Controllers;

/// <summary>
/// Handles site operational data from Edge Agents: BNA reports, pump totals,
/// price snapshots, and pump control history.
/// </summary>
[ApiController]
[Route("api/v1/sites")]
public sealed class SiteDataController : ControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IAuthoritativeWriteFenceService _writeFence;
    private readonly ILogger<SiteDataController> _logger;

    public SiteDataController(
        FccMiddlewareDbContext db,
        IAuthoritativeWriteFenceService writeFence,
        ILogger<SiteDataController> logger)
    {
        _db = db;
        _writeFence = writeFence;
        _logger = logger;
    }

    // ── BNA Reports ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Receive BNA (Banknote Acceptor) reports from edge agents for cash reconciliation.
    /// </summary>
    [HttpPost("{siteCode}/bna-reports")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(SiteDataAcceptedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReceiveBnaReports(
        string siteCode,
        [FromBody] BnaReportBatchRequest request,
        CancellationToken ct)
    {
        var (deviceId, legalEntityId, error) = ExtractClaims(siteCode);
        if (error is not null) return error;

        if (request.Reports is not { Count: > 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one BNA report is required."));

        var records = request.Reports.Select(r => new BnaReportRecord
        {
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            TerminalId = r.TerminalId ?? "",
            NotesAccepted = r.NotesAccepted,
            ReportedAtUtc = r.ReportedAtUtc ?? DateTimeOffset.UtcNow,
            DeviceId = deviceId,
        }).ToList();

        _db.BnaReports.AddRange(records);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("BNA reports ingested: {Count} report(s) for site {SiteCode}.",
            records.Count, siteCode);

        return Created($"api/v1/sites/{siteCode}/bna-reports", new SiteDataAcceptedResponse
        {
            Message = $"{records.Count} BNA report(s) accepted.",
            Count = records.Count,
        });
    }

    // ── Pump Totals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Query pump totals for a site (shift reconciliation).
    /// </summary>
    [HttpGet("{siteCode}/pump-totals")]
    [Authorize(Policy = "PortalUser")]
    [ProducesResponseType(typeof(PumpTotalsQueryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPumpTotals(
        string siteCode,
        [FromQuery] int? pumpNumber,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var query = _db.PumpTotals
            .Where(t => t.SiteCode == siteCode);

        if (pumpNumber.HasValue)
            query = query.Where(t => t.PumpNumber == pumpNumber.Value);
        if (from.HasValue)
            query = query.Where(t => t.ObservedAtUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(t => t.ObservedAtUtc <= to.Value);

        var totals = await query
            .OrderByDescending(t => t.ObservedAtUtc)
            .Take(100)
            .Select(t => new PumpTotalsItem
            {
                PumpNumber = t.PumpNumber,
                TotalVolumeMicrolitres = t.TotalVolumeMicrolitres,
                TotalAmountMinorUnits = t.TotalAmountMinorUnits,
                ObservedAtUtc = t.ObservedAtUtc,
            })
            .ToListAsync(ct);

        return Ok(new PumpTotalsQueryResponse { Totals = totals });
    }

    /// <summary>
    /// Receive pump totals snapshots from edge agents.
    /// </summary>
    [HttpPost("{siteCode}/pump-totals")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(SiteDataAcceptedResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> ReceivePumpTotals(
        string siteCode,
        [FromBody] PumpTotalsBatchRequest request,
        CancellationToken ct)
    {
        var (deviceId, legalEntityId, error) = ExtractClaims(siteCode);
        if (error is not null) return error;

        if (request.Totals is not { Count: > 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one pump totals record is required."));

        var records = request.Totals.Select(t => new PumpTotalsRecord
        {
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            PumpNumber = t.PumpNumber,
            TotalVolumeMicrolitres = t.TotalVolumeMicrolitres,
            TotalAmountMinorUnits = t.TotalAmountMinorUnits,
            ObservedAtUtc = t.ObservedAtUtc ?? DateTimeOffset.UtcNow,
            DeviceId = deviceId,
        }).ToList();

        _db.PumpTotals.AddRange(records);
        await _db.SaveChangesAsync(ct);

        return Created($"api/v1/sites/{siteCode}/pump-totals", new SiteDataAcceptedResponse
        {
            Message = $"{records.Count} pump totals record(s) accepted.",
            Count = records.Count,
        });
    }

    // ── Pump Control History ────────────────────────────────────────────────────

    /// <summary>
    /// Query pump block/unblock/control audit trail.
    /// </summary>
    [HttpGet("{siteCode}/pump-control-history")]
    [Authorize(Policy = "PortalUser")]
    [ProducesResponseType(typeof(PumpControlHistoryQueryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPumpControlHistory(
        string siteCode,
        [FromQuery] int? pumpNumber,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.PumpControlHistory
            .Where(h => h.SiteCode == siteCode);

        if (pumpNumber.HasValue)
            query = query.Where(h => h.PumpNumber == pumpNumber.Value);
        if (from.HasValue)
            query = query.Where(h => h.ActionAtUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(h => h.ActionAtUtc <= to.Value);

        var history = await query
            .OrderByDescending(h => h.ActionAtUtc)
            .Take(pageSize)
            .Select(h => new PumpControlHistoryItem
            {
                PumpNumber = h.PumpNumber,
                ActionType = h.ActionType,
                Source = h.Source,
                Note = h.Note,
                ActionAtUtc = h.ActionAtUtc,
            })
            .ToListAsync(ct);

        return Ok(new PumpControlHistoryQueryResponse { History = history });
    }

    /// <summary>
    /// Receive pump control history from edge agents.
    /// </summary>
    [HttpPost("{siteCode}/pump-control-history")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(SiteDataAcceptedResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> ReceivePumpControlHistory(
        string siteCode,
        [FromBody] PumpControlHistoryBatchRequest request,
        CancellationToken ct)
    {
        var (deviceId, legalEntityId, error) = ExtractClaims(siteCode);
        if (error is not null) return error;

        if (request.Events is not { Count: > 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one pump control event is required."));

        var records = request.Events.Select(e => new PumpControlHistoryRecord
        {
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            PumpNumber = e.PumpNumber,
            ActionType = e.ActionType ?? "",
            Source = e.Source ?? "EdgeAgent",
            Note = e.Note,
            DeviceId = deviceId,
            ActionAtUtc = e.ActionAtUtc ?? DateTimeOffset.UtcNow,
        }).ToList();

        _db.PumpControlHistory.AddRange(records);
        await _db.SaveChangesAsync(ct);

        return Created($"api/v1/sites/{siteCode}/pump-control-history", new SiteDataAcceptedResponse
        {
            Message = $"{records.Count} pump control event(s) accepted.",
            Count = records.Count,
        });
    }

    // ── Price Snapshots ─────────────────────────────────────────────────────────

    /// <summary>
    /// Receive fuel price change snapshots from edge agents.
    /// </summary>
    [HttpPost("{siteCode}/price-snapshots")]
    [Authorize(Policy = "EdgeAgentDevice")]
    [ProducesResponseType(typeof(SiteDataAcceptedResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> ReceivePriceSnapshots(
        string siteCode,
        [FromBody] PriceSnapshotBatchRequest request,
        CancellationToken ct)
    {
        var (deviceId, legalEntityId, error) = ExtractClaims(siteCode);
        if (error is not null) return error;

        if (request.Snapshots is not { Count: > 0 })
            return BadRequest(BuildError("VALIDATION.EMPTY_BATCH", "At least one price snapshot is required."));

        var records = request.Snapshots.Select(s => new PriceSnapshotRecord
        {
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            PriceSetId = s.PriceSetId ?? "01",
            GradeId = s.GradeId ?? "",
            GradeName = s.GradeName ?? "",
            PriceMinorUnits = s.PriceMinorUnits,
            CurrencyCode = s.CurrencyCode ?? "",
            ObservedAtUtc = s.ObservedAtUtc ?? DateTimeOffset.UtcNow,
            DeviceId = deviceId,
        }).ToList();

        _db.PriceSnapshots.AddRange(records);
        await _db.SaveChangesAsync(ct);

        return Created($"api/v1/sites/{siteCode}/price-snapshots", new SiteDataAcceptedResponse
        {
            Message = $"{records.Count} price snapshot(s) accepted.",
            Count = records.Count,
        });
    }

    /// <summary>
    /// Query current fuel prices for a site.
    /// </summary>
    [HttpGet("{siteCode}/prices")]
    [Authorize(Policy = "PortalUser")]
    [ProducesResponseType(typeof(PriceQueryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentPrices(
        string siteCode,
        CancellationToken ct)
    {
        // Return the most recent price snapshot per grade
        var latestPrices = await _db.PriceSnapshots
            .Where(p => p.SiteCode == siteCode)
            .GroupBy(p => p.GradeId)
            .Select(g => g.OrderByDescending(p => p.ObservedAtUtc).First())
            .Select(p => new PriceSnapshotItem
            {
                GradeId = p.GradeId,
                GradeName = p.GradeName,
                PriceMinorUnits = p.PriceMinorUnits,
                CurrencyCode = p.CurrencyCode,
                ObservedAtUtc = p.ObservedAtUtc,
            })
            .ToListAsync(ct);

        return Ok(new PriceQueryResponse { Prices = latestPrices });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private (string deviceId, Guid legalEntityId, IActionResult? error) ExtractClaims(string siteCode)
    {
        var jwtSiteCode = User.FindFirstValue("site") ?? string.Empty;
        var deviceId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
        var leiStr = User.FindFirstValue("lei") ?? string.Empty;

        if (!Guid.TryParse(leiStr, out var legalEntityId))
        {
            return ("", Guid.Empty, BadRequest(BuildError(
                "VALIDATION.INVALID_LEI",
                "JWT 'lei' claim is not a valid UUID.")));
        }

        if (!string.Equals(jwtSiteCode, siteCode, StringComparison.OrdinalIgnoreCase))
        {
            return ("", Guid.Empty, BadRequest(BuildError(
                "VALIDATION.SITE_MISMATCH",
                $"JWT site claim '{jwtSiteCode}' does not match route siteCode '{siteCode}'.")));
        }

        return (deviceId, legalEntityId, null);
    }

    private static ErrorResponse BuildError(string code, string message) => new()
    {
        ErrorCode = code,
        Message = message,
        TraceId = Activity.Current?.Id ?? "",
        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
    };
}
