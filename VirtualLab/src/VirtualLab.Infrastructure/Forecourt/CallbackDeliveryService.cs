using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualLab.Application.Forecourt;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.Forecourt;

public sealed class CallbackDeliveryService(
    VirtualLabDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    IOptions<CallbackDeliveryOptions> options,
    ILogger<CallbackDeliveryService> logger)
{
    private const string ClientName = "VirtualLab.CallbackDispatch";
    private const string AttemptIdHeaderName = "X-VirtualLab-Attempt-Id";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<PushTransactionAttemptSummary>> QueueAndDispatchAsync(
        Site site,
        IReadOnlyList<SimulatedTransaction> transactions,
        string? requestedTargetKey,
        CancellationToken cancellationToken = default)
    {
        if (transactions.Count == 0)
        {
            return [];
        }

        CallbackTarget? target = await ResolveCallbackTargetAsync(site, requestedTargetKey, cancellationToken);
        if (target is null)
        {
            return [];
        }

        List<(Guid AttemptId, bool DuplicateInjected)> attemptsToDispatch = [];

        foreach (SimulatedTransaction transaction in transactions.OrderBy(x => x.OccurredAtUtc))
        {
            CallbackAttempt? pendingAttempt = await dbContext.CallbackAttempts
                .Where(x =>
                    x.CallbackTargetId == target.Id &&
                    x.SimulatedTransactionId == transaction.Id &&
                    x.Status == CallbackAttemptStatus.Pending)
                .OrderBy(x => x.AttemptNumber)
                .FirstOrDefaultAsync(cancellationToken);

            if (pendingAttempt is not null)
            {
                attemptsToDispatch.Add((pendingAttempt.Id, false));
                continue;
            }

            bool hasSuccessfulAttempt = await dbContext.CallbackAttempts.AnyAsync(
                x =>
                    x.CallbackTargetId == target.Id &&
                    x.SimulatedTransactionId == transaction.Id &&
                    x.Status == CallbackAttemptStatus.Succeeded,
                cancellationToken);

            if (hasSuccessfulAttempt)
            {
                continue;
            }

            TransactionSimulationMetadata metadata = LoadTransactionMetadata(transaction);
            int nextAttemptNumber = await GetNextAttemptNumberAsync(target.Id, transaction.Id, cancellationToken);

            CallbackAttempt primaryAttempt = CreatePendingAttempt(target, transaction, nextAttemptNumber);
            dbContext.CallbackAttempts.Add(primaryAttempt);
            attemptsToDispatch.Add((primaryAttempt.Id, false));

            AppendTransactionTimeline(
                transaction,
                DateTimeOffset.UtcNow,
                "CallbackDispatchQueued",
                transaction.Status.ToString(),
                "Callback delivery attempt queued.",
                new
                {
                    target.TargetKey,
                    primaryAttempt.AttemptNumber,
                });
            AddEventLog(
                site,
                transaction,
                "CallbackAttempt",
                "CallbackDispatchQueued",
                "Callback delivery attempt queued.",
                metadata: new
                {
                    target.TargetKey,
                    primaryAttempt.AttemptNumber,
                });

            if (metadata.DuplicateInjectionEnabled && metadata.PushDuplicateEmissionCount == 0)
            {
                metadata.PushDuplicateEmissionCount++;

                CallbackAttempt duplicateAttempt = CreatePendingAttempt(target, transaction, nextAttemptNumber + 1);
                dbContext.CallbackAttempts.Add(duplicateAttempt);
                attemptsToDispatch.Add((duplicateAttempt.Id, true));

                AppendTransactionTimeline(
                    transaction,
                    DateTimeOffset.UtcNow,
                    "DuplicatePushQueued",
                    transaction.Status.ToString(),
                    "Duplicate push delivery queued for testing.",
                    new
                    {
                        target.TargetKey,
                        duplicateAttempt.AttemptNumber,
                    });
                AddEventLog(
                    site,
                    transaction,
                    "CallbackAttempt",
                    "DuplicatePushQueued",
                    "Duplicate push delivery queued for testing.",
                    metadata: new
                    {
                        target.TargetKey,
                        duplicateAttempt.AttemptNumber,
                    });
            }

            transaction.MetadataJson = Serialize(metadata);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        List<PushTransactionAttemptSummary> results = [];
        foreach ((Guid attemptId, bool duplicateInjected) in attemptsToDispatch)
        {
            results.Add(await DispatchAttemptAsync(attemptId, duplicateInjected, cancellationToken));
        }

        return results;
    }

    public async Task<int> DispatchDueAttemptsAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int take = Math.Max(options.Value.DispatchBatchSize, 1);

        List<Guid> dueAttemptIds = await dbContext.CallbackAttempts
            .Where(x =>
                x.Status == CallbackAttemptStatus.Pending &&
                x.NextRetryAtUtc.HasValue &&
                x.NextRetryAtUtc <= now)
            .OrderBy(x => x.NextRetryAtUtc)
            .ThenBy(x => x.AttemptNumber)
            .Select(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        foreach (Guid attemptId in dueAttemptIds)
        {
            await DispatchAttemptAsync(attemptId, duplicateInjected: false, cancellationToken);
        }

        return dueAttemptIds.Count;
    }

    private async Task<PushTransactionAttemptSummary> DispatchAttemptAsync(
        Guid attemptId,
        bool duplicateInjected,
        CancellationToken cancellationToken)
    {
        CallbackAttempt? claimedAttempt = await TryClaimAttemptAsync(attemptId, cancellationToken);
        if (claimedAttempt is null)
        {
            CallbackAttempt? currentAttempt = await LoadAttemptGraphAsync(attemptId, cancellationToken);
            return currentAttempt is null
                ? new PushTransactionAttemptSummary(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "Missing",
                    duplicateInjected,
                    0,
                    0,
                    0,
                    false,
                    null)
                : CreateSummary(currentAttempt, duplicateInjected);
        }

        SimulatedTransaction transaction = claimedAttempt.SimulatedTransaction;
        Site site = transaction.Site;
        CallbackTarget target = claimedAttempt.CallbackTarget;
        TransactionSimulationMetadata metadata = LoadTransactionMetadata(transaction);
        DateTimeOffset completedAtUtc = DateTimeOffset.UtcNow;

        CallbackDispatchOutcome outcome = metadata.SimulateFailureEnabled && metadata.PushFailureCount == 0
            ? CreateInjectedFailureOutcome(metadata.FailureMessage)
            : await SendAsync(target, claimedAttempt, transaction, cancellationToken);

        if (metadata.SimulateFailureEnabled && metadata.PushFailureCount == 0)
        {
            metadata.PushFailureCount++;
        }

        claimedAttempt.ResponseStatusCode = outcome.StatusCode;
        claimedAttempt.ResponseHeadersJson = outcome.ResponseHeadersJson;
        claimedAttempt.ResponsePayloadJson = outcome.ResponseBody;
        claimedAttempt.ErrorMessage = outcome.ErrorMessage;
        claimedAttempt.CompletedAtUtc = completedAtUtc;
        transaction.MetadataJson = Serialize(metadata);

        if (outcome.IsSuccessStatusCode)
        {
            bool successAlreadyRecorded = claimedAttempt.AcknowledgedAtUtc.HasValue;

            claimedAttempt.Status = CallbackAttemptStatus.Succeeded;
            claimedAttempt.NextRetryAtUtc = null;
            claimedAttempt.AcknowledgedAtUtc ??= completedAtUtc;

            if (!successAlreadyRecorded)
            {
                transaction.Status = SimulatedTransactionStatus.Delivered;
                transaction.DeliveredAtUtc ??= completedAtUtc;
                metadata.PushDeliveryCount++;
                transaction.MetadataJson = Serialize(metadata);

                AppendTransactionTimeline(
                    transaction,
                    completedAtUtc,
                    duplicateInjected ? "DuplicatePushInjected" : "TransactionPushed",
                    "Delivered",
                    duplicateInjected
                        ? "Duplicate push delivery completed."
                        : "Transaction delivered through callback push.",
                    new
                    {
                        target.TargetKey,
                        claimedAttempt.AttemptNumber,
                        claimedAttempt.RetryCount,
                        claimedAttempt.ResponseStatusCode,
                    });
                AddEventLog(
                    site,
                    transaction,
                    "TransactionPushed",
                    duplicateInjected ? "DuplicatePushInjected" : "TransactionPushed",
                    duplicateInjected
                        ? "Duplicate push delivery completed."
                        : "Transaction delivered through callback push.",
                    metadata: new
                    {
                        target.TargetKey,
                        claimedAttempt.AttemptNumber,
                        claimedAttempt.RetryCount,
                        duplicateInjected,
                    });
                AddEventLog(
                    site,
                    transaction,
                    "CallbackAttempt",
                    "CallbackAcknowledged",
                    "Callback target acknowledged delivery.",
                    metadata: new
                    {
                        target.TargetKey,
                        claimedAttempt.AttemptNumber,
                        claimedAttempt.ResponseStatusCode,
                    });

                logger.LogInformation(
                    "Callback delivery succeeded for site {SiteCode}, transaction {TransactionId}, target {TargetKey}, attempt {AttemptNumber}, retries {RetryCount}.",
                    site.SiteCode,
                    transaction.ExternalTransactionId,
                    target.TargetKey,
                    claimedAttempt.AttemptNumber,
                    claimedAttempt.RetryCount);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return CreateSummary(claimedAttempt, duplicateInjected);
        }

        transaction.Status = SimulatedTransactionStatus.Failed;
        claimedAttempt.RetryCount++;

        bool hasRetryRemaining = claimedAttempt.RetryCount <= claimedAttempt.MaxRetryCount;
        if (hasRetryRemaining)
        {
            claimedAttempt.Status = CallbackAttemptStatus.Pending;
            claimedAttempt.NextRetryAtUtc = completedAtUtc.AddSeconds(ResolveRetryDelaySeconds(claimedAttempt.RetryCount));

            AppendTransactionTimeline(
                transaction,
                completedAtUtc,
                "PushDeliveryFailed",
                "Failed",
                "Callback delivery failed and will be retried.",
                new
                {
                    target.TargetKey,
                    claimedAttempt.AttemptNumber,
                    claimedAttempt.RetryCount,
                    claimedAttempt.MaxRetryCount,
                    claimedAttempt.NextRetryAtUtc,
                    claimedAttempt.ResponseStatusCode,
                });
            AddEventLog(
                site,
                transaction,
                "CallbackFailure",
                "PushDeliveryFailed",
                "Callback delivery failed and will be retried.",
                metadata: new
                {
                    target.TargetKey,
                    claimedAttempt.AttemptNumber,
                    claimedAttempt.RetryCount,
                    claimedAttempt.MaxRetryCount,
                    claimedAttempt.ResponseStatusCode,
                    claimedAttempt.ErrorMessage,
                });
            AddEventLog(
                site,
                transaction,
                "CallbackAttempt",
                "CallbackRetryScheduled",
                "Callback retry scheduled.",
                metadata: new
                {
                    target.TargetKey,
                    claimedAttempt.AttemptNumber,
                    claimedAttempt.NextRetryAtUtc,
                    claimedAttempt.RetryCount,
                });

            logger.LogWarning(
                "Callback delivery failed for site {SiteCode}, transaction {TransactionId}, target {TargetKey}, attempt {AttemptNumber}. Retry {RetryCount}/{MaxRetryCount} scheduled for {NextRetryAtUtc}.",
                site.SiteCode,
                transaction.ExternalTransactionId,
                target.TargetKey,
                claimedAttempt.AttemptNumber,
                claimedAttempt.RetryCount,
                claimedAttempt.MaxRetryCount,
                claimedAttempt.NextRetryAtUtc);
        }
        else
        {
            claimedAttempt.Status = CallbackAttemptStatus.Failed;
            claimedAttempt.NextRetryAtUtc = null;

            AppendTransactionTimeline(
                transaction,
                completedAtUtc,
                "PushDeliveryFailed",
                "Failed",
                "Callback delivery failed with no retries remaining.",
                new
                {
                    target.TargetKey,
                    claimedAttempt.AttemptNumber,
                    claimedAttempt.RetryCount,
                    claimedAttempt.MaxRetryCount,
                    claimedAttempt.ResponseStatusCode,
                });
            AddEventLog(
                site,
                transaction,
                "CallbackFailure",
                "PushDeliveryFailed",
                "Callback delivery failed with no retries remaining.",
                metadata: new
                {
                    target.TargetKey,
                    claimedAttempt.AttemptNumber,
                    claimedAttempt.RetryCount,
                    claimedAttempt.MaxRetryCount,
                    claimedAttempt.ResponseStatusCode,
                    claimedAttempt.ErrorMessage,
                });

            logger.LogWarning(
                "Callback delivery exhausted retries for site {SiteCode}, transaction {TransactionId}, target {TargetKey}, attempt {AttemptNumber}.",
                site.SiteCode,
                transaction.ExternalTransactionId,
                target.TargetKey,
                claimedAttempt.AttemptNumber);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return CreateSummary(claimedAttempt, duplicateInjected);
    }

    private async Task<CallbackAttempt?> TryClaimAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int updated = await dbContext.CallbackAttempts
            .Where(x => x.Id == attemptId && x.Status == CallbackAttemptStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, CallbackAttemptStatus.InProgress)
                    .SetProperty(x => x.NextRetryAtUtc, (DateTimeOffset?)null)
                    .SetProperty(x => x.AttemptedAtUtc, now),
                cancellationToken);

        return updated == 0
            ? null
            : await LoadAttemptGraphAsync(attemptId, cancellationToken);
    }

    private async Task<CallbackAttempt?> LoadAttemptGraphAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        return await dbContext.CallbackAttempts
            .Include(x => x.CallbackTarget)
            .Include(x => x.SimulatedTransaction)
                .ThenInclude(x => x.Site)
            .SingleOrDefaultAsync(x => x.Id == attemptId, cancellationToken);
    }

    private async Task<int> GetNextAttemptNumberAsync(Guid callbackTargetId, Guid transactionId, CancellationToken cancellationToken)
    {
        return (await dbContext.CallbackAttempts
            .Where(x => x.CallbackTargetId == callbackTargetId && x.SimulatedTransactionId == transactionId)
            .Select(x => (int?)x.AttemptNumber)
            .MaxAsync(cancellationToken) ?? 0) + 1;
    }

    private CallbackAttempt CreatePendingAttempt(CallbackTarget target, SimulatedTransaction transaction, int attemptNumber)
    {
        Dictionary<string, string> requestHeaders = BuildRequestHeaders(target, transaction.CorrelationId);

        return new CallbackAttempt
        {
            Id = Guid.NewGuid(),
            CallbackTargetId = target.Id,
            SimulatedTransactionId = transaction.Id,
            CorrelationId = transaction.CorrelationId,
            AttemptNumber = attemptNumber,
            Status = CallbackAttemptStatus.Pending,
            ResponseStatusCode = 0,
            RequestUrl = target.CallbackUrl.ToString(),
            RequestHeadersJson = Serialize(requestHeaders),
            RequestPayloadJson = transaction.RawPayloadJson,
            ResponseHeadersJson = "{}",
            ResponsePayloadJson = "{}",
            ErrorMessage = string.Empty,
            RetryCount = 0,
            MaxRetryCount = Math.Max(options.Value.MaxRetryCount, 0),
            AttemptedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private async Task<CallbackTarget?> ResolveCallbackTargetAsync(Site site, string? requestedTargetKey, CancellationToken cancellationToken)
    {
        string? configuredTargetKey = requestedTargetKey;
        if (string.IsNullOrWhiteSpace(configuredTargetKey))
        {
            SiteSettings settings = Deserialize<SiteSettings>(site.SettingsJson, new SiteSettings());
            configuredTargetKey = settings.DefaultCallbackTargetKey;
        }

        return await dbContext.CallbackTargets
            .Where(x =>
                x.SiteId == site.Id &&
                x.IsActive &&
                (string.IsNullOrWhiteSpace(configuredTargetKey) || x.TargetKey == configuredTargetKey))
            .OrderBy(x => x.TargetKey)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CallbackDispatchOutcome> SendAsync(
        CallbackTarget target,
        CallbackAttempt attempt,
        SimulatedTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target.CallbackUrl.Host, "example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            return new CallbackDispatchOutcome(
                true,
                StatusCodes.Status202Accepted,
                """{"content-type":"application/json"}""",
                Serialize(new
                {
                    accepted = true,
                    simulated = true,
                    targetKey = target.TargetKey,
                    transactionId = transaction.ExternalTransactionId,
                }),
                string.Empty);
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, target.CallbackUrl);
            request.Headers.Add("X-Correlation-Id", transaction.CorrelationId);
            request.Headers.Add(AttemptIdHeaderName, attempt.Id.ToString("D"));

            ApplyAuthHeader(target, request.Headers);

            string requestBody = string.IsNullOrWhiteSpace(attempt.RequestPayloadJson) ? "{}" : attempt.RequestPayloadJson;
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            HttpClient client = httpClientFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            return new CallbackDispatchOutcome(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                SerializeResponseHeaders(response),
                string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody,
                string.Empty);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new CallbackDispatchOutcome(
                false,
                StatusCodes.Status504GatewayTimeout,
                "{}",
                "{}",
                exception.Message);
        }
        catch (HttpRequestException exception)
        {
            int statusCode = exception.StatusCode.HasValue
                ? (int)exception.StatusCode.Value
                : StatusCodes.Status503ServiceUnavailable;

            return new CallbackDispatchOutcome(
                false,
                statusCode,
                "{}",
                "{}",
                exception.Message);
        }
    }

    private static CallbackDispatchOutcome CreateInjectedFailureOutcome(string? failureMessage)
    {
        return new CallbackDispatchOutcome(
            false,
            StatusCodes.Status503ServiceUnavailable,
            """{"content-type":"application/json"}""",
            Serialize(new
            {
                accepted = false,
                message = string.IsNullOrWhiteSpace(failureMessage) ? "Injected delivery failure." : failureMessage.Trim(),
            }),
            string.IsNullOrWhiteSpace(failureMessage) ? "Injected delivery failure." : failureMessage.Trim());
    }

    private static void ApplyAuthHeader(CallbackTarget target, HttpRequestHeaders headers)
    {
        switch (target.AuthMode)
        {
            case SimulatedAuthMode.ApiKey when
                !string.IsNullOrWhiteSpace(target.ApiKeyHeaderName) &&
                !string.IsNullOrWhiteSpace(target.ApiKeyValue):
                headers.TryAddWithoutValidation(target.ApiKeyHeaderName, target.ApiKeyValue);
                break;

            case SimulatedAuthMode.BasicAuth when
                !string.IsNullOrWhiteSpace(target.BasicAuthUsername) &&
                !string.IsNullOrWhiteSpace(target.BasicAuthPassword):
                string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.BasicAuthUsername}:{target.BasicAuthPassword}"));
                headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                break;
        }
    }

    private static Dictionary<string, string> BuildRequestHeaders(CallbackTarget target, string correlationId)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/json",
            ["x-correlation-id"] = correlationId,
        };

        switch (target.AuthMode)
        {
            case SimulatedAuthMode.ApiKey when !string.IsNullOrWhiteSpace(target.ApiKeyHeaderName):
                headers[target.ApiKeyHeaderName] = "[REDACTED]";
                break;

            case SimulatedAuthMode.BasicAuth:
                headers["authorization"] = "[REDACTED]";
                break;
        }

        return headers;
    }

    private static string SerializeResponseHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (response.Content.Headers.ContentType is not null)
        {
            headers["content-type"] = response.Content.Headers.ContentType.ToString();
        }

        return Serialize(headers);
    }

    private int ResolveRetryDelaySeconds(int retryCount)
    {
        int[] configuredDelays = options.Value.RetryDelaysSeconds is { Length: > 0 }
            ? options.Value.RetryDelaysSeconds
            : [2, 10, 30];

        int index = Math.Clamp(retryCount - 1, 0, configuredDelays.Length - 1);
        return Math.Max(configuredDelays[index], 1);
    }

    private static PushTransactionAttemptSummary CreateSummary(CallbackAttempt attempt, bool duplicateInjected)
    {
        return new PushTransactionAttemptSummary(
            attempt.SimulatedTransaction.ExternalTransactionId,
            attempt.CorrelationId,
            attempt.CallbackTarget.TargetKey,
            attempt.Status switch
            {
                CallbackAttemptStatus.Pending when attempt.NextRetryAtUtc.HasValue => "PendingRetry",
                CallbackAttemptStatus.Pending => "Pending",
                CallbackAttemptStatus.InProgress => "InProgress",
                _ => attempt.Status.ToString(),
            },
            duplicateInjected,
            attempt.AttemptNumber,
            attempt.RetryCount,
            attempt.ResponseStatusCode,
            attempt.AcknowledgedAtUtc.HasValue,
            attempt.NextRetryAtUtc);
    }

    private void AddEventLog(
        Site site,
        SimulatedTransaction transaction,
        string category,
        string eventType,
        string message,
        object? metadata = null)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            FccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
            PreAuthSessionId = transaction.PreAuthSessionId,
            SimulatedTransactionId = transaction.Id,
            CorrelationId = transaction.CorrelationId,
            Severity = category == "CallbackFailure" ? "Warning" : "Information",
            Category = category,
            EventType = eventType,
            Message = message,
            RawPayloadJson = transaction.RawPayloadJson,
            CanonicalPayloadJson = transaction.CanonicalPayloadJson,
            MetadataJson = Serialize(metadata ?? new { }),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private static void AppendTransactionTimeline(
        SimulatedTransaction transaction,
        DateTimeOffset atUtc,
        string eventType,
        string state,
        string message,
        object? metadata)
    {
        List<TimelineEntry> timeline = Deserialize<List<TimelineEntry>>(transaction.TimelineJson, []);
        timeline.Add(new TimelineEntry
        {
            Event = eventType,
            State = state,
            Message = message,
            AtUtc = atUtc,
            Metadata = metadata,
        });
        transaction.TimelineJson = Serialize(timeline);
    }

    private static TransactionSimulationMetadata LoadTransactionMetadata(SimulatedTransaction transaction)
    {
        return Deserialize<TransactionSimulationMetadata>(transaction.MetadataJson, new TransactionSimulationMetadata());
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static T Deserialize<T>(string json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private sealed class CallbackDispatchOutcome(
        bool isSuccessStatusCode,
        int statusCode,
        string responseHeadersJson,
        string responseBody,
        string errorMessage)
    {
        public bool IsSuccessStatusCode { get; } = isSuccessStatusCode;
        public int StatusCode { get; } = statusCode;
        public string ResponseHeadersJson { get; } = responseHeadersJson;
        public string ResponseBody { get; } = responseBody;
        public string ErrorMessage { get; } = errorMessage;
    }

    private sealed class SiteSettings
    {
        public string? DefaultCallbackTargetKey { get; set; }
    }

    private sealed class TransactionSimulationMetadata
    {
        public bool DuplicateInjectionEnabled { get; set; }
        public bool SimulateFailureEnabled { get; set; }
        public string FailureMessage { get; set; } = string.Empty;
        public decimal FlowRateLitresPerMinute { get; set; }
        public decimal? TargetAmount { get; set; }
        public decimal? TargetVolume { get; set; }
        public int TotalDispenseSeconds { get; set; }
        public int PullDeliveryCount { get; set; }
        public int PushDeliveryCount { get; set; }
        public int PullDuplicateEmissionCount { get; set; }
        public int PushDuplicateEmissionCount { get; set; }
        public int PushFailureCount { get; set; }
    }

    private sealed class TimelineEntry
    {
        public string Event { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTimeOffset AtUtc { get; set; }
        public object? Metadata { get; set; }
    }
}
