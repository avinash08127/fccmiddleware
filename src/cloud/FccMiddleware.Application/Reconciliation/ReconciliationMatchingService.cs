using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Events;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Application.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Application.Reconciliation;

public sealed class ReconciliationMatchingService
{
    private const string MatchMethodNone = "NONE";
    private const string MatchMethodCorrelationId = "CORRELATION_ID";
    private const string MatchMethodPumpNozzleTime = "PUMP_NOZZLE_TIME";
    private const string MatchMethodOdooOrderId = "ODOO_ORDER_ID";

    private readonly IReconciliationDbContext _db;
    private readonly IEventPublisher _eventPublisher;
    private readonly ReconciliationOptions _options;
    private readonly ILogger<ReconciliationMatchingService> _logger;
    private readonly IObservabilityMetrics _metrics;

    public ReconciliationMatchingService(
        IReconciliationDbContext db,
        IEventPublisher eventPublisher,
        IOptions<ReconciliationOptions> options,
        ILogger<ReconciliationMatchingService> logger,
        IObservabilityMetrics metrics)
    {
        _db = db;
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<ReconciliationMatchResult> MatchAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var existing = await _db.FindByTransactionIdAsync(transaction.Id, cancellationToken);
        if (existing is not null)
        {
            return new ReconciliationMatchResult(
                Skipped: false,
                CreatedOrUpdated: false,
                Status: existing.Status,
                ReconciliationId: existing.Id);
        }

        var attemptAt = DateTimeOffset.UtcNow;
        var siteContext = await _db.FindSiteContextAsync(
            transaction.LegalEntityId,
            transaction.SiteCode,
            _options,
            cancellationToken);

        if (siteContext is null || !siteContext.Settings.SiteUsesPreAuth)
        {
            var skipReason = siteContext is null ? "SITE_NOT_FOUND" : "PRE_AUTH_DISABLED";
            _metrics.RecordReconciliationSkipped(
                transaction.LegalEntityId, transaction.SiteCode, skipReason);
            _logger.LogDebug(
                "Reconciliation skipped for transaction {TransactionId} at site {SiteCode}: {Reason}",
                transaction.Id, transaction.SiteCode, skipReason);

            return new ReconciliationMatchResult(
                Skipped: true,
                CreatedOrUpdated: false,
                Status: null,
                ReconciliationId: null);
        }

        var record = CreateRecord(transaction, attemptAt);
        _db.AddReconciliationRecord(record);

        return await MatchCoreAsync(transaction, record, siteContext, attemptAt, cancellationToken);
    }

    public Task<ReconciliationMatchResult> RetryUnmatchedAsync(
        Transaction transaction,
        ReconciliationRecord record,
        CancellationToken cancellationToken)
    {
        if (record.TransactionId != transaction.Id)
        {
            throw new InvalidOperationException(
                $"Reconciliation record {record.Id} does not belong to transaction {transaction.Id}.");
        }

        if (record.Status != ReconciliationStatus.UNMATCHED)
        {
            return Task.FromResult(new ReconciliationMatchResult(
                Skipped: false,
                CreatedOrUpdated: false,
                Status: record.Status,
                ReconciliationId: record.Id));
        }

        return MatchRetryAsync(transaction, record, DateTimeOffset.UtcNow, cancellationToken);
    }

    public ReconciliationMatchResult EscalateUnmatched(
        Transaction transaction,
        ReconciliationRecord record,
        DateTimeOffset escalatedAt)
    {
        if (record.EscalatedAtUtc.HasValue)
        {
            return new ReconciliationMatchResult(
                Skipped: false,
                CreatedOrUpdated: false,
                Status: record.Status,
                ReconciliationId: record.Id);
        }

        record.EscalatedAtUtc = escalatedAt;
        record.LastMatchAttemptAt = escalatedAt;
        record.UpdatedAt = escalatedAt;
        transaction.ReconciliationStatus = ReconciliationStatus.UNMATCHED;
        transaction.UpdatedAt = escalatedAt;

        var ageMinutes = Math.Max(0, (int)Math.Floor((escalatedAt - record.CreatedAt).TotalMinutes));
        _eventPublisher.Publish(new ReconciliationUnmatchedAged
        {
            ReconciliationId = record.Id,
            TransactionId = transaction.Id,
            FirstAttemptedAt = record.CreatedAt,
            AgedAt = escalatedAt,
            AgeMinutes = ageMinutes,
            CorrelationId = transaction.CorrelationId,
            LegalEntityId = transaction.LegalEntityId,
            SiteCode = transaction.SiteCode,
            Source = "cloud-reconciliation"
        });

        return new ReconciliationMatchResult(
            Skipped: false,
            CreatedOrUpdated: true,
            Status: record.Status,
            ReconciliationId: record.Id);
    }

