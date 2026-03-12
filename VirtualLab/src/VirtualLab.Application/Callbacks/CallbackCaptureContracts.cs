namespace VirtualLab.Application.Callbacks;

public sealed class CallbackCaptureRequest
{
    public string HttpMethod { get; init; } = "POST";
    public string RequestUrl { get; init; } = string.Empty;
    public string RequestHeadersJson { get; init; } = "{}";
    public string RequestPayloadJson { get; init; } = "{}";
    public string AuthOutcome { get; init; } = "Authorized";
    public string AuthMode { get; init; } = "None";
    public int ResponseStatusCode { get; init; } = 202;
    public string ResponseHeadersJson { get; init; } = "{}";
    public string ResponsePayloadJson { get; init; } = "{}";
    public string? CorrelationMetadataJson { get; init; }
    public Guid? ReplayOfCaptureId { get; init; }
    public Guid? LinkedAttemptId { get; init; }
}

public sealed record CallbackCaptureResult(
    Guid CaptureId,
    string TargetKey,
    string CorrelationId,
    int ResponseStatusCode,
    string ResponsePayloadJson);

public sealed record CallbackHistoryItemView(
    Guid Id,
    Guid CallbackTargetId,
    Guid? SiteId,
    Guid? ProfileId,
    Guid? SimulatedTransactionId,
    string TargetKey,
    string TargetName,
    string CorrelationId,
    string? ExternalTransactionId,
    string AuthOutcome,
    string AuthMode,
    string HttpMethod,
    string RequestUrl,
    string RequestHeadersJson,
    string RequestPayloadJson,
    int ResponseStatusCode,
    string ResponseHeadersJson,
    string ResponsePayloadJson,
    string CorrelationMetadataJson,
    bool IsReplay,
    Guid? ReplayedFromId,
    DateTimeOffset CapturedAtUtc);

public sealed record CallbackReplayResult(
    Guid CaptureId,
    string TargetKey,
    string CorrelationId,
    string Message);

public interface ICallbackCaptureService
{
    Task<CallbackCaptureResult?> CaptureAsync(
        string targetKey,
        CallbackCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CallbackHistoryItemView>> ListHistoryAsync(
        string targetKey,
        int limit,
        CancellationToken cancellationToken = default);

    Task<CallbackReplayResult?> ReplayAsync(
        string targetKey,
        Guid captureId,
        CancellationToken cancellationToken = default);
}
