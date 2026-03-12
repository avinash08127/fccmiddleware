using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.DeadLetter;

/// <summary>
/// Creates dead-letter records in the database when pipeline failures occur.
/// Injected into ingestion, upload, and pre-auth handlers.
/// </summary>
public sealed class DeadLetterService : IDeadLetterService
{
    private readonly FccMiddlewareDbContext _db;
    private readonly ILogger<DeadLetterService> _logger;

    public DeadLetterService(FccMiddlewareDbContext db, ILogger<DeadLetterService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Guid> CreateAsync(
        Guid legalEntityId,
        string siteCode,
        DeadLetterType type,
        DeadLetterReason reason,
        string errorCode,
        string errorMessage,
        string? fccTransactionId = null,
        string? rawPayloadJson = null,
        string? rawPayloadRef = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new DeadLetterItem
        {
            Id = Guid.NewGuid(),
            LegalEntityId = legalEntityId,
            SiteCode = siteCode,
            Type = type,
            FailureReason = reason,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage.Length > 4000 ? errorMessage[..4000] : errorMessage,
            FccTransactionId = fccTransactionId,
            RawPayloadJson = rawPayloadJson,
            RawPayloadRef = rawPayloadRef,
            Status = DeadLetterStatus.PENDING,
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.DeadLetterItems.Add(item);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Dead-letter item {DeadLetterId} created: type={Type} reason={Reason} errorCode={ErrorCode} site={SiteCode} fccTxId={FccTransactionId}",
                item.Id, type, reason, errorCode, siteCode, fccTransactionId);
        }
        catch (Exception ex)
        {
            // Dead-letter creation must not break the original pipeline flow.
            // Log the failure but do not rethrow.
            _logger.LogError(ex,
                "Failed to persist dead-letter item for site={SiteCode} fccTxId={FccTransactionId} error={ErrorCode}",
                siteCode, fccTransactionId, errorCode);
        }

        return item.Id;
    }
}
