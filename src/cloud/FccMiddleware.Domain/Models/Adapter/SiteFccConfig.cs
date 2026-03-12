using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Common;

namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Minimum config inputs required by IFccAdapterFactory.Resolve and the adapter implementations.
/// Sourced from the FccConfig entity + site master data. The ApiKey value must be resolved
/// from AWS Secrets Manager before constructing this record — never stored directly.
/// </summary>
public sealed record SiteFccConfig
{
    /// <summary>Site identifier — used as an adapter context key.</summary>
    public required string SiteCode { get; init; }

    /// <summary>FCC vendor. Must match the adapter being resolved.</summary>
    public required FccVendor FccVendor { get; init; }

    /// <summary>Connection protocol for the FCC endpoint.</summary>
    public required ConnectionProtocol ConnectionProtocol { get; init; }

    /// <summary>FCC host address (IP or hostname).</summary>
    public required string HostAddress { get; init; }

    /// <summary>FCC port number.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// API key used in the X-API-Key header. Must be resolved from Secrets Manager
    /// (FccConfig.CredentialRef) before calling Resolve — never the raw credential ref.
    /// </summary>
    [Sensitive]
    public required string ApiKey { get; init; }

    /// <summary>How transactions flow from the FCC to the middleware.</summary>
    public required IngestionMethod IngestionMethod { get; init; }

    /// <summary>Pull interval in seconds. Required when IngestionMethod is PULL or HYBRID.</summary>
    public int? PullIntervalSeconds { get; init; }

    /// <summary>ISO 4217 currency code for monetary field conversions.</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>IANA timezone identifier for the site (e.g., "Africa/Blantyre").</summary>
    public required string Timezone { get; init; }

    /// <summary>
    /// Offset subtracted from FCC pump numbers to produce canonical pump numbers.
    /// Zero means FCC pump numbers are used directly.
    /// </summary>
    public int PumpNumberOffset { get; init; } = 0;

    /// <summary>
    /// Maps raw FCC product codes to canonical codes (e.g., {"01" → "PMS", "02" → "AGO"}).
    /// When a code is absent the raw FCC code is preserved as-is.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProductCodeMapping { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Radix: SHA-1 signing password for message authentication.</summary>
    [Sensitive]
    public string? SharedSecret { get; init; }

    /// <summary>Radix: Unique Station Number (1–999999).</summary>
    public int? UsnCode { get; init; }

    /// <summary>Radix: External Authorization port; transaction port = AuthPort + 1.</summary>
    public int? AuthPort { get; init; }

    /// <summary>Radix: Maps canonical pump numbers to (PumpAddr, Fp) pairs for Radix three-level addressing.</summary>
    public IReadOnlyDictionary<int, RadixPumpAddress>? FccPumpAddressMap { get; init; }

    // ── DOMS TCP/JPL fields ──────────────────────────────────────────────────

    /// <summary>DOMS TCP: JPL binary-framed port number.</summary>
    public int? JplPort { get; init; }

    /// <summary>DOMS TCP: DPP port list (comma-separated).</summary>
    public string? DppPorts { get; init; }

    /// <summary>DOMS TCP: FcLogon access code credential.</summary>
    [Sensitive]
    public string? FcAccessCode { get; init; }

    /// <summary>DOMS TCP: Country code for locale-specific formatting.</summary>
    public string? DomsCountryCode { get; init; }

    /// <summary>DOMS TCP: POS version identifier sent during FcLogon handshake.</summary>
    public string? PosVersionId { get; init; }

    /// <summary>DOMS TCP: Heartbeat interval in seconds (default 30).</summary>
    public int? HeartbeatIntervalSeconds { get; init; }

    /// <summary>DOMS TCP: Maximum reconnection backoff in seconds.</summary>
    public int? ReconnectBackoffMaxSeconds { get; init; }

    /// <summary>DOMS TCP: Comma-separated list of configured pump numbers (e.g., "1,2,3,4").</summary>
    public string? ConfiguredPumps { get; init; }

    // ── Petronite OAuth2 fields ──────────────────────────────────────────────

    /// <summary>Petronite: OAuth2 client ID for Client Credentials flow.</summary>
    [Sensitive]
    public string? ClientId { get; init; }

    /// <summary>Petronite: OAuth2 client secret for Client Credentials flow.</summary>
    [Sensitive]
    public string? ClientSecret { get; init; }

    /// <summary>Petronite: Webhook HMAC secret for payload validation.</summary>
    [Sensitive]
    public string? WebhookSecret { get; init; }

    /// <summary>Petronite: OAuth2 token endpoint URL.</summary>
    public string? OAuthTokenEndpoint { get; init; }

    // ── Advatec EFD fields ──────────────────────────────────────────────────

    /// <summary>Advatec: Device HTTP port (default 5560).</summary>
    public int? AdvatecDevicePort { get; init; }

    /// <summary>Advatec: Shared token for webhook URL authentication.</summary>
    [Sensitive]
    public string? AdvatecWebhookToken { get; init; }

    /// <summary>Advatec: TRA-registered EFD serial number for validation (e.g., "10TZ101807").</summary>
    public string? AdvatecEfdSerialNumber { get; init; }

    /// <summary>Advatec: Default CustIdType for Customer submissions (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL).</summary>
    public int? AdvatecCustIdType { get; init; }
}

/// <summary>Radix pump addressing: maps to the (PUMP_ADDR, FP) pair in the Radix protocol.</summary>
public sealed record RadixPumpAddress(int PumpAddr, int Fp);
