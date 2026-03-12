using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.DeadLetter;

/// <summary>
/// Replays dead-letter items through the original ingestion pipeline.
/// Supports TRANSACTION and PRE_AUTH types that have stored raw payload.
/// </summary>
public sealed class DlqReplayService : IDlqReplayService
{
    private readonly FccMiddlewareDbContext _db;
    private readonly IMediator _mediator;
    private readonly ILogger<DlqReplayService> _logger;

    public DlqReplayService(
        FccMiddlewareDbContext db,
        IMediator mediator,
        ILogger<DlqReplayService> logger)
    {
        _db = db;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<DlqReplayResult> ReplayAsync(Guid deadLetterId, CancellationToken cancellationToken = default)
    {
        var item = await _db.DeadLetterItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == deadLetterId, cancellationToken);

        if (item is null)
        {
            return new DlqReplayResult
            {
                Success = false,
                ErrorCode = "NOT_FOUND",
                ErrorMessage = "Dead-letter item not found."
            };
        }

        if (item.Status is DeadLetterStatus.RESOLVED or DeadLetterStatus.DISCARDED)
        {
            return new DlqReplayResult
            {
                Success = false,
                ErrorCode = "INVALID_STATE",
                ErrorMessage = $"Cannot replay item in {item.Status} state."
            };
        }

        // Transition to RETRYING
        item.Status = DeadLetterStatus.RETRYING;
        item.RetryCount += 1;
        item.LastRetryAt = DateTimeOffset.UtcNow;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var result = item.Type switch
            {
                DeadLetterType.TRANSACTION => await ReplayTransactionAsync(item, cancellationToken),
                DeadLetterType.PRE_AUTH => await ReplayPreAuthAsync(item, cancellationToken),
                _ => new DlqReplayResult
                {
                    Success = false,
                    ErrorCode = "UNSUPPORTED_TYPE",
                    ErrorMessage = $"Replay not supported for type '{item.Type}'."
                }
            };

            // Record outcome in retry history
            var history = DeserializeHistory(item);
            history.Add(new RetryHistoryEntryDto
            {
                AttemptNumber = item.RetryCount,
                AttemptedAt = DateTimeOffset.UtcNow,
                Outcome = result.Success ? "SUCCESS" : "FAILED",
                ErrorCode = result.ErrorCode,
                ErrorMessage = result.ErrorMessage
            });
            item.RetryHistoryJson = JsonSerializer.Serialize(history);

            if (result.Success)
            {
                item.Status = DeadLetterStatus.RESOLVED;
                _logger.LogInformation(
                    "Dead-letter {DeadLetterId} replayed successfully, created entity {EntityId}",
                    deadLetterId, result.ReplayedEntityId);
            }
            else
            {
                item.Status = DeadLetterStatus.REPLAY_FAILED;
                _logger.LogWarning(
                    "Dead-letter {DeadLetterId} replay failed: {ErrorCode} — {ErrorMessage}",
                    deadLetterId, result.ErrorCode, result.ErrorMessage);
            }

            item.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error replaying dead-letter {DeadLetterId}", deadLetterId);

            var history = DeserializeHistory(item);
            history.Add(new RetryHistoryEntryDto
            {
                AttemptNumber = item.RetryCount,
                AttemptedAt = DateTimeOffset.UtcNow,
                Outcome = "FAILED",
                ErrorCode = "REPLAY_EXCEPTION",
                ErrorMessage = ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message
            });
            item.RetryHistoryJson = JsonSerializer.Serialize(history);
            item.Status = DeadLetterStatus.REPLAY_FAILED;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            return new DlqReplayResult
            {
                Success = false,
                ErrorCode = "REPLAY_EXCEPTION",
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<DlqReplayResult> ReplayTransactionAsync(
        Domain.Entities.DeadLetterItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.RawPayloadJson))
        {
            return new DlqReplayResult
            {
                Success = false,
                ErrorCode = "NO_PAYLOAD",
                ErrorMessage = "Dead-letter item has no stored raw payload to replay."
            };
        }

        // Parse the stored payload to extract the vendor and reconstruct the command.
        // The payload is the original raw FCC payload that was sent during ingestion.
        var command = new IngestTransactionCommand
        {
            FccVendor = Enum.TryParse<FccVendor>(ExtractField(item.RawPayloadJson, "fccVendor"), true, out var v)
                ? v : FccVendor.DOMS,
            SiteCode = item.SiteCode,
            CapturedAt = DateTimeOffset.UtcNow,
            RawPayload = item.RawPayloadJson,
            ContentType = "application/json",
            CorrelationId = Guid.NewGuid()
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            return new DlqReplayResult
            {
                Success = true,
                ReplayedEntityId = result.Value!.TransactionId
            };
        }

        return new DlqReplayResult
        {
            Success = false,
            ErrorCode = result.Error?.Code,
            ErrorMessage = result.Error?.Message
        };
    }

    private Task<DlqReplayResult> ReplayPreAuthAsync(
        Domain.Entities.DeadLetterItem item,
        CancellationToken cancellationToken)
    {
        // Pre-auth replay requires reconstructing the ForwardPreAuthCommand from the stored payload.
        // If no payload is stored, replay is not possible.
        if (string.IsNullOrWhiteSpace(item.RawPayloadJson))
        {
            return Task.FromResult(new DlqReplayResult
            {
                Success = false,
                ErrorCode = "NO_PAYLOAD",
                ErrorMessage = "Dead-letter item has no stored raw payload to replay."
            });
        }

        // Pre-auth replay is more complex because it involves state machine transitions.
        // For now, mark as unsupported and require manual intervention.
        return Task.FromResult(new DlqReplayResult
        {
            Success = false,
            ErrorCode = "PREAUTH_REPLAY_NOT_IMPLEMENTED",
            ErrorMessage = "Pre-auth replay requires manual state reconciliation. Use the pre-auth management screen."
        });
    }

    private static string? ExtractField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var prop))
                return prop.GetString();
        }
        catch { }
        return null;
    }

    private static List<RetryHistoryEntryDto> DeserializeHistory(Domain.Entities.DeadLetterItem item) =>
        string.IsNullOrWhiteSpace(item.RetryHistoryJson)
            ? []
            : JsonSerializer.Deserialize<List<RetryHistoryEntryDto>>(item.RetryHistoryJson) ?? [];
}
