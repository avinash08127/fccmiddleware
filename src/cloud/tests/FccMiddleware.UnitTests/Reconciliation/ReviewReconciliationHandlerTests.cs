using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using NSubstitute;

namespace FccMiddleware.UnitTests.Reconciliation;

public sealed class ReviewReconciliationHandlerTests
{
    private readonly IReconciliationDbContext _db = Substitute.For<IReconciliationDbContext>();
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();

    [Fact]
    public async Task Handle_WhenSaveConflicts_ReturnsRaceConditionFailure()
    {
        var reconciliationId = Guid.NewGuid();
        var legalEntityId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var record = new ReconciliationRecord
        {
            Id = reconciliationId,
            LegalEntityId = legalEntityId,
            SiteCode = "SITE-1",
            TransactionId = transactionId,
            Status = ReconciliationStatus.VARIANCE_FLAGGED,
            MatchMethod = "CORRELATION_ID",
            ActualAmountMinorUnits = 5000,
            LastMatchAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var transaction = new Transaction
        {
            Id = transactionId,
            LegalEntityId = legalEntityId,
            SiteCode = "SITE-1",
            FccTransactionId = "TX-1",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "PMS",
            VolumeMicrolitres = 10_000_000,
            AmountMinorUnits = 5000,
            UnitPriceMinorPerLitre = 500,
            CurrencyCode = "MWK",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FccVendor = FccVendor.DOMS,
            Status = TransactionStatus.PENDING,
            IngestionSource = IngestionSource.FCC_PUSH,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var sut = new ReviewReconciliationHandler(_db, _eventPublisher);

        _db.FindByIdAsync(reconciliationId, Arg.Any<CancellationToken>())
            .Returns(record);
        _db.FindTransactionByIdAsync(transactionId, Arg.Any<CancellationToken>())
            .Returns(transaction);
        _db.TrySaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await sut.Handle(new ReviewReconciliationCommand
        {
            ReconciliationId = reconciliationId,
            TargetStatus = ReconciliationStatus.APPROVED,
            Reason = "Validated against end-of-day closeout records.",
            ReviewedByUserId = "portal-user-1",
            ScopedLegalEntityIds = [legalEntityId]
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("CONFLICT.RACE_CONDITION");
        result.Error.Message.Should().Contain("already reviewed");
    }
}
