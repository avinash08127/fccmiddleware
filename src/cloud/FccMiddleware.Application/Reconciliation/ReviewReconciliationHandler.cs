using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Events;
using FccMiddleware.Domain.Interfaces;
using MediatR;

namespace FccMiddleware.Application.Reconciliation;

public sealed class ReviewReconciliationHandler
    : IRequestHandler<ReviewReconciliationCommand, Result<ReviewReconciliationResult>>
{
    private readonly IReconciliationDbContext _db;
    private readonly IEventPublisher _eventPublisher;

    public ReviewReconciliationHandler(IReconciliationDbContext db, IEventPublisher eventPublisher)
    {
        _db = db;
        _eventPublisher = eventPublisher;
    }

    public async Task<Result<ReviewReconciliationResult>> Handle(
        ReviewReconciliationCommand request,
        CancellationToken cancellationToken)
    {
        if (request.TargetStatus is not (ReconciliationStatus.APPROVED or ReconciliationStatus.REJECTED))
        {
            return Result<ReviewReconciliationResult>.Failure(
                "VALIDATION.INVALID_STATUS",
                "Review target status must be APPROVED or REJECTED.");
        }

        var reason = request.Reason.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result<ReviewReconciliationResult>.Failure(
                "VALIDATION.REASON_REQUIRED",
                "reason is required.");
        }

        var record = await _db.FindByIdAsync(request.ReconciliationId, cancellationToken);
        if (record is null)
        {
            return Result<ReviewReconciliationResult>.Failure(
                "NOT_FOUND.RECONCILIATION",
                $"Reconciliation record '{request.ReconciliationId}' was not found.");
        }

        if (!request.AllowAllLegalEntities
            && !request.ScopedLegalEntityIds.Contains(record.LegalEntityId))
        {
            return Result<ReviewReconciliationResult>.Failure(
                "FORBIDDEN.LEGAL_ENTITY_SCOPE",
                "The reconciliation record is outside the caller's legal entity scope.");
        }

        if (record.Status != ReconciliationStatus.VARIANCE_FLAGGED)
        {
            return Result<ReviewReconciliationResult>.Failure(
                "CONFLICT.INVALID_TRANSITION",
                $"Only VARIANCE_FLAGGED records can be reviewed. Current status is {record.Status}.");
        }

        var transaction = await _db.FindTransactionByIdAsync(record.TransactionId, cancellationToken);
        if (transaction is null)
        {
            return Result<ReviewReconciliationResult>.Failure(
                "NOT_FOUND.TRANSACTION",
                $"Transaction '{record.TransactionId}' for reconciliation '{record.Id}' was not found.");
        }

        var reviewedAt = DateTimeOffset.UtcNow;
        record.Status = request.TargetStatus;
        record.ReviewedByUserId = request.ReviewedByUserId;
        record.ReviewedAtUtc = reviewedAt;
        record.ReviewReason = reason;
        record.UpdatedAt = reviewedAt;

        if (request.TargetStatus == ReconciliationStatus.APPROVED)
        {
            _eventPublisher.Publish(new ReconciliationApproved
            {
                ReconciliationId = record.Id,
                ApprovedBy = request.ReviewedByUserId,
                ApprovalNote = reason,
                CorrelationId = transaction.CorrelationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = "cloud-reconciliation"
            });
        }
        else
        {
            _eventPublisher.Publish(new ReconciliationRejected
            {
                ReconciliationId = record.Id,
                RejectedBy = request.ReviewedByUserId,
                RejectionNote = reason,
                CorrelationId = transaction.CorrelationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = "cloud-reconciliation"
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Result<ReviewReconciliationResult>.Success(new ReviewReconciliationResult(
            record.Id,
            record.Status,
            record.LegalEntityId,
            record.SiteCode,
            record.ReviewedByUserId,
            record.ReviewedAtUtc!.Value,
            record.ReviewReason!));
    }
}
