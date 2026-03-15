namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that support price queries and updates.
/// Ported from legacy ForecourtClient: RequestFcPriceSet(), SendDynamicPriceUpdate().
/// </summary>
public interface IFccPriceManagement
{
    /// <summary>
    /// Request current price set from the FCC.
    /// Equivalent to legacy ForecourtClient.RequestFcPriceSet().
    /// </summary>
    Task<PriceSetSnapshot?> GetCurrentPricesAsync(CancellationToken ct);

    /// <summary>
    /// Push a price update to the FCC.
    /// Equivalent to legacy ForecourtClient.SendDynamicPriceUpdate().
    /// </summary>
    Task<PriceUpdateResult> UpdatePricesAsync(PriceUpdateCommand command, CancellationToken ct);
}

/// <summary>
/// Snapshot of current fuel prices from the FCC.
/// Ported from legacy DomsPriceSet: PriceSetId, PriceGroupIds, GradeIds, CurrentPrices.
/// </summary>
public sealed record PriceSetSnapshot(
    string PriceSetId,
    IReadOnlyList<string> PriceGroupIds,
    IReadOnlyList<GradePrice> Grades,
    DateTimeOffset ObservedAtUtc);

public sealed record GradePrice(
    string GradeId,
    string? GradeName,
    long PriceMinorUnits,
    string CurrencyCode);

public sealed record PriceUpdateCommand(
    IReadOnlyList<GradePriceUpdate> Updates,
    DateTimeOffset? ActivationTime);

public sealed record GradePriceUpdate(string GradeId, long NewPriceMinorUnits);

public sealed record PriceUpdateResult(bool Success, string? ErrorMessage = null);
