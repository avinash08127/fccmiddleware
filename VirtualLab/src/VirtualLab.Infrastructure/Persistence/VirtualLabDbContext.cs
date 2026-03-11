using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VirtualLab.Domain.Models;

namespace VirtualLab.Infrastructure.Persistence;

public sealed class VirtualLabDbContext(DbContextOptions<VirtualLabDbContext> options) : DbContext(options)
{
    public DbSet<LabEnvironment> LabEnvironments => Set<LabEnvironment>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<FccSimulatorProfile> FccSimulatorProfiles => Set<FccSimulatorProfile>();
    public DbSet<Pump> Pumps => Set<Pump>();
    public DbSet<Nozzle> Nozzles => Set<Nozzle>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PreAuthSession> PreAuthSessions => Set<PreAuthSession>();
    public DbSet<SimulatedTransaction> SimulatedTransactions => Set<SimulatedTransaction>();
    public DbSet<CallbackTarget> CallbackTargets => Set<CallbackTarget>();
    public DbSet<CallbackAttempt> CallbackAttempts => Set<CallbackAttempt>();
    public DbSet<LabEventLog> LabEventLogs => Set<LabEventLog>();
    public DbSet<ScenarioDefinition> ScenarioDefinitions => Set<ScenarioDefinition>();
    public DbSet<ScenarioRun> ScenarioRuns => Set<ScenarioRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VirtualLabDbContext).Assembly);

        ValueConverter<DateTimeOffset, DateTime> dateTimeOffsetConverter =
            new(value => value.UtcDateTime, value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        ValueConverter<DateTimeOffset?, DateTime?> nullableDateTimeOffsetConverter =
            new(
                value => value.HasValue ? value.Value.UtcDateTime : null,
                value => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null);

        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateTimeOffsetConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }
    }
}