    private async Task<ReconciliationMatchResult> MatchRetryAsync(
        Transaction transaction,
        ReconciliationRecord record,
        DateTimeOffset attemptAt,
        CancellationToken cancellationToken)
    {
        var siteContext = await _db.FindSiteContextAsync(
            transaction.LegalEntityId,
            transaction.SiteCode,
            _options,
            cancellationToken);

        if (siteContext is null || !siteContext.Settings.SiteUsesPreAuth)
        {
            var retrySkipReason = siteContext is null ? "SITE_NOT_FOUND" : "PRE_AUTH_DISABLED";
            _metrics.RecordReconciliationSkipped(
                transaction.LegalEntityId, transaction.SiteCode, retrySkipReason);
            _logger.LogDebug(
                "Reconciliation retry skipped for record {ReconciliationId} at site {SiteCode}: {Reason}",
                record.Id, transaction.SiteCode, retrySkipReason);

            record.LastMatchAttemptAt = attemptAt;
            record.UpdatedAt = attemptAt;

            return new ReconciliationMatchResult(
                Skipped: true,
                CreatedOrUpdated: false,
                Status: record.Status,
                ReconciliationId: record.Id);
        }

        return await MatchCoreAsync(transaction, record, siteContext, attemptAt, cancellationToken);
    }

    private async Task<ReconciliationMatchResult> MatchCoreAsync(
        Transaction transaction,
        ReconciliationRecord record,
        ReconciliationSiteContext siteContext,
        DateTimeOffset attemptAt,
        CancellationToken cancellationToken)
    {
        var candidate = await ResolveCandidateAsync(transaction, siteContext, cancellationToken);
        ResetForAttempt(transaction, record, attemptAt, candidate);

        if (candidate is null)
        {
            _metrics.RecordReconciliationMatchRate(
                transaction.LegalEntityId,
                transaction.SiteCode,
                record.MatchMethod,
                matched: false);
            return MarkUnmatched(transaction, record);
        }

        if (!candidate.PreAuth.AuthorizedAmountMinorUnits.HasValue
            || candidate.PreAuth.AuthorizedAmountMinorUnits.Value <= 0)
        {
            record.Status = ReconciliationStatus.UNMATCHED;
            record.AmbiguityReason = "INVALID_AUTHORIZED_AMOUNT";
            transaction.ReconciliationStatus = ReconciliationStatus.UNMATCHED;
            _metrics.RecordReconciliationMatchRate(
                transaction.LegalEntityId,
                transaction.SiteCode,
                record.MatchMethod,
                matched: false);
            return new ReconciliationMatchResult(false, true, record.Status, record.Id);
        }

        ApplyMatchedOutcome(transaction, candidate.PreAuth, record, siteContext.Settings, candidate, attemptAt);
        PublishEvents(record, transaction, candidate.PreAuth);
        _metrics.RecordReconciliationMatchRate(
            transaction.LegalEntityId,
            transaction.SiteCode,
            record.MatchMethod,
            matched: true);

        _logger.LogInformation(
            "Reconciled transaction {TransactionId} at site {SiteCode} with pre-auth {PreAuthId} via {MatchMethod}; status={Status}",
            transaction.Id,
            transaction.SiteCode,
            candidate.PreAuth.Id,
            record.MatchMethod,
            record.Status);

        return new ReconciliationMatchResult(false, true, record.Status, record.Id);
    }

    private static ReconciliationRecord CreateRecord(Transaction transaction, DateTimeOffset attemptAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            LegalEntityId = transaction.LegalEntityId,
            SiteCode = transaction.SiteCode,
            TransactionId = transaction.Id,
            OdooOrderId = transaction.OdooOrderId,
            PumpNumber = transaction.PumpNumber,
            NozzleNumber = transaction.NozzleNumber,
            ActualAmountMinorUnits = transaction.AmountMinorUnits,
            MatchMethod = MatchMethodNone,
            LastMatchAttemptAt = attemptAt,
            CreatedAt = attemptAt,
            UpdatedAt = attemptAt
        };

