using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Application.Observability;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FccMiddleware.UnitTests.Reconciliation;

public sealed class ReconciliationMatchingServiceTests
{
    private readonly IReconciliationDbContext _db = Substitute.For<IReconciliationDbContext>();
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();
    private readonly ILogger<ReconciliationMatchingService> _logger = Substitute.For<ILogger<ReconciliationMatchingService>>();
    private readonly IObservabilityMetrics _metrics = Substitute.For<IObservabilityMetrics>();

    private ReconciliationMatchingService CreateSut() =>
        new(
            _db,
            _eventPublisher,
            Options.Create(new ReconciliationOptions
            {
                DefaultAmountTolerancePercent = 2.0m,
                DefaultAmountToleranceAbsolute = 100,
                DefaultTimeWindowMinutes = 15
            }),
            _logger,
            _metrics);

    [Fact]
    public async Task MatchAsync_CorrelationIdStep_MatchesAndCompletesPreAuth()
    {
        var sut = CreateSut();
        var tx = CreateTransaction(fccCorrelationId: "CORR-1", amountMinorUnits: 5000);
        var candidate = CreatePreAuth("ORDER-1", authorizedAmount: 5000, correlationId: "CORR-1");
        ReconciliationRecord? saved = null;

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindCorrelationCandidatesAsync(tx.LegalEntityId, tx.SiteCode, "CORR-1", Arg.Any<CancellationToken>())
            .Returns([candidate]);
        _db.AddReconciliationRecord(Arg.Do<ReconciliationRecord>(r => saved = r));

        var result = await sut.MatchAsync(tx, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.MATCHED);
        tx.PreAuthId.Should().Be(candidate.Id);
        tx.ReconciliationStatus.Should().Be(ReconciliationStatus.MATCHED);
        candidate.Status.Should().Be(PreAuthStatus.COMPLETED);
        candidate.MatchedTransactionId.Should().Be(tx.Id);
        saved.Should().NotBeNull();
        saved!.MatchMethod.Should().Be("CORRELATION_ID");
        saved.Status.Should().Be(ReconciliationStatus.MATCHED);
    }

