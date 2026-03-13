using MediatR;

namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Represents a single transaction record in the batch upload command.
/// Contains the canonical transaction fields submitted by the Edge Agent.
/// </summary>
public sealed record UploadTransactionItem
{
    public required string FccTransactionId { get; init; }
    public required string SiteCode { get; init; }
    public required string FccVendor { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public required string ProductCode { get; init; }
    public required long VolumeMicrolitres { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required long UnitPriceMinorPerLitre { get; init; }
    public required string CurrencyCode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? FccCorrelationId { get; init; }
    public string? OdooOrderId { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? AttendantId { get; init; }
}

/// <summary>
/// MediatR command to process a batch of pre-normalized Edge Agent transactions.
/// Each record is individually deduped and persisted. JWT claims are pre-validated by the controller.
/// </summary>
public sealed record UploadTransactionBatchCommand : IRequest<UploadTransactionBatchResult>
{
    /// <summary>Batch of pre-normalized canonical transaction items.</summary>
    public required IReadOnlyList<UploadTransactionItem> Records { get; init; }

    /// <summary>Legal entity ID extracted from the device JWT 'lei' claim.</summary>
    public required Guid LegalEntityId { get; init; }

    /// <summary>Site code extracted from the device JWT 'site' claim. All records must match.</summary>
    public required string DeviceSiteCode { get; init; }

    /// <summary>Device ID extracted from the device JWT 'sub' claim. Used for logging.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Correlation ID for end-to-end tracing.</summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>
    /// Optional batch-level idempotency key. When present, the handler checks Redis
    /// for a cached result before processing and caches the result after processing.
    /// </summary>
    public string? UploadBatchId { get; init; }
}
