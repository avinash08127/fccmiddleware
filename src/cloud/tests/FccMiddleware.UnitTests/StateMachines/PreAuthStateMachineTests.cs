using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;

namespace FccMiddleware.UnitTests.StateMachines;

public class PreAuthStateMachineTests
{
    private static PreAuthRecord CreateRecord(PreAuthStatus status = PreAuthStatus.PENDING) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    // --- Valid transitions from PENDING ---

    [Theory]
    [InlineData(PreAuthStatus.AUTHORIZED)]
    [InlineData(PreAuthStatus.CANCELLED)]
    [InlineData(PreAuthStatus.EXPIRED)]
    [InlineData(PreAuthStatus.FAILED)]
    public void Transition_PendingToValidState_Succeeds(PreAuthStatus to)
    {
        var record = CreateRecord(PreAuthStatus.PENDING);
        record.Transition(to);
        record.Status.Should().Be(to);
    }

    // --- Valid transitions from AUTHORIZED ---

    [Theory]
    [InlineData(PreAuthStatus.DISPENSING)]
    [InlineData(PreAuthStatus.COMPLETED)]
    [InlineData(PreAuthStatus.CANCELLED)]
    [InlineData(PreAuthStatus.EXPIRED)]
    [InlineData(PreAuthStatus.FAILED)]
    public void Transition_AuthorizedToValidState_Succeeds(PreAuthStatus to)
    {
        var record = CreateRecord(PreAuthStatus.AUTHORIZED);
        record.Transition(to);
        record.Status.Should().Be(to);
    }

    // --- Valid transitions from DISPENSING ---

    [Theory]
    [InlineData(PreAuthStatus.COMPLETED)]
    [InlineData(PreAuthStatus.CANCELLED)]
    [InlineData(PreAuthStatus.EXPIRED)]
    [InlineData(PreAuthStatus.FAILED)]
    public void Transition_DispensingToValidState_Succeeds(PreAuthStatus to)
    {
        var record = CreateRecord(PreAuthStatus.DISPENSING);
        record.Transition(to);
        record.Status.Should().Be(to);
    }

    [Fact]
    public void Transition_UpdatesUpdatedAt()
    {
        var record = CreateRecord(PreAuthStatus.PENDING);
        var before = record.UpdatedAt;
        record.Transition(PreAuthStatus.AUTHORIZED);
        record.UpdatedAt.Should().BeAfter(before);
    }

    // --- Terminal states have no valid outgoing transitions ---

    [Theory]
    [InlineData(PreAuthStatus.COMPLETED)]
    [InlineData(PreAuthStatus.CANCELLED)]
    [InlineData(PreAuthStatus.EXPIRED)]
    [InlineData(PreAuthStatus.FAILED)]
    public void Transition_FromTerminalState_ThrowsForAnyTarget(PreAuthStatus terminal)
    {
        var record = CreateRecord(terminal);
        // Every transition from a terminal state should throw
        var act = () => record.Transition(PreAuthStatus.PENDING);
        act.Should().Throw<InvalidPreAuthTransitionException>();
    }

    // --- Invalid backward transitions ---

    [Theory]
    [InlineData(PreAuthStatus.AUTHORIZED, PreAuthStatus.PENDING)]
    [InlineData(PreAuthStatus.DISPENSING, PreAuthStatus.PENDING)]
    [InlineData(PreAuthStatus.DISPENSING, PreAuthStatus.AUTHORIZED)]
    public void Transition_InvalidBackwardTransition_Throws(PreAuthStatus from, PreAuthStatus to)
    {
        var record = CreateRecord(from);
        var act = () => record.Transition(to);
        act.Should().Throw<InvalidPreAuthTransitionException>()
           .Which.From.Should().Be(from);
    }

    [Fact]
    public void Transition_ExceptionCarriesToState()
    {
        var record = CreateRecord(PreAuthStatus.AUTHORIZED);
        var act = () => record.Transition(PreAuthStatus.PENDING);
        act.Should().Throw<InvalidPreAuthTransitionException>()
           .Which.To.Should().Be(PreAuthStatus.PENDING);
    }

    // --- DISPENSING → EXPIRED gap closure (§5.2 of state machine spec) ---

    [Fact]
    public void Transition_DispensingToExpired_IsAllowed()
    {
        var record = CreateRecord(PreAuthStatus.DISPENSING);
        record.Transition(PreAuthStatus.EXPIRED);
        record.Status.Should().Be(PreAuthStatus.EXPIRED);
    }

    // --- PENDING → DISPENSING is NOT a valid direct transition ---

    [Fact]
    public void Transition_PendingToDispensing_Throws()
    {
        var record = CreateRecord(PreAuthStatus.PENDING);
        var act = () => record.Transition(PreAuthStatus.DISPENSING);
        act.Should().Throw<InvalidPreAuthTransitionException>();
    }
}
