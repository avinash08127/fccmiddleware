using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Configuration for connecting to and polling from a Forecourt Controller at a site.
/// CredentialRef is a pointer to AWS Secrets Manager — never the credential itself.
/// </summary>
public class FccConfig
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid LegalEntityId { get; set; }
    public FccVendor FccVendor { get; set; }
    public string? FccModel { get; set; }
    public ConnectionProtocol ConnectionProtocol { get; set; }
    public string HostAddress { get; set; } = null!;
    public int Port { get; set; }

    /// <summary>Reference to the credential in AWS Secrets Manager — not the credential itself.</summary>
    public string CredentialRef { get; set; } = null!;

    public IngestionMethod IngestionMethod { get; set; } = IngestionMethod.PUSH;
    public IngestionMode IngestionMode { get; set; } = IngestionMode.CLOUD_DIRECT;
    public int? PullIntervalSeconds { get; set; }
    public int? CatchUpPullIntervalSeconds { get; set; }
    public int? HybridCatchUpIntervalSeconds { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public int HeartbeatTimeoutSeconds { get; set; } = 180;
    public bool IsActive { get; set; } = true;
    public int ConfigVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // ── Radix-specific fields (already exist in DB — verify) ─────────────────
    public string? SharedSecret { get; set; }
    public int? UsnCode { get; set; }
    public int? AuthPort { get; set; }
    public string? FccPumpAddressMap { get; set; }

    // ── DOMS TCP/JPL fields ──────────────────────────────────────────────────
    public int? JplPort { get; set; }
    public string? FcAccessCode { get; set; }
    public string? DomsCountryCode { get; set; }
    public string? PosVersionId { get; set; }
    public int? ReconnectBackoffMaxSeconds { get; set; }
    public string? ConfiguredPumps { get; set; }
    public string? DppPorts { get; set; }

    // ── Petronite OAuth2 fields ──────────────────────────────────────────────
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? WebhookSecret { get; set; }
    public string? OAuthTokenEndpoint { get; set; }

    // ── Advatec EFD fields ──────────────────────────────────────────────────
    public int? AdvatecDevicePort { get; set; }
    public string? AdvatecWebhookToken { get; set; }

    /// <summary>
    /// SHA-256 hash of <see cref="AdvatecWebhookToken"/> for indexed DB lookup.
    /// Avoids loading all Advatec configs into memory for token matching (H-04).
    /// Populated by the application layer when the token is set/updated.
    /// </summary>
    public string? AdvatecWebhookTokenHash { get; set; }

    public string? AdvatecEfdSerialNumber { get; set; }
    public int? AdvatecCustIdType { get; set; }

    /// <summary>JSON map of EFD serial number → pump number, e.g. {"10TZ101807": 1, "10TZ101808": 2}.</summary>
    public string? AdvatecPumpMap { get; set; }

    // Navigation properties
    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
}
