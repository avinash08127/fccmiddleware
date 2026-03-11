using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Events;

namespace FccMiddleware.UnitTests.Workers;

public class StaleTransactionDetectionTests
{
    private static Transaction CreatePendingTransaction(int daysOld = 5) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysOld),
        LegalEntityId = Guid.NewGuid(),
        FccTransactionId = $"FCC-{Guid.NewGuid():N}",
        SiteCode = "SITE001",
        PumpNumber = 1,
        NozzleNumber = 1,
        ProductCode = "ULP95",
        VolumeMicrolitres = 50_000_000,
        AmountMinorUnits = 150_000,
        UnitPriceMinorPerLitre = 3000,
        CurrencyCode = "ZAR",
        StartedAt = DateTimeOffset.UtcNow.AddDays(-daysOld),
        CompletedAt = DateTimeOffset.UtcNow.AddDays(-daysOld).AddMinutes(2),
        FccVendor = FccVendor.DOMS,
        Status = TransactionStatus.PENDING,
        IngestionSource = IngestionSource.FCC_PUSH,
        IsStale = false,
        CorrelationId = Guid.NewGuid(),
        UpdatedAt = DateTimeOffset.UtcNow.AddDays(-daysOld)
    };

    // --- Stale flag behavior ---

    [Fact]
    public void SettingIsStale_DoesNotChangeStatus()
    {
        var tx = CreatePendingTransaction();

        tx.IsStale = true;

        tx.Status.Should().Be(TransactionStatus.PENDING);
    }

    [Fact]
    public void StaleTransaction_CanStillTransitionToSyncedToOdoo()
    {
        var tx = CreatePendingTransaction();
        tx.IsStale = true;

        tx.Transition(TransactionStatus.SYNCED_TO_ODOO);

        tx.Status.Should().Be(TransactionStatus.SYNCED_TO_ODOO);
        tx.IsStale.Should().BeTrue();
    }

    [Fact]
    public void StaleTransaction_CanStillTransitionToArchived()
    {
        var tx = CreatePendingTransaction();
        tx.IsStale = true;

        tx.Transition(TransactionStatus.ARCHIVED);

        tx.Status.Should().Be(TransactionStatus.ARCHIVED);
    }

    // --- Stale detection criteria ---

    [Theory]
    [InlineData(1, 3, false)]  // 1 day old, threshold 3 → not stale
    [InlineData(2, 3, false)]  // 2 days old, threshold 3 → not stale
    [InlineData(3, 3, true)]   // exactly 3 days → stale (created_at < cutoff due to execution time)
    [InlineData(4, 3, true)]   // 4 days old, threshold 3 → stale
    [InlineData(10, 3, true)]  // 10 days old, threshold 3 → stale
    [InlineData(5, 7, false)]  // 5 days old, threshold 7 → not stale
    [InlineData(8, 7, true)]   // 8 days old, threshold 7 → stale
    public void StaleCriteria_CorrectlyIdentifiesStaleTransactions(
        int daysOld, int thresholdDays, bool expectedStale)
    {
        var tx = CreatePendingTransaction(daysOld);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-thresholdDays);

        var isStaleCandidate = tx.Status == TransactionStatus.PENDING
                            && !tx.IsStale
                            && tx.CreatedAt < cutoff;

        isStaleCandidate.Should().Be(expectedStale);
    }

    [Fact]
    public void StaleCriteria_SkipsAlreadyStaleTransactions()
    {
        var tx = CreatePendingTransaction(daysOld: 5);
        tx.IsStale = true;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-3);

        var isStaleCandidate = tx.Status == TransactionStatus.PENDING
                            && !tx.IsStale
                            && tx.CreatedAt < cutoff;

        isStaleCandidate.Should().BeFalse();
    }

    [Fact]
    public void StaleCriteria_SkipsNonPendingTransactions()
    {
        var tx = CreatePendingTransaction(daysOld: 5);
        tx.Transition(TransactionStatus.SYNCED_TO_ODOO);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-3);

        var isStaleCandidate = tx.Status == TransactionStatus.PENDING
                            && !tx.IsStale
                            && tx.CreatedAt < cutoff;

        isStaleCandidate.Should().BeFalse();
    }

    // --- TransactionStaleFlagged event ---

    [Fact]
    public void TransactionStaleFlagged_HasCorrectEventType()
    {
        var evt = new TransactionStaleFlagged
        {
            TransactionId = Guid.NewGuid(),
            FccTransactionId = "FCC-001",
            StalePendingThresholdDays = 3,
            DetectedAt = DateTimeOffset.UtcNow,
            Source = "cloud-stale-detection",
            LegalEntityId = Guid.NewGuid(),
            SiteCode = "SITE001",
            CorrelationId = Guid.NewGuid()
        };

        evt.EventType.Should().Be("TransactionStaleFlagged");
        evt.StalePendingThresholdDays.Should().Be(3);
        evt.Source.Should().Be("cloud-stale-detection");
    }

    [Fact]
    public void TransactionStaleFlagged_InheritsFromDomainEvent()
    {
        var evt = new TransactionStaleFlagged();

        evt.Should().BeAssignableTo<DomainEvent>();
        evt.EventId.Should().NotBeEmpty();
        evt.SchemaVersion.Should().Be(1);
    }
}