    [Fact]
    public async Task MatchAsync_TerminalPreAuth_DoesNotForceCompletionOrStampCompletionFields()
    {
        var sut = CreateSut();
        var tx = CreateTransaction(fccCorrelationId: "CORR-LATE", amountMinorUnits: 5000);
        var candidate = CreatePreAuth(
            "ORDER-LATE",
            authorizedAmount: 5000,
            correlationId: "CORR-LATE",
            status: PreAuthStatus.EXPIRED);

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindCorrelationCandidatesAsync(tx.LegalEntityId, tx.SiteCode, "CORR-LATE", Arg.Any<CancellationToken>())
            .Returns([candidate]);

        var result = await sut.MatchAsync(tx, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.MATCHED);
        tx.PreAuthId.Should().Be(candidate.Id);
        candidate.Status.Should().Be(PreAuthStatus.EXPIRED);
        candidate.MatchedTransactionId.Should().BeNull();
        candidate.MatchedFccTransactionId.Should().BeNull();
        candidate.ActualAmountMinorUnits.Should().BeNull();
        candidate.ActualVolumeMillilitres.Should().BeNull();
        candidate.AmountVarianceMinorUnits.Should().BeNull();
        candidate.VarianceBps.Should().BeNull();
        candidate.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task MatchAsync_PartialAuthorization_UsesAuthorizedAmountForPreAuthVariance()
    {
        var sut = CreateSut();
        var tx = CreateTransaction(fccCorrelationId: "CORR-PARTIAL", amountMinorUnits: 8400);
        var candidate = CreatePreAuth(
            "ORDER-PARTIAL",
            authorizedAmount: 8000,
            requestedAmount: 10000,
            correlationId: "CORR-PARTIAL");
        ReconciliationRecord? saved = null;

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindCorrelationCandidatesAsync(tx.LegalEntityId, tx.SiteCode, "CORR-PARTIAL", Arg.Any<CancellationToken>())
            .Returns([candidate]);
        _db.AddReconciliationRecord(Arg.Do<ReconciliationRecord>(r => saved = r));

        var result = await sut.MatchAsync(tx, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.VARIANCE_FLAGGED);
        saved.Should().NotBeNull();
        saved!.VariancePercent.Should().Be(5.0000m);
        candidate.AmountVarianceMinorUnits.Should().Be(400);
        candidate.VarianceBps.Should().Be(500);
    }

    [Fact]
    public async Task MatchAsync_PumpNozzleTimeStep_UsesSmallestDeltaTieBreaker()
    {
        var sut = CreateSut();
        var completedAt = new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);
        var tx = CreateTransaction(completedAt: completedAt, amountMinorUnits: 5000);
        var farther = CreatePreAuth("ORDER-OLD", authorizedAt: completedAt.AddMinutes(-5), authorizedAmount: 5000);
        var closer = CreatePreAuth("ORDER-CLOSE", authorizedAt: completedAt.AddMinutes(-2), authorizedAmount: 5000);

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindCorrelationCandidatesAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _db.FindPumpNozzleTimeCandidatesAsync(
                tx.LegalEntityId,
                tx.SiteCode,
                tx.PumpNumber,
                tx.NozzleNumber,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns([farther, closer]);

        await sut.MatchAsync(tx, CancellationToken.None);

        tx.PreAuthId.Should().Be(closer.Id);
        tx.ReconciliationStatus.Should().Be(ReconciliationStatus.VARIANCE_FLAGGED);
    }

    [Fact]
    public async Task MatchAsync_OdooOrderFallbackStep_MatchesWhenEarlierStepsMiss()
    {
        var sut = CreateSut();
        var tx = CreateTransaction(odooOrderId: "ODOO-9", amountMinorUnits: 5000);
        var candidate = CreatePreAuth("ODOO-9", authorizedAmount: 5000);

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindPumpNozzleTimeCandidatesAsync(
                tx.LegalEntityId,
                tx.SiteCode,
                tx.PumpNumber,
                tx.NozzleNumber,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _db.FindOdooOrderCandidatesAsync(tx.LegalEntityId, tx.SiteCode, "ODOO-9", Arg.Any<CancellationToken>())
            .Returns([candidate]);

        var result = await sut.MatchAsync(tx, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.MATCHED);
        tx.PreAuthId.Should().Be(candidate.Id);
    }

    [Fact]
    public async Task MatchAsync_AmbiguousMatch_ForcesVarianceFlagged()
    {
        var sut = CreateSut();
        var tx = CreateTransaction(fccCorrelationId: "CORR-AMB", amountMinorUnits: 5000);
        var older = CreatePreAuth("ORDER-A", correlationId: "CORR-AMB", authorizedAmount: 5000, authorizedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var latest = CreatePreAuth("ORDER-B", correlationId: "CORR-AMB", authorizedAmount: 5000, authorizedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        ReconciliationRecord? saved = null;

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindCorrelationCandidatesAsync(tx.LegalEntityId, tx.SiteCode, "CORR-AMB", Arg.Any<CancellationToken>())
            .Returns([older, latest]);
        _db.AddReconciliationRecord(Arg.Do<ReconciliationRecord>(r => saved = r));

        var result = await sut.MatchAsync(tx, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.VARIANCE_FLAGGED);
        tx.PreAuthId.Should().Be(latest.Id);
        saved.Should().NotBeNull();
        saved!.AmbiguityFlag.Should().BeTrue();
        saved.Status.Should().Be(ReconciliationStatus.VARIANCE_FLAGGED);
    }

    [Fact]
    public async Task MatchAsync_NoCandidate_CreatesUnmatchedRecord()
    {
        var sut = CreateSut();
        var tx = CreateTransaction();
        ReconciliationRecord? saved = null;

        _db.FindByTransactionIdAsync(tx.Id, Arg.Any<CancellationToken>())
            .Returns((ReconciliationRecord?)null);
        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindPumpNozzleTimeCandidatesAsync(
                tx.LegalEntityId,
                tx.SiteCode,
                tx.PumpNumber,
                tx.NozzleNumber,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _db.AddReconciliationRecord(Arg.Do<ReconciliationRecord>(r => saved = r));

        var result = await sut.MatchAsync(tx, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.UNMATCHED);
        tx.PreAuthId.Should().BeNull();
        tx.ReconciliationStatus.Should().Be(ReconciliationStatus.UNMATCHED);
        saved.Should().NotBeNull();
        saved!.MatchMethod.Should().Be("NONE");
    }

    [Fact]
    public async Task RetryUnmatchedAsync_MatchFound_UpdatesExistingRecord()
    {
        var sut = CreateSut();
        var tx = CreateTransaction(fccCorrelationId: "CORR-RETRY", amountMinorUnits: 5000);
        var existing = new ReconciliationRecord
        {
            Id = Guid.NewGuid(),
            LegalEntityId = tx.LegalEntityId,
            SiteCode = tx.SiteCode,
            TransactionId = tx.Id,
            PumpNumber = tx.PumpNumber,
            NozzleNumber = tx.NozzleNumber,
            ActualAmountMinorUnits = tx.AmountMinorUnits,
            Status = ReconciliationStatus.UNMATCHED,
            MatchMethod = "NONE",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            LastMatchAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var candidate = CreatePreAuth("ORDER-RETRY", authorizedAmount: 5000, correlationId: "CORR-RETRY");

        _db.FindSiteContextAsync(tx.LegalEntityId, tx.SiteCode, Arg.Any<ReconciliationOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSiteContext(tx.LegalEntityId, tx.SiteCode));
        _db.FindCorrelationCandidatesAsync(tx.LegalEntityId, tx.SiteCode, "CORR-RETRY", Arg.Any<CancellationToken>())
            .Returns([candidate]);

        var result = await sut.RetryUnmatchedAsync(tx, existing, CancellationToken.None);

        result.Status.Should().Be(ReconciliationStatus.MATCHED);
        result.ReconciliationId.Should().Be(existing.Id);
        existing.Status.Should().Be(ReconciliationStatus.MATCHED);
        existing.PreAuthId.Should().Be(candidate.Id);
        existing.MatchMethod.Should().Be("CORRELATION_ID");
        tx.PreAuthId.Should().Be(candidate.Id);
        candidate.Status.Should().Be(PreAuthStatus.COMPLETED);
        _db.DidNotReceive().AddReconciliationRecord(Arg.Any<ReconciliationRecord>());
    }

    private static Transaction CreateTransaction(
        string? fccCorrelationId = null,
        string? odooOrderId = null,
        long amountMinorUnits = 5000,
        DateTimeOffset? completedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            LegalEntityId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            SiteCode = "SITE-1",
            PumpNumber = 1,
            NozzleNumber = 1,
            FccTransactionId = $"TX-{Guid.NewGuid():N}",
            ProductCode = "PMS",
            AmountMinorUnits = amountMinorUnits,
            VolumeMicrolitres = 10_000_000,
            UnitPriceMinorPerLitre = 500,
            CurrencyCode = "MWK",
            StartedAt = completedAt?.AddMinutes(-1) ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = completedAt ?? DateTimeOffset.UtcNow,
            FccVendor = FccVendor.DOMS,
            Status = TransactionStatus.PENDING,
            IngestionSource = IngestionSource.FCC_PUSH,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            FccCorrelationId = fccCorrelationId,
            OdooOrderId = odooOrderId
        };

    private static PreAuthRecord CreatePreAuth(
        string odooOrderId,
        long authorizedAmount,
        long? requestedAmount = null,
        string? correlationId = null,
        DateTimeOffset? authorizedAt = null,
        PreAuthStatus status = PreAuthStatus.AUTHORIZED) =>
        new()
        {
            Id = Guid.NewGuid(),
            LegalEntityId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            SiteCode = "SITE-1",
            OdooOrderId = odooOrderId,
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "PMS",
            CurrencyCode = "MWK",
            RequestedAmountMinorUnits = requestedAmount ?? authorizedAmount,
            AuthorizedAmountMinorUnits = authorizedAmount,
            Status = status,
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            AuthorizedAt = authorizedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiredAt = status == PreAuthStatus.EXPIRED ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FccCorrelationId = correlationId
        };

    private static ReconciliationSiteContext CreateSiteContext(Guid legalEntityId, string siteCode) =>
        new(
            legalEntityId,
            siteCode,
            new ReconciliationSettings(
                SiteUsesPreAuth: true,
                AmountTolerancePercent: 2.0m,
                AmountToleranceAbsolute: 100,
                TimeWindowMinutes: 15));
}
