namespace FccMiddleware.Domain.Events;

/// <summary>Pre-auth request received.</summary>
public sealed class PreAuthCreated : DomainEvent
{
    public override string EventType => "PreAuthCreated";
    public Guid PreAuthId { get; init; }
    public int PumpNumber { get; init; }
    public int NozzleNumber { get; init; }
    public long RequestedAmountMinorUnits { get; init; }
    public string CurrencyCode { get; init; } = null!;
}

/// <summary>FCC confirmed authorization.</summary>
public sealed class PreAuthAuthorized : DomainEvent
{
    public override string EventType => "PreAuthAuthorized";
    public Guid PreAuthId { get; init; }
    public long AuthorizedAmountMinorUnits { get; init; }
    public string? FccAuthCode { get; init; }
}

/// <summary>Dispensing finished; matched to transaction.</summary>
public sealed class PreAuthCompleted : DomainEvent
{
    public override string EventType => "PreAuthCompleted";
    public Guid PreAuthId { get; init; }
    public long DispensedAmountMinorUnits { get; init; }
    public Guid? MatchedTransactionId { get; init; }
}

/// <summary>Manually cancelled.</summary>
public sealed class PreAuthCancelled : DomainEvent
{
    public override string EventType => "PreAuthCancelled";
    public Guid PreAuthId { get; init; }
    public string CancelledBy { get; init; } = null!;
    public string? Reason { get; init; }
}

/// <summary>Timed out without completion.</summary>
public sealed class PreAuthExpired : DomainEvent
{
    public override string EventType => "PreAuthExpired";
    public Guid PreAuthId { get; init; }
    public int ExpiredAfterSeconds { get; init; }
}
