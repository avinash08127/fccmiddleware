namespace VirtualLab.Infrastructure.DomsRest;

// Request — matches DomsPreAuthRequest in desktop agent
public sealed record DomsSimPreAuthRequest(
    string PreAuthId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    long AmountMinorUnits,
    long UnitPriceMinorPerLitre,
    string CurrencyCode,
    string? VehicleNumber,
    string CorrelationId);

// Response — matches DomsPreAuthResponse in desktop agent
public sealed record DomsSimPreAuthResponse(
    bool Accepted,
    string CorrelationId,
    string? AuthorizationCode,
    string? ErrorCode,
    string Message,
    DateTimeOffset? ExpiresAtUtc);

// Heartbeat response
public sealed record DomsSimHeartbeatResponse(string Status);

// Pump status item
public sealed record DomsSimPumpStatusItem(
    int PumpNumber,
    string State,
    string? ProductCode,
    decimal? CurrentVolume,
    decimal? CurrentAmount);

// Transaction fetch response
public sealed record DomsSimTransactionResponse(
    IReadOnlyList<DomsSimTransactionItem> Transactions,
    string? Cursor,
    bool HasMore);

public sealed record DomsSimTransactionItem(
    string TransactionId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    long AmountMinorUnits,
    long VolumeCentilitres,
    long UnitPriceMinorPerLitre,
    string CurrencyCode,
    DateTimeOffset CompletedAt);