    private static void ResetForAttempt(
        Transaction transaction,
        ReconciliationRecord record,
        DateTimeOffset attemptAt,
        SelectedCandidate? candidate)
    {
        record.OdooOrderId = transaction.OdooOrderId;
        record.PumpNumber = transaction.PumpNumber;
        record.NozzleNumber = transaction.NozzleNumber;
        record.ActualAmountMinorUnits = transaction.AmountMinorUnits;
        record.PreAuthId = null;
        record.AuthorizedAmountMinorUnits = null;
        record.VarianceMinorUnits = null;
        record.AbsoluteVarianceMinorUnits = null;
        record.VariancePercent = null;
        record.WithinTolerance = null;
        record.MatchMethod = candidate?.MatchMethod ?? MatchMethodNone;
        record.AmbiguityFlag = candidate?.AmbiguityFlag ?? false;
        record.AmbiguityReason = candidate?.AmbiguityReason;
        record.MatchedAt = null;
        record.LastMatchAttemptAt = attemptAt;
        record.UpdatedAt = attemptAt;
    }

    private static ReconciliationMatchResult MarkUnmatched(
        Transaction transaction,
        ReconciliationRecord record)
    {
        record.Status = ReconciliationStatus.UNMATCHED;
        transaction.ReconciliationStatus = ReconciliationStatus.UNMATCHED;
        return new ReconciliationMatchResult(false, true, record.Status, record.Id);
    }

    private async Task<SelectedCandidate?> ResolveCandidateAsync(
        Transaction transaction,
        ReconciliationSiteContext siteContext,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(transaction.FccCorrelationId))
        {
            var correlationCandidates = await _db.FindCorrelationCandidatesAsync(
                transaction.LegalEntityId,
                transaction.SiteCode,
                transaction.FccCorrelationId,
                cancellationToken);

            var selected = SelectByAuthorizedAt(
                correlationCandidates,
                MatchMethodCorrelationId,
                "MULTIPLE_CORRELATION_ID_CANDIDATES");

            if (selected is not null)
            {
                return selected;
            }
        }

        var window = siteContext.Settings.TimeWindowMinutes;
        var timeCandidates = await _db.FindPumpNozzleTimeCandidatesAsync(
            transaction.LegalEntityId,
            transaction.SiteCode,
            transaction.PumpNumber,
            transaction.NozzleNumber,
            transaction.CompletedAt.AddMinutes(-window),
            transaction.CompletedAt.AddMinutes(window),
            cancellationToken);

        var timeSelected = SelectByTimeDelta(
            timeCandidates,
            transaction.CompletedAt,
            "MULTIPLE_PUMP_NOZZLE_TIME_CANDIDATES");

        if (timeSelected is not null)
        {
            return timeSelected;
        }

        if (!string.IsNullOrWhiteSpace(transaction.OdooOrderId))
        {
            var odooCandidates = await _db.FindOdooOrderCandidatesAsync(
                transaction.LegalEntityId,
                transaction.SiteCode,
                transaction.OdooOrderId,
                cancellationToken);

            return SelectByAuthorizedAt(
                odooCandidates,
                MatchMethodOdooOrderId,
                "MULTIPLE_ODOO_ORDER_ID_CANDIDATES");
        }

