namespace FccMiddleware.Contracts.SiteData;

// ── Common Response ──────────────────────────────────────────────────────────

public sealed class SiteDataAcceptedResponse
{
    public string Message { get; init; } = "";
    public int Count { get; init; }
}

// ── BNA Reports ──────────────────────────────────────────────────────────────

public sealed class BnaReportBatchRequest
{
    public IReadOnlyList<BnaReportItem>? Reports { get; init; }
}

public sealed class BnaReportItem
{
    public string? TerminalId { get; init; }
    public int NotesAccepted { get; init; }
    public DateTimeOffset? ReportedAtUtc { get; init; }
}

// ── Pump Totals ──────────────────────────────────────────────────────────────

public sealed class PumpTotalsBatchRequest
{
    public IReadOnlyList<PumpTotalsUploadItem>? Totals { get; init; }
}

public sealed class PumpTotalsUploadItem
{
    public int PumpNumber { get; init; }
    public long TotalVolumeMicrolitres { get; init; }
    public long TotalAmountMinorUnits { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

public sealed class PumpTotalsQueryResponse
{
    public List<PumpTotalsItem> Totals { get; init; } = [];
}

public sealed class PumpTotalsItem
{
    public int PumpNumber { get; init; }
    public long TotalVolumeMicrolitres { get; init; }
    public long TotalAmountMinorUnits { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
}

// ── Pump Control History ─────────────────────────────────────────────────────

public sealed class PumpControlHistoryBatchRequest
{
    public IReadOnlyList<PumpControlHistoryUploadItem>? Events { get; init; }
}

public sealed class PumpControlHistoryUploadItem
{
    public int PumpNumber { get; init; }
    public string? ActionType { get; init; }
    public string? Source { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset? ActionAtUtc { get; init; }
}

public sealed class PumpControlHistoryQueryResponse
{
    public List<PumpControlHistoryItem> History { get; init; } = [];
}

public sealed class PumpControlHistoryItem
{
    public int PumpNumber { get; init; }
    public string ActionType { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Note { get; init; }
    public DateTimeOffset ActionAtUtc { get; init; }
}

// ── Price Snapshots ──────────────────────────────────────────────────────────

public sealed class PriceSnapshotBatchRequest
{
    public IReadOnlyList<PriceSnapshotUploadItem>? Snapshots { get; init; }
}

public sealed class PriceSnapshotUploadItem
{
    public string? PriceSetId { get; init; }
    public string? GradeId { get; init; }
    public string? GradeName { get; init; }
    public long PriceMinorUnits { get; init; }
    public string? CurrencyCode { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

public sealed class PriceQueryResponse
{
    public List<PriceSnapshotItem> Prices { get; init; } = [];
}

public sealed class PriceSnapshotItem
{
    public string GradeId { get; init; } = "";
    public string GradeName { get; init; } = "";
    public long PriceMinorUnits { get; init; }
    public string CurrencyCode { get; init; } = "";
    public DateTimeOffset ObservedAtUtc { get; init; }
}
