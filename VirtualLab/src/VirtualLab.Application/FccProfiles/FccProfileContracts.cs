using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Profiles;

namespace VirtualLab.Application.FccProfiles;

public sealed record FccProfileSummary(
    Guid Id,
    string ProfileKey,
    string Name,
    string VendorFamily,
    SimulatedAuthMode AuthMode,
    TransactionDeliveryMode DeliveryMode,
    PreAuthFlowMode PreAuthMode,
    bool IsActive,
    bool IsDefault);

public sealed class FccProfileRecord
{
    public Guid? Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ProfileKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string VendorFamily { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public FccProfileContract Contract { get; set; } = new();
}

public sealed record FccProfilePreviewRequest(
    Guid? ProfileId,
    FccProfileRecord? Draft,
    string Operation,
    IReadOnlyDictionary<string, string>? SampleValues);

public sealed record FccProfilePreviewResult(
    string Operation,
    string RequestBody,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string ResponseBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    IReadOnlyDictionary<string, string> SampleValues);

public sealed record FccProfileValidationMessage(
    string Path,
    string Message,
    string Severity);

public sealed record FccProfileValidationResult(
    bool IsValid,
    IReadOnlyList<FccProfileValidationMessage> Messages);

public sealed class ResolvedFccProfile
{
    public Guid SiteId { get; init; }
    public string SiteCode { get; init; } = string.Empty;
    public Guid ProfileId { get; init; }
    public string ProfileKey { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string VendorFamily { get; init; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; init; }
    public PreAuthFlowMode PreAuthMode { get; init; }
    public FccProfileContract Contract { get; init; } = new();
}

public interface IFccProfileService
{
    Task<IReadOnlyList<FccProfileSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<FccProfileRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ResolvedFccProfile?> ResolveBySiteCodeAsync(string siteCode, CancellationToken cancellationToken = default);
    Task<FccProfileValidationResult> ValidateAsync(FccProfileRecord record, CancellationToken cancellationToken = default);
    Task<FccProfilePreviewResult> PreviewAsync(FccProfilePreviewRequest request, CancellationToken cancellationToken = default);
    Task<FccProfileRecord> CreateAsync(FccProfileRecord record, CancellationToken cancellationToken = default);
    Task<FccProfileRecord?> UpdateAsync(Guid id, FccProfileRecord record, CancellationToken cancellationToken = default);
}
