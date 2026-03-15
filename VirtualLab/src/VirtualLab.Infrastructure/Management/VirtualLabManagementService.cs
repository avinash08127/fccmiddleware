using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Application.Management;
using VirtualLab.Application.Observability;
using VirtualLab.Domain.Diagnostics;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Domain.Profiles;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;

namespace VirtualLab.Infrastructure.Management;

public sealed class VirtualLabManagementService(
    VirtualLabDbContext dbContext,
    IFccProfileService fccProfileService,
    IVirtualLabSeedService seedService,
    IVirtualLabTelemetry telemetry) : IVirtualLabManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<LabEnvironmentDetailView?> GetDefaultLabEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        LabEnvironment? environment = await dbContext.LabEnvironments
            .AsNoTracking()
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return environment is null ? null : MapEnvironmentDetail(environment);
    }

    public async Task<LabEnvironmentDetailView?> UpdateDefaultLabEnvironmentAsync(
        LabEnvironmentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        LabEnvironment? environment = await dbContext.LabEnvironments
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (environment is null)
        {
            return null;
        }

        environment.Name = string.IsNullOrWhiteSpace(request.Name) ? environment.Name : request.Name.Trim();
        environment.Description = string.IsNullOrWhiteSpace(request.Description) ? environment.Description : request.Description.Trim();
        environment.SettingsJson = SerializeEnvironmentSettings(request.Settings);
        environment.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapEnvironmentDetail(environment);
    }

    public async Task<LabEnvironmentPruneResult?> PruneDefaultLabEnvironmentAsync(
        LabEnvironmentPruneRequest request,
        CancellationToken cancellationToken = default)
    {
        LabEnvironment? environment = await dbContext.LabEnvironments
            .AsTracking()
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (environment is null)
        {
            return null;
        }

        LabEnvironmentSettingsView settings = DeserializeEnvironmentSettings(environment.SettingsJson);
        LabEnvironmentRetentionSettingsView retention = BuildEffectiveRetentionSettings(settings.Retention, request);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset logCutoffUtc = now.AddDays(-retention.LogRetentionDays);
        DateTimeOffset callbackCutoffUtc = now.AddDays(-retention.CallbackHistoryRetentionDays);
        DateTimeOffset transactionCutoffUtc = now.AddDays(-retention.TransactionRetentionDays);

        using IDisposable? activity = telemetry.StartEnvironmentOperation(
            "virtual-lab.environment.prune",
            environment.Key,
            settings.Telemetry.EmitActivities);

        List<Guid> siteIds = await dbContext.Sites
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        List<Guid> profileIds = await dbContext.FccSimulatorProfiles
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        SimulatedTransactionStatus[] terminalTransactionStatuses =
        [
            SimulatedTransactionStatus.Delivered,
            SimulatedTransactionStatus.Acknowledged,
            SimulatedTransactionStatus.Failed,
        ];

        PreAuthSessionStatus[] terminalPreAuthStatuses =
        [
            PreAuthSessionStatus.Completed,
            PreAuthSessionStatus.Cancelled,
            PreAuthSessionStatus.Expired,
            PreAuthSessionStatus.Failed,
        ];

        List<SimulatedTransaction> transactions = await dbContext.SimulatedTransactions
            .Where(x =>
                siteIds.Contains(x.SiteId) &&
                x.OccurredAtUtc < transactionCutoffUtc &&
                terminalTransactionStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);

        if (retention.PreserveTimelineIntegrity)
        {
            transactions = transactions.Where(x => x.ScenarioRunId is null).ToList();
        }

        HashSet<Guid> transactionIdsToDelete = transactions.Select(x => x.Id).ToHashSet();

        List<CallbackAttempt> callbackAttempts = await dbContext.CallbackAttempts
            .Where(x =>
                x.AttemptedAtUtc < callbackCutoffUtc &&
                transactionIdsToDelete.Contains(x.SimulatedTransactionId))
            .ToListAsync(cancellationToken);

        List<PreAuthSession> preAuthSessions = await dbContext.PreAuthSessions
            .Include(x => x.Transactions)
            .Where(x =>
                siteIds.Contains(x.SiteId) &&
                terminalPreAuthStatuses.Contains(x.Status) &&
                (x.CompletedAtUtc ?? x.ExpiresAtUtc ?? x.AuthorizedAtUtc ?? x.CreatedAtUtc) < transactionCutoffUtc)
            .ToListAsync(cancellationToken);

        if (retention.PreserveTimelineIntegrity)
        {
            preAuthSessions = preAuthSessions
                .Where(x => x.ScenarioRunId is null && x.Transactions.All(transaction => transactionIdsToDelete.Contains(transaction.Id)))
                .ToList();
        }

        HashSet<Guid> preAuthSessionIdsToDelete = preAuthSessions.Select(x => x.Id).ToHashSet();

        List<LabEventLog> logs = await dbContext.LabEventLogs
            .Where(x =>
                ((x.SiteId.HasValue && siteIds.Contains(x.SiteId.Value)) ||
                 (x.FccSimulatorProfileId.HasValue && profileIds.Contains(x.FccSimulatorProfileId.Value))) &&
                x.OccurredAtUtc < logCutoffUtc)
            .ToListAsync(cancellationToken);

        logs = logs.Where(log =>
        {
            if (log.SimulatedTransactionId.HasValue && transactionIdsToDelete.Contains(log.SimulatedTransactionId.Value))
            {
                return true;
            }

            if (log.PreAuthSessionId.HasValue && preAuthSessionIdsToDelete.Contains(log.PreAuthSessionId.Value))
            {
                return true;
            }

            if (!retention.PreserveTimelineIntegrity)
            {
                return true;
            }

            return !log.SimulatedTransactionId.HasValue && !log.PreAuthSessionId.HasValue && !log.ScenarioRunId.HasValue;
        }).ToList();

        int preservedScenarioRunCount = await dbContext.ScenarioRuns
            .AsNoTracking()
            .CountAsync(x => siteIds.Contains(x.SiteId), cancellationToken);

        LabEnvironmentPruneResult result = new()
        {
            LabEnvironmentId = environment.Id,
            EnvironmentKey = environment.Key,
            DryRun = request.DryRun,
            ExecutedAtUtc = now,
            LogCutoffUtc = logCutoffUtc,
            CallbackCutoffUtc = callbackCutoffUtc,
            TransactionCutoffUtc = transactionCutoffUtc,
            LogsRemoved = logs.Count,
            CallbackAttemptsRemoved = callbackAttempts.Count,
            TransactionsRemoved = transactions.Count,
            PreAuthSessionsRemoved = preAuthSessions.Count,
            ScenarioRunsPreserved = preservedScenarioRunCount,
        };

        if (!request.DryRun)
        {
            if (callbackAttempts.Count > 0)
            {
                dbContext.CallbackAttempts.RemoveRange(callbackAttempts);
            }

            if (logs.Count > 0)
            {
                dbContext.LabEventLogs.RemoveRange(logs);
            }

            if (transactions.Count > 0)
            {
                dbContext.SimulatedTransactions.RemoveRange(transactions);
            }

            if (preAuthSessions.Count > 0)
            {
                dbContext.PreAuthSessions.RemoveRange(preAuthSessions);
            }

            environment.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        telemetry.RecordEnvironmentPrune(
            environment.Key,
            result.TransactionsRemoved,
            result.CallbackAttemptsRemoved,
            result.LogsRemoved,
            result.DryRun,
            settings.Telemetry.EmitMetrics);

        return result;
    }

    public async Task<LabEnvironmentExportPackage?> ExportDefaultLabEnvironmentAsync(
        LabEnvironmentExportRequest request,
        CancellationToken cancellationToken = default)
    {
        LabEnvironment? environment = await dbContext.LabEnvironments
            .AsNoTracking()
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (environment is null)
        {
            return null;
        }

        LabEnvironmentSettingsView settings = DeserializeEnvironmentSettings(environment.SettingsJson);
        bool includeRuntimeData = request.IncludeRuntimeData ?? settings.Backup.IncludeRuntimeDataByDefault;

        using IDisposable? activity = telemetry.StartEnvironmentOperation(
            "virtual-lab.environment.export",
            environment.Key,
            settings.Telemetry.EmitActivities);

        List<Guid> siteIds = await dbContext.Sites
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        List<Guid> transactionIds = includeRuntimeData
            ? await dbContext.SimulatedTransactions
                .AsNoTracking()
                .Where(x => siteIds.Contains(x.SiteId))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken)
            : [];

        List<Guid> preAuthSessionIds = includeRuntimeData
            ? await dbContext.PreAuthSessions
                .AsNoTracking()
                .Where(x => siteIds.Contains(x.SiteId))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken)
            : [];

        List<Guid> scenarioRunIds = includeRuntimeData
            ? await dbContext.ScenarioRuns
                .AsNoTracking()
                .Where(x => siteIds.Contains(x.SiteId))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken)
            : [];

        List<FccSimulatorProfile> profiles = await dbContext.FccSimulatorProfiles
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .OrderBy(x => x.ProfileKey)
            .ToListAsync(cancellationToken);

        List<Product> products = await dbContext.Products
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .OrderBy(x => x.ProductCode)
            .ToListAsync(cancellationToken);

        List<Site> sites = await dbContext.Sites
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .OrderBy(x => x.SiteCode)
            .ToListAsync(cancellationToken);

        List<CallbackTarget> callbackTargets = await dbContext.CallbackTargets
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .OrderBy(x => x.TargetKey)
            .ToListAsync(cancellationToken);

        List<Pump> pumps = await dbContext.Pumps
            .AsNoTracking()
            .Where(x => siteIds.Contains(x.SiteId))
            .OrderBy(x => x.SiteId)
            .ThenBy(x => x.PumpNumber)
            .ToListAsync(cancellationToken);

        List<Nozzle> nozzles = await dbContext.Nozzles
            .AsNoTracking()
            .Where(x => siteIds.Contains(x.Pump.SiteId))
            .OrderBy(x => x.PumpId)
            .ThenBy(x => x.NozzleNumber)
            .ToListAsync(cancellationToken);

        List<ScenarioDefinition> scenarioDefinitions = await dbContext.ScenarioDefinitions
            .AsNoTracking()
            .Where(x => x.LabEnvironmentId == environment.Id)
            .OrderBy(x => x.ScenarioKey)
            .ToListAsync(cancellationToken);

        List<ScenarioRun> scenarioRuns = includeRuntimeData
            ? await dbContext.ScenarioRuns
                .AsNoTracking()
                .Where(x => siteIds.Contains(x.SiteId))
                .OrderBy(x => x.StartedAtUtc)
                .ToListAsync(cancellationToken)
            : [];

        List<PreAuthSession> exportedPreAuthSessions = includeRuntimeData
            ? await dbContext.PreAuthSessions
                .AsNoTracking()
                .Where(x => siteIds.Contains(x.SiteId))
                .OrderBy(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken)
            : [];

        List<SimulatedTransaction> exportedTransactions = includeRuntimeData
            ? await dbContext.SimulatedTransactions
                .AsNoTracking()
                .Where(x => siteIds.Contains(x.SiteId))
                .OrderBy(x => x.OccurredAtUtc)
                .ToListAsync(cancellationToken)
            : [];

        List<CallbackAttempt> exportedCallbackAttempts = includeRuntimeData
            ? await dbContext.CallbackAttempts
                .AsNoTracking()
                .Where(x => transactionIds.Contains(x.SimulatedTransactionId))
                .OrderBy(x => x.AttemptedAtUtc)
                .ToListAsync(cancellationToken)
            : [];

        List<LabEventLog> exportedLogs = includeRuntimeData
            ? await dbContext.LabEventLogs
                .AsNoTracking()
                .Where(x =>
                    (x.SiteId.HasValue && siteIds.Contains(x.SiteId.Value)) ||
                    (x.SimulatedTransactionId.HasValue && transactionIds.Contains(x.SimulatedTransactionId.Value)) ||
                    (x.PreAuthSessionId.HasValue && preAuthSessionIds.Contains(x.PreAuthSessionId.Value)) ||
                    (x.ScenarioRunId.HasValue && scenarioRunIds.Contains(x.ScenarioRunId.Value)))
                .OrderBy(x => x.OccurredAtUtc)
                .ToListAsync(cancellationToken)
            : [];

        LabEnvironmentExportPackage package = new()
        {
            FormatVersion = 1,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            IncludesRuntimeData = includeRuntimeData,
            Environment = MapEnvironmentSnapshot(environment),
            Profiles = profiles.Select(MapProfileSnapshot).ToArray(),
            Products = products.Select(MapProductSnapshot).ToArray(),
            Sites = sites.Select(MapSiteSnapshot).ToArray(),
            CallbackTargets = callbackTargets.Select(MapCallbackTargetSnapshot).ToArray(),
            Pumps = pumps.Select(MapPumpSnapshot).ToArray(),
            Nozzles = nozzles.Select(MapNozzleSnapshot).ToArray(),
            ScenarioDefinitions = scenarioDefinitions.Select(MapScenarioDefinitionSnapshot).ToArray(),
            ScenarioRuns = scenarioRuns.Select(MapScenarioRunSnapshot).ToArray(),
            PreAuthSessions = exportedPreAuthSessions.Select(MapPreAuthSessionSnapshot).ToArray(),
            Transactions = exportedTransactions.Select(MapTransactionSnapshot).ToArray(),
            CallbackAttempts = exportedCallbackAttempts.Select(MapCallbackAttemptSnapshot).ToArray(),
            Logs = exportedLogs.Select(MapLabEventLogSnapshot).ToArray(),
        };

        telemetry.RecordEnvironmentExport(
            environment.Key,
            package.Sites.Count,
            includeRuntimeData,
            settings.Telemetry.EmitMetrics);

        return package;
    }

    public async Task<LabEnvironmentImportResult> ImportLabEnvironmentAsync(
        LabEnvironmentImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Package.Environment.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Package.Environment.Key))
        {
            throw new ManagementOperationException(
                StatusCodes.Status400BadRequest,
                "Import package is missing environment metadata.",
                [new("package.environment", "Environment id and key are required.", "Error", "environment_package_invalid")]);
        }

        using IDisposable? activity = telemetry.StartEnvironmentOperation(
            "virtual-lab.environment.import",
            request.Package.Environment.Key,
            emitActivities: true);

        bool hasExistingEnvironment = await dbContext.LabEnvironments.AnyAsync(cancellationToken);
        if (hasExistingEnvironment && !request.ReplaceExisting)
        {
            throw new ManagementOperationException(
                StatusCodes.Status409Conflict,
                "Lab environment already exists.",
                [new("replaceExisting", "Set replaceExisting to true to overwrite current lab data.", "Error", "environment_exists")]);
        }

        if (request.ReplaceExisting)
        {
            await ResetEntireLabAsync(cancellationToken);
        }

        LabEnvironmentSnapshot environmentSnapshot = request.Package.Environment;
        LabEnvironment environment = new()
        {
            Id = environmentSnapshot.Id,
            Key = environmentSnapshot.Key.Trim(),
            Name = environmentSnapshot.Name.Trim(),
            Description = environmentSnapshot.Description.Trim(),
            SettingsJson = string.IsNullOrWhiteSpace(environmentSnapshot.SettingsJson) ? "{}" : environmentSnapshot.SettingsJson,
            SeedVersion = environmentSnapshot.SeedVersion,
            DeterministicSeed = environmentSnapshot.DeterministicSeed,
            CreatedAtUtc = environmentSnapshot.CreatedAtUtc,
            UpdatedAtUtc = environmentSnapshot.UpdatedAtUtc,
            LastSeededAtUtc = environmentSnapshot.LastSeededAtUtc,
        };

        dbContext.LabEnvironments.Add(environment);
        dbContext.FccSimulatorProfiles.AddRange(request.Package.Profiles.Select(MapProfileEntity));
        dbContext.Products.AddRange(request.Package.Products.Select(MapProductEntity));
        dbContext.Sites.AddRange(request.Package.Sites.Select(MapSiteEntity));
        dbContext.CallbackTargets.AddRange(request.Package.CallbackTargets.Select(MapCallbackTargetEntity));
        dbContext.Pumps.AddRange(request.Package.Pumps.Select(MapPumpEntity));
        dbContext.Nozzles.AddRange(request.Package.Nozzles.Select(MapNozzleEntity));
        dbContext.ScenarioDefinitions.AddRange(request.Package.ScenarioDefinitions.Select(MapScenarioDefinitionEntity));
        dbContext.ScenarioRuns.AddRange(request.Package.ScenarioRuns.Select(MapScenarioRunEntity));
        dbContext.PreAuthSessions.AddRange(request.Package.PreAuthSessions.Select(MapPreAuthSessionEntity));
        dbContext.SimulatedTransactions.AddRange(request.Package.Transactions.Select(MapSimulatedTransactionEntity));
        dbContext.CallbackAttempts.AddRange(request.Package.CallbackAttempts.Select(MapCallbackAttemptEntity));
        dbContext.LabEventLogs.AddRange(request.Package.Logs.Select(MapLabEventLogEntity));

        await dbContext.SaveChangesAsync(cancellationToken);

        LabEnvironmentSettingsView settings = DeserializeEnvironmentSettings(environment.SettingsJson);
        telemetry.RecordEnvironmentImport(
            environment.Key,
            request.Package.Sites.Count,
            request.ReplaceExisting,
            settings.Telemetry.EmitMetrics);

        return new LabEnvironmentImportResult
        {
            LabEnvironmentId = environment.Id,
            EnvironmentKey = environment.Key,
            ReplaceExisting = request.ReplaceExisting,
            SiteCount = request.Package.Sites.Count,
            ProfileCount = request.Package.Profiles.Count,
            ProductCount = request.Package.Products.Count,
            ScenarioDefinitionCount = request.Package.ScenarioDefinitions.Count,
            ScenarioRunCount = request.Package.ScenarioRuns.Count,
            TransactionCount = request.Package.Transactions.Count,
            PreAuthSessionCount = request.Package.PreAuthSessions.Count,
            CallbackAttemptCount = request.Package.CallbackAttempts.Count,
            LogCount = request.Package.Logs.Count,
        };
    }

    public async Task<IReadOnlyList<SiteListItemView>> ListSitesAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        IQueryable<Site> query = dbContext.Sites
            .AsNoTracking()
            .Include(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pumps)
                .ThenInclude(x => x.Nozzles)
            .Include(x => x.CallbackTargets)
            .OrderBy(x => x.SiteCode);

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        List<Site> sites = await query.ToListAsync(cancellationToken);
        return sites.Select(CreateSiteSummary).ToList();
    }

    public async Task<SiteDetailView?> GetSiteAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        Site? site = await LoadSiteAsync(siteId, asNoTracking: true, cancellationToken);
        if (site is null)
        {
            return null;
        }

        IReadOnlyList<FccProfileSummary> availableProfiles = await fccProfileService.ListAsync(cancellationToken);
        return CreateSiteDetail(site, availableProfiles);
    }

    public async Task<SiteDetailView> CreateSiteAsync(SiteUpsertRequest request, CancellationToken cancellationToken = default)
    {
        Guid environmentId = await ResolveEnvironmentIdAsync(request.LabEnvironmentId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Site site = new()
        {
            Id = Guid.NewGuid(),
            LabEnvironmentId = environmentId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        ApplySiteRequest(site, request, environmentId, now);
        await ValidateSiteRequestAsync(site, site.Id, request.CallbackTargets, cancellationToken);

        dbContext.Sites.Add(site);
        if (request.CallbackTargets is { Count: > 0 })
        {
            dbContext.CallbackTargets.AddRange(BuildCallbackTargets(site, request.CallbackTargets, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetSiteAsync(site.Id, cancellationToken)
            ?? throw new InvalidOperationException("Created site could not be reloaded.");
    }

    public async Task<SiteDetailView?> UpdateSiteAsync(Guid siteId, SiteUpsertRequest request, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .Include(x => x.CallbackTargets)
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);

        if (site is null)
        {
            return null;
        }

        Guid environmentId = request.LabEnvironmentId == Guid.Empty ? site.LabEnvironmentId : request.LabEnvironmentId;
        environmentId = await ResolveEnvironmentIdAsync(environmentId, cancellationToken);

        ApplySiteRequest(site, request, environmentId, DateTimeOffset.UtcNow);
        await ValidateSiteRequestAsync(site, site.Id, request.CallbackTargets, cancellationToken);
        if (request.CallbackTargets is not null)
        {
            SyncCallbackTargets(site, request.CallbackTargets, DateTimeOffset.UtcNow);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetSiteAsync(siteId, cancellationToken);
    }

    public async Task<SiteDetailView?> ArchiveSiteAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites.SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);
        if (site is null)
        {
            return null;
        }

        site.IsActive = false;
        site.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetSiteAsync(siteId, cancellationToken);
    }

    public async Task<SiteDetailView?> DuplicateSiteAsync(Guid siteId, DuplicateSiteRequest request, CancellationToken cancellationToken = default)
    {
        Site? source = await dbContext.Sites
            .Include(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pumps.OrderBy(p => p.PumpNumber))
                .ThenInclude(x => x.Nozzles.OrderBy(n => n.NozzleNumber))
                    .ThenInclude(x => x.Product)
            .Include(x => x.CallbackTargets)
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);

        if (source is null)
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        SiteSettingsView sourceSettings = DeserializeSettings(source.SettingsJson);
        SiteSettingsView newSettings = CloneSettings(sourceSettings);
        newSettings.IsTemplate = request.MarkAsTemplate;

        Site duplicate = new()
        {
            Id = Guid.NewGuid(),
            LabEnvironmentId = source.LabEnvironmentId,
            ActiveFccSimulatorProfileId = request.ActiveFccSimulatorProfileId ?? source.ActiveFccSimulatorProfileId,
            SiteCode = request.SiteCode.Trim(),
            Name = request.Name.Trim(),
            TimeZone = source.TimeZone,
            CurrencyCode = source.CurrencyCode,
            ExternalReference = string.IsNullOrWhiteSpace(request.ExternalReference) ? $"{source.ExternalReference}-copy" : request.ExternalReference.Trim(),
            InboundAuthMode = source.InboundAuthMode,
            ApiKeyHeaderName = source.ApiKeyHeaderName,
            ApiKeyValue = source.ApiKeyValue,
            BasicAuthUsername = source.BasicAuthUsername,
            BasicAuthPassword = source.BasicAuthPassword,
            DeliveryMode = source.DeliveryMode,
            PreAuthMode = source.PreAuthMode,
            FccVendor = source.FccVendor,
            IsActive = request.Activate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        Dictionary<string, string> callbackKeyMap = new(StringComparer.OrdinalIgnoreCase);
        List<CallbackTarget> copiedTargets = [];

        if (request.CopyCallbackTargets)
        {
            int index = 1;
            foreach (CallbackTarget target in source.CallbackTargets.OrderBy(x => x.Name))
            {
                string newKey = await CreateUniqueCallbackTargetKeyAsync(request.SiteCode, index++, cancellationToken);
                callbackKeyMap[target.TargetKey] = newKey;
                copiedTargets.Add(new CallbackTarget
                {
                    Id = Guid.NewGuid(),
                    LabEnvironmentId = source.LabEnvironmentId,
                    SiteId = duplicate.Id,
                    TargetKey = newKey,
                    Name = target.Name,
                    CallbackUrl = target.CallbackUrl,
                    AuthMode = target.AuthMode,
                    ApiKeyHeaderName = target.ApiKeyHeaderName,
                    ApiKeyValue = target.ApiKeyValue,
                    BasicAuthUsername = target.BasicAuthUsername,
                    BasicAuthPassword = target.BasicAuthPassword,
                    IsActive = target.IsActive,
                    CreatedAtUtc = now,
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceSettings.DefaultCallbackTargetKey) &&
            callbackKeyMap.TryGetValue(sourceSettings.DefaultCallbackTargetKey, out string? duplicatedDefaultKey))
        {
            newSettings.DefaultCallbackTargetKey = duplicatedDefaultKey;
        }
        else if (!request.CopyCallbackTargets)
        {
            newSettings.DefaultCallbackTargetKey = string.Empty;
        }

        duplicate.SettingsJson = SerializeSettings(newSettings);

        List<Pump> copiedPumps = [];
        List<Nozzle> copiedNozzles = [];
        if (request.CopyForecourt)
        {
            foreach (Pump sourcePump in source.Pumps.OrderBy(x => x.PumpNumber))
            {
                Pump pump = new()
                {
                    Id = Guid.NewGuid(),
                    SiteId = duplicate.Id,
                    PumpNumber = sourcePump.PumpNumber,
                    FccPumpNumber = sourcePump.FccPumpNumber,
                    LayoutX = sourcePump.LayoutX,
                    LayoutY = sourcePump.LayoutY,
                    Label = sourcePump.Label,
                    IsActive = sourcePump.IsActive,
                    CreatedAtUtc = now,
                };

                copiedPumps.Add(pump);

                foreach (Nozzle sourceNozzle in sourcePump.Nozzles.OrderBy(x => x.NozzleNumber))
                {
                    copiedNozzles.Add(new Nozzle
                    {
                        Id = Guid.NewGuid(),
                        PumpId = pump.Id,
                        ProductId = sourceNozzle.ProductId,
                        NozzleNumber = sourceNozzle.NozzleNumber,
                        FccNozzleNumber = sourceNozzle.FccNozzleNumber,
                        Label = sourceNozzle.Label,
                        State = NozzleState.Idle,
                        SimulationStateJson = "{}",
                        IsActive = sourceNozzle.IsActive,
                        UpdatedAtUtc = now,
                    });
                }
            }
        }

        await ValidateSiteAsync(
            duplicate,
            duplicate.Id,
            copiedTargets.Select(x => x.TargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        dbContext.Sites.Add(duplicate);
        if (copiedTargets.Count > 0)
        {
            dbContext.CallbackTargets.AddRange(copiedTargets);
        }

        if (copiedPumps.Count > 0)
        {
            dbContext.Pumps.AddRange(copiedPumps);
        }

        if (copiedNozzles.Count > 0)
        {
            dbContext.Nozzles.AddRange(copiedNozzles);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetSiteAsync(duplicate.Id, cancellationToken);
    }

    public async Task<SiteForecourtView?> GetForecourtAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .AsNoTracking()
            .Include(x => x.Pumps.OrderBy(p => p.PumpNumber))
                .ThenInclude(x => x.Nozzles.OrderBy(n => n.NozzleNumber))
                    .ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);

        return site is null ? null : MapForecourt(site);
    }

    public async Task<SiteForecourtView?> SaveForecourtAsync(Guid siteId, SaveForecourtRequest request, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .Include(x => x.Pumps)
                .ThenInclude(x => x.Nozzles)
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);

        if (site is null)
        {
            return null;
        }

        List<ManagementValidationMessage> messages = ValidateForecourtRequest(request);
        HashSet<Guid> productIds = request.Pumps
            .SelectMany(x => x.Nozzles)
            .Select(x => x.ProductId)
            .Where(x => x != Guid.Empty)
            .ToHashSet();

        Dictionary<Guid, Product> products = await dbContext.Products
            .Where(x => productIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        foreach ((ForecourtPumpUpsertRequest pump, int pumpIndex) in request.Pumps.Select((value, index) => (value, index)))
        {
            foreach ((ForecourtNozzleUpsertRequest nozzle, int nozzleIndex) in pump.Nozzles.Select((value, index) => (value, index)))
            {
                if (!products.TryGetValue(nozzle.ProductId, out Product? product))
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].productId", "The selected product was not found.", "Error", "product_not_found"));
                    continue;
                }

                if (product.LabEnvironmentId != site.LabEnvironmentId)
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].productId", $"Product '{product.ProductCode}' belongs to a different lab environment.", "Error", "product_environment_mismatch"));
                }

                if (!product.IsActive)
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].productId", $"Product '{product.ProductCode}' is inactive and cannot be assigned to a nozzle.", "Error", "product_inactive"));
                }
            }
        }

        if (messages.Any(x => x.Severity == "Error"))
        {
            throw new ManagementOperationException(400, "Forecourt validation failed.", messages);
        }

        List<Pump> existingPumps = site.Pumps.ToList();
        HashSet<Guid> retainedPumpIds = [];
        List<Nozzle> removedNozzles = [];

        foreach ((ForecourtPumpUpsertRequest pumpRequest, int pumpOrdinal) in request.Pumps
                     .OrderBy(x => x.PumpNumber)
                     .Select((value, index) => (value, index)))
        {
            Pump? pump = MatchPump(existingPumps, pumpRequest);
            if (pump is null)
            {
                (int layoutX, int layoutY) = GetDefaultPumpLayout(pumpOrdinal);
                pump = new Pump
                {
                    Id = Guid.NewGuid(),
                    SiteId = site.Id,
                    LayoutX = pumpRequest.LayoutX ?? layoutX,
                    LayoutY = pumpRequest.LayoutY ?? layoutY,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                site.Pumps.Add(pump);
            }

            retainedPumpIds.Add(pump.Id);
            pump.PumpNumber = pumpRequest.PumpNumber;
            pump.FccPumpNumber = pumpRequest.FccPumpNumber;
            pump.LayoutX = pumpRequest.LayoutX ?? pump.LayoutX;
            pump.LayoutY = pumpRequest.LayoutY ?? pump.LayoutY;
            pump.Label = string.IsNullOrWhiteSpace(pumpRequest.Label) ? $"Pump {pumpRequest.PumpNumber}" : pumpRequest.Label.Trim();
            pump.IsActive = pumpRequest.IsActive;

            List<Nozzle> existingNozzles = pump.Nozzles.ToList();
            HashSet<Guid> retainedNozzleIds = [];

            foreach (ForecourtNozzleUpsertRequest nozzleRequest in pumpRequest.Nozzles.OrderBy(x => x.NozzleNumber))
            {
                Nozzle? nozzle = MatchNozzle(existingNozzles, nozzleRequest);
                if (nozzle is null)
                {
                    nozzle = new Nozzle
                    {
                        Id = Guid.NewGuid(),
                        PumpId = pump.Id,
                        State = NozzleState.Idle,
                        SimulationStateJson = "{}",
                    };
                    pump.Nozzles.Add(nozzle);
                }

                retainedNozzleIds.Add(nozzle.Id);
                nozzle.ProductId = nozzleRequest.ProductId;
                nozzle.NozzleNumber = nozzleRequest.NozzleNumber;
                nozzle.FccNozzleNumber = nozzleRequest.FccNozzleNumber;
                nozzle.Label = string.IsNullOrWhiteSpace(nozzleRequest.Label)
                    ? $"P{pumpRequest.PumpNumber}-N{nozzleRequest.NozzleNumber}"
                    : nozzleRequest.Label.Trim();
                nozzle.IsActive = nozzleRequest.IsActive;
                nozzle.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            removedNozzles.AddRange(existingNozzles.Where(x => !retainedNozzleIds.Contains(x.Id)));
        }

        List<Pump> removedPumps = existingPumps.Where(x => !retainedPumpIds.Contains(x.Id)).ToList();
        removedNozzles.AddRange(removedPumps.SelectMany(x => x.Nozzles));

        await ValidateForecourtRemovalsAsync(removedPumps, removedNozzles, cancellationToken);

        if (removedNozzles.Count > 0)
        {
            dbContext.Nozzles.RemoveRange(removedNozzles.DistinctBy(x => x.Id));
        }

        if (removedPumps.Count > 0)
        {
            dbContext.Pumps.RemoveRange(removedPumps);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetForecourtAsync(siteId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductView>> ListProductsAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        IQueryable<Product> query = dbContext.Products
            .AsNoTracking()
            .Include(x => x.Nozzles)
            .OrderBy(x => x.ProductCode);

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .Select(x => new ProductView
            {
                Id = x.Id,
                LabEnvironmentId = x.LabEnvironmentId,
                ProductCode = x.ProductCode,
                Name = x.Name,
                Grade = x.Grade,
                ColorHex = x.ColorHex,
                UnitPrice = x.UnitPrice,
                CurrencyCode = x.CurrencyCode,
                IsActive = x.IsActive,
                AssignedNozzleCount = x.Nozzles.Count,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductView?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Products
            .AsNoTracking()
            .Where(x => x.Id == productId)
            .Select(x => new ProductView
            {
                Id = x.Id,
                LabEnvironmentId = x.LabEnvironmentId,
                ProductCode = x.ProductCode,
                Name = x.Name,
                Grade = x.Grade,
                ColorHex = x.ColorHex,
                UnitPrice = x.UnitPrice,
                CurrencyCode = x.CurrencyCode,
                IsActive = x.IsActive,
                AssignedNozzleCount = x.Nozzles.Count,
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ProductView> CreateProductAsync(ProductUpsertRequest request, CancellationToken cancellationToken = default)
    {
        Guid environmentId = await ResolveEnvironmentIdAsync(request.LabEnvironmentId, cancellationToken);
        await ValidateProductAsync(request, null, environmentId, cancellationToken);

        Product product = new()
        {
            Id = Guid.NewGuid(),
            LabEnvironmentId = environmentId,
            ProductCode = request.ProductCode.Trim(),
            Name = request.Name.Trim(),
            Grade = request.Grade.Trim(),
            ColorHex = NormalizeColorHex(request.ColorHex),
            UnitPrice = request.UnitPrice,
            CurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant(),
            IsActive = request.IsActive,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductAsync(product.Id, cancellationToken)
            ?? throw new InvalidOperationException("Created product could not be reloaded.");
    }

    public async Task<ProductView?> UpdateProductAsync(Guid productId, ProductUpsertRequest request, CancellationToken cancellationToken = default)
    {
        Product? product = await dbContext.Products.SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (product is null)
        {
            return null;
        }

        Guid environmentId = request.LabEnvironmentId == Guid.Empty ? product.LabEnvironmentId : request.LabEnvironmentId;
        await ValidateProductAsync(request, productId, environmentId, cancellationToken);

        product.LabEnvironmentId = environmentId;
        product.ProductCode = request.ProductCode.Trim();
        product.Name = request.Name.Trim();
        product.Grade = request.Grade.Trim();
        product.ColorHex = NormalizeColorHex(request.ColorHex);
        product.UnitPrice = request.UnitPrice;
        product.CurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant();
        product.IsActive = request.IsActive;
        product.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductAsync(productId, cancellationToken);
    }

    public async Task<ProductView?> ArchiveProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        Product? product = await dbContext.Products
            .Include(x => x.Nozzles)
            .SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);

        if (product is null)
        {
            return null;
        }

        if (product.Nozzles.Any(x => x.IsActive))
        {
            throw new ManagementOperationException(
                409,
                "Product cannot be archived while it is assigned to active nozzles.",
                [new("productId", $"Product '{product.ProductCode}' is assigned to active nozzle mappings.", "Error", "product_in_use")]);
        }

        product.IsActive = false;
        product.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProductAsync(productId, cancellationToken);
    }

    public async Task<SiteSeedResult?> SeedSiteAsync(Guid siteId, SiteSeedRequest request, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .Include(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pumps.OrderBy(p => p.PumpNumber))
                .ThenInclude(x => x.Nozzles.OrderBy(n => n.NozzleNumber))
            .Include(x => x.CallbackTargets)
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);

        if (site is null)
        {
            return null;
        }

        SiteSeedResult result = request.ResetBeforeSeed
            ? await ResetSiteInternalAsync(site, cancellationToken)
            : CreateSeedResult(site);

        Pump? pump = site.Pumps.Where(x => x.IsActive).OrderBy(x => x.PumpNumber).FirstOrDefault();
        Nozzle? nozzle = pump?.Nozzles.Where(x => x.IsActive).OrderBy(x => x.NozzleNumber).FirstOrDefault();
        if (pump is null || nozzle is null)
        {
            throw new ManagementOperationException(
                409,
                "Site seed requires at least one active pump and nozzle.",
                [new("forecourt", $"Site '{site.SiteCode}' does not have an active pump/nozzle pair to seed.", "Error", "forecourt_missing")]);
        }

        Product product = await dbContext.Products.SingleAsync(x => x.Id == nozzle.ProductId, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string correlationToken = Guid.NewGuid().ToString("N")[..10];
        string preAuthToken = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string transactionToken = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string cursorToken = Guid.NewGuid().ToString("N")[..6];
        string correlationId = $"seed-{site.SiteCode.ToLowerInvariant()}-{correlationToken}";
        PreAuthSession? preAuthSession = null;

        if (request.IncludeCompletedPreAuth)
        {
            preAuthSession = new PreAuthSession
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                PumpId = pump.Id,
                NozzleId = nozzle.Id,
                CorrelationId = correlationId,
                ExternalReference = $"PA-{preAuthToken}",
                Mode = FccProfileService.ToRecord(site.ActiveFccSimulatorProfile).Contract.PreAuthMode,
                Status = PreAuthSessionStatus.Completed,
                ReservedAmount = 15000m,
                AuthorizedAmount = 15000m,
                FinalAmount = 9725.5m,
                FinalVolume = 42.113m,
                RawRequestJson = Serialize(new { correlationId, pump = pump.PumpNumber, nozzle = nozzle.NozzleNumber, amount = 15000m }),
                CanonicalRequestJson = Serialize(new
                {
                    siteCode = site.SiteCode,
                    preAuthId = $"PA-{preAuthToken}",
                    correlationId,
                    pumpNumber = pump.PumpNumber,
                    nozzleNumber = nozzle.NozzleNumber,
                    productCode = product.ProductCode,
                    requestedAmountMinorUnits = 1500000,
                    unitPriceMinorPerLitre = decimal.ToInt64(decimal.Round(product.UnitPrice * 100m, 0, MidpointRounding.AwayFromZero)),
                    currencyCode = product.CurrencyCode,
                    status = "COMPLETED",
                    requestedAt = now.AddSeconds(-5),
                    expiresAt = now.AddMinutes(5),
                }),
                RawResponseJson = Serialize(new { status = "completed", preauthId = $"PA-{preAuthToken}", correlationId, expiresAtUtc = now.AddMinutes(5) }),
                CanonicalResponseJson = Serialize(new { status = "COMPLETED", preAuthId = $"PA-{preAuthToken}", correlationId, expiresAtUtc = now.AddMinutes(5), authorizedAmountMinorUnits = 1500000, finalAmountMinorUnits = 972550, finalVolumeMillilitres = 42113 }),
                TimelineJson = Serialize(new[]
                {
                    new { eventType = "StateTransition", status = "PENDING", occurredAtUtc = now.AddSeconds(-5) },
                    new { eventType = "StateTransition", status = "AUTHORIZED", occurredAtUtc = now.AddSeconds(-4) },
                    new { eventType = "StateTransition", status = "COMPLETED", occurredAtUtc = now.AddSeconds(-1) },
                }),
                CreatedAtUtc = now.AddSeconds(-5),
                AuthorizedAtUtc = now.AddSeconds(-4),
                CompletedAtUtc = now.AddSeconds(-1),
                ExpiresAtUtc = now.AddMinutes(5),
            };

            dbContext.PreAuthSessions.Add(preAuthSession);
            result.PreAuthSessionsCreated++;
        }

        SimulatedTransactionStatus transactionStatus = site.DeliveryMode == TransactionDeliveryMode.Pull
            ? SimulatedTransactionStatus.ReadyForDelivery
            : SimulatedTransactionStatus.Delivered;

        SimulatedTransaction transaction = new()
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            PumpId = pump.Id,
            NozzleId = nozzle.Id,
            ProductId = product.Id,
            PreAuthSessionId = preAuthSession?.Id,
            CorrelationId = correlationId,
            ExternalTransactionId = $"TX-{transactionToken}",
            DeliveryMode = site.DeliveryMode,
            Status = transactionStatus,
            Volume = 42.113m,
            UnitPrice = product.UnitPrice,
            TotalAmount = 9725.5m,
            OccurredAtUtc = now,
            CreatedAtUtc = now,
            DeliveredAtUtc = transactionStatus == SimulatedTransactionStatus.Delivered ? now : null,
            RawPayloadJson = Serialize(new
            {
                transactionId = $"TX-{transactionToken}",
                correlationId,
                siteCode = site.SiteCode,
                pumpNumber = pump.PumpNumber,
                nozzleNumber = nozzle.NozzleNumber,
                productCode = product.ProductCode,
                productName = product.Name,
                volume = 42.113m,
                amount = 9725.5m,
                unitPrice = product.UnitPrice,
                currencyCode = product.CurrencyCode,
                occurredAtUtc = now,
                preAuthId = preAuthSession?.ExternalReference,
            }),
            CanonicalPayloadJson = Serialize(new
            {
                fccTransactionId = $"TX-{transactionToken}",
                correlationId,
                siteCode = site.SiteCode,
                pumpNumber = pump.PumpNumber,
                nozzleNumber = nozzle.NozzleNumber,
                productCode = product.ProductCode,
                volumeMicrolitres = 42113000,
                amountMinorUnits = 972550,
                unitPriceMinorPerLitre = decimal.ToInt64(decimal.Round(product.UnitPrice * 100m, 0, MidpointRounding.AwayFromZero)),
                currencyCode = product.CurrencyCode,
                startedAt = now.AddSeconds(-5),
                completedAt = now,
                fccVendor = site.ActiveFccSimulatorProfile.VendorFamily.ToUpperInvariant(),
                status = "PENDING",
                preAuthId = preAuthSession?.ExternalReference,
                schemaVersion = 1,
                source = "management-api",
            }),
            RawHeadersJson = """{"content-type":"application/json"}""",
            DeliveryCursor = $"{now:yyyyMMddHHmmssfff}-{cursorToken}",
            MetadataJson = Serialize(new { seeded = true, source = "management-api" }),
            TimelineJson = Serialize(new[]
            {
                new { eventType = "TransactionGenerated", occurredAtUtc = now },
            }),
        };

        dbContext.SimulatedTransactions.Add(transaction);
        result.TransactionsCreated++;

        CallbackTarget? callbackTarget = ResolveDefaultCallbackTarget(site, DeserializeSettings(site.SettingsJson));
        if (callbackTarget is not null && site.DeliveryMode is TransactionDeliveryMode.Push or TransactionDeliveryMode.Hybrid)
        {
            dbContext.CallbackAttempts.Add(new CallbackAttempt
            {
                Id = Guid.NewGuid(),
                CallbackTargetId = callbackTarget.Id,
                SimulatedTransactionId = transaction.Id,
                CorrelationId = correlationId,
                AttemptNumber = 1,
                Status = CallbackAttemptStatus.Succeeded,
                ResponseStatusCode = StatusCodes.Status202Accepted,
                RequestUrl = callbackTarget.CallbackUrl.ToString(),
                RequestHeadersJson = """{"content-type":"application/json"}""",
                RequestPayloadJson = transaction.RawPayloadJson,
                ResponseHeadersJson = """{"content-type":"application/json"}""",
                ResponsePayloadJson = """{"accepted":true}""",
                RetryCount = 0,
                MaxRetryCount = 0,
                AttemptedAtUtc = now,
                CompletedAtUtc = now,
                AcknowledgedAtUtc = now,
            });
            result.CallbackAttemptsCreated++;
        }

        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            FccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
            PreAuthSessionId = preAuthSession?.Id,
            SimulatedTransactionId = transaction.Id,
            CorrelationId = correlationId,
            Severity = "Information",
            Category = "TransactionGenerated",
            EventType = "ManagementSeededTransaction",
            Message = $"Management API seeded transaction for site '{site.SiteCode}'.",
            RawPayloadJson = transaction.RawPayloadJson,
            CanonicalPayloadJson = transaction.CanonicalPayloadJson,
            MetadataJson = Serialize(new { site.DeliveryMode, request.IncludeCompletedPreAuth }),
            OccurredAtUtc = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<SiteSeedResult?> ResetSiteAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        Site? site = await dbContext.Sites
            .Include(x => x.Pumps)
                .ThenInclude(x => x.Nozzles)
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);

        if (site is null)
        {
            return null;
        }

        SiteSeedResult result = await ResetSiteInternalAsync(site, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<LabSeedResult> SeedLabAsync(bool reset, CancellationToken cancellationToken = default)
    {
        await seedService.SeedAsync(reset, cancellationToken);

        return new LabSeedResult
        {
            ResetApplied = reset,
            SiteCount = await dbContext.Sites.CountAsync(cancellationToken),
            ProfileCount = await dbContext.FccSimulatorProfiles.CountAsync(cancellationToken),
            ProductCount = await dbContext.Products.CountAsync(cancellationToken),
            TransactionCount = await dbContext.SimulatedTransactions.CountAsync(cancellationToken),
        };
    }

    private async Task ValidateSiteAsync(
        Site candidate,
        Guid currentSiteId,
        IReadOnlySet<string>? additionalCallbackTargetKeys,
        CancellationToken cancellationToken)
    {
        List<ManagementValidationMessage> messages = await BuildSiteValidationMessagesAsync(
            candidate,
            currentSiteId,
            additionalCallbackTargetKeys,
            cancellationToken);

        if (messages.Any(x => x.Severity == "Error"))
        {
            throw new ManagementOperationException(400, "Site validation failed.", messages);
        }
    }

    private async Task ValidateSiteRequestAsync(
        Site candidate,
        Guid currentSiteId,
        IReadOnlyList<CallbackTargetUpsertRequest>? callbackTargets,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? callbackKeys = callbackTargets?
            .Select(x => x.TargetKey.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ManagementValidationMessage> messages = await BuildSiteValidationMessagesAsync(
            candidate,
            currentSiteId,
            callbackKeys,
            cancellationToken);

        if (callbackTargets is not null)
        {
            messages.AddRange(await BuildCallbackTargetValidationMessagesAsync(
                candidate.LabEnvironmentId,
                currentSiteId,
                callbackTargets,
                cancellationToken));
        }

        if (messages.Any(x => x.Severity == "Error"))
        {
            throw new ManagementOperationException(400, "Site validation failed.", messages);
        }
    }

    private async Task<List<ManagementValidationMessage>> BuildSiteValidationMessagesAsync(
        Site candidate,
        Guid currentSiteId,
        IReadOnlySet<string>? additionalCallbackTargetKeys,
        CancellationToken cancellationToken)
    {
        List<ManagementValidationMessage> messages = [];

        if (string.IsNullOrWhiteSpace(candidate.SiteCode))
        {
            messages.Add(new("siteCode", "Site code is required.", "Error", "required"));
        }

        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            messages.Add(new("name", "Site name is required.", "Error", "required"));
        }

        if (string.IsNullOrWhiteSpace(candidate.TimeZone))
        {
            messages.Add(new("timeZone", "Time zone is required.", "Error", "required"));
        }

        if (string.IsNullOrWhiteSpace(candidate.CurrencyCode))
        {
            messages.Add(new("currencyCode", "Currency code is required.", "Error", "required"));
        }

        bool siteCodeExists = await dbContext.Sites
            .AsNoTracking()
            .AnyAsync(x => x.SiteCode == candidate.SiteCode && x.Id != currentSiteId, cancellationToken);

        if (siteCodeExists)
        {
            messages.Add(new("siteCode", $"Site code '{candidate.SiteCode}' already exists.", "Error", "duplicate"));
        }

        FccSimulatorProfile? profile = await dbContext.FccSimulatorProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == candidate.ActiveFccSimulatorProfileId, cancellationToken);

        if (profile is null)
        {
            messages.Add(new("activeFccSimulatorProfileId", "An active FCC simulator profile is required.", "Error", "profile_not_found"));
        }
        else
        {
            messages.AddRange(await BuildCompatibilityMessagesAsync(candidate, profile, currentSiteId, additionalCallbackTargetKeys, cancellationToken));
        }

        return messages;
    }

    private async Task<List<ManagementValidationMessage>> BuildCallbackTargetValidationMessagesAsync(
        Guid environmentId,
        Guid currentSiteId,
        IReadOnlyList<CallbackTargetUpsertRequest> callbackTargets,
        CancellationToken cancellationToken)
    {
        List<ManagementValidationMessage> messages = [];
        HashSet<string> requestKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<Guid> requestIds = [];

        foreach ((CallbackTargetUpsertRequest target, int index) in callbackTargets.Select((value, index) => (value, index)))
        {
            string path = $"callbackTargets[{index}]";
            string targetKey = target.TargetKey.Trim();

            if (string.IsNullOrWhiteSpace(targetKey))
            {
                messages.Add(new($"{path}.targetKey", "Callback target key is required.", "Error", "required"));
            }
            else if (!requestKeys.Add(targetKey))
            {
                messages.Add(new($"{path}.targetKey", $"Callback target key '{targetKey}' is duplicated in this request.", "Error", "duplicate"));
            }

            if (target.Id.HasValue && !requestIds.Add(target.Id.Value))
            {
                messages.Add(new($"{path}.id", "Callback target id is duplicated in this request.", "Error", "duplicate"));
            }

            if (string.IsNullOrWhiteSpace(target.Name))
            {
                messages.Add(new($"{path}.name", "Callback target name is required.", "Error", "required"));
            }

            if (string.IsNullOrWhiteSpace(target.CallbackUrl))
            {
                messages.Add(new($"{path}.callbackUrl", "Callback URL is required.", "Error", "required"));
            }
            else if (!Uri.TryCreate(target.CallbackUrl.Trim(), UriKind.Absolute, out Uri? callbackUri) ||
                     (callbackUri.Scheme != Uri.UriSchemeHttp && callbackUri.Scheme != Uri.UriSchemeHttps))
            {
                messages.Add(new($"{path}.callbackUrl", "Callback URL must be an absolute HTTP or HTTPS address.", "Error", "invalid_url"));
            }

            switch (target.AuthMode)
            {
                case SimulatedAuthMode.ApiKey when string.IsNullOrWhiteSpace(target.ApiKeyHeaderName) || string.IsNullOrWhiteSpace(target.ApiKeyValue):
                    messages.Add(new($"{path}.authMode", "API key callback auth requires both header name and value.", "Error", "auth_incomplete"));
                    break;
                case SimulatedAuthMode.BasicAuth when string.IsNullOrWhiteSpace(target.BasicAuthUsername) || string.IsNullOrWhiteSpace(target.BasicAuthPassword):
                    messages.Add(new($"{path}.authMode", "Basic auth callback target requires both username and password.", "Error", "auth_incomplete"));
                    break;
            }
        }

        List<string> requestedKeys = requestKeys.ToList();
        if (requestedKeys.Count > 0)
        {
            List<(Guid Id, string TargetKey)> conflicts = await dbContext.CallbackTargets
                .AsNoTracking()
                .Where(x => x.LabEnvironmentId == environmentId && requestedKeys.Contains(x.TargetKey))
                .Select(x => new ValueTuple<Guid, string>(x.Id, x.TargetKey))
                .ToListAsync(cancellationToken);

            foreach ((CallbackTargetUpsertRequest target, int index) in callbackTargets.Select((value, index) => (value, index)))
            {
                string targetKey = target.TargetKey.Trim();
                if (string.IsNullOrWhiteSpace(targetKey))
                {
                    continue;
                }

                bool conflict = conflicts.Any(x => string.Equals(x.TargetKey, targetKey, StringComparison.OrdinalIgnoreCase) && (!target.Id.HasValue || x.Id != target.Id.Value));
                if (conflict)
                {
                    messages.Add(new($"callbackTargets[{index}].targetKey", $"Callback target key '{targetKey}' already exists in this environment.", "Error", "duplicate"));
                }
            }
        }

        if (requestIds.Count > 0)
        {
            HashSet<Guid> siteTargetIds = await dbContext.CallbackTargets
                .AsNoTracking()
                .Where(x => x.SiteId == currentSiteId)
                .Select(x => x.Id)
                .ToHashSetAsync(cancellationToken);

            foreach ((CallbackTargetUpsertRequest target, int index) in callbackTargets.Select((value, index) => (value, index)))
            {
                if (target.Id.HasValue && !siteTargetIds.Contains(target.Id.Value))
                {
                    messages.Add(new($"callbackTargets[{index}].id", "Callback target does not belong to this site.", "Error", "callback_target_not_found"));
                }
            }
        }

        return messages;
    }

    private async Task<List<ManagementValidationMessage>> BuildCompatibilityMessagesAsync(
        Site candidate,
        FccSimulatorProfile profile,
        Guid currentSiteId,
        IReadOnlySet<string>? additionalCallbackTargetKeys,
        CancellationToken cancellationToken)
    {
        List<ManagementValidationMessage> messages = [];
        SiteSettingsView settings = DeserializeSettings(candidate.SettingsJson);
        FccProfileContract contract = FccProfileService.ToRecord(profile).Contract;

        if (!profile.IsActive)
        {
            messages.Add(new("activeFccSimulatorProfileId", $"Profile '{profile.ProfileKey}' is inactive and cannot be assigned to a site.", "Error", "profile_inactive"));
        }

        if (profile.LabEnvironmentId != candidate.LabEnvironmentId)
        {
            messages.Add(new("activeFccSimulatorProfileId", $"Profile '{profile.ProfileKey}' belongs to a different lab environment.", "Error", "profile_environment_mismatch"));
        }

        if (!IsDeliveryModeSupported(candidate.DeliveryMode, contract.Capabilities))
        {
            messages.Add(new(
                "deliveryMode",
                $"Profile '{profile.ProfileKey}' does not support site delivery mode '{candidate.DeliveryMode}'.",
                "Error",
                "delivery_mode_incompatible"));
        }

        if (candidate.PreAuthMode != contract.PreAuthMode)
        {
            messages.Add(new(
                "preAuthMode",
                $"Site pre-auth mode '{candidate.PreAuthMode}' does not match profile '{profile.ProfileKey}' mode '{contract.PreAuthMode}'.",
                "Error",
                "preauth_mode_incompatible"));
        }

        bool usesSiteAuthOverride =
            candidate.InboundAuthMode != contract.Auth.Mode ||
            !string.IsNullOrWhiteSpace(candidate.ApiKeyHeaderName) ||
            !string.IsNullOrWhiteSpace(candidate.ApiKeyValue) ||
            !string.IsNullOrWhiteSpace(candidate.BasicAuthUsername) ||
            !string.IsNullOrWhiteSpace(candidate.BasicAuthPassword);

        if (usesSiteAuthOverride)
        {
            switch (candidate.InboundAuthMode)
            {
                case SimulatedAuthMode.ApiKey when string.IsNullOrWhiteSpace(candidate.ApiKeyHeaderName) || string.IsNullOrWhiteSpace(candidate.ApiKeyValue):
                    messages.Add(new("inboundAuthMode", "API key site override requires both header name and value.", "Error", "auth_override_incomplete"));
                    break;
                case SimulatedAuthMode.BasicAuth when string.IsNullOrWhiteSpace(candidate.BasicAuthUsername) || string.IsNullOrWhiteSpace(candidate.BasicAuthPassword):
                    messages.Add(new("inboundAuthMode", "Basic auth site override requires both username and password.", "Error", "auth_override_incomplete"));
                    break;
            }
        }

        if (settings.PullPageSize <= 0)
        {
            messages.Add(new("settings.pullPageSize", "Pull page size must be greater than zero.", "Error", "invalid_pull_page_size"));
        }

        if (candidate.DeliveryMode is TransactionDeliveryMode.Push or TransactionDeliveryMode.Hybrid &&
            string.IsNullOrWhiteSpace(settings.DefaultCallbackTargetKey))
        {
            messages.Add(new(
                "settings.defaultCallbackTargetKey",
                $"Site delivery mode '{candidate.DeliveryMode}' requires an explicit default callback target.",
                "Error",
                "callback_target_required"));
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultCallbackTargetKey))
        {
            bool callbackExistsInDb = await dbContext.CallbackTargets
                .AsNoTracking()
                .AnyAsync(
                    x =>
                        x.TargetKey == settings.DefaultCallbackTargetKey &&
                        x.LabEnvironmentId == candidate.LabEnvironmentId &&
                        x.IsActive &&
                        (x.SiteId == null || x.SiteId == currentSiteId),
                    cancellationToken);

            bool callbackExistsInRequest = additionalCallbackTargetKeys?.Contains(settings.DefaultCallbackTargetKey) == true;
            if (!callbackExistsInDb && !callbackExistsInRequest)
            {
                messages.Add(new(
                    "settings.defaultCallbackTargetKey",
                    $"Default callback target '{settings.DefaultCallbackTargetKey}' was not found for this site.",
                    "Error",
                    "callback_target_not_found"));
            }
        }

        if (settings.Fiscalization.Mode.Length == 0)
        {
            messages.Add(new("settings.fiscalization.mode", "Fiscalization mode must be supplied when fiscalization settings are present.", "Warning", "fiscalization_mode_missing"));
        }

        return messages;
    }

    private static bool IsDeliveryModeSupported(TransactionDeliveryMode deliveryMode, FccDeliveryCapabilities capabilities)
    {
        return deliveryMode switch
        {
            TransactionDeliveryMode.Push => capabilities.SupportsPush,
            TransactionDeliveryMode.Pull => capabilities.SupportsPull,
            TransactionDeliveryMode.Hybrid => capabilities.SupportsPush && capabilities.SupportsPull,
            _ => false,
        };
    }

    private static void ApplySiteRequest(Site site, SiteUpsertRequest request, Guid environmentId, DateTimeOffset now)
    {
        site.LabEnvironmentId = environmentId;
        site.ActiveFccSimulatorProfileId = request.ActiveFccSimulatorProfileId;
        site.SiteCode = request.SiteCode.Trim();
        site.Name = request.Name.Trim();
        site.TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim();
        site.CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? "USD" : request.CurrencyCode.Trim().ToUpperInvariant();
        site.ExternalReference = request.ExternalReference.Trim();
        site.InboundAuthMode = request.InboundAuthMode;
        site.ApiKeyHeaderName = request.ApiKeyHeaderName.Trim();
        site.ApiKeyValue = request.ApiKeyValue.Trim();
        site.BasicAuthUsername = request.BasicAuthUsername.Trim();
        site.BasicAuthPassword = request.BasicAuthPassword.Trim();
        site.DeliveryMode = request.DeliveryMode;
        site.PreAuthMode = request.PreAuthMode;
        site.FccVendor = string.IsNullOrWhiteSpace(request.FccVendor) ? "Generic" : request.FccVendor.Trim();
        site.IsActive = request.IsActive;
        site.SettingsJson = SerializeSettings(request.Settings);
        site.UpdatedAtUtc = now;
    }

    private static List<CallbackTarget> BuildCallbackTargets(
        Site site,
        IReadOnlyList<CallbackTargetUpsertRequest> requests,
        DateTimeOffset now)
    {
        return requests.Select(request => new CallbackTarget
        {
            Id = request.Id ?? Guid.NewGuid(),
            LabEnvironmentId = site.LabEnvironmentId,
            SiteId = site.Id,
            TargetKey = request.TargetKey.Trim(),
            Name = request.Name.Trim(),
            CallbackUrl = new Uri(request.CallbackUrl.Trim()),
            AuthMode = request.AuthMode,
            ApiKeyHeaderName = request.ApiKeyHeaderName.Trim(),
            ApiKeyValue = request.ApiKeyValue.Trim(),
            BasicAuthUsername = request.BasicAuthUsername.Trim(),
            BasicAuthPassword = request.BasicAuthPassword.Trim(),
            IsActive = request.IsActive,
            CreatedAtUtc = now,
        }).ToList();
    }

    private void SyncCallbackTargets(
        Site site,
        IReadOnlyList<CallbackTargetUpsertRequest> requests,
        DateTimeOffset now)
    {
        List<CallbackTarget> existingTargets = site.CallbackTargets.ToList();
        HashSet<Guid> retainedIds = [];

        foreach (CallbackTargetUpsertRequest request in requests)
        {
            CallbackTarget? target = request.Id.HasValue
                ? existingTargets.SingleOrDefault(x => x.Id == request.Id.Value)
                : existingTargets.SingleOrDefault(x => string.Equals(x.TargetKey, request.TargetKey.Trim(), StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                target = new CallbackTarget
                {
                    Id = request.Id ?? Guid.NewGuid(),
                    LabEnvironmentId = site.LabEnvironmentId,
                    SiteId = site.Id,
                    CreatedAtUtc = now,
                };

                site.CallbackTargets.Add(target);
            }

            retainedIds.Add(target.Id);
            target.LabEnvironmentId = site.LabEnvironmentId;
            target.SiteId = site.Id;
            target.TargetKey = request.TargetKey.Trim();
            target.Name = request.Name.Trim();
            target.CallbackUrl = new Uri(request.CallbackUrl.Trim());
            target.AuthMode = request.AuthMode;
            target.ApiKeyHeaderName = request.ApiKeyHeaderName.Trim();
            target.ApiKeyValue = request.ApiKeyValue.Trim();
            target.BasicAuthUsername = request.BasicAuthUsername.Trim();
            target.BasicAuthPassword = request.BasicAuthPassword.Trim();
            target.IsActive = request.IsActive;
        }

        List<CallbackTarget> removedTargets = existingTargets.Where(x => !retainedIds.Contains(x.Id)).ToList();
        if (removedTargets.Count > 0)
        {
            dbContext.CallbackTargets.RemoveRange(removedTargets);
        }
    }

    private async Task ValidateProductAsync(ProductUpsertRequest request, Guid? currentProductId, Guid environmentId, CancellationToken cancellationToken)
    {
        List<ManagementValidationMessage> messages = [];

        if (string.IsNullOrWhiteSpace(request.ProductCode))
        {
            messages.Add(new("productCode", "Product code is required.", "Error", "required"));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            messages.Add(new("name", "Product name is required.", "Error", "required"));
        }

        if (request.UnitPrice < 0)
        {
            messages.Add(new("unitPrice", "Unit price cannot be negative.", "Error", "invalid_price"));
        }

        if (!IsValidColorHex(request.ColorHex))
        {
            messages.Add(new("colorHex", "Color hex must be in #RRGGBB or #RGB format.", "Error", "invalid_color"));
        }

        if (string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            messages.Add(new("currencyCode", "Currency code is required.", "Error", "required"));
        }

        bool exists = await dbContext.Products
            .AsNoTracking()
            .AnyAsync(
                x => x.LabEnvironmentId == environmentId &&
                     x.ProductCode == request.ProductCode.Trim() &&
                     (!currentProductId.HasValue || x.Id != currentProductId.Value),
                cancellationToken);

        if (exists)
        {
            messages.Add(new("productCode", $"Product code '{request.ProductCode.Trim()}' already exists in this environment.", "Error", "duplicate"));
        }

        if (messages.Any(x => x.Severity == "Error"))
        {
            throw new ManagementOperationException(400, "Product validation failed.", messages);
        }
    }

    private static List<ManagementValidationMessage> ValidateForecourtRequest(SaveForecourtRequest request)
    {
        List<ManagementValidationMessage> messages = [];
        HashSet<int> pumpNumbers = [];
        HashSet<int> fccPumpNumbers = [];

        foreach ((ForecourtPumpUpsertRequest pump, int pumpIndex) in request.Pumps.Select((value, index) => (value, index)))
        {
            if (pump.PumpNumber <= 0)
            {
                messages.Add(new($"pumps[{pumpIndex}].pumpNumber", "Pump number must be greater than zero.", "Error", "invalid_pump_number"));
            }

            if (!pumpNumbers.Add(pump.PumpNumber))
            {
                messages.Add(new($"pumps[{pumpIndex}].pumpNumber", $"Pump number '{pump.PumpNumber}' is duplicated in this forecourt payload.", "Error", "duplicate_pump_number"));
            }

            if (pump.FccPumpNumber <= 0)
            {
                messages.Add(new($"pumps[{pumpIndex}].fccPumpNumber", "FCC pump number must be greater than zero.", "Error", "invalid_fcc_pump_number"));
            }

            if (!fccPumpNumbers.Add(pump.FccPumpNumber))
            {
                messages.Add(new($"pumps[{pumpIndex}].fccPumpNumber", $"FCC pump number '{pump.FccPumpNumber}' is duplicated in this forecourt payload.", "Error", "duplicate_fcc_pump_number"));
            }

            if (pump.LayoutX is < 0)
            {
                messages.Add(new($"pumps[{pumpIndex}].layoutX", "Pump layout X must be zero or greater.", "Error", "invalid_layout_x"));
            }

            if (pump.LayoutY is < 0)
            {
                messages.Add(new($"pumps[{pumpIndex}].layoutY", "Pump layout Y must be zero or greater.", "Error", "invalid_layout_y"));
            }

            HashSet<int> nozzleNumbers = [];
            HashSet<int> fccNozzleNumbers = [];
            foreach ((ForecourtNozzleUpsertRequest nozzle, int nozzleIndex) in pump.Nozzles.Select((value, index) => (value, index)))
            {
                if (nozzle.NozzleNumber <= 0)
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].nozzleNumber", "Nozzle number must be greater than zero.", "Error", "invalid_nozzle_number"));
                }

                if (!nozzleNumbers.Add(nozzle.NozzleNumber))
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].nozzleNumber", $"Nozzle number '{nozzle.NozzleNumber}' is duplicated within pump '{pump.PumpNumber}'.", "Error", "duplicate_nozzle_number"));
                }

                if (nozzle.FccNozzleNumber <= 0)
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].fccNozzleNumber", "FCC nozzle number must be greater than zero.", "Error", "invalid_fcc_nozzle_number"));
                }

                if (!fccNozzleNumbers.Add(nozzle.FccNozzleNumber))
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].fccNozzleNumber", $"FCC nozzle number '{nozzle.FccNozzleNumber}' is duplicated within pump '{pump.PumpNumber}'.", "Error", "duplicate_fcc_nozzle_number"));
                }

                if (nozzle.ProductId == Guid.Empty)
                {
                    messages.Add(new($"pumps[{pumpIndex}].nozzles[{nozzleIndex}].productId", "Product id is required for each nozzle.", "Error", "required"));
                }
            }
        }

        return messages;
    }

    private async Task ValidateForecourtRemovalsAsync(
        IReadOnlyCollection<Pump> removedPumps,
        IReadOnlyCollection<Nozzle> removedNozzles,
        CancellationToken cancellationToken)
    {
        List<ManagementValidationMessage> messages = [];
        Guid[] removedPumpIds = removedPumps.Select(x => x.Id).Distinct().ToArray();
        Guid[] removedNozzleIds = removedNozzles.Select(x => x.Id).Distinct().ToArray();

        if (removedPumpIds.Length > 0)
        {
            bool pumpReferenced = await dbContext.SimulatedTransactions
                .AsNoTracking()
                .AnyAsync(x => removedPumpIds.Contains(x.PumpId), cancellationToken)
                || await dbContext.PreAuthSessions
                    .AsNoTracking()
                    .AnyAsync(x => x.PumpId.HasValue && removedPumpIds.Contains(x.PumpId.Value), cancellationToken);

            if (pumpReferenced)
            {
                messages.Add(new("pumps", "A pump cannot be removed because it is referenced by existing simulated transactions or pre-auth sessions.", "Error", "pump_in_use"));
            }
        }

        if (removedNozzleIds.Length > 0)
        {
            bool nozzleReferenced = await dbContext.SimulatedTransactions
                .AsNoTracking()
                .AnyAsync(x => removedNozzleIds.Contains(x.NozzleId), cancellationToken)
                || await dbContext.PreAuthSessions
                    .AsNoTracking()
                    .AnyAsync(x => x.NozzleId.HasValue && removedNozzleIds.Contains(x.NozzleId.Value), cancellationToken);

            if (nozzleReferenced)
            {
                messages.Add(new("pumps[].nozzles", "A nozzle cannot be removed because it is referenced by existing simulated transactions or pre-auth sessions.", "Error", "nozzle_in_use"));
            }
        }

        if (messages.Any())
        {
            throw new ManagementOperationException(409, "Forecourt update would remove pumps/nozzles that are already in use.", messages);
        }
    }

    private async Task<Site?> LoadSiteAsync(Guid siteId, bool asNoTracking, CancellationToken cancellationToken)
    {
        IQueryable<Site> query = dbContext.Sites;
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query
            .Include(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pumps.OrderBy(p => p.PumpNumber))
                .ThenInclude(x => x.Nozzles.OrderBy(n => n.NozzleNumber))
                    .ThenInclude(x => x.Product)
            .Include(x => x.CallbackTargets.OrderBy(c => c.Name))
            .SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken);
    }

    private SiteListItemView CreateSiteSummary(Site site)
    {
        SiteSettingsView settings = DeserializeSettings(site.SettingsJson);
        List<ManagementValidationMessage> compatibilityMessages = BuildCompatibilityMessagesSnapshot(site, site.ActiveFccSimulatorProfile, site.CallbackTargets.Select(x => x.TargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase));

        return new SiteListItemView
        {
            Id = site.Id,
            LabEnvironmentId = site.LabEnvironmentId,
            SiteCode = site.SiteCode,
            Name = site.Name,
            TimeZone = site.TimeZone,
            CurrencyCode = site.CurrencyCode,
            ExternalReference = site.ExternalReference,
            IsActive = site.IsActive,
            InboundAuthMode = site.InboundAuthMode,
            ApiKeyHeaderName = site.ApiKeyHeaderName,
            ApiKeyValue = site.ApiKeyValue,
            BasicAuthUsername = site.BasicAuthUsername,
            BasicAuthPassword = site.BasicAuthPassword,
            DeliveryMode = site.DeliveryMode,
            PreAuthMode = site.PreAuthMode,
            FccVendor = site.FccVendor,
            Settings = settings,
            ActiveProfile = ToProfileSummary(site.ActiveFccSimulatorProfile),
            Forecourt = new SiteForecourtSummaryView
            {
                PumpCount = site.Pumps.Count,
                NozzleCount = site.Pumps.SelectMany(x => x.Nozzles).Count(),
                ActivePumpCount = site.Pumps.Count(x => x.IsActive),
                ActiveNozzleCount = site.Pumps.SelectMany(x => x.Nozzles).Count(x => x.IsActive),
            },
            Compatibility = new SiteCompatibilityView
            {
                IsValid = compatibilityMessages.All(x => x.Severity != "Error"),
                Messages = compatibilityMessages,
            },
        };
    }

    private SiteDetailView CreateSiteDetail(Site site, IReadOnlyList<FccProfileSummary> availableProfiles)
    {
        SiteListItemView summary = CreateSiteSummary(site);
        return new SiteDetailView
        {
            Id = summary.Id,
            LabEnvironmentId = summary.LabEnvironmentId,
            SiteCode = summary.SiteCode,
            Name = summary.Name,
            TimeZone = summary.TimeZone,
            CurrencyCode = summary.CurrencyCode,
            ExternalReference = summary.ExternalReference,
            IsActive = summary.IsActive,
            InboundAuthMode = summary.InboundAuthMode,
            ApiKeyHeaderName = summary.ApiKeyHeaderName,
            ApiKeyValue = summary.ApiKeyValue,
            BasicAuthUsername = summary.BasicAuthUsername,
            BasicAuthPassword = summary.BasicAuthPassword,
            DeliveryMode = summary.DeliveryMode,
            PreAuthMode = summary.PreAuthMode,
            FccVendor = summary.FccVendor,
            Settings = summary.Settings,
            ActiveProfile = summary.ActiveProfile,
            Forecourt = summary.Forecourt,
            Compatibility = summary.Compatibility,
            ForecourtConfiguration = MapForecourt(site),
            CallbackTargets = site.CallbackTargets
                .OrderBy(x => x.Name)
                .Select(x => new CallbackTargetSummaryView
                {
                    Id = x.Id,
                    TargetKey = x.TargetKey,
                    Name = x.Name,
                    CallbackUrl = x.CallbackUrl.ToString(),
                    AuthMode = x.AuthMode,
                    ApiKeyHeaderName = x.ApiKeyHeaderName,
                    ApiKeyValue = x.ApiKeyValue,
                    BasicAuthUsername = x.BasicAuthUsername,
                    BasicAuthPassword = x.BasicAuthPassword,
                    IsActive = x.IsActive,
                })
                .ToList(),
            AvailableProfiles = availableProfiles,
        };
    }

    private static SiteForecourtView MapForecourt(Site site)
    {
        return new SiteForecourtView
        {
            SiteId = site.Id,
            SiteCode = site.SiteCode,
            SiteName = site.Name,
            Pumps = site.Pumps
                .OrderBy(x => x.PumpNumber)
                .Select(pump => new ForecourtPumpView
                {
                    Id = pump.Id,
                    PumpNumber = pump.PumpNumber,
                    FccPumpNumber = pump.FccPumpNumber,
                    LayoutX = pump.LayoutX,
                    LayoutY = pump.LayoutY,
                    Label = pump.Label,
                    IsActive = pump.IsActive,
                    Nozzles = pump.Nozzles
                        .OrderBy(x => x.NozzleNumber)
                        .Select(nozzle => new ForecourtNozzleView
                        {
                            Id = nozzle.Id,
                            ProductId = nozzle.ProductId,
                            ProductCode = nozzle.Product.ProductCode,
                            ProductName = nozzle.Product.Name,
                            NozzleNumber = nozzle.NozzleNumber,
                            FccNozzleNumber = nozzle.FccNozzleNumber,
                            Label = nozzle.Label,
                            State = nozzle.State,
                            IsActive = nozzle.IsActive,
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }

    private static FccProfileSummary ToProfileSummary(FccSimulatorProfile profile)
    {
        return new FccProfileSummary(
            profile.Id,
            profile.ProfileKey,
            profile.Name,
            profile.VendorFamily,
            profile.AuthMode,
            profile.DeliveryMode,
            profile.PreAuthMode,
            profile.IsActive,
            profile.IsDefault);
    }

    private static Pump? MatchPump(IReadOnlyCollection<Pump> existingPumps, ForecourtPumpUpsertRequest request)
    {
        if (request.Id.HasValue)
        {
            return existingPumps.SingleOrDefault(x => x.Id == request.Id.Value);
        }

        return existingPumps.SingleOrDefault(x => x.PumpNumber == request.PumpNumber);
    }

    private static (int LayoutX, int LayoutY) GetDefaultPumpLayout(int ordinal)
    {
        const int columns = 5;
        const int originX = 120;
        const int originY = 100;
        const int horizontalGap = 240;
        const int verticalGap = 260;

        int column = ordinal % columns;
        int row = ordinal / columns;
        return (originX + (column * horizontalGap), originY + (row * verticalGap));
    }

    private static Nozzle? MatchNozzle(IReadOnlyCollection<Nozzle> existingNozzles, ForecourtNozzleUpsertRequest request)
    {
        if (request.Id.HasValue)
        {
            return existingNozzles.SingleOrDefault(x => x.Id == request.Id.Value);
        }

        return existingNozzles.SingleOrDefault(x => x.NozzleNumber == request.NozzleNumber);
    }

    private async Task<Guid> ResolveEnvironmentIdAsync(Guid requestedEnvironmentId, CancellationToken cancellationToken)
    {
        if (requestedEnvironmentId != Guid.Empty)
        {
            bool exists = await dbContext.LabEnvironments.AsNoTracking().AnyAsync(x => x.Id == requestedEnvironmentId, cancellationToken);
            if (!exists)
            {
                throw new ManagementOperationException(
                    400,
                    "Lab environment validation failed.",
                    [new("labEnvironmentId", $"Lab environment '{requestedEnvironmentId}' was not found.", "Error", "environment_not_found")]);
            }

            return requestedEnvironmentId;
        }

        Guid? environmentId = await dbContext.LabEnvironments
            .AsNoTracking()
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (environmentId is null)
        {
            throw new ManagementOperationException(
                409,
                "Lab environment is not initialized.",
                [new("labEnvironmentId", "Seed the lab before creating or updating management records.", "Error", "environment_missing")]);
        }

        return environmentId.Value;
    }

    private async Task ResetEntireLabAsync(CancellationToken cancellationToken)
    {
        dbContext.CallbackAttempts.RemoveRange(dbContext.CallbackAttempts);
        dbContext.LabEventLogs.RemoveRange(dbContext.LabEventLogs);
        dbContext.SimulatedTransactions.RemoveRange(dbContext.SimulatedTransactions);
        dbContext.PreAuthSessions.RemoveRange(dbContext.PreAuthSessions);
        dbContext.ScenarioRuns.RemoveRange(dbContext.ScenarioRuns);
        dbContext.ScenarioDefinitions.RemoveRange(dbContext.ScenarioDefinitions);
        dbContext.Nozzles.RemoveRange(dbContext.Nozzles);
        dbContext.Pumps.RemoveRange(dbContext.Pumps);
        dbContext.CallbackTargets.RemoveRange(dbContext.CallbackTargets);
        dbContext.Sites.RemoveRange(dbContext.Sites);
        dbContext.Products.RemoveRange(dbContext.Products);
        dbContext.FccSimulatorProfiles.RemoveRange(dbContext.FccSimulatorProfiles);
        dbContext.LabEnvironments.RemoveRange(dbContext.LabEnvironments);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static LabEnvironmentRetentionSettingsView BuildEffectiveRetentionSettings(
        LabEnvironmentRetentionSettingsView baseline,
        LabEnvironmentPruneRequest request)
    {
        return new LabEnvironmentRetentionSettingsView
        {
            LogRetentionDays = NormalizeRetentionDays(request.LogRetentionDays ?? baseline.LogRetentionDays, 30),
            CallbackHistoryRetentionDays = NormalizeRetentionDays(request.CallbackHistoryRetentionDays ?? baseline.CallbackHistoryRetentionDays, 30),
            TransactionRetentionDays = NormalizeRetentionDays(request.TransactionRetentionDays ?? baseline.TransactionRetentionDays, 90),
            PreserveTimelineIntegrity = request.PreserveTimelineIntegrity ?? baseline.PreserveTimelineIntegrity,
        };
    }

    private static int NormalizeRetentionDays(int value, int fallback)
    {
        return value <= 0 ? fallback : value;
    }

    private static DateTimeOffset ResolveRetentionTimestamp(PreAuthSession session)
    {
        return session.CompletedAtUtc ?? session.ExpiresAtUtc ?? session.AuthorizedAtUtc ?? session.CreatedAtUtc;
    }

    private static LabEnvironmentSettingsView DeserializeEnvironmentSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LabEnvironmentSettingsView();
        }

        try
        {
            LabEnvironmentSettingsView settings = JsonSerializer.Deserialize<LabEnvironmentSettingsView>(json, JsonOptions) ?? new LabEnvironmentSettingsView();
            settings.Retention.LogRetentionDays = NormalizeRetentionDays(settings.Retention.LogRetentionDays, 30);
            settings.Retention.CallbackHistoryRetentionDays = NormalizeRetentionDays(settings.Retention.CallbackHistoryRetentionDays, 30);
            settings.Retention.TransactionRetentionDays = NormalizeRetentionDays(settings.Retention.TransactionRetentionDays, 90);
            return settings;
        }
        catch (JsonException)
        {
            return new LabEnvironmentSettingsView();
        }
    }

    private static string SerializeEnvironmentSettings(LabEnvironmentSettingsView settings)
    {
        settings.Retention.LogRetentionDays = NormalizeRetentionDays(settings.Retention.LogRetentionDays, 30);
        settings.Retention.CallbackHistoryRetentionDays = NormalizeRetentionDays(settings.Retention.CallbackHistoryRetentionDays, 30);
        settings.Retention.TransactionRetentionDays = NormalizeRetentionDays(settings.Retention.TransactionRetentionDays, 90);
        return Serialize(settings);
    }

    private static LabEnvironmentDetailView MapEnvironmentDetail(LabEnvironment environment)
    {
        return new LabEnvironmentDetailView
        {
            Id = environment.Id,
            Key = environment.Key,
            Name = environment.Name,
            Description = environment.Description,
            LastSeededAtUtc = environment.LastSeededAtUtc,
            SeedVersion = environment.SeedVersion,
            DeterministicSeed = environment.DeterministicSeed,
            CreatedAtUtc = environment.CreatedAtUtc,
            UpdatedAtUtc = environment.UpdatedAtUtc,
            Settings = DeserializeEnvironmentSettings(environment.SettingsJson),
            LogCategories = LabDiagnosticsCatalog.Categories
                .Select(x => new LabEnvironmentLogCategoryView
                {
                    Category = x.Category,
                    DefaultSeverity = x.DefaultSeverity,
                    Description = x.Description,
                })
                .ToArray(),
        };
    }

    private static LabEnvironmentSnapshot MapEnvironmentSnapshot(LabEnvironment environment)
    {
        return new LabEnvironmentSnapshot
        {
            Id = environment.Id,
            Key = environment.Key,
            Name = environment.Name,
            Description = environment.Description,
            SettingsJson = environment.SettingsJson,
            SeedVersion = environment.SeedVersion,
            DeterministicSeed = environment.DeterministicSeed,
            CreatedAtUtc = environment.CreatedAtUtc,
            UpdatedAtUtc = environment.UpdatedAtUtc,
            LastSeededAtUtc = environment.LastSeededAtUtc,
        };
    }

    private static FccSimulatorProfileSnapshot MapProfileSnapshot(FccSimulatorProfile profile)
    {
        return new FccSimulatorProfileSnapshot
        {
            Id = profile.Id,
            LabEnvironmentId = profile.LabEnvironmentId,
            ProfileKey = profile.ProfileKey,
            Name = profile.Name,
            VendorFamily = profile.VendorFamily,
            AuthMode = profile.AuthMode,
            DeliveryMode = profile.DeliveryMode,
            PreAuthMode = profile.PreAuthMode,
            EndpointBasePath = profile.EndpointBasePath,
            EndpointSurfaceJson = profile.EndpointSurfaceJson,
            AuthConfigurationJson = profile.AuthConfigurationJson,
            CapabilitiesJson = profile.CapabilitiesJson,
            RequestTemplatesJson = profile.RequestTemplatesJson,
            ResponseTemplatesJson = profile.ResponseTemplatesJson,
            ValidationRulesJson = profile.ValidationRulesJson,
            FieldMappingsJson = profile.FieldMappingsJson,
            FailureSimulationJson = profile.FailureSimulationJson,
            ExtensionConfigurationJson = profile.ExtensionConfigurationJson,
            IsActive = profile.IsActive,
            IsDefault = profile.IsDefault,
            CreatedAtUtc = profile.CreatedAtUtc,
            UpdatedAtUtc = profile.UpdatedAtUtc,
        };
    }

    private static ProductSnapshot MapProductSnapshot(Product product)
    {
        return new ProductSnapshot
        {
            Id = product.Id,
            LabEnvironmentId = product.LabEnvironmentId,
            ProductCode = product.ProductCode,
            Name = product.Name,
            Grade = product.Grade,
            ColorHex = product.ColorHex,
            UnitPrice = product.UnitPrice,
            CurrencyCode = product.CurrencyCode,
            IsActive = product.IsActive,
            UpdatedAtUtc = product.UpdatedAtUtc,
        };
    }

    private static SiteSnapshot MapSiteSnapshot(Site site)
    {
        return new SiteSnapshot
        {
            Id = site.Id,
            LabEnvironmentId = site.LabEnvironmentId,
            ActiveFccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
            SiteCode = site.SiteCode,
            Name = site.Name,
            TimeZone = site.TimeZone,
            CurrencyCode = site.CurrencyCode,
            ExternalReference = site.ExternalReference,
            InboundAuthMode = site.InboundAuthMode,
            ApiKeyHeaderName = site.ApiKeyHeaderName,
            ApiKeyValue = site.ApiKeyValue,
            BasicAuthUsername = site.BasicAuthUsername,
            BasicAuthPassword = site.BasicAuthPassword,
            DeliveryMode = site.DeliveryMode,
            PreAuthMode = site.PreAuthMode,
            FccVendor = site.FccVendor,
            SettingsJson = site.SettingsJson,
            IsActive = site.IsActive,
            CreatedAtUtc = site.CreatedAtUtc,
            UpdatedAtUtc = site.UpdatedAtUtc,
        };
    }

    private static CallbackTargetSnapshot MapCallbackTargetSnapshot(CallbackTarget target)
    {
        return new CallbackTargetSnapshot
        {
            Id = target.Id,
            LabEnvironmentId = target.LabEnvironmentId,
            SiteId = target.SiteId,
            TargetKey = target.TargetKey,
            Name = target.Name,
            CallbackUrl = target.CallbackUrl.ToString(),
            AuthMode = target.AuthMode,
            ApiKeyHeaderName = target.ApiKeyHeaderName,
            ApiKeyValue = target.ApiKeyValue,
            BasicAuthUsername = target.BasicAuthUsername,
            BasicAuthPassword = target.BasicAuthPassword,
            IsActive = target.IsActive,
            CreatedAtUtc = target.CreatedAtUtc,
        };
    }

    private static PumpSnapshot MapPumpSnapshot(Pump pump)
    {
        return new PumpSnapshot
        {
            Id = pump.Id,
            SiteId = pump.SiteId,
            PumpNumber = pump.PumpNumber,
            FccPumpNumber = pump.FccPumpNumber,
            LayoutX = pump.LayoutX,
            LayoutY = pump.LayoutY,
            Label = pump.Label,
            IsActive = pump.IsActive,
            CreatedAtUtc = pump.CreatedAtUtc,
        };
    }

    private static NozzleSnapshot MapNozzleSnapshot(Nozzle nozzle)
    {
        return new NozzleSnapshot
        {
            Id = nozzle.Id,
            PumpId = nozzle.PumpId,
            ProductId = nozzle.ProductId,
            NozzleNumber = nozzle.NozzleNumber,
            FccNozzleNumber = nozzle.FccNozzleNumber,
            Label = nozzle.Label,
            State = nozzle.State,
            SimulationStateJson = nozzle.SimulationStateJson,
            IsActive = nozzle.IsActive,
            UpdatedAtUtc = nozzle.UpdatedAtUtc,
        };
    }

    private static ScenarioDefinitionSnapshot MapScenarioDefinitionSnapshot(ScenarioDefinition definition)
    {
        return new ScenarioDefinitionSnapshot
        {
            Id = definition.Id,
            LabEnvironmentId = definition.LabEnvironmentId,
            ScenarioKey = definition.ScenarioKey,
            Name = definition.Name,
            Description = definition.Description,
            DeterministicSeed = definition.DeterministicSeed,
            DefinitionJson = definition.DefinitionJson,
            ReplaySignature = definition.ReplaySignature,
            IsActive = definition.IsActive,
            CreatedAtUtc = definition.CreatedAtUtc,
            UpdatedAtUtc = definition.UpdatedAtUtc,
        };
    }

    private static ScenarioRunSnapshot MapScenarioRunSnapshot(ScenarioRun run)
    {
        return new ScenarioRunSnapshot
        {
            Id = run.Id,
            SiteId = run.SiteId,
            ScenarioDefinitionId = run.ScenarioDefinitionId,
            CorrelationId = run.CorrelationId,
            ReplaySeed = run.ReplaySeed,
            ReplaySignature = run.ReplaySignature,
            Status = run.Status,
            InputSnapshotJson = run.InputSnapshotJson,
            ResultSummaryJson = run.ResultSummaryJson,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
        };
    }

    private static PreAuthSessionSnapshot MapPreAuthSessionSnapshot(PreAuthSession session)
    {
        return new PreAuthSessionSnapshot
        {
            Id = session.Id,
            SiteId = session.SiteId,
            PumpId = session.PumpId,
            NozzleId = session.NozzleId,
            ScenarioRunId = session.ScenarioRunId,
            CorrelationId = session.CorrelationId,
            ExternalReference = session.ExternalReference,
            Mode = session.Mode,
            Status = session.Status,
            ReservedAmount = session.ReservedAmount,
            AuthorizedAmount = session.AuthorizedAmount,
            FinalAmount = session.FinalAmount,
            FinalVolume = session.FinalVolume,
            RawRequestJson = session.RawRequestJson,
            CanonicalRequestJson = session.CanonicalRequestJson,
            RawResponseJson = session.RawResponseJson,
            CanonicalResponseJson = session.CanonicalResponseJson,
            TimelineJson = session.TimelineJson,
            CreatedAtUtc = session.CreatedAtUtc,
            AuthorizedAtUtc = session.AuthorizedAtUtc,
            CompletedAtUtc = session.CompletedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc,
        };
    }

    private static SimulatedTransactionSnapshot MapTransactionSnapshot(SimulatedTransaction transaction)
    {
        return new SimulatedTransactionSnapshot
        {
            Id = transaction.Id,
            SiteId = transaction.SiteId,
            PumpId = transaction.PumpId,
            NozzleId = transaction.NozzleId,
            ProductId = transaction.ProductId,
            PreAuthSessionId = transaction.PreAuthSessionId,
            ScenarioRunId = transaction.ScenarioRunId,
            CorrelationId = transaction.CorrelationId,
            ExternalTransactionId = transaction.ExternalTransactionId,
            DeliveryMode = transaction.DeliveryMode,
            Status = transaction.Status,
            Volume = transaction.Volume,
            UnitPrice = transaction.UnitPrice,
            TotalAmount = transaction.TotalAmount,
            OccurredAtUtc = transaction.OccurredAtUtc,
            CreatedAtUtc = transaction.CreatedAtUtc,
            DeliveredAtUtc = transaction.DeliveredAtUtc,
            RawPayloadJson = transaction.RawPayloadJson,
            CanonicalPayloadJson = transaction.CanonicalPayloadJson,
            RawHeadersJson = transaction.RawHeadersJson,
            DeliveryCursor = transaction.DeliveryCursor,
            MetadataJson = transaction.MetadataJson,
            TimelineJson = transaction.TimelineJson,
        };
    }

    private static CallbackAttemptSnapshot MapCallbackAttemptSnapshot(CallbackAttempt attempt)
    {
        return new CallbackAttemptSnapshot
        {
            Id = attempt.Id,
            CallbackTargetId = attempt.CallbackTargetId,
            SimulatedTransactionId = attempt.SimulatedTransactionId,
            CorrelationId = attempt.CorrelationId,
            AttemptNumber = attempt.AttemptNumber,
            Status = attempt.Status,
            ResponseStatusCode = attempt.ResponseStatusCode,
            RequestUrl = attempt.RequestUrl,
            RequestHeadersJson = attempt.RequestHeadersJson,
            RequestPayloadJson = attempt.RequestPayloadJson,
            ResponseHeadersJson = attempt.ResponseHeadersJson,
            ResponsePayloadJson = attempt.ResponsePayloadJson,
            ErrorMessage = attempt.ErrorMessage,
            RetryCount = attempt.RetryCount,
            MaxRetryCount = attempt.MaxRetryCount,
            AttemptedAtUtc = attempt.AttemptedAtUtc,
            CompletedAtUtc = attempt.CompletedAtUtc,
            NextRetryAtUtc = attempt.NextRetryAtUtc,
            AcknowledgedAtUtc = attempt.AcknowledgedAtUtc,
        };
    }

    private static LabEventLogSnapshot MapLabEventLogSnapshot(LabEventLog log)
    {
        return new LabEventLogSnapshot
        {
            Id = log.Id,
            SiteId = log.SiteId,
            FccSimulatorProfileId = log.FccSimulatorProfileId,
            PreAuthSessionId = log.PreAuthSessionId,
            SimulatedTransactionId = log.SimulatedTransactionId,
            ScenarioRunId = log.ScenarioRunId,
            CorrelationId = log.CorrelationId,
            Severity = log.Severity,
            Category = log.Category,
            EventType = log.EventType,
            Message = log.Message,
            RawPayloadJson = log.RawPayloadJson,
            CanonicalPayloadJson = log.CanonicalPayloadJson,
            MetadataJson = log.MetadataJson,
            OccurredAtUtc = log.OccurredAtUtc,
        };
    }

    private static FccSimulatorProfile MapProfileEntity(FccSimulatorProfileSnapshot profile)
    {
        return new FccSimulatorProfile
        {
            Id = profile.Id,
            LabEnvironmentId = profile.LabEnvironmentId,
            ProfileKey = profile.ProfileKey,
            Name = profile.Name,
            VendorFamily = profile.VendorFamily,
            AuthMode = profile.AuthMode,
            DeliveryMode = profile.DeliveryMode,
            PreAuthMode = profile.PreAuthMode,
            EndpointBasePath = profile.EndpointBasePath,
            EndpointSurfaceJson = profile.EndpointSurfaceJson,
            AuthConfigurationJson = profile.AuthConfigurationJson,
            CapabilitiesJson = profile.CapabilitiesJson,
            RequestTemplatesJson = profile.RequestTemplatesJson,
            ResponseTemplatesJson = profile.ResponseTemplatesJson,
            ValidationRulesJson = profile.ValidationRulesJson,
            FieldMappingsJson = profile.FieldMappingsJson,
            FailureSimulationJson = profile.FailureSimulationJson,
            ExtensionConfigurationJson = profile.ExtensionConfigurationJson,
            IsActive = profile.IsActive,
            IsDefault = profile.IsDefault,
            CreatedAtUtc = profile.CreatedAtUtc,
            UpdatedAtUtc = profile.UpdatedAtUtc,
        };
    }

    private static Product MapProductEntity(ProductSnapshot product)
    {
        return new Product
        {
            Id = product.Id,
            LabEnvironmentId = product.LabEnvironmentId,
            ProductCode = product.ProductCode,
            Name = product.Name,
            Grade = product.Grade,
            ColorHex = product.ColorHex,
            UnitPrice = product.UnitPrice,
            CurrencyCode = product.CurrencyCode,
            IsActive = product.IsActive,
            UpdatedAtUtc = product.UpdatedAtUtc,
        };
    }

    private static Site MapSiteEntity(SiteSnapshot site)
    {
        return new Site
        {
            Id = site.Id,
            LabEnvironmentId = site.LabEnvironmentId,
            ActiveFccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
            SiteCode = site.SiteCode,
            Name = site.Name,
            TimeZone = site.TimeZone,
            CurrencyCode = site.CurrencyCode,
            ExternalReference = site.ExternalReference,
            InboundAuthMode = site.InboundAuthMode,
            ApiKeyHeaderName = site.ApiKeyHeaderName,
            ApiKeyValue = site.ApiKeyValue,
            BasicAuthUsername = site.BasicAuthUsername,
            BasicAuthPassword = site.BasicAuthPassword,
            DeliveryMode = site.DeliveryMode,
            PreAuthMode = site.PreAuthMode,
            FccVendor = site.FccVendor,
            SettingsJson = site.SettingsJson,
            IsActive = site.IsActive,
            CreatedAtUtc = site.CreatedAtUtc,
            UpdatedAtUtc = site.UpdatedAtUtc,
        };
    }

    private static CallbackTarget MapCallbackTargetEntity(CallbackTargetSnapshot target)
    {
        return new CallbackTarget
        {
            Id = target.Id,
            LabEnvironmentId = target.LabEnvironmentId,
            SiteId = target.SiteId,
            TargetKey = target.TargetKey,
            Name = target.Name,
            CallbackUrl = new Uri(target.CallbackUrl, UriKind.Absolute),
            AuthMode = target.AuthMode,
            ApiKeyHeaderName = target.ApiKeyHeaderName,
            ApiKeyValue = target.ApiKeyValue,
            BasicAuthUsername = target.BasicAuthUsername,
            BasicAuthPassword = target.BasicAuthPassword,
            IsActive = target.IsActive,
            CreatedAtUtc = target.CreatedAtUtc,
        };
    }

    private static Pump MapPumpEntity(PumpSnapshot pump)
    {
        return new Pump
        {
            Id = pump.Id,
            SiteId = pump.SiteId,
            PumpNumber = pump.PumpNumber,
            FccPumpNumber = pump.FccPumpNumber,
            LayoutX = pump.LayoutX,
            LayoutY = pump.LayoutY,
            Label = pump.Label,
            IsActive = pump.IsActive,
            CreatedAtUtc = pump.CreatedAtUtc,
        };
    }

    private static Nozzle MapNozzleEntity(NozzleSnapshot nozzle)
    {
        return new Nozzle
        {
            Id = nozzle.Id,
            PumpId = nozzle.PumpId,
            ProductId = nozzle.ProductId,
            NozzleNumber = nozzle.NozzleNumber,
            FccNozzleNumber = nozzle.FccNozzleNumber,
            Label = nozzle.Label,
            State = nozzle.State,
            SimulationStateJson = nozzle.SimulationStateJson,
            IsActive = nozzle.IsActive,
            UpdatedAtUtc = nozzle.UpdatedAtUtc,
        };
    }

    private static ScenarioDefinition MapScenarioDefinitionEntity(ScenarioDefinitionSnapshot definition)
    {
        return new ScenarioDefinition
        {
            Id = definition.Id,
            LabEnvironmentId = definition.LabEnvironmentId,
            ScenarioKey = definition.ScenarioKey,
            Name = definition.Name,
            Description = definition.Description,
            DeterministicSeed = definition.DeterministicSeed,
            DefinitionJson = definition.DefinitionJson,
            ReplaySignature = definition.ReplaySignature,
            IsActive = definition.IsActive,
            CreatedAtUtc = definition.CreatedAtUtc,
            UpdatedAtUtc = definition.UpdatedAtUtc,
        };
    }

    private static ScenarioRun MapScenarioRunEntity(ScenarioRunSnapshot run)
    {
        return new ScenarioRun
        {
            Id = run.Id,
            SiteId = run.SiteId,
            ScenarioDefinitionId = run.ScenarioDefinitionId,
            CorrelationId = run.CorrelationId,
            ReplaySeed = run.ReplaySeed,
            ReplaySignature = run.ReplaySignature,
            Status = run.Status,
            InputSnapshotJson = run.InputSnapshotJson,
            ResultSummaryJson = run.ResultSummaryJson,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
        };
    }

    private static PreAuthSession MapPreAuthSessionEntity(PreAuthSessionSnapshot session)
    {
        return new PreAuthSession
        {
            Id = session.Id,
            SiteId = session.SiteId,
            PumpId = session.PumpId,
            NozzleId = session.NozzleId,
            ScenarioRunId = session.ScenarioRunId,
            CorrelationId = session.CorrelationId,
            ExternalReference = session.ExternalReference,
            Mode = session.Mode,
            Status = session.Status,
            ReservedAmount = session.ReservedAmount,
            AuthorizedAmount = session.AuthorizedAmount,
            FinalAmount = session.FinalAmount,
            FinalVolume = session.FinalVolume,
            RawRequestJson = session.RawRequestJson,
            CanonicalRequestJson = session.CanonicalRequestJson,
            RawResponseJson = session.RawResponseJson,
            CanonicalResponseJson = session.CanonicalResponseJson,
            TimelineJson = session.TimelineJson,
            CreatedAtUtc = session.CreatedAtUtc,
            AuthorizedAtUtc = session.AuthorizedAtUtc,
            CompletedAtUtc = session.CompletedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc,
        };
    }

    private static SimulatedTransaction MapSimulatedTransactionEntity(SimulatedTransactionSnapshot transaction)
    {
        return new SimulatedTransaction
        {
            Id = transaction.Id,
            SiteId = transaction.SiteId,
            PumpId = transaction.PumpId,
            NozzleId = transaction.NozzleId,
            ProductId = transaction.ProductId,
            PreAuthSessionId = transaction.PreAuthSessionId,
            ScenarioRunId = transaction.ScenarioRunId,
            CorrelationId = transaction.CorrelationId,
            ExternalTransactionId = transaction.ExternalTransactionId,
            DeliveryMode = transaction.DeliveryMode,
            Status = transaction.Status,
            Volume = transaction.Volume,
            UnitPrice = transaction.UnitPrice,
            TotalAmount = transaction.TotalAmount,
            OccurredAtUtc = transaction.OccurredAtUtc,
            CreatedAtUtc = transaction.CreatedAtUtc,
            DeliveredAtUtc = transaction.DeliveredAtUtc,
            RawPayloadJson = transaction.RawPayloadJson,
            CanonicalPayloadJson = transaction.CanonicalPayloadJson,
            RawHeadersJson = transaction.RawHeadersJson,
            DeliveryCursor = transaction.DeliveryCursor,
            MetadataJson = transaction.MetadataJson,
            TimelineJson = transaction.TimelineJson,
        };
    }

    private static CallbackAttempt MapCallbackAttemptEntity(CallbackAttemptSnapshot attempt)
    {
        return new CallbackAttempt
        {
            Id = attempt.Id,
            CallbackTargetId = attempt.CallbackTargetId,
            SimulatedTransactionId = attempt.SimulatedTransactionId,
            CorrelationId = attempt.CorrelationId,
            AttemptNumber = attempt.AttemptNumber,
            Status = attempt.Status,
            ResponseStatusCode = attempt.ResponseStatusCode,
            RequestUrl = attempt.RequestUrl,
            RequestHeadersJson = attempt.RequestHeadersJson,
            RequestPayloadJson = attempt.RequestPayloadJson,
            ResponseHeadersJson = attempt.ResponseHeadersJson,
            ResponsePayloadJson = attempt.ResponsePayloadJson,
            ErrorMessage = attempt.ErrorMessage,
            RetryCount = attempt.RetryCount,
            MaxRetryCount = attempt.MaxRetryCount,
            AttemptedAtUtc = attempt.AttemptedAtUtc,
            CompletedAtUtc = attempt.CompletedAtUtc,
            NextRetryAtUtc = attempt.NextRetryAtUtc,
            AcknowledgedAtUtc = attempt.AcknowledgedAtUtc,
        };
    }

    private static LabEventLog MapLabEventLogEntity(LabEventLogSnapshot log)
    {
        return new LabEventLog
        {
            Id = log.Id,
            SiteId = log.SiteId,
            FccSimulatorProfileId = log.FccSimulatorProfileId,
            PreAuthSessionId = log.PreAuthSessionId,
            SimulatedTransactionId = log.SimulatedTransactionId,
            ScenarioRunId = log.ScenarioRunId,
            CorrelationId = log.CorrelationId,
            Severity = log.Severity,
            Category = log.Category,
            EventType = log.EventType,
            Message = log.Message,
            RawPayloadJson = log.RawPayloadJson,
            CanonicalPayloadJson = log.CanonicalPayloadJson,
            MetadataJson = log.MetadataJson,
            OccurredAtUtc = log.OccurredAtUtc,
        };
    }

    private static SiteSettingsView DeserializeSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SiteSettingsView();
        }

        try
        {
            return JsonSerializer.Deserialize<SiteSettingsView>(json, JsonOptions) ?? new SiteSettingsView();
        }
        catch (JsonException)
        {
            return new SiteSettingsView();
        }
    }

    private static SiteSettingsView CloneSettings(SiteSettingsView settings)
    {
        return new SiteSettingsView
        {
            IsTemplate = settings.IsTemplate,
            DefaultCallbackTargetKey = settings.DefaultCallbackTargetKey,
            PullPageSize = settings.PullPageSize,
            Fiscalization = new SiteFiscalizationSettings
            {
                Mode = settings.Fiscalization.Mode,
                RequireCustomerTaxId = settings.Fiscalization.RequireCustomerTaxId,
                FiscalReceiptRequired = settings.Fiscalization.FiscalReceiptRequired,
                TaxAuthorityName = settings.Fiscalization.TaxAuthorityName,
                TaxAuthorityEndpoint = settings.Fiscalization.TaxAuthorityEndpoint,
            },
        };
    }

    private static string SerializeSettings(SiteSettingsView settings) => Serialize(settings);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static bool IsValidColorHex(string colorHex)
    {
        string normalized = colorHex.Trim();
        if (normalized.Length is not (4 or 7) || normalized[0] != '#')
        {
            return false;
        }

        return normalized[1..].All(Uri.IsHexDigit);
    }

    private static string NormalizeColorHex(string colorHex)
    {
        string trimmed = colorHex.Trim();
        return trimmed.StartsWith('#') ? trimmed.ToUpperInvariant() : $"#{trimmed.ToUpperInvariant()}";
    }

    private async Task<string> CreateUniqueCallbackTargetKeyAsync(string siteCode, int index, CancellationToken cancellationToken)
    {
        string baseKey = $"{siteCode.Trim().ToLowerInvariant().Replace(' ', '-')}-callback-{index}";
        string candidate = baseKey;
        int suffix = 1;

        while (await dbContext.CallbackTargets.AsNoTracking().AnyAsync(x => x.TargetKey == candidate, cancellationToken))
        {
            candidate = $"{baseKey}-{suffix++}";
        }

        return candidate;
    }

    private static List<ManagementValidationMessage> BuildCompatibilityMessagesSnapshot(
        Site site,
        FccSimulatorProfile profile,
        IReadOnlySet<string> callbackKeys)
    {
        SiteSettingsView settings = DeserializeSettings(site.SettingsJson);
        FccProfileContract contract = FccProfileService.ToRecord(profile).Contract;
        List<ManagementValidationMessage> messages = [];

        if (!profile.IsActive)
        {
            messages.Add(new("activeFccSimulatorProfileId", $"Profile '{profile.ProfileKey}' is inactive and cannot be assigned to a site.", "Error", "profile_inactive"));
        }

        if (profile.LabEnvironmentId != site.LabEnvironmentId)
        {
            messages.Add(new("activeFccSimulatorProfileId", $"Profile '{profile.ProfileKey}' belongs to a different lab environment.", "Error", "profile_environment_mismatch"));
        }

        if (!IsDeliveryModeSupported(site.DeliveryMode, contract.Capabilities))
        {
            messages.Add(new("deliveryMode", $"Profile '{profile.ProfileKey}' does not support site delivery mode '{site.DeliveryMode}'.", "Error", "delivery_mode_incompatible"));
        }

        if (site.PreAuthMode != contract.PreAuthMode)
        {
            messages.Add(new("preAuthMode", $"Site pre-auth mode '{site.PreAuthMode}' does not match profile '{profile.ProfileKey}' mode '{contract.PreAuthMode}'.", "Error", "preauth_mode_incompatible"));
        }

        if (site.DeliveryMode is TransactionDeliveryMode.Push or TransactionDeliveryMode.Hybrid &&
            string.IsNullOrWhiteSpace(settings.DefaultCallbackTargetKey))
        {
            messages.Add(new(
                "settings.defaultCallbackTargetKey",
                $"Site delivery mode '{site.DeliveryMode}' requires an explicit default callback target.",
                "Error",
                "callback_target_required"));
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultCallbackTargetKey) && !callbackKeys.Contains(settings.DefaultCallbackTargetKey))
        {
            messages.Add(new("settings.defaultCallbackTargetKey", $"Default callback target '{settings.DefaultCallbackTargetKey}' was not found for this site.", "Error", "callback_target_not_found"));
        }

        if (!site.Pumps.Any())
        {
            messages.Add(new("forecourt", "Site does not have any pumps configured yet.", "Warning", "forecourt_empty"));
        }

        return messages;
    }

    private static CallbackTarget? ResolveDefaultCallbackTarget(Site site, SiteSettingsView settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultCallbackTargetKey))
        {
            return null;
        }

        return site.CallbackTargets
            .FirstOrDefault(x => x.IsActive && x.TargetKey == settings.DefaultCallbackTargetKey);
    }

    private static SiteSeedResult CreateSeedResult(Site site)
    {
        return new SiteSeedResult
        {
            SiteId = site.Id,
            SiteCode = site.SiteCode,
        };
    }

    private async Task<SiteSeedResult> ResetSiteInternalAsync(Site site, CancellationToken cancellationToken)
    {
        SiteSeedResult result = CreateSeedResult(site);
        result.ResetApplied = true;

        List<Guid> transactionIds = await dbContext.SimulatedTransactions
            .Where(x => x.SiteId == site.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        List<CallbackAttempt> attempts = transactionIds.Count == 0
            ? []
            : await dbContext.CallbackAttempts
                .Where(x => transactionIds.Contains(x.SimulatedTransactionId))
                .ToListAsync(cancellationToken);

        List<LabEventLog> logs = await dbContext.LabEventLogs
            .Where(x => x.SiteId == site.Id)
            .ToListAsync(cancellationToken);

        List<SimulatedTransaction> transactions = await dbContext.SimulatedTransactions
            .Where(x => x.SiteId == site.Id)
            .ToListAsync(cancellationToken);

        List<PreAuthSession> sessions = await dbContext.PreAuthSessions
            .Where(x => x.SiteId == site.Id)
            .ToListAsync(cancellationToken);

        List<ScenarioRun> scenarioRuns = await dbContext.ScenarioRuns
            .Where(x => x.SiteId == site.Id)
            .ToListAsync(cancellationToken);

        result.CallbackAttemptsRemoved = attempts.Count;
        result.LogsRemoved = logs.Count;
        result.TransactionsRemoved = transactions.Count;
        result.PreAuthSessionsRemoved = sessions.Count;

        if (attempts.Count > 0)
        {
            dbContext.CallbackAttempts.RemoveRange(attempts);
        }

        if (logs.Count > 0)
        {
            dbContext.LabEventLogs.RemoveRange(logs);
        }

        if (transactions.Count > 0)
        {
            dbContext.SimulatedTransactions.RemoveRange(transactions);
        }

        if (sessions.Count > 0)
        {
            dbContext.PreAuthSessions.RemoveRange(sessions);
        }

        if (scenarioRuns.Count > 0)
        {
            dbContext.ScenarioRuns.RemoveRange(scenarioRuns);
        }

        int nozzlesReset = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (Nozzle nozzle in site.Pumps.SelectMany(x => x.Nozzles))
        {
            nozzle.State = NozzleState.Idle;
            nozzle.SimulationStateJson = "{}";
            nozzle.UpdatedAtUtc = now;
            nozzlesReset++;
        }

        result.NozzlesReset = nozzlesReset;
        return result;
    }
}

public sealed class ManagementOperationException(
    int statusCode,
    string message,
    IReadOnlyList<ManagementValidationMessage> messages) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyList<ManagementValidationMessage> Messages { get; } = messages;
}
