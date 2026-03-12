using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>Raw FCC payload envelope wrapping unparsed JSON received from the FCC.</summary>
public sealed record RawPayloadEnvelope(
    string FccVendor,
    string SiteCode,
    string RawJson,
    DateTimeOffset ReceivedAt);

/// <summary>Cursor for incremental transaction fetching from the FCC.</summary>
public sealed record FetchCursor(
    string? LastSequence,
    DateTimeOffset? Since,
    int MaxCount = 50);

/// <summary>Batch of raw transaction payloads returned by a single FCC fetch.</summary>
public sealed record TransactionBatch(
    IReadOnlyList<RawPayloadEnvelope> Records,
    string? NextCursor,
    bool HasMore);

/// <summary>Pre-authorization command directed at a specific FCC pump/nozzle.</summary>
public sealed record PreAuthCommand(
    string PreAuthId,
    string SiteCode,
    int FccPumpNumber,
    int FccNozzleNumber,
    string ProductCode,
    long RequestedAmountMinorUnits,
    long UnitPriceMinorPerLitre,
    string Currency,
    string? VehicleNumber,
    string? FccCorrelationId);

/// <summary>Result of a pre-auth command sent to the FCC.</summary>
public sealed record PreAuthResult(
    bool Accepted,
    string? FccCorrelationId,
    string? FccAuthorizationCode,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Connection configuration for a FCC adapter instance.</summary>
public sealed record FccConnectionConfig(
    string BaseUrl,
    [property: SensitiveData] string ApiKey,
    TimeSpan RequestTimeout,
    string SiteCode = "");
