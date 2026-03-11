using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Exceptions;

/// <summary>
/// Thrown when a requested Transaction status transition is not permitted by the state machine.
/// See §5.1 of tier-1-2-state-machine-formal-definitions.md for valid transitions.
/// </summary>
public sealed class InvalidTransactionTransitionException : InvalidOperationException
{
    public TransactionStatus From { get; }
    public TransactionStatus To { get; }

    public InvalidTransactionTransitionException(TransactionStatus from, TransactionStatus to)
        : base($"Invalid transaction state transition: {from} → {to}.")
    {
        From = from;
        To = to;
    }
}
