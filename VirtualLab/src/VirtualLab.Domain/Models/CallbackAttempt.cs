using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class CallbackAttempt
{
    public Guid Id { get; set; }
    public Guid CallbackTargetId { get; set; }
    public Guid SimulatedTransactionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public int AttemptNumber { get; set; }
    public CallbackAttemptStatus Status { get; set; }
    public int ResponseStatusCode { get; set; }
    public string RequestHeadersJson { get; set; } = "{}";
    public string RequestPayloadJson { get; set; } = "{}";
    public string ResponseHeadersJson { get; set; } = "{}";
    public string ResponsePayloadJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTimeOffset AttemptedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public CallbackTarget CallbackTarget { get; set; } = null!;
    public SimulatedTransaction SimulatedTransaction { get; set; } = null!;
}
