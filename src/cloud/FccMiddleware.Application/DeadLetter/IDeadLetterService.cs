using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.DeadLetter;

/// <summary>
/// Application-layer service for producing dead-letter records when pipeline failures occur.
/// Injected into ingestion, upload, and pre-auth handlers to capture unrecoverable errors.
/// </summary>
public interface IDeadLetterService
{
    /// <summary>
    /// Creates a new dead-letter record capturing a pipeline failure.
    /// </summary>
    Task<Guid> CreateAsync(
        Guid legalEntityId,
        string siteCode,
        DeadLetterType type,
        DeadLetterReason reason,
        string errorCode,
        string errorMessage,
        string? fccTransactionId = null,
        string? rawPayloadJson = null,
        string? rawPayloadRef = null,
        CancellationToken cancellationToken = default);
}
