using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.Callbacks;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.Callbacks;

public sealed class CallbackCaptureService(VirtualLabDbContext dbContext) : ICallbackCaptureService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<CallbackCaptureResult?> CaptureAsync(
        string targetKey,
        CallbackCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        CallbackTarget? target = await dbContext.CallbackTargets
            .AsNoTracking()
            .Include(x => x.Site)
            .SingleOrDefaultAsync(x => x.TargetKey == targetKey && x.IsActive, cancellationToken);

        if (target is null)
        {
            return null;
        }

        DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
        string authMode = string.Equals(request.AuthMode, "Inherited", StringComparison.OrdinalIgnoreCase)
            ? target.AuthMode.ToString()
            : request.AuthMode;
        string authOutcome = target.AuthMode == SimulatedAuthMode.None && string.Equals(request.AuthOutcome, "Authorized", StringComparison.OrdinalIgnoreCase)
            ? "NotRequired"
            : request.AuthOutcome;
        string requestPayloadJson = SafeJson(request.RequestPayloadJson);
        string requestHeadersJson = SafeJson(request.RequestHeadersJson);
        string responseHeadersJson = SafeJson(request.ResponseHeadersJson);
        string responsePayloadJson = SafeJson(request.ResponsePayloadJson);
        Dictionary<string, string> sampleValues = ExtractSampleValues(requestPayloadJson);
        Dictionary<string, string> requestHeaders = DeserializeStringDictionary(requestHeadersJson);

        string correlationId = ResolveCorrelationId(requestHeaders, sampleValues);
        string? requestTransactionId = sampleValues.TryGetValue("transactionId", out string? externalTransactionId) &&
            !string.IsNullOrWhiteSpace(externalTransactionId)
            ? externalTransactionId
            : null;

        SimulatedTransaction? transaction = null;
        Guid? linkedAttemptId = request.LinkedAttemptId;

        if (!linkedAttemptId.HasValue &&
            requestHeaders.TryGetValue("X-VirtualLab-Attempt-Id", out string? attemptHeaderValue) &&
            Guid.TryParse(attemptHeaderValue, out Guid parsedAttemptId))
        {
            linkedAttemptId = parsedAttemptId;
        }

        if (linkedAttemptId.HasValue)
        {
            CallbackAttempt? matchedAttempt = await dbContext.CallbackAttempts
                .AsNoTracking()
                .Include(x => x.SimulatedTransaction)
                .SingleOrDefaultAsync(x => x.Id == linkedAttemptId.Value && x.CallbackTargetId == target.Id, cancellationToken);

            if (matchedAttempt is not null)
            {
                transaction = matchedAttempt.SimulatedTransaction;
                correlationId = matchedAttempt.CorrelationId;
            }
            else
            {
                linkedAttemptId = null;
            }
        }

        if (transaction is null && !string.IsNullOrWhiteSpace(correlationId))
        {
            transaction = await dbContext.SimulatedTransactions
                .AsNoTracking()
                .Where(x => x.CorrelationId == correlationId)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (transaction is null && !string.IsNullOrWhiteSpace(requestTransactionId))
        {
            transaction = await dbContext.SimulatedTransactions
                .AsNoTracking()
                .Where(x => x.ExternalTransactionId == requestTransactionId)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (transaction is not null && !linkedAttemptId.HasValue)
        {
            int nextAttemptNumber = await dbContext.CallbackAttempts
                .Where(x => x.CallbackTargetId == target.Id && x.SimulatedTransactionId == transaction.Id)
                .Select(x => (int?)x.AttemptNumber)
                .MaxAsync(cancellationToken) ?? 0;

            dbContext.CallbackAttempts.Add(new CallbackAttempt
            {
                Id = Guid.NewGuid(),
                CallbackTargetId = target.Id,
                SimulatedTransactionId = transaction.Id,
                CorrelationId = correlationId,
                AttemptNumber = nextAttemptNumber + 1,
                Status = request.ResponseStatusCode is >= 200 and < 300
                    ? CallbackAttemptStatus.Succeeded
                    : CallbackAttemptStatus.Failed,
                ResponseStatusCode = request.ResponseStatusCode,
                RequestUrl = request.RequestUrl,
                RequestHeadersJson = requestHeadersJson,
                RequestPayloadJson = requestPayloadJson,
                ResponseHeadersJson = responseHeadersJson,
                ResponsePayloadJson = responsePayloadJson,
                RetryCount = 0,
                MaxRetryCount = 0,
                AttemptedAtUtc = capturedAtUtc,
                CompletedAtUtc = capturedAtUtc,
                NextRetryAtUtc = null,
                AcknowledgedAtUtc = request.ResponseStatusCode is >= 200 and < 300 ? capturedAtUtc : null,
            });
        }

        JsonObject correlationMetadata = ParseObject(request.CorrelationMetadataJson);
        correlationMetadata["requestCorrelationId"] = sampleValues.TryGetValue("correlationId", out string? requestCorrelationId) ? requestCorrelationId : correlationId;
        correlationMetadata["requestTransactionId"] = requestTransactionId;
        correlationMetadata["matchedTransactionId"] = transaction?.Id;
        correlationMetadata["matchedTransactionExternalId"] = transaction?.ExternalTransactionId;
        correlationMetadata["linkedAttemptId"] = linkedAttemptId;
        correlationMetadata["isReplay"] = request.ReplayOfCaptureId.HasValue;
        correlationMetadata["replayedFromCaptureId"] = request.ReplayOfCaptureId;
        if (request.ReplayOfCaptureId.HasValue)
        {
            correlationMetadata["originalCorrelationId"] = correlationId;
        }

        LabEventLog captureLog = new()
        {
            Id = Guid.NewGuid(),
            SiteId = target.SiteId,
            FccSimulatorProfileId = target.Site?.ActiveFccSimulatorProfileId,
            SimulatedTransactionId = transaction?.Id,
            CorrelationId = correlationId,
            Severity = request.ResponseStatusCode is >= 200 and < 300 ? "Information" : "Warning",
            Category = "CallbackAttempt",
            EventType = request.ReplayOfCaptureId.HasValue ? "CallbackReplayed" : "CallbackCaptured",
            Message = request.ReplayOfCaptureId.HasValue
                ? $"Replayed callback payload for target '{target.TargetKey}'."
                : $"Captured callback payload for target '{target.TargetKey}'.",
            RawPayloadJson = requestPayloadJson,
            CanonicalPayloadJson = transaction?.CanonicalPayloadJson ?? "{}",
            MetadataJson = JsonSerializer.Serialize(
                new
                {
                    targetKey = target.TargetKey,
                    targetName = target.Name,
                    callbackTargetId = target.Id,
                    authOutcome,
                    authMode,
                    method = string.IsNullOrWhiteSpace(request.HttpMethod) ? HttpMethods.Post : request.HttpMethod,
                    requestUrl = request.RequestUrl,
                    requestHeaders = DeserializeObject(requestHeadersJson),
                    responseStatusCode = request.ResponseStatusCode,
                    responseHeaders = DeserializeObject(responseHeadersJson),
                    responsePayload = DeserializeObject(responsePayloadJson),
                    correlationMetadata = DeserializeObject(correlationMetadata.ToJsonString(JsonOptions)),
                    isReplay = request.ReplayOfCaptureId.HasValue,
                    replayOfCaptureId = request.ReplayOfCaptureId,
                    linkedAttemptId,
                    matchedTransactionId = transaction?.Id,
                    matchedTransactionExternalId = transaction?.ExternalTransactionId,
                },
                JsonOptions),
            OccurredAtUtc = capturedAtUtc,
        };

        dbContext.LabEventLogs.Add(captureLog);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CallbackCaptureResult(
            captureLog.Id,
            target.TargetKey,
            correlationId,
            request.ResponseStatusCode,
            responsePayloadJson);
    }

    public async Task<IReadOnlyList<CallbackHistoryItemView>> ListHistoryAsync(
        string targetKey,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CallbackTarget? target = await dbContext.CallbackTargets
            .AsNoTracking()
            .Include(x => x.Site)
            .SingleOrDefaultAsync(x => x.TargetKey == targetKey && x.IsActive, cancellationToken);

        if (target is null)
        {
            return [];
        }

        int take = Math.Clamp(limit, 1, 200);
        IQueryable<LabEventLog> query = dbContext.LabEventLogs
            .AsNoTracking()
            .Where(x => x.Category == "CallbackAttempt" || (x.Category == "AuthFailure" && x.EventType == "CallbackAuthRejected"));

        if (target.SiteId.HasValue)
        {
            Guid siteId = target.SiteId.Value;
            query = query.Where(x => x.SiteId == siteId);
        }

        List<LabEventLog> logs = await query
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(Math.Max(take * 4, 50))
            .ToListAsync(cancellationToken);

        return logs
            .Where(x => string.Equals(ReadString(x.MetadataJson, "targetKey"), targetKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(take)
            .Select(x => MapHistoryItem(x, target))
            .ToArray();
    }

    public async Task<CallbackReplayResult?> ReplayAsync(
        string targetKey,
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        CallbackTarget? target = await dbContext.CallbackTargets
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TargetKey == targetKey && x.IsActive, cancellationToken);

        if (target is null)
        {
            return null;
        }

        LabEventLog? source = await dbContext.LabEventLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == captureId, cancellationToken);

        if (source is null || !string.Equals(ReadString(source.MetadataJson, "targetKey"), targetKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Dictionary<string, string> replayHeaders = DeserializeStringDictionary(ExtractJsonProperty(source.MetadataJson, "requestHeaders") ?? "{}");
        ApplyAuthHeaders(replayHeaders, target);
        replayHeaders["X-VirtualLab-Replay-Of"] = captureId.ToString("D");

        CallbackCaptureResult? replay = await CaptureAsync(
            targetKey,
            new CallbackCaptureRequest
            {
                HttpMethod = ReadString(source.MetadataJson, "method") ?? HttpMethods.Post,
                RequestUrl = ReadString(source.MetadataJson, "requestUrl") ?? $"/callbacks/{targetKey}",
                RequestHeadersJson = JsonSerializer.Serialize(replayHeaders, JsonOptions),
                RequestPayloadJson = source.RawPayloadJson,
                AuthOutcome = target.AuthMode == SimulatedAuthMode.None ? "NotRequired" : "Authorized",
                AuthMode = target.AuthMode.ToString(),
                ResponseStatusCode = StatusCodes.Status202Accepted,
                ResponseHeadersJson = """{"content-type":"application/json"}""",
                ResponsePayloadJson = JsonSerializer.Serialize(
                    new
                    {
                        accepted = true,
                        targetKey,
                        replayed = true,
                        replayOfCaptureId = captureId,
                        capturedAtUtc = DateTimeOffset.UtcNow,
                    },
                    JsonOptions),
                CorrelationMetadataJson = ExtractJsonProperty(source.MetadataJson, "correlationMetadata"),
                ReplayOfCaptureId = captureId,
            },
            cancellationToken);

        return replay is null
            ? null
            : new CallbackReplayResult(
                replay.CaptureId,
                targetKey,
                replay.CorrelationId,
                "Callback replay captured successfully.");
    }

    private static CallbackHistoryItemView MapHistoryItem(LabEventLog log, CallbackTarget target)
    {
        string requestHeadersJson = ExtractJsonProperty(log.MetadataJson, "requestHeaders") ?? "{}";
        string responseHeadersJson = ExtractJsonProperty(log.MetadataJson, "responseHeaders") ?? "{}";
        string responsePayloadJson = ExtractJsonProperty(log.MetadataJson, "responsePayload") ?? "{}";
        string correlationMetadataJson = ExtractJsonProperty(log.MetadataJson, "correlationMetadata") ?? "{}";
        int responseStatusCode = ReadInt(log.MetadataJson, "responseStatusCode") ??
            (string.Equals(log.EventType, "CallbackAuthRejected", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status202Accepted);

        return new CallbackHistoryItemView(
            log.Id,
            ReadGuid(log.MetadataJson, "callbackTargetId") ?? target.Id,
            log.SiteId,
            log.FccSimulatorProfileId,
            log.SimulatedTransactionId,
            target.TargetKey,
            target.Name,
            log.CorrelationId,
            ReadString(correlationMetadataJson, "matchedTransactionExternalId"),
            ReadString(log.MetadataJson, "authOutcome") ??
            (string.Equals(log.EventType, "CallbackAuthRejected", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Authorized"),
            ReadString(log.MetadataJson, "authMode") ?? target.AuthMode.ToString(),
            ReadString(log.MetadataJson, "method") ?? HttpMethods.Post,
            ReadString(log.MetadataJson, "requestUrl") ?? $"/callbacks/{target.TargetKey}",
            requestHeadersJson,
            SafeJson(log.RawPayloadJson),
            responseStatusCode,
            responseHeadersJson,
            responsePayloadJson,
            correlationMetadataJson,
            ReadBool(log.MetadataJson, "isReplay"),
            ReadGuid(log.MetadataJson, "replayOfCaptureId"),
            log.OccurredAtUtc);
    }

    private static void ApplyAuthHeaders(IDictionary<string, string> headers, CallbackTarget target)
    {
        switch (target.AuthMode)
        {
            case SimulatedAuthMode.ApiKey when
                !string.IsNullOrWhiteSpace(target.ApiKeyHeaderName) &&
                !string.IsNullOrWhiteSpace(target.ApiKeyValue):
                headers[target.ApiKeyHeaderName] = target.ApiKeyValue;
                break;
            case SimulatedAuthMode.BasicAuth when
                !string.IsNullOrWhiteSpace(target.BasicAuthUsername) &&
                !string.IsNullOrWhiteSpace(target.BasicAuthPassword):
                string raw = $"{target.BasicAuthUsername}:{target.BasicAuthPassword}";
                headers["Authorization"] = $"Basic {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))}";
                break;
        }
    }

    private static Dictionary<string, string> ExtractSampleValues(string json)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
        {
            return values;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }

    private static string ResolveCorrelationId(
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> sampleValues)
    {
        if (sampleValues.TryGetValue("correlationId", out string? correlationId) && !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        if (headers.TryGetValue("X-Correlation-Id", out string? headerCorrelationId) && !string.IsNullOrWhiteSpace(headerCorrelationId))
        {
            return headerCorrelationId;
        }

        return $"callback-{Guid.NewGuid():N}"[..24];
    }

    private static Dictionary<string, string> DeserializeStringDictionary(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static object? DeserializeObject(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            JsonNode? node = JsonNode.Parse(json);
            return node as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SafeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(json, JsonOptions);
        }
    }

    private static string? ExtractJsonProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? SafeJson(value.GetString()) : value.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? ReadGuid(string json, string propertyName)
    {
        string? value = ReadString(json, propertyName);
        return Guid.TryParse(value, out Guid parsed) ? parsed : null;
    }

    private static int? ReadInt(string json, string propertyName)
    {
        string? value = ReadString(json, propertyName);
        return int.TryParse(value, out int parsed) ? parsed : null;
    }

    private static bool ReadBool(string json, string propertyName)
    {
        string? value = ReadString(json, propertyName);
        return bool.TryParse(value, out bool parsed) && parsed;
    }
}
