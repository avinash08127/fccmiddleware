using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Api.AgentControl;

public sealed record PushHintRequest(
    PushHintKind Kind,
    Guid DeviceId,
    int? CommandCount = null,
    int? ConfigVersion = null);

public sealed record PushHintProviderResult(
    bool Succeeded,
    string? ProviderMessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record PushHintDispatchSummary(
    int AttemptedCount,
    int SuccessCount,
    int FailureCount);
