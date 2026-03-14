using System.Text.Json;
using FccMiddleware.Application.Observability;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Api.AgentControl;

public sealed class AgentPushHintDispatcher : IAgentPushHintDispatcher
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IAgentPushHintSender _sender;
    private readonly IObservabilityMetrics _metrics;
    private readonly IOptions<AgentCommandsOptions> _options;
    private readonly ILogger<AgentPushHintDispatcher> _logger;

    public AgentPushHintDispatcher(
        FccMiddlewareDbContext db,
        IAgentPushHintSender sender,
        IObservabilityMetrics metrics,
        IOptions<AgentCommandsOptions> options,
        ILogger<AgentPushHintDispatcher> logger)
    {
        _db = db;
        _sender = sender;
        _metrics = metrics;
        _options = options;
        _logger = logger;
    }

    public async Task<PushHintDispatchSummary> SendCommandPendingHintAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled || !_options.Value.FcmHintsEnabled)
        {
            return new PushHintDispatchSummary(0, 0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var installations = await LoadAndroidInstallationsForDeviceAsync(command.DeviceId, cancellationToken);
        if (installations.Count == 0)
        {
            return new PushHintDispatchSummary(0, 0, 0);
        }

        var commandCount = await _db.AgentCommands
            .IgnoreQueryFilters()
            .CountAsync(item =>
                item.DeviceId == command.DeviceId
                && item.ExpiresAt > now
                && (item.Status == AgentCommandStatus.PENDING || item.Status == AgentCommandStatus.DELIVERY_HINT_SENT),
                cancellationToken);

        var request = new PushHintRequest(
            PushHintKind.COMMAND_PENDING,
            command.DeviceId,
            CommandCount: commandCount);

        var summary = await DispatchAsync(
            installations,
            request,
            command.LegalEntityId,
            command.SiteCode,
            commandId: command.Id,
            cancellationToken);

        command.AttemptCount += summary.AttemptedCount;
        if (summary.AttemptedCount > 0)
        {
            command.UpdatedAt = now;
        }

        if (summary.FailureCount > 0 && summary.SuccessCount == 0)
        {
            command.LastError = "Push hint delivery failed for all Android installations.";
        }

        if (summary.SuccessCount > 0 && command.Status == AgentCommandStatus.PENDING)
        {
            command.Status = AgentCommandStatus.DELIVERY_HINT_SENT;
            command.UpdatedAt = now;
            command.LastError = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return summary;
    }

    public async Task<PushHintDispatchSummary> SendConfigChangedHintsForSiteAsync(
        Guid legalEntityId,
        string siteCode,
        int configVersion,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled || !_options.Value.FcmHintsEnabled)
        {
            return new PushHintDispatchSummary(0, 0, 0);
        }

        var installations = await _db.AgentInstallations
            .IgnoreQueryFilters()
            .Where(item =>
                item.LegalEntityId == legalEntityId
                && item.SiteCode == siteCode
                && item.Platform == "ANDROID"
                && item.PushProvider == "FCM")
            .Join(
                _db.AgentRegistrations.IgnoreQueryFilters().Where(agent => agent.IsActive),
                installation => installation.DeviceId,
                agent => agent.Id,
                (installation, _) => installation)
            .ToListAsync(cancellationToken);

        if (installations.Count == 0)
        {
            return new PushHintDispatchSummary(0, 0, 0);
        }

        var summary = new PushHintDispatchSummary(0, 0, 0);
        foreach (var group in installations.GroupBy(item => item.DeviceId))
        {
            var request = new PushHintRequest(
                PushHintKind.CONFIG_CHANGED,
                group.Key,
                ConfigVersion: configVersion);

            var deviceSummary = await DispatchAsync(
                group.ToList(),
                request,
                legalEntityId,
                siteCode,
                commandId: null,
                cancellationToken);

            summary = new PushHintDispatchSummary(
                summary.AttemptedCount + deviceSummary.AttemptedCount,
                summary.SuccessCount + deviceSummary.SuccessCount,
                summary.FailureCount + deviceSummary.FailureCount);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return summary;
    }

    private async Task<PushHintDispatchSummary> DispatchAsync(
        IReadOnlyList<AgentInstallation> installations,
        PushHintRequest request,
        Guid legalEntityId,
        string siteCode,
        Guid? commandId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;

        foreach (var installation in installations)
        {
            attempted++;
            _metrics.RecordAgentPushHintAttempted(legalEntityId, siteCode, installation.DeviceId, request.Kind.ToString());

            var result = await _sender.SendAsync(
                installation.RegistrationToken,
                request,
                cancellationToken);

            if (result.Succeeded)
            {
                succeeded++;
                installation.LastHintSentAt = now;
                installation.UpdatedAt = now;
                _metrics.RecordAgentPushHintSucceeded(legalEntityId, siteCode, installation.DeviceId, request.Kind.ToString());

                _db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now,
                    LegalEntityId = legalEntityId,
                    EventType = AgentControlAuditEventTypes.AgentPushHintSent,
                    CorrelationId = Guid.NewGuid(),
                    SiteCode = siteCode,
                    Source = nameof(AgentPushHintDispatcher),
                    EntityId = installation.DeviceId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        DeviceId = installation.DeviceId,
                        InstallationId = installation.Id,
                        Kind = request.Kind,
                        CommandId = commandId,
                        request.CommandCount,
                        request.ConfigVersion,
                        SentAt = now,
                        ProviderMessageId = result.ProviderMessageId
                    })
                });

                continue;
            }

            failed++;
            _metrics.RecordAgentPushHintFailed(legalEntityId, siteCode, installation.DeviceId, request.Kind.ToString());

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                LegalEntityId = legalEntityId,
                EventType = AgentControlAuditEventTypes.AgentPushHintFailed,
                CorrelationId = Guid.NewGuid(),
                SiteCode = siteCode,
                Source = nameof(AgentPushHintDispatcher),
                EntityId = installation.DeviceId,
                Payload = JsonSerializer.Serialize(new
                {
                    DeviceId = installation.DeviceId,
                    InstallationId = installation.Id,
                    Kind = request.Kind,
                    CommandId = commandId,
                    request.CommandCount,
                    request.ConfigVersion,
                    FailedAt = now,
                    result.ErrorCode,
                    result.ErrorMessage
                })
            });

            _logger.LogWarning(
                "Push hint failed for device {DeviceId}, installation {InstallationId}, kind {Kind}. ErrorCode={ErrorCode}",
                installation.DeviceId,
                installation.Id,
                request.Kind,
                result.ErrorCode);
        }

        return new PushHintDispatchSummary(attempted, succeeded, failed);
    }

    private Task<List<AgentInstallation>> LoadAndroidInstallationsForDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken) =>
        _db.AgentInstallations
            .IgnoreQueryFilters()
            .Where(item =>
                item.DeviceId == deviceId
                && item.Platform == "ANDROID"
                && item.PushProvider == "FCM")
            .ToListAsync(cancellationToken);
}
