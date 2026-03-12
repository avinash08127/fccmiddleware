using VirtualLab.Domain.Profiles;

namespace VirtualLab.Application.ContractValidation;

public static class ContractValidationScopes
{
    public const string Transaction = "transaction";
    public const string PreAuthRequest = "preauth.request";
    public const string PreAuthResponse = "preauth.response";
}

public sealed record PayloadContractValidationIssue(
    string Code,
    string Severity,
    string PayloadKind,
    string Path,
    string Message);

public sealed record PayloadFieldComparison(
    string Scope,
    string SourceField,
    string TargetField,
    string Status,
    string? SourceValue,
    string? TargetValue,
    string Transform,
    string Message);

public sealed record PayloadContractValidationReport(
    string Scope,
    bool Enabled,
    string Outcome,
    int ErrorCount,
    int WarningCount,
    int MatchedCount,
    int MissingCount,
    int MismatchCount,
    IReadOnlyList<PayloadContractValidationIssue> Issues,
    IReadOnlyList<PayloadFieldComparison> Comparisons)
{
    public static PayloadContractValidationReport Disabled(string scope)
    {
        return new PayloadContractValidationReport(
            scope,
            false,
            "NotConfigured",
            0,
            0,
            0,
            0,
            0,
            [],
            []);
    }
}

public interface IContractValidationService
{
    PayloadContractValidationReport Validate(
        FccProfileContract contract,
        string scope,
        string rawPayloadJson,
        string canonicalPayloadJson);
}
