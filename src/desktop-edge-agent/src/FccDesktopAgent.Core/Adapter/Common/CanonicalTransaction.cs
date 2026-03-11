using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Canonical transaction model shared between the Edge Agent and Cloud Backend.
/// Money values use long (minor units). Timestamps use DateTimeOffset. UUIDs are string.
/// Dedup key: (FccTransactionId, SiteCode).
/// </summary>
public sealed record CanonicalTransaction
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("fccTransactionId")]
    public string FccTransactionId { get; init; } = string.Empty;

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; init; } = string.Empty;

    [JsonPropertyName("pumpNumber")]
    public int PumpNumber { get; init; }

    [JsonPropertyName("nozzleNumber")]
    public int NozzleNumber { get; init; }

    [JsonPropertyName("productCode")]
    public string ProductCode { get; init; } = string.Empty;

    /// <summary>Volume in microlitres. NEVER floating point.</summary>
    [JsonPropertyName("volumeMicrolitres")]
    public long VolumeMicrolitres { get; init; }

    /// <summary>Amount in minor currency units (e.g. cents). NEVER floating point.</summary>
    [JsonPropertyName("amountMinorUnits")]
    public long AmountMinorUnits { get; init; }

    /// <summary>Unit price in minor units per litre. NEVER floating point.</summary>
    [JsonPropertyName("unitPriceMinorPerLitre")]
    public long UnitPriceMinorPerLitre { get; init; }

    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; init; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; init; }

    [JsonPropertyName("fiscalReceiptNumber")]
    public string? FiscalReceiptNumber { get; init; }

    [JsonPropertyName("fccVendor")]
    public string FccVendor { get; init; } = string.Empty;

    [JsonPropertyName("attendantId")]
    public string? AttendantId { get; init; }

    [JsonPropertyName("legalEntityId")]
    public string LegalEntityId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public TransactionStatus Status { get; init; } = TransactionStatus.Pending;

    [JsonPropertyName("ingestionSource")]
    public string IngestionSource { get; init; } = string.Empty;

    [JsonPropertyName("ingestedAt")]
    public DateTimeOffset IngestedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("isDuplicate")]
    public bool IsDuplicate { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("rawPayloadRef")]
    public string? RawPayloadRef { get; init; }

    [JsonPropertyName("rawPayloadJson")]
    public string? RawPayloadJson { get; init; }

    [JsonPropertyName("odooOrderId")]
    public string? OdooOrderId { get; init; }

    [JsonPropertyName("preAuthId")]
    public string? PreAuthId { get; init; }

    [JsonPropertyName("reconciliationStatus")]
    public string? ReconciliationStatus { get; init; }

    /// <summary>Set when IsDuplicate=true.</summary>
    [JsonPropertyName("duplicateOfId")]
    public string? DuplicateOfId { get; init; }
}
