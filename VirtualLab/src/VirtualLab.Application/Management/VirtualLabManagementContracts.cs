using VirtualLab.Application.FccProfiles;
using VirtualLab.Domain.Enums;

namespace VirtualLab.Application.Management;

public sealed record ManagementValidationMessage(
    string Path,
    string Message,
    string Severity,
    string Code);

public sealed record ManagementValidationResult(
    bool IsValid,
    IReadOnlyList<ManagementValidationMessage> Messages);

public sealed class SiteFiscalizationSettings
{
    public string Mode { get; set; } = "NONE";
    public bool RequireCustomerTaxId { get; set; }
    public bool FiscalReceiptRequired { get; set; }
    public string TaxAuthorityName { get; set; } = string.Empty;
    public string TaxAuthorityEndpoint { get; set; } = string.Empty;
}

public sealed class SiteSettingsView
{
    public bool IsTemplate { get; set; }
    public string DefaultCallbackTargetKey { get; set; } = string.Empty;
    public int PullPageSize { get; set; } = 100;
    public SiteFiscalizationSettings Fiscalization { get; set; } = new();
}

public sealed class CallbackTargetSummaryView
{
    public Guid Id { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public SimulatedAuthMode AuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class CallbackTargetUpsertRequest
{
    public Guid? Id { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public SimulatedAuthMode AuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class LabEnvironmentSummaryView
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset? LastSeededAtUtc { get; set; }
}

public sealed class LabEnvironmentRetentionSettingsView
{
    public int LogRetentionDays { get; set; } = 30;
    public int CallbackHistoryRetentionDays { get; set; } = 30;
    public int TransactionRetentionDays { get; set; } = 90;
    public bool PreserveTimelineIntegrity { get; set; } = true;
}

public sealed class LabEnvironmentBackupSettingsView
{
    public bool IncludeRuntimeDataByDefault { get; set; } = true;
    public bool IncludeScenarioRunsByDefault { get; set; } = true;
}

public sealed class LabEnvironmentTelemetrySettingsView
{
    public bool EmitMetrics { get; set; } = true;
    public bool EmitActivities { get; set; } = true;
}

public sealed class LabEnvironmentSettingsView
{
    public LabEnvironmentRetentionSettingsView Retention { get; set; } = new();
    public LabEnvironmentBackupSettingsView Backup { get; set; } = new();
    public LabEnvironmentTelemetrySettingsView Telemetry { get; set; } = new();
}

public sealed class LabEnvironmentLogCategoryView
{
    public string Category { get; set; } = string.Empty;
    public string DefaultSeverity { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class LabEnvironmentDetailView : LabEnvironmentSummaryView
{
    public int SeedVersion { get; set; }
    public int DeterministicSeed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public LabEnvironmentSettingsView Settings { get; set; } = new();
    public IReadOnlyList<LabEnvironmentLogCategoryView> LogCategories { get; set; } = [];
}

public sealed class LabEnvironmentUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public LabEnvironmentSettingsView Settings { get; set; } = new();
}

public sealed class LabEnvironmentPruneRequest
{
    public bool DryRun { get; set; }
    public int? LogRetentionDays { get; set; }
    public int? CallbackHistoryRetentionDays { get; set; }
    public int? TransactionRetentionDays { get; set; }
    public bool? PreserveTimelineIntegrity { get; set; }
}

public sealed class LabEnvironmentPruneResult
{
    public Guid LabEnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; }
    public DateTimeOffset? LogCutoffUtc { get; set; }
    public DateTimeOffset? CallbackCutoffUtc { get; set; }
    public DateTimeOffset? TransactionCutoffUtc { get; set; }
    public int LogsRemoved { get; set; }
    public int CallbackAttemptsRemoved { get; set; }
    public int TransactionsRemoved { get; set; }
    public int PreAuthSessionsRemoved { get; set; }
    public int ScenarioRunsPreserved { get; set; }
}

public sealed class LabEnvironmentExportRequest
{
    public bool? IncludeRuntimeData { get; set; }
}

public sealed class LabEnvironmentImportRequest
{
    public bool ReplaceExisting { get; set; }
    public LabEnvironmentExportPackage Package { get; set; } = new();
}

public sealed class LabEnvironmentImportResult
{
    public Guid LabEnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public bool ReplaceExisting { get; set; }
    public int SiteCount { get; set; }
    public int ProfileCount { get; set; }
    public int ProductCount { get; set; }
    public int ScenarioDefinitionCount { get; set; }
    public int ScenarioRunCount { get; set; }
    public int TransactionCount { get; set; }
    public int PreAuthSessionCount { get; set; }
    public int CallbackAttemptCount { get; set; }
    public int LogCount { get; set; }
}

public sealed class LabEnvironmentExportPackage
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public bool IncludesRuntimeData { get; set; } = true;
    public LabEnvironmentSnapshot Environment { get; set; } = new();
    public IReadOnlyList<FccSimulatorProfileSnapshot> Profiles { get; set; } = [];
    public IReadOnlyList<ProductSnapshot> Products { get; set; } = [];
    public IReadOnlyList<SiteSnapshot> Sites { get; set; } = [];
    public IReadOnlyList<CallbackTargetSnapshot> CallbackTargets { get; set; } = [];
    public IReadOnlyList<PumpSnapshot> Pumps { get; set; } = [];
    public IReadOnlyList<NozzleSnapshot> Nozzles { get; set; } = [];
    public IReadOnlyList<ScenarioDefinitionSnapshot> ScenarioDefinitions { get; set; } = [];
    public IReadOnlyList<ScenarioRunSnapshot> ScenarioRuns { get; set; } = [];
    public IReadOnlyList<PreAuthSessionSnapshot> PreAuthSessions { get; set; } = [];
    public IReadOnlyList<SimulatedTransactionSnapshot> Transactions { get; set; } = [];
    public IReadOnlyList<CallbackAttemptSnapshot> CallbackAttempts { get; set; } = [];
    public IReadOnlyList<LabEventLogSnapshot> Logs { get; set; } = [];
}

public sealed class LabEnvironmentSnapshot
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public int SeedVersion { get; set; }
    public int DeterministicSeed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastSeededAtUtc { get; set; }
}

public sealed class FccSimulatorProfileSnapshot
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ProfileKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string VendorFamily { get; set; } = string.Empty;
    public SimulatedAuthMode AuthMode { get; set; }
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public PreAuthFlowMode PreAuthMode { get; set; }
    public string EndpointBasePath { get; set; } = "/fcc";
    public string EndpointSurfaceJson { get; set; } = "[]";
    public string AuthConfigurationJson { get; set; } = "{}";
    public string CapabilitiesJson { get; set; } = "{}";
    public string RequestTemplatesJson { get; set; } = "{}";
    public string ResponseTemplatesJson { get; set; } = "{}";
    public string ValidationRulesJson { get; set; } = "[]";
    public string FieldMappingsJson { get; set; } = "{}";
    public string FailureSimulationJson { get; set; } = "{}";
    public string ExtensionConfigurationJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ProductSnapshot
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#000000";
    public decimal UnitPrice { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class SiteSnapshot
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public Guid ActiveFccSimulatorProfileId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string CurrencyCode { get; set; } = "USD";
    public string ExternalReference { get; set; } = string.Empty;
    public SimulatedAuthMode InboundAuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public PreAuthFlowMode PreAuthMode { get; set; }
    public string FccVendor { get; set; } = "Generic";
    public string SettingsJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CallbackTargetSnapshot
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public Guid? SiteId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public SimulatedAuthMode AuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class PumpSnapshot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public int PumpNumber { get; set; }
    public int FccPumpNumber { get; set; }
    public int LayoutX { get; set; }
    public int LayoutY { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class NozzleSnapshot
{
    public Guid Id { get; set; }
    public Guid PumpId { get; set; }
    public Guid ProductId { get; set; }
    public int NozzleNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public NozzleState State { get; set; }
    public string SimulationStateJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ScenarioDefinitionSnapshot
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ScenarioKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DeterministicSeed { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public string ReplaySignature { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ScenarioRunSnapshot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid ScenarioDefinitionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public int ReplaySeed { get; set; }
    public string ReplaySignature { get; set; } = string.Empty;
    public ScenarioRunStatus Status { get; set; }
    public string InputSnapshotJson { get; set; } = "{}";
    public string ResultSummaryJson { get; set; } = "{}";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class PreAuthSessionSnapshot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid? PumpId { get; set; }
    public Guid? NozzleId { get; set; }
    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public PreAuthFlowMode Mode { get; set; }
    public PreAuthSessionStatus Status { get; set; }
    public decimal ReservedAmount { get; set; }
    public decimal? AuthorizedAmount { get; set; }
    public decimal? FinalAmount { get; set; }
    public decimal? FinalVolume { get; set; }
    public string RawRequestJson { get; set; } = "{}";
    public string CanonicalRequestJson { get; set; } = "{}";
    public string RawResponseJson { get; set; } = "{}";
    public string CanonicalResponseJson { get; set; } = "{}";
    public string TimelineJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? AuthorizedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

public sealed class SimulatedTransactionSnapshot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid PumpId { get; set; }
    public Guid NozzleId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? PreAuthSessionId { get; set; }
    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string ExternalTransactionId { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public SimulatedTransactionStatus Status { get; set; }
    public decimal Volume { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public string RawPayloadJson { get; set; } = "{}";
    public string CanonicalPayloadJson { get; set; } = "{}";
    public string RawHeadersJson { get; set; } = "{}";
    public string DeliveryCursor { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public string TimelineJson { get; set; } = "[]";
}

public sealed class CallbackAttemptSnapshot
{
    public Guid Id { get; set; }
    public Guid CallbackTargetId { get; set; }
    public Guid SimulatedTransactionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public int AttemptNumber { get; set; }
    public CallbackAttemptStatus Status { get; set; }
    public int ResponseStatusCode { get; set; }
    public string RequestUrl { get; set; } = string.Empty;
    public string RequestHeadersJson { get; set; } = "{}";
    public string RequestPayloadJson { get; set; } = "{}";
    public string ResponseHeadersJson { get; set; } = "{}";
    public string ResponsePayloadJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public int MaxRetryCount { get; set; }
    public DateTimeOffset AttemptedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? NextRetryAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}

public sealed class LabEventLogSnapshot
{
    public Guid Id { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? FccSimulatorProfileId { get; set; }
    public Guid? PreAuthSessionId { get; set; }
    public Guid? SimulatedTransactionId { get; set; }
    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Severity { get; set; } = "Information";
    public string Category { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawPayloadJson { get; set; } = "{}";
    public string CanonicalPayloadJson { get; set; } = "{}";
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class SiteCompatibilityView
{
    public bool IsValid { get; set; }
    public IReadOnlyList<ManagementValidationMessage> Messages { get; set; } = [];
}

public sealed class SiteForecourtSummaryView
{
    public int PumpCount { get; set; }
    public int NozzleCount { get; set; }
    public int ActivePumpCount { get; set; }
    public int ActiveNozzleCount { get; set; }
}

public class SiteListItemView
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string CurrencyCode { get; set; } = "USD";
    public string ExternalReference { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public SimulatedAuthMode InboundAuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public PreAuthFlowMode PreAuthMode { get; set; }
    public string FccVendor { get; set; } = "Generic";
    public SiteSettingsView Settings { get; set; } = new();
    public FccProfileSummary ActiveProfile { get; set; } = new(Guid.Empty, string.Empty, string.Empty, string.Empty, SimulatedAuthMode.None, TransactionDeliveryMode.Pull, PreAuthFlowMode.CreateOnly, false, false);
    public SiteForecourtSummaryView Forecourt { get; set; } = new();
    public SiteCompatibilityView Compatibility { get; set; } = new();
}

public sealed class SiteDetailView : SiteListItemView
{
    public SiteForecourtView ForecourtConfiguration { get; set; } = new();
    public IReadOnlyList<CallbackTargetSummaryView> CallbackTargets { get; set; } = [];
    public IReadOnlyList<FccProfileSummary> AvailableProfiles { get; set; } = [];
}

public sealed class SiteUpsertRequest
{
    public Guid LabEnvironmentId { get; set; }
    public Guid ActiveFccSimulatorProfileId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string CurrencyCode { get; set; } = "USD";
    public string ExternalReference { get; set; } = string.Empty;
    public SimulatedAuthMode InboundAuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public PreAuthFlowMode PreAuthMode { get; set; }
    public string FccVendor { get; set; } = "Generic";
    public bool IsActive { get; set; } = true;
    public SiteSettingsView Settings { get; set; } = new();
    public IReadOnlyList<CallbackTargetUpsertRequest>? CallbackTargets { get; set; }
}

public sealed class DuplicateSiteRequest
{
    public string SiteCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public Guid? ActiveFccSimulatorProfileId { get; set; }
    public bool CopyForecourt { get; set; } = true;
    public bool CopyCallbackTargets { get; set; } = true;
    public bool MarkAsTemplate { get; set; }
    public bool Activate { get; set; } = true;
}

public sealed class SiteForecourtView
{
    public Guid SiteId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public IReadOnlyList<ForecourtPumpView> Pumps { get; set; } = [];
}

public sealed class ForecourtPumpView
{
    public Guid Id { get; set; }
    public int PumpNumber { get; set; }
    public int FccPumpNumber { get; set; }
    public int LayoutX { get; set; }
    public int LayoutY { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public IReadOnlyList<ForecourtNozzleView> Nozzles { get; set; } = [];
}

public sealed class ForecourtNozzleView
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int NozzleNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public NozzleState State { get; set; }
    public bool IsActive { get; set; }
}

public sealed class SaveForecourtRequest
{
    public IReadOnlyList<ForecourtPumpUpsertRequest> Pumps { get; set; } = [];
}

public sealed class ForecourtPumpUpsertRequest
{
    public Guid? Id { get; set; }
    public int PumpNumber { get; set; }
    public int FccPumpNumber { get; set; }
    public int? LayoutX { get; set; }
    public int? LayoutY { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public IReadOnlyList<ForecourtNozzleUpsertRequest> Nozzles { get; set; } = [];
}

public sealed class ForecourtNozzleUpsertRequest
{
    public Guid? Id { get; set; }
    public Guid ProductId { get; set; }
    public int NozzleNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class ProductView
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#000000";
    public decimal UnitPrice { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool IsActive { get; set; }
    public int AssignedNozzleCount { get; set; }
}

public sealed class ProductUpsertRequest
{
    public Guid LabEnvironmentId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#000000";
    public decimal UnitPrice { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
}

public sealed class SiteSeedRequest
{
    public bool ResetBeforeSeed { get; set; } = true;
    public bool IncludeCompletedPreAuth { get; set; } = true;
}

public sealed class SiteSeedResult
{
    public Guid SiteId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public bool ResetApplied { get; set; }
    public int TransactionsRemoved { get; set; }
    public int PreAuthSessionsRemoved { get; set; }
    public int CallbackAttemptsRemoved { get; set; }
    public int LogsRemoved { get; set; }
    public int TransactionsCreated { get; set; }
    public int PreAuthSessionsCreated { get; set; }
    public int CallbackAttemptsCreated { get; set; }
    public int NozzlesReset { get; set; }
}

public sealed class LabSeedResult
{
    public bool ResetApplied { get; set; }
    public int SiteCount { get; set; }
    public int ProfileCount { get; set; }
    public int ProductCount { get; set; }
    public int TransactionCount { get; set; }
}

public interface IVirtualLabManagementService
{
    Task<LabEnvironmentDetailView?> GetDefaultLabEnvironmentAsync(CancellationToken cancellationToken = default);
    Task<LabEnvironmentDetailView?> UpdateDefaultLabEnvironmentAsync(LabEnvironmentUpsertRequest request, CancellationToken cancellationToken = default);
    Task<LabEnvironmentPruneResult?> PruneDefaultLabEnvironmentAsync(LabEnvironmentPruneRequest request, CancellationToken cancellationToken = default);
    Task<LabEnvironmentExportPackage?> ExportDefaultLabEnvironmentAsync(LabEnvironmentExportRequest request, CancellationToken cancellationToken = default);
    Task<LabEnvironmentImportResult> ImportLabEnvironmentAsync(LabEnvironmentImportRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SiteListItemView>> ListSitesAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<SiteDetailView?> GetSiteAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<SiteDetailView> CreateSiteAsync(SiteUpsertRequest request, CancellationToken cancellationToken = default);
    Task<SiteDetailView?> UpdateSiteAsync(Guid siteId, SiteUpsertRequest request, CancellationToken cancellationToken = default);
    Task<SiteDetailView?> ArchiveSiteAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<SiteDetailView?> DuplicateSiteAsync(Guid siteId, DuplicateSiteRequest request, CancellationToken cancellationToken = default);
    Task<SiteForecourtView?> GetForecourtAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<SiteForecourtView?> SaveForecourtAsync(Guid siteId, SaveForecourtRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductView>> ListProductsAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<ProductView?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<ProductView> CreateProductAsync(ProductUpsertRequest request, CancellationToken cancellationToken = default);
    Task<ProductView?> UpdateProductAsync(Guid productId, ProductUpsertRequest request, CancellationToken cancellationToken = default);
    Task<ProductView?> ArchiveProductAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<SiteSeedResult?> SeedSiteAsync(Guid siteId, SiteSeedRequest request, CancellationToken cancellationToken = default);
    Task<SiteSeedResult?> ResetSiteAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<LabSeedResult> SeedLabAsync(bool reset, CancellationToken cancellationToken = default);
}
