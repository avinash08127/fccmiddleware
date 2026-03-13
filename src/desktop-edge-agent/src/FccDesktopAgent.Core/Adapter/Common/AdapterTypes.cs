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
    string? FccCorrelationId,
    /// <summary>Radix: Customer TIN — maps to CUSTID when CUSTIDTYPE=1. PII — never log.</summary>
    string? CustomerTaxId = null,
    /// <summary>Radix: Customer name — maps to CUSTNAME.</summary>
    string? CustomerName = null,
    /// <summary>Radix: Customer ID type — maps to CUSTIDTYPE (1=TIN, 2=DrivingLicense, etc.).</summary>
    int? CustomerIdType = null,
    /// <summary>Radix: Customer phone — maps to MOBILENUM.</summary>
    string? CustomerPhone = null);

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
    string SiteCode = "",
    /// <summary>Radix: SHA-1 signing password for message authentication.</summary>
    [property: SensitiveData] string? SharedSecret = null,
    /// <summary>Radix: Unique Station Number (1–999999), sent as USN-Code HTTP header.</summary>
    int? UsnCode = null,
    /// <summary>Radix: External Authorization port; transaction port is AuthPort + 1.</summary>
    int? AuthPort = null,
    /// <summary>Radix: JSON dictionary mapping canonical pump numbers to (PUMP_ADDR, FP) pairs.</summary>
    string? FccPumpAddressMap = null,
    // ── DOMS TCP/JPL fields ──────────────────────────────────────────────────
    /// <summary>DOMS: Connection protocol ("REST" or "TCP"). Determines which adapter class the factory creates.</summary>
    string? ConnectionProtocol = null,
    /// <summary>DOMS TCP: JPL binary-framed port number.</summary>
    int? JplPort = null,
    /// <summary>DOMS TCP: FcLogon access code credential.</summary>
    [property: SensitiveData] string? FcAccessCode = null,
    /// <summary>DOMS TCP: Country code for locale-specific formatting.</summary>
    string? DomsCountryCode = null,
    /// <summary>DOMS TCP: POS version identifier sent during FcLogon.</summary>
    string? PosVersionId = null,
    /// <summary>DOMS TCP: Heartbeat interval in seconds (default 30).</summary>
    int? HeartbeatIntervalSeconds = null,
    /// <summary>DOMS TCP: Maximum reconnection backoff in seconds.</summary>
    int? ReconnectBackoffMaxSeconds = null,
    /// <summary>DOMS TCP: Comma-separated list of configured pump numbers (e.g., "1,2,3,4").</summary>
    string? ConfiguredPumps = null,
    // ── Petronite OAuth2 fields ──────────────────────────────────────────────
    /// <summary>Petronite: OAuth2 client ID for Client Credentials flow.</summary>
    [property: SensitiveData] string? ClientId = null,
    /// <summary>Petronite: OAuth2 client secret for Client Credentials flow.</summary>
    [property: SensitiveData] string? ClientSecret = null,
    /// <summary>Petronite: Webhook HMAC secret for payload validation.</summary>
    [property: SensitiveData] string? WebhookSecret = null,
    /// <summary>Petronite: OAuth2 token endpoint URL.</summary>
    string? OAuthTokenEndpoint = null,
    /// <summary>Petronite: HTTP port for the local webhook listener that receives transaction callbacks (default 8090).</summary>
    int? WebhookListenerPort = null,
    // ── Advatec EFD fields ──────────────────────────────────────────────────
    /// <summary>Advatec: Device host address (default "127.0.0.1" — Advatec runs on localhost).</summary>
    string? AdvatecDeviceAddress = null,
    /// <summary>Advatec: Device HTTP port (default 5560).</summary>
    int? AdvatecDevicePort = null,
    /// <summary>Advatec: Port for the local webhook listener that receives Receipt callbacks (default 8091).</summary>
    int? AdvatecWebhookListenerPort = null,
    /// <summary>Advatec: Shared token for webhook URL authentication.</summary>
    [property: SensitiveData] string? AdvatecWebhookToken = null,
    /// <summary>Advatec: TRA-registered EFD serial number for validation (e.g., "10TZ101807").</summary>
    string? AdvatecEfdSerialNumber = null,
    /// <summary>Advatec: Default CustIdType for Customer submissions (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL).</summary>
    int? AdvatecCustIdType = null,
    /// <summary>Resolved legal entity identifier used by normalization and correlation flows.</summary>
    string? LegalEntityId = null,
    /// <summary>Resolved ISO 4217 currency code for the site.</summary>
    string? CurrencyCode = null,
    /// <summary>Resolved IANA timezone for the site.</summary>
    string? Timezone = null,
    /// <summary>Resolved pump number offset applied during normalization.</summary>
    int PumpNumberOffset = 0,
    /// <summary>Resolved FCC product code mapping.</summary>
    IReadOnlyDictionary<string, string>? ProductCodeMapping = null,
    // ── Configurable timeouts ────────────────────────────────────────────────
    /// <summary>Pre-auth request timeout in seconds. Null = adapter default.</summary>
    int? PreAuthTimeoutSeconds = null,
    /// <summary>Fiscal receipt wait timeout in seconds. Null = adapter default (30s).</summary>
    int? FiscalReceiptTimeoutSeconds = null,
    /// <summary>HTTP request timeout in seconds for FCC API calls. Null = adapter default (10s).</summary>
    int? ApiRequestTimeoutSeconds = null);

// ---------------------------------------------------------------------------
// Pre-auth matching contract (GAP-5)
// ---------------------------------------------------------------------------

/// <summary>Matching confidence level for pre-auth → transaction correlation.</summary>
public enum PreAuthMatchingStrategy { Deterministic, Heuristic }

/// <summary>Result of matching a transaction to an active pre-auth.</summary>
public sealed record PreAuthMatchResult(
    string CorrelationId,
    PreAuthMatchingStrategy Strategy,
    string? OdooOrderId = null);

/// <summary>Snapshot of an active pre-auth for diagnostics.</summary>
public sealed record ActivePreAuthSnapshot(
    string CorrelationId,
    int PumpNumber,
    DateTimeOffset RegisteredAt,
    string? OdooOrderId = null);

/// <summary>
/// Standardized pre-auth matching interface (GAP-5).
/// Each adapter implements vendor-specific matching behind this common contract.
/// </summary>
public interface IPreAuthMatcher
{
    PreAuthMatchingStrategy MatchingStrategy { get; }
    string RegisterPreAuth(PreAuthCommand command, string? vendorRef);
    PreAuthMatchResult? MatchTransaction(int pumpNumber, string? vendorMatchKey);
    bool RemovePreAuth(string correlationId);
    IReadOnlyList<ActivePreAuthSnapshot> GetActivePreAuths();
}
