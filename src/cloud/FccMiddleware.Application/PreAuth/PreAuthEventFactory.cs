using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Events;

namespace FccMiddleware.Application.PreAuth;

public static class PreAuthEventFactory
{
    public static DomainEvent CreateForStatus(
        PreAuthRecord record,
        Guid correlationId,
        string source,
        string cancelledBy = "operator")
    {
        return record.Status switch
        {
            PreAuthStatus.PENDING => new PreAuthCreated
            {
                PreAuthId = record.Id,
                PumpNumber = record.PumpNumber,
                NozzleNumber = record.NozzleNumber,
                RequestedAmountMinorUnits = record.RequestedAmountMinorUnits,
                CurrencyCode = record.CurrencyCode,
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            PreAuthStatus.AUTHORIZED => new PreAuthAuthorized
            {
                PreAuthId = record.Id,
                AuthorizedAmountMinorUnits = record.AuthorizedAmountMinorUnits ?? record.RequestedAmountMinorUnits,
                FccAuthCode = record.FccAuthorizationCode,
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            PreAuthStatus.DISPENSING => new PreAuthDispensing
            {
                PreAuthId = record.Id,
                PumpNumber = record.PumpNumber,
                NozzleNumber = record.NozzleNumber,
                FccCorrelationId = record.FccCorrelationId,
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            PreAuthStatus.COMPLETED => new PreAuthCompleted
            {
                PreAuthId = record.Id,
                DispensedAmountMinorUnits = record.ActualAmountMinorUnits ?? 0,
                MatchedTransactionId = record.MatchedTransactionId,
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            PreAuthStatus.CANCELLED => new PreAuthCancelled
            {
                PreAuthId = record.Id,
                CancelledBy = cancelledBy,
                Reason = record.FailureReason,
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            PreAuthStatus.EXPIRED => new PreAuthExpired
            {
                PreAuthId = record.Id,
                ExpiredAfterSeconds = Math.Max(0, (int)(record.ExpiresAt - record.RequestedAt).TotalSeconds),
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            PreAuthStatus.FAILED => new PreAuthFailed
            {
                PreAuthId = record.Id,
                Reason = record.FailureReason,
                CorrelationId = correlationId,
                LegalEntityId = record.LegalEntityId,
                SiteCode = record.SiteCode,
                Source = source
            },
            _ => throw new ArgumentOutOfRangeException(nameof(record.Status), record.Status, "Unknown pre-auth status.")
        };
    }
}
