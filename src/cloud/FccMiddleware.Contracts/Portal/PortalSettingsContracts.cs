namespace FccMiddleware.Contracts.Portal;

public sealed record SystemSettingsDto
{
    public required GlobalDefaultsDto GlobalDefaults { get; init; }
    public required IReadOnlyList<LegalEntityOverrideDto> LegalEntityOverrides { get; init; }
    public required AlertConfigurationDto Alerts { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record GlobalDefaultsDto
{
    public required ToleranceDefaultsDto Tolerance { get; init; }
    public required RetentionDefaultsDto Retention { get; init; }
}

public sealed record ToleranceDefaultsDto
{
    public required decimal AmountTolerancePercent { get; init; }
    public required long AmountToleranceAbsoluteMinorUnits { get; init; }
    public required int TimeWindowMinutes { get; init; }
    public required int StalePendingThresholdDays { get; init; }
}

public sealed record RetentionDefaultsDto
{
    public required int ArchiveRetentionMonths { get; init; }
    public required int OutboxCleanupDays { get; init; }
    public required int RawPayloadRetentionDays { get; init; }
    public required int AuditEventRetentionDays { get; init; }
    public required int DeadLetterRetentionDays { get; init; }
}

public sealed record LegalEntityOverrideDto
{
    public required Guid LegalEntityId { get; init; }
    public required string LegalEntityName { get; init; }
    public required string LegalEntityCode { get; init; }
    public decimal? AmountTolerancePercent { get; init; }
    public long? AmountToleranceAbsoluteMinorUnits { get; init; }
    public int? TimeWindowMinutes { get; init; }
    public int? StalePendingThresholdDays { get; init; }
}

public sealed record AlertConfigurationDto
{
    public required IReadOnlyList<AlertThresholdDto> Thresholds { get; init; }
    public required IReadOnlyList<string> EmailRecipientsHigh { get; init; }
    public required IReadOnlyList<string> EmailRecipientsCritical { get; init; }
    public required int RenotifyIntervalHours { get; init; }
    public required int AutoResolveHealthyCount { get; init; }
}

public sealed record AlertThresholdDto
{
    public required string AlertKey { get; init; }
    public required string Label { get; init; }
    public required decimal Threshold { get; init; }
    public required string Unit { get; init; }
    public required int EvaluationWindowMinutes { get; init; }
}

public sealed record UpdateGlobalDefaultsRequestDto
{
    public required PartialToleranceDefaultsDto Tolerance { get; init; }
    public required PartialRetentionDefaultsDto Retention { get; init; }
}

public sealed record PartialToleranceDefaultsDto
{
    public decimal? AmountTolerancePercent { get; init; }
    public long? AmountToleranceAbsoluteMinorUnits { get; init; }
    public int? TimeWindowMinutes { get; init; }
    public int? StalePendingThresholdDays { get; init; }
}

public sealed record PartialRetentionDefaultsDto
{
    public int? ArchiveRetentionMonths { get; init; }
    public int? OutboxCleanupDays { get; init; }
    public int? RawPayloadRetentionDays { get; init; }
    public int? AuditEventRetentionDays { get; init; }
    public int? DeadLetterRetentionDays { get; init; }
}

public sealed record UpsertLegalEntityOverrideRequestDto
{
    public required Guid LegalEntityId { get; init; }
    public decimal? AmountTolerancePercent { get; init; }
    public long? AmountToleranceAbsoluteMinorUnits { get; init; }
    public int? TimeWindowMinutes { get; init; }
    public int? StalePendingThresholdDays { get; init; }
}

public sealed record UpdateAlertConfigurationRequestDto
{
    public required IReadOnlyList<AlertThresholdPatchDto> Thresholds { get; init; }
    public required IReadOnlyList<string> EmailRecipientsHigh { get; init; }
    public required IReadOnlyList<string> EmailRecipientsCritical { get; init; }
    public required int RenotifyIntervalHours { get; init; }
    public required int AutoResolveHealthyCount { get; init; }
}

public sealed record AlertThresholdPatchDto
{
    public required string AlertKey { get; init; }
    public required decimal Threshold { get; init; }
    public required int EvaluationWindowMinutes { get; init; }
}
