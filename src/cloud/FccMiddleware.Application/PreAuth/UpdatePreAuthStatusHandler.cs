using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.PreAuth;

public sealed class UpdatePreAuthStatusHandler
    : IRequestHandler<UpdatePreAuthStatusCommand, Result<UpdatePreAuthStatusResult>>
{
    private readonly IPreAuthDbContext _db;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<UpdatePreAuthStatusHandler> _logger;

    public UpdatePreAuthStatusHandler(
        IPreAuthDbContext db,
        IEventPublisher eventPublisher,
        ILogger<UpdatePreAuthStatusHandler> logger)
    {
        _db = db;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<Result<UpdatePreAuthStatusResult>> Handle(
        UpdatePreAuthStatusCommand command,
        CancellationToken cancellationToken)
    {
        var record = await _db.FindByIdAsync(command.PreAuthId, command.LegalEntityId, cancellationToken);
        if (record is null || !string.Equals(record.SiteCode, command.ExpectedSiteCode, StringComparison.OrdinalIgnoreCase))
        {
            return Result<UpdatePreAuthStatusResult>.Failure(
                "NOT_FOUND.PREAUTH",
                $"Pre-auth '{command.PreAuthId}' was not found.");
        }

        if (record.Status == command.Status)
        {
            ApplyFields(record, command);
            await _db.SaveChangesAsync(cancellationToken);

            return Result<UpdatePreAuthStatusResult>.Success(BuildResult(record));
        }

        try
        {
            record.Transition(command.Status);
        }
        catch (InvalidPreAuthTransitionException ex)
        {
            _logger.LogWarning(
                "Invalid pre-auth transition for {PreAuthId}: {From} -> {To}",
                record.Id, ex.From, ex.To);

            return Result<UpdatePreAuthStatusResult>.Failure(
                "CONFLICT.INVALID_TRANSITION",
                $"Cannot transition pre-auth from {ex.From} to {ex.To}.");
        }

        ApplyFields(record, command);
        ApplyStatusTimestamp(record, command.Status);

        _eventPublisher.Publish(PreAuthEventFactory.CreateForStatus(
            record,
            command.CorrelationId,
            source: "cloud-preauth"));

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Pre-auth {PreAuthId} updated to {Status}",
            record.Id, record.Status);

        return Result<UpdatePreAuthStatusResult>.Success(BuildResult(record));
    }

    private static UpdatePreAuthStatusResult BuildResult(Domain.Entities.PreAuthRecord record) => new()
    {
        PreAuthId = record.Id,
        Status = record.Status,
        SiteCode = record.SiteCode,
        OdooOrderId = record.OdooOrderId,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt
    };

    private static void ApplyFields(
        Domain.Entities.PreAuthRecord record,
        UpdatePreAuthStatusCommand command)
    {
        if (command.FccCorrelationId is not null)
            record.FccCorrelationId = command.FccCorrelationId;

        if (command.FccAuthorizationCode is not null)
        {
            record.FccAuthorizationCode = command.FccAuthorizationCode;
            record.AuthorizedAmountMinorUnits ??= record.RequestedAmountMinorUnits;
        }

        if (command.FailureReason is not null)
            record.FailureReason = command.FailureReason;

        if (command.ActualAmountMinorUnits.HasValue)
            record.ActualAmountMinorUnits = command.ActualAmountMinorUnits.Value;

        if (command.ActualVolumeMillilitres.HasValue)
            record.ActualVolumeMillilitres = command.ActualVolumeMillilitres.Value;

        if (command.MatchedFccTransactionId is not null)
            record.MatchedFccTransactionId = command.MatchedFccTransactionId;

        if (command.MatchedTransactionId.HasValue)
            record.MatchedTransactionId = command.MatchedTransactionId.Value;
    }

    private static void ApplyStatusTimestamp(Domain.Entities.PreAuthRecord record, PreAuthStatus status)
    {
        var now = DateTimeOffset.UtcNow;

        switch (status)
        {
            case PreAuthStatus.AUTHORIZED:
                record.AuthorizedAt ??= now;
                break;
            case PreAuthStatus.DISPENSING:
                record.DispensingAt ??= now;
                break;
            case PreAuthStatus.COMPLETED:
                record.CompletedAt ??= now;
                break;
            case PreAuthStatus.CANCELLED:
                record.CancelledAt ??= now;
                break;
            case PreAuthStatus.EXPIRED:
                record.ExpiredAt ??= now;
                break;
            case PreAuthStatus.FAILED:
                record.FailedAt ??= now;
                break;
        }
    }
}