        return null;
    }

    private static SelectedCandidate? SelectByAuthorizedAt(
        IEnumerable<PreAuthRecord> candidates,
        string matchMethod,
        string ambiguityReason)
    {
        var filtered = candidates.ToList();
        if (filtered.Count == 0)
        {
            return null;
        }

        var ordered = filtered
            .OrderByDescending(c => c.AuthorizedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.CreatedAt)
            .ToList();

        return new SelectedCandidate(
            ordered[0],
            matchMethod,
            filtered.Count > 1,
            filtered.Count > 1 ? ambiguityReason : null);
    }

    private static SelectedCandidate? SelectByTimeDelta(
        IEnumerable<PreAuthRecord> candidates,
        DateTimeOffset completedAt,
        string ambiguityReason)
    {
        var filtered = candidates
            .Where(c => c.AuthorizedAt.HasValue)
            .Select(c => new
            {
                Candidate = c,
                Delta = AbsMinutes(completedAt - c.AuthorizedAt!.Value)
            })
            .OrderBy(x => x.Delta)
            .ThenByDescending(x => x.Candidate.AuthorizedAt)
            .ThenByDescending(x => x.Candidate.CreatedAt)
            .ToList();

        if (filtered.Count == 0)
        {
            return null;
        }

        return new SelectedCandidate(
            filtered[0].Candidate,
            MatchMethodPumpNozzleTime,
            filtered.Count > 1,
            filtered.Count > 1 ? ambiguityReason : null);
    }

    private static double AbsMinutes(TimeSpan value) => Math.Abs(value.TotalMinutes);

    private void ApplyMatchedOutcome(
        Transaction transaction,
        PreAuthRecord preAuth,
        ReconciliationRecord record,
        ReconciliationSettings settings,
        SelectedCandidate candidate,
        DateTimeOffset matchedAt)
    {
        var authorizedAmount = preAuth.AuthorizedAmountMinorUnits!.Value;
        var variance = transaction.AmountMinorUnits - authorizedAmount;
        var absoluteVariance = Math.Abs(variance);
        var variancePercent = CalculateVariancePercent(absoluteVariance, authorizedAmount);
        var varianceBps = CalculateVarianceBps(variancePercent);
        var withinTolerance = absoluteVariance <= settings.AmountToleranceAbsolute
            || variancePercent <= settings.AmountTolerancePercent;

        record.PreAuthId = preAuth.Id;
        record.AuthorizedAmountMinorUnits = authorizedAmount;
        record.VarianceMinorUnits = variance;
        record.AbsoluteVarianceMinorUnits = absoluteVariance;
        record.VariancePercent = variancePercent;
        record.WithinTolerance = withinTolerance;
        record.MatchedAt = matchedAt;

        record.Status = absoluteVariance == 0
            ? ReconciliationStatus.MATCHED
            : withinTolerance
                ? ReconciliationStatus.VARIANCE_WITHIN_TOLERANCE
                : ReconciliationStatus.VARIANCE_FLAGGED;

        if (candidate.AmbiguityFlag)
        {
            record.Status = ReconciliationStatus.VARIANCE_FLAGGED;
        }

        transaction.PreAuthId = preAuth.Id;
        transaction.ReconciliationStatus = record.Status;
        transaction.UpdatedAt = matchedAt;

        try
        {
            preAuth.Transition(PreAuthStatus.COMPLETED);
        }
        catch (InvalidPreAuthTransitionException ex)
        {
            _logger.LogWarning(
                ex,
                "Matched transaction {TransactionId} to pre-auth {PreAuthId}, but skipped completion stamping because transition {From} -> {To} is invalid",
                transaction.Id,
                preAuth.Id,
                ex.From,
                ex.To);
            return;
        }

        preAuth.MatchedTransactionId = transaction.Id;
        preAuth.MatchedFccTransactionId = transaction.FccTransactionId;
        preAuth.ActualAmountMinorUnits = transaction.AmountMinorUnits;
        preAuth.ActualVolumeMillilitres = transaction.VolumeMicrolitres / 1000L;
        preAuth.AmountVarianceMinorUnits = variance;
        preAuth.VarianceBps = varianceBps;
        preAuth.CompletedAt = matchedAt;
        preAuth.UpdatedAt = matchedAt;
    }

    private void PublishEvents(
        ReconciliationRecord record,
        Transaction transaction,
        PreAuthRecord preAuth)
    {
        _eventPublisher.Publish(new ReconciliationMatched
        {
            ReconciliationId = record.Id,
            TransactionId = transaction.Id,
            PreAuthId = preAuth.Id,
            MatchMethod = record.MatchMethod.ToLowerInvariant(),
            CorrelationId = transaction.CorrelationId,
            LegalEntityId = transaction.LegalEntityId,
            SiteCode = transaction.SiteCode,
            Source = "cloud-reconciliation"
        });

        if (record.Status != ReconciliationStatus.VARIANCE_FLAGGED)
        {
            return;
        }

        var varianceBps = !record.VariancePercent.HasValue
            ? 0
            : CalculateVarianceBps(record.VariancePercent.Value);

        _eventPublisher.Publish(new ReconciliationVarianceFlagged
        {
            ReconciliationId = record.Id,
            TransactionId = transaction.Id,
            PreAuthId = preAuth.Id,
            VarianceAmountMinorUnits = record.VarianceMinorUnits ?? 0,
            VarianceBps = varianceBps,
            ToleranceExceeded = !record.WithinTolerance.GetValueOrDefault(),
            CorrelationId = transaction.CorrelationId,
            LegalEntityId = transaction.LegalEntityId,
            SiteCode = transaction.SiteCode,
            Source = "cloud-reconciliation"
        });
    }

    private sealed record SelectedCandidate(
        PreAuthRecord PreAuth,
        string MatchMethod,
        bool AmbiguityFlag,
        string? AmbiguityReason);

    private static decimal CalculateVariancePercent(long absoluteVariance, long baselineAmount) =>
        baselineAmount == 0
            ? 0m
            : decimal.Round((decimal)absoluteVariance * 100m / baselineAmount, 4, MidpointRounding.AwayFromZero);

    private static int CalculateVarianceBps(decimal variancePercent) =>
        (int)decimal.Round(variancePercent * 100m, 0, MidpointRounding.AwayFromZero);
}
