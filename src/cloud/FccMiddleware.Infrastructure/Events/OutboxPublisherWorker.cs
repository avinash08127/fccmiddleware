using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Infrastructure.Events;

/// <summary>
/// Background worker that processes the transactional outbox.
/// Polls outbox_messages WHERE processed_at IS NULL ordered by id (sequential bigint).
/// For each message: writes an audit_event, marks processed_at, and logs.
/// Actual message broker (SNS) integration comes later — for now audit_events + log is the sink.
/// </summary>
public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxPublisherWorker> _logger;
    private readonly OutboxWorkerOptions _options;

    public OutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxPublisherWorker> logger,
        IOptions<OutboxWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisherWorker started. PollInterval={PollInterval}s, BatchSize={BatchSize}",
            _options.PollIntervalSeconds, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);

                if (processed == 0)
                {
                    // No messages — wait before next poll
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                }
                // If we processed a full batch, loop immediately to check for more
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxPublisherWorker error during batch processing");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("OutboxPublisherWorker stopped");
    }

    /// <summary>
    /// Fetches a batch of unprocessed outbox messages and processes each one.
    /// Returns the count of messages processed in this batch.
    /// </summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var messages = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (messages.Count == 0)
            return 0;

        var processedCount = 0;

        foreach (var message in messages)
        {
            try
            {
                await ProcessMessageAsync(db, message, ct);
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process outbox message {MessageId} (EventType={EventType})",
                    message.Id, message.EventType);
                // Skip this message and continue with the rest; it will be retried next cycle
            }
        }

        // Cleanup old processed messages (older than 7 days)
        await CleanupOldMessagesAsync(db, ct);

        _logger.LogInformation("OutboxPublisherWorker processed {Count}/{Total} messages",
            processedCount, messages.Count);

        return processedCount;
    }

    private async Task ProcessMessageAsync(FccMiddlewareDbContext db, OutboxMessage message, CancellationToken ct)
    {
        // Parse the envelope to extract fields for audit_events
        var envelope = JsonSerializer.Deserialize<JsonElement>(message.Payload);

        var legalEntityId = envelope.TryGetProperty("legalEntityId", out var leiProp)
            && Guid.TryParse(leiProp.GetString(), out var lei)
            ? lei
            : Guid.Empty;

        var siteCode = envelope.TryGetProperty("siteCode", out var scProp)
            ? scProp.GetString()
            : null;

        var source = envelope.TryGetProperty("source", out var srcProp)
            ? srcProp.GetString() ?? "cloud-outbox"
            : "cloud-outbox";

        // Write to audit_events table
        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            LegalEntityId = legalEntityId,
            EventType = message.EventType,
            CorrelationId = message.CorrelationId,
            SiteCode = siteCode,
            Source = source,
            Payload = message.Payload
        };

        db.AuditEvents.Add(auditEvent);

        // Mark outbox message as processed
        message.ProcessedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Outbox message {MessageId} processed: EventType={EventType}, AuditEventId={AuditEventId}",
            message.Id, message.EventType, auditEvent.Id);
    }

    private async Task CleanupOldMessagesAsync(FccMiddlewareDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);

        var deleted = await db.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            _logger.LogInformation("OutboxPublisherWorker cleaned up {Count} old processed messages", deleted);
        }
    }
}

/// <summary>
/// Configuration options for the OutboxPublisherWorker.
/// Bound from configuration section "OutboxWorker".
/// </summary>
public sealed class OutboxWorkerOptions
{
    public const string SectionName = "OutboxWorker";

    /// <summary>Seconds between poll cycles when no messages are found. Default: 5.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Max messages to fetch per poll cycle. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Seconds to wait after an error before retrying. Default: 10.</summary>
    public int ErrorDelaySeconds { get; set; } = 10;

    /// <summary>Days to retain processed outbox messages before cleanup. Default: 7.</summary>
    public int RetentionDays { get; set; } = 7;
}
