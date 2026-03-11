using System.Text.Json.Serialization;
using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Domain model for a pre-authorization record.
/// Not an EF entity — used for in-memory processing and cloud serialization.
/// Idempotency key: (OdooOrderId, SiteCode).
/// </summary>
public sealed record PreAuthRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; init; } = string.Empty;

    [JsonPropertyName("odooOrderId")]
    public string OdooOrderId { get; init; } = string.Empty;

    [JsonPropertyName("pumpNumber")]
    public int PumpNumber { get; init; }

    [JsonPropertyName("nozzleNumber")]
    public int NozzleNumber { get; init; }

    [JsonPropertyName("productCode")]
    public string ProductCode { get; init; } = string.Empty;

    /// <summary>Requested authorization amount in minor currency units.</summary>
    [JsonPropertyName("requestedAmount")]
    public long RequestedAmount { get; init; }

    /// <summary>Unit price in minor units per litre at time of pre-auth.</summary>
    [JsonPropertyName("unitPrice")]
    public long UnitPrice { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public PreAuthStatus Status { get; init; } = PreAuthStatus.Pending;

    [JsonPropertyName("vehicleNumber")]
    public string? VehicleNumber { get; init; }

    [SensitiveData, JsonPropertyName("customerName")]
    public string? CustomerName { get; init; }

    [SensitiveData, JsonPropertyName("customerTaxId")]
    public string? CustomerTaxId { get; init; }

    [JsonPropertyName("customerBusinessName")]
    public string? CustomerBusinessName { get; init; }

    [JsonPropertyName("attendantId")]
    public string? AttendantId { get; init; }

    [JsonPropertyName("fccCorrelationId")]
    public string? FccCorrelationId { get; init; }

    [JsonPropertyName("fccAuthorizationCode")]
    public string? FccAuthorizationCode { get; init; }

    [JsonPropertyName("matchedFccTransactionId")]
    public string? MatchedFccTransactionId { get; init; }

    [JsonPropertyName("actualAmount")]
    public long? ActualAmount { get; init; }

    [JsonPropertyName("actualVolume")]
    public long? ActualVolume { get; init; }

    [JsonPropertyName("amountVariance")]
    public long? AmountVariance { get; init; }

    [JsonPropertyName("varianceBps")]
    public int? VarianceBps { get; init; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("authorizedAt")]
    public DateTimeOffset? AuthorizedAt { get; init; }

    [JsonPropertyName("dispensingAt")]
    public DateTimeOffset? DispensingAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("cancelledAt")]
    public DateTimeOffset? CancelledAt { get; init; }

    [JsonPropertyName("expiredAt")]
    public DateTimeOffset? ExpiredAt { get; init; }

    [JsonPropertyName("failedAt")]
    public DateTimeOffset? FailedAt { get; init; }

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
