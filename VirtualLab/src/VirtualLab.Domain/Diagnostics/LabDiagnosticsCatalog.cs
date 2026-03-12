using VirtualLab.Domain.Models;

namespace VirtualLab.Domain.Diagnostics;

public static class LabDiagnosticsCatalog
{
    public const string SeverityDebug = "Debug";
    public const string SeverityInformation = "Information";
    public const string SeverityWarning = "Warning";
    public const string SeverityError = "Error";
    public const string SeverityCritical = "Critical";

    private static readonly IReadOnlyList<LabLogCategoryDescriptor> CategoryDescriptors =
    [
        new("LabAction", SeverityInformation, "Interactive nozzle, pump, and management actions initiated in the virtual lab."),
        new("FccRequest", SeverityInformation, "Inbound FCC emulator requests captured before simulation logic runs."),
        new("FccResponse", SeverityInformation, "Outbound FCC emulator responses emitted after simulation logic runs."),
        new("PreAuthSequence", SeverityInformation, "Pre-auth orchestration milestones and reconciliation checkpoints."),
        new("TransactionGenerated", SeverityInformation, "Transactions created by forecourt simulation or replay workflows."),
        new("TransactionPushed", SeverityInformation, "Transactions delivered through configured callback push flows."),
        new("TransactionPulled", SeverityInformation, "Transactions listed or acknowledged through FCC pull endpoints."),
        new("CallbackAttempt", SeverityInformation, "Callback dispatch, capture, acknowledgement, and replay lifecycle events."),
        new("CallbackFailure", SeverityWarning, "Callback delivery failures and retry exhaustion events."),
        new("AuthFailure", SeverityWarning, "Rejected inbound authentication attempts with sanitized request metadata."),
        new("StateTransition", SeverityInformation, "State changes for nozzle, pre-auth, and transaction lifecycles."),
        new("ScenarioRun", SeverityInformation, "Scenario execution start, completion, and assertion summary events."),
    ];

    public static IReadOnlyList<LabLogCategoryDescriptor> Categories => CategoryDescriptors;

    public static void ApplyConventions(LabEventLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        log.Category = NormalizeCategory(log.Category);
        log.Severity = NormalizeSeverity(log.Severity, GetDefaultSeverity(log.Category));
        log.EventType = string.IsNullOrWhiteSpace(log.EventType) ? "Unknown" : log.EventType.Trim();
        log.Message = string.IsNullOrWhiteSpace(log.Message) ? log.EventType : log.Message.Trim();
        log.CorrelationId = string.IsNullOrWhiteSpace(log.CorrelationId) ? string.Empty : log.CorrelationId.Trim();
        log.RawPayloadJson = NormalizeJsonOrDefault(log.RawPayloadJson, "{}");
        log.CanonicalPayloadJson = NormalizeJsonOrDefault(log.CanonicalPayloadJson, "{}");
        log.MetadataJson = NormalizeJsonOrDefault(log.MetadataJson, "{}");
    }

    public static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "LabAction";
        }

        string trimmed = category.Trim();
        LabLogCategoryDescriptor? descriptor = CategoryDescriptors.FirstOrDefault(
            x => string.Equals(x.Category, trimmed, StringComparison.OrdinalIgnoreCase));

        return descriptor?.Category ?? trimmed;
    }

    public static string GetDefaultSeverity(string category)
    {
        LabLogCategoryDescriptor? descriptor = CategoryDescriptors.FirstOrDefault(
            x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase));

        return descriptor?.DefaultSeverity ?? SeverityInformation;
    }

    public static string NormalizeSeverity(string? severity, string? fallback = null)
    {
        string candidate = string.IsNullOrWhiteSpace(severity) ? fallback ?? SeverityInformation : severity.Trim();

        return candidate.ToLowerInvariant() switch
        {
            "debug" => SeverityDebug,
            "information" or "info" => SeverityInformation,
            "warning" or "warn" => SeverityWarning,
            "error" => SeverityError,
            "critical" or "fatal" => SeverityCritical,
            _ => fallback ?? SeverityInformation,
        };
    }

    private static string NormalizeJsonOrDefault(string? json, string fallback)
    {
        return string.IsNullOrWhiteSpace(json) ? fallback : json.Trim();
    }
}

public sealed record LabLogCategoryDescriptor(
    string Category,
    string DefaultSeverity,
    string Description);
