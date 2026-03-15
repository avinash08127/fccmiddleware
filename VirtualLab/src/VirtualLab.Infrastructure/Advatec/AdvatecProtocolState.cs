using System.Collections.Concurrent;

namespace VirtualLab.Infrastructure.Advatec;

/// <summary>
/// Singleton state for the Advatec vendor-faithful protocol simulator.
/// Tracks active pre-auths per pump, mapped through to PreAuthSimulationService.
/// Separate from AdvatecSimulatorState which manages the standalone EFD device simulator.
/// </summary>
public sealed class AdvatecProtocolState
{
    /// <summary>Active pre-auths keyed by "{siteCode}:{pumpNumber}".</summary>
    public ConcurrentDictionary<string, AdvatecSimActivePreAuth> ActivePreAuths { get; } = new();

    /// <summary>Recent receipt webhook deliveries for diagnostics.</summary>
    public ConcurrentDictionary<string, AdvatecReceiptDelivery> RecentDeliveries { get; } = new();

    public static string Key(string siteCode, int pumpNumber) => $"{siteCode}:{pumpNumber}";

    public sealed class AdvatecReceiptDelivery
    {
        public string TransactionId { get; init; } = string.Empty;
        public int PumpNumber { get; init; }
        public string? CustomerId { get; init; }
        public decimal Amount { get; init; }
        public string? CallbackUrl { get; init; }
        public int? ResponseStatusCode { get; set; }
        public DateTimeOffset SentAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
