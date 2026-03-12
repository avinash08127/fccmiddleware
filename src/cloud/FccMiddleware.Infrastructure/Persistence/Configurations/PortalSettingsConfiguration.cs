using FccMiddleware.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FccMiddleware.Infrastructure.Persistence.Configurations;

internal sealed class PortalSettingsConfiguration : IEntityTypeConfiguration<PortalSettings>
{
    public static readonly Guid SingletonId = new("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset SeededAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string GlobalDefaultsJson = """
        {"tolerance":{"amountTolerancePercent":5.0,"amountToleranceAbsoluteMinorUnits":500,"timeWindowMinutes":60,"stalePendingThresholdDays":7},"retention":{"archiveRetentionMonths":84,"outboxCleanupDays":7,"rawPayloadRetentionDays":30,"auditEventRetentionDays":90,"deadLetterRetentionDays":30}}
        """;

    private const string AlertConfigurationJson = """
        {"thresholds":[{"alertKey":"offline_agents_hours","label":"Edge agent offline","threshold":2,"unit":"hours","evaluationWindowMinutes":120},{"alertKey":"dlq_depth","label":"Dead-letter depth","threshold":1,"unit":"items","evaluationWindowMinutes":15},{"alertKey":"stale_transactions","label":"Stale pending transactions","threshold":10,"unit":"items","evaluationWindowMinutes":60},{"alertKey":"reconciliation_exceptions","label":"Reconciliation exceptions","threshold":10,"unit":"items","evaluationWindowMinutes":60}],"emailRecipientsHigh":[],"emailRecipientsCritical":[],"renotifyIntervalHours":4,"autoResolveHealthyCount":3}
        """;

    public void Configure(EntityTypeBuilder<PortalSettings> builder)
    {
        builder.ToTable("portal_settings");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.GlobalDefaultsJson)
            .HasColumnName("global_defaults_json")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(e => e.AlertConfigurationJson)
            .HasColumnName("alert_configuration_json")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200);

        builder.HasData(new PortalSettings
        {
            Id = SingletonId,
            GlobalDefaultsJson = GlobalDefaultsJson,
            AlertConfigurationJson = AlertConfigurationJson,
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
            UpdatedBy = "seed"
        });
    }
}
