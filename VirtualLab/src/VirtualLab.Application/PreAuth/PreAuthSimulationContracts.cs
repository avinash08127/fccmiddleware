namespace VirtualLab.Application.PreAuth;

public sealed record PreAuthSimulationRequest(
    string SiteCode,
    string Operation,
    string Method,
    string Path,
    string TraceIdentifier,
    string RawRequestJson,
    IReadOnlyDictionary<string, string> Fields);

public sealed record PreAuthSimulationResponse(
    int StatusCode,
    string ContentType,
    string Body);

public sealed record PreAuthSessionSummary(
    Guid Id,
    string SiteCode,
    string ProfileKey,
    string CorrelationId,
    string ExternalReference,
    string Mode,
    string Status,
    decimal ReservedAmount,
    decimal? AuthorizedAmount,
    decimal? FinalAmount,
    decimal? FinalVolume,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AuthorizedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string RawRequestJson,
    string CanonicalRequestJson,
    string RawResponseJson,
    string CanonicalResponseJson,
    string TimelineJson);

public interface IPreAuthSimulationService
{
    Task<PreAuthSimulationResponse> HandleAsync(PreAuthSimulationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PreAuthSessionSummary>> ListSessionsAsync(
        string? siteCode,
        string? correlationId,
        int limit,
        CancellationToken cancellationToken = default);
    Task<int> ExpireSessionsAsync(CancellationToken cancellationToken = default);
}
