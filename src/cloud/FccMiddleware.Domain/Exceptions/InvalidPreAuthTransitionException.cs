using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Exceptions;

/// <summary>
/// Thrown when a requested PreAuthRecord status transition is not permitted by the state machine.
/// See §5.2 of tier-1-2-state-machine-formal-definitions.md for valid transitions.
/// </summary>
public sealed class InvalidPreAuthTransitionException : InvalidOperationException
{
    public PreAuthStatus From { get; }
    public PreAuthStatus To { get; }

    public InvalidPreAuthTransitionException(PreAuthStatus from, PreAuthStatus to)
        : base($"Invalid pre-auth state transition: {from} → {to}.")
    {
        From = from;
        To = to;
    }
}
