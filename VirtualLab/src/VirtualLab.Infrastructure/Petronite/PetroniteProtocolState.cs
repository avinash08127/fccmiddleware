using System.Collections.Concurrent;

namespace VirtualLab.Infrastructure.Petronite;

/// <summary>
/// Singleton state for the Petronite vendor-faithful protocol simulator.
/// Tracks OAuth tokens and orders mapped through to the PreAuthSimulationService.
/// </summary>
public sealed class PetroniteProtocolState
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _activeTokens = new();
    private int _tokenCounter;

    public ConcurrentDictionary<string, PetroniteOrder> Orders { get; } = new();

    /// <summary>Create a simple bearer token (lab environment, no real OAuth validation).</summary>
    public string CreateToken()
    {
        int counter = Interlocked.Increment(ref _tokenCounter);
        string token = $"sim-petronite-token-{counter}";
        _activeTokens[token] = DateTimeOffset.UtcNow.AddHours(1);
        return token;
    }

    /// <summary>Validate a bearer token (presence and not expired).</summary>
    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (_activeTokens.TryGetValue(token, out DateTimeOffset expiresAt))
        {
            if (expiresAt > DateTimeOffset.UtcNow) return true;
            _activeTokens.TryRemove(token, out _);
        }

        return false;
    }

    public sealed class PetroniteOrder
    {
        public string OrderId { get; init; } = string.Empty;
        public string SiteCode { get; init; } = string.Empty;
        public string NozzleId { get; init; } = string.Empty;
        public int PumpNumber { get; init; }
        public int NozzleNumber { get; init; }
        public decimal MaxAmountMajor { get; init; }
        public string Currency { get; init; } = string.Empty;
        public string ExternalReference { get; init; } = string.Empty;
        public string Status { get; set; } = "PENDING";
        public string? AuthorizationCode { get; set; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
