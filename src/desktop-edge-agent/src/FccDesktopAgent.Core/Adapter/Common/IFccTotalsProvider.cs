namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that support pump totals queries.
/// Ported from legacy FpTotals (EXTC 0x09): TotalVol, TotalMoney per pump.
/// Used for shift reconciliation and audit trails.
/// </summary>
public interface IFccTotalsProvider
{
    /// <summary>
    /// Fetch cumulative pump totals from the FCC.
    /// </summary>
    Task<IReadOnlyList<PumpTotals>> GetPumpTotalsAsync(CancellationToken ct);
}

/// <summary>
/// Cumulative pump totals for shift reconciliation.
/// Ported from legacy FpTotals: FpId, TotalVol, TotalMoney.
/// </summary>
public sealed record PumpTotals(
    int PumpNumber,
    long TotalVolumeMicrolitres,
    long TotalAmountMinorUnits,
    string CurrencyCode,
    DateTimeOffset ObservedAtUtc);
