using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Enums;
using MediatR;

namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// MediatR command to ingest a single raw FCC transaction payload.
/// Implements the full ingestion pipeline:
///   validate → normalize → primary dedup → store → secondary fuzzy match → archive → publish event.
/// </summary>
public sealed record IngestTransactionCommand : IRequest<Result<IngestTransactionResult>>
{
    /// <summary>FCC vendor resolved from the request.</summary>
    public required FccVendor FccVendor { get; init; }

    /// <summary>Site code from the request.</summary>
    public required string SiteCode { get; init; }

    /// <summary>UTC timestamp when the payload reached the cloud boundary.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Raw payload string (JSON-serialized vendor object).</summary>
    public required string RawPayload { get; init; }

    /// <summary>MIME content type of the payload (e.g., "application/json").</summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Correlation ID threading this ingest call through all downstream events.
    /// Populated by the controller from the incoming HTTP trace context.
    /// </summary>
    public required Guid CorrelationId { get; init; }
}
