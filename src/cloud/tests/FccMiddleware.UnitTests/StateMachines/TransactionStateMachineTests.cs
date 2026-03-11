using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;

namespace FccMiddleware.UnitTests.StateMachines;

public class TransactionStateMachineTests
{
    private static Transaction CreateTransaction(TransactionStatus status = TransactionStatus.PENDING) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    // --- Valid transitions ---

    [Fact]
    public void Transition_PendingToSyncedToOdoo_Succeeds()
    {
        var tx = CreateTransaction(TransactionStatus.PENDING);
        tx.Transition(TransactionStatus.SYNCED_TO_ODOO);
        tx.Status.Should().Be(TransactionStatus.SYNCED_TO_ODOO);
    }

    [Fact]
    public void Transition_PendingToDuplicate_Succeeds()
    {
        var tx = CreateTransaction(TransactionStatus.PENDING);
        tx.Transition(TransactionStatus.DUPLICATE);
        tx.Status.Should().Be(TransactionStatus.DUPLICATE);
    }

    [Fact]
    public void Transition_PendingToArchived_Succeeds()
    {
        var tx = CreateTransaction(TransactionStatus.PENDING);
        tx.Transition(TransactionStatus.ARCHIVED);
        tx.Status.Should().Be(TransactionStatus.ARCHIVED);
    }

    [Fact]
    public void Transition_SyncedToOdooToArchived_Succeeds()
    {
        var tx = CreateTransaction(TransactionStatus.SYNCED_TO_ODOO);
        tx.Transition(TransactionStatus.ARCHIVED);
        tx.Status.Should().Be(TransactionStatus.ARCHIVED);
    }

    [Fact]
    public void Transition_DuplicateToArchived_Succeeds()
    {
        var tx = CreateTransaction(TransactionStatus.DUPLICATE);
        tx.Transition(TransactionStatus.ARCHIVED);
        tx.Status.Should().Be(TransactionStatus.ARCHIVED);
    }

    [Fact]
    public void Transition_UpdatesUpdatedAt()
    {
        var tx = CreateTransaction(TransactionStatus.PENDING);
        var before = tx.UpdatedAt;
        tx.Transition(TransactionStatus.SYNCED_TO_ODOO);
        tx.UpdatedAt.Should().BeAfter(before);
    }

    // --- Invalid transitions (backward/unsupported) ---

    [Theory]
    [InlineData(TransactionStatus.SYNCED_TO_ODOO, TransactionStatus.PENDING)]
    [InlineData(TransactionStatus.SYNCED_TO_ODOO, TransactionStatus.DUPLICATE)]
    [InlineData(TransactionStatus.ARCHIVED, TransactionStatus.PENDING)]
    [InlineData(TransactionStatus.ARCHIVED, TransactionStatus.SYNCED_TO_ODOO)]
    [InlineData(TransactionStatus.ARCHIVED, TransactionStatus.DUPLICATE)]
    [InlineData(TransactionStatus.DUPLICATE, TransactionStatus.PENDING)]
    [InlineData(TransactionStatus.DUPLICATE, TransactionStatus.SYNCED_TO_ODOO)]
    public void Transition_InvalidTransition_ThrowsInvalidTransactionTransitionException(
        TransactionStatus from, TransactionStatus to)
    {
        var tx = CreateTransaction(from);
        var act = () => tx.Transition(to);
        act.Should().Throw<InvalidTransactionTransitionException>()
           .Which.From.Should().Be(from);
    }

    [Fact]
    public void Transition_ExceptionCarriesToState()
    {
        var tx = CreateTransaction(TransactionStatus.SYNCED_TO_ODOO);
        var act = () => tx.Transition(TransactionStatus.PENDING);
        act.Should().Throw<InvalidTransactionTransitionException>()
           .Which.To.Should().Be(TransactionStatus.PENDING);
    }

    [Fact]
    public void Transition_SameStatus_Throws()
    {
        var tx = CreateTransaction(TransactionStatus.PENDING);
        var act = () => tx.Transition(TransactionStatus.PENDING);
        act.Should().Throw<InvalidTransactionTransitionException>();
    }
}
