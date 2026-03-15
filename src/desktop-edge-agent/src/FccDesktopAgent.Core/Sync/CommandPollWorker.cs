using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

public sealed class CommandPollExecutionResult
{
    public int CommandCount { get; init; }
    public int AckedCount { get; init; }
    public bool HaltRequested { get; init; }
    public string? FailureMessage { get; init; }

    public bool IsEmpty => CommandCount == 0 && string.IsNullOrWhiteSpace(FailureMessage) && !HaltRequested;

    public static CommandPollExecutionResult Empty() => new();

    public static CommandPollExecutionResult Failure(string message) => new()
    {
        FailureMessage = message
    };

    public static CommandPollExecutionResult Processed(int commandCount, int ackedCount, bool haltRequested = false) => new()
    {
        CommandCount = commandCount,
        AckedCount = ackedCount,
        HaltRequested = haltRequested
    };
}

internal sealed class CommandPollFetchResult
{
    public EdgeCommandPollResponse? Response { get; init; }
    public bool HaltRequested { get; init; }
    public string? FailureMessage { get; init; }
}

public interface IAgentCommandPoller
{
    Task<CommandPollExecutionResult> PollAsync(CancellationToken ct);
}

internal enum CommandAckTransportOutcome
{
    Applied,
    DuplicateApplied,
    Conflict,
    FeatureDisabled,
    CommandNotFound,
    ForbiddenDecommissioned,
    TransportFailure
}

internal sealed class CommandAckTransportResult
{
    public required CommandAckTransportOutcome Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

public sealed class CommandPollWorker : IAgentCommandPoller
{
    private const string CommandsPath = "/api/v1/agent/commands";
    private const string DecommissionedCode = "DEVICE_DECOMMISSIONED";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IAgentCommandExecutor _executor;
    private readonly IAgentCommandStateStore _stateStore;
    private readonly IRegistrationManager _registrationManager;
    private readonly IConfigManager _configManager;
    private readonly ILogger<CommandPollWorker> _logger;

    public CommandPollWorker(
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IDeviceTokenProvider tokenProvider,
        IAgentCommandExecutor executor,
        IAgentCommandStateStore stateStore,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        ILogger<CommandPollWorker> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _tokenProvider = tokenProvider;
        _executor = executor;
        _stateStore = stateStore;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task<CommandPollExecutionResult> PollAsync(CancellationToken ct)
    {
        if (_registrationManager.IsDecommissioned)
        {
            _logger.LogDebug("Command poll skipped: device is decommissioned");
            return CommandPollExecutionResult.Processed(0, 0, haltRequested: true);
        }

        if (!_registrationManager.IsRegistered)
        {
            _logger.LogDebug("Command poll skipped: device is not registered");
            return CommandPollExecutionResult.Empty();
        }

        var pollResult = await PollCommandsWithRefreshAsync(ct);
        if (pollResult.HaltRequested)
            return CommandPollExecutionResult.Processed(0, 0, haltRequested: true);

        if (!string.IsNullOrWhiteSpace(pollResult.FailureMessage))
            return CommandPollExecutionResult.Failure(pollResult.FailureMessage);

        var response = pollResult.Response;
        if (response is null || response.Commands.Count == 0)
            return CommandPollExecutionResult.Empty();

        var token = await _tokenProvider.GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            return CommandPollExecutionResult.Failure("Command acknowledgement skipped: no device token available");

        return await HandleCommandsAsync(response, token, ct);
    }

    private async Task<CommandPollFetchResult> PollCommandsWithRefreshAsync(CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogDebug("Command poll skipped: no device token available");
            return new CommandPollFetchResult();
        }

        try
        {
            var response = await SendPollRequestAsync(token, ct);
            return new CommandPollFetchResult { Response = response };
        }
        catch (UnauthorizedAccessException)
        {
            var refreshed = await RefreshTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(refreshed))
                return new CommandPollFetchResult { FailureMessage = "Command poll token refresh failed" };

            try
            {
                var response = await SendPollRequestAsync(refreshed, ct);
                return new CommandPollFetchResult { Response = response };
            }
            catch (DeviceDecommissionedException)
            {
                await _registrationManager.MarkDecommissionedAsync(ct);
                return new CommandPollFetchResult { HaltRequested = true };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Command poll failed after token refresh");
                return new CommandPollFetchResult { FailureMessage = ex.Message };
            }
        }
        catch (DeviceDecommissionedException)
        {
            await _registrationManager.MarkDecommissionedAsync(ct);
            return new CommandPollFetchResult { HaltRequested = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Command poll failed");
            return new CommandPollFetchResult { FailureMessage = ex.Message };
        }
    }

    private async Task<CommandPollExecutionResult> HandleCommandsAsync(
        EdgeCommandPollResponse response,
        string initialToken,
        CancellationToken ct)
    {
        var orderedCommands = response.Commands
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.CommandId)
            .ToList();

        var ackedCount = 0;
        foreach (var command in orderedCommands)
        {
            var execution = await _executor.ExecuteAsync(command, response.ServerTimeUtc, ct);
            var ack = await AcknowledgeCommandAsync(command, execution, initialToken, ct);

            if (ack.Outcome is CommandAckTransportOutcome.Applied or CommandAckTransportOutcome.DuplicateApplied)
            {
                ackedCount++;

                if (execution.PostAckAction != PostAckAction.None)
                {
                    await _executor.CompletePostAckActionAsync(execution.PostAckAction, command.CommandId, ct);
                    return CommandPollExecutionResult.Processed(orderedCommands.Count, ackedCount, haltRequested: true);
                }

                continue;
            }

            if (execution.PostAckAction != PostAckAction.None
                && ack.Outcome is CommandAckTransportOutcome.Conflict
                    or CommandAckTransportOutcome.CommandNotFound
                    or CommandAckTransportOutcome.FeatureDisabled)
            {
                await _stateStore.ClearPendingActionAsync(ct);
                await _stateStore.ClearNoticeAsync(ct);
            }

            if (ack.Outcome == CommandAckTransportOutcome.ForbiddenDecommissioned)
            {
                return CommandPollExecutionResult.Processed(orderedCommands.Count, ackedCount, haltRequested: true);
            }
        }

        return CommandPollExecutionResult.Processed(orderedCommands.Count, ackedCount);
    }

    private async Task<EdgeCommandPollResponse?> SendPollRequestAsync(string token, CancellationToken ct)
    {
        var config = _config.Value;
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{CommandsPath}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var http = _httpFactory.CreateClient("cloud");
        using var response = await http.SendAsync(request, ct);

        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains(DecommissionedCode, StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(body);

            throw new HttpRequestException($"403 Forbidden: {body}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var error = await ReadErrorAsync(response, ct);
            if (string.Equals(error.ErrorCode, "FEATURE_DISABLED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Command poll skipped: cloud command API is disabled");
                return new EdgeCommandPollResponse
                {
                    ServerTimeUtc = DateTimeOffset.UtcNow,
                    Commands = []
                };
            }

            throw new HttpRequestException(error.Message ?? "404 Not Found");
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<EdgeCommandPollResponse>(JsonOptions, ct);
    }

    private async Task<CommandAckTransportResult> AcknowledgeCommandAsync(
        EdgeCommandItem command,
        AgentCommandExecutionResult execution,
        string initialToken,
        CancellationToken ct)
    {
        var request = BuildAckRequest(execution);
        var firstAttempt = await SendAckRequestAsync(command.CommandId, request, initialToken, ct);
        if (firstAttempt.Outcome != CommandAckTransportOutcome.TransportFailure || firstAttempt.ErrorCode != "UNAUTHORIZED")
            return firstAttempt;

        var refreshed = await RefreshTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(refreshed))
            return new CommandAckTransportResult
            {
                Outcome = CommandAckTransportOutcome.TransportFailure,
                Message = "Command acknowledgement token refresh failed"
            };

        return await SendAckRequestAsync(command.CommandId, request, refreshed, ct);
    }

    private async Task<CommandAckTransportResult> SendAckRequestAsync(
        Guid commandId,
        CommandAckRequest request,
        string token,
        CancellationToken ct)
    {
        var config = _config.Value;
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{CommandsPath}/{commandId}/ack";
        var http = _httpFactory.CreateClient("cloud");

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(message, ct);
        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new CommandAckTransportResult
            {
                Outcome = CommandAckTransportOutcome.TransportFailure,
                ErrorCode = "UNAUTHORIZED",
                Message = "401 Unauthorized"
            };
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var error = await ReadErrorAsync(response, ct);
            if (string.Equals(error.ErrorCode, DecommissionedCode, StringComparison.OrdinalIgnoreCase))
            {
                await _registrationManager.MarkDecommissionedAsync(ct);
                return new CommandAckTransportResult
                {
                    Outcome = CommandAckTransportOutcome.ForbiddenDecommissioned,
                    ErrorCode = error.ErrorCode,
                    Message = error.Message
                };
            }

            return new CommandAckTransportResult
            {
                Outcome = CommandAckTransportOutcome.TransportFailure,
                ErrorCode = error.ErrorCode,
                Message = error.Message
            };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var error = await ReadErrorAsync(response, ct);
            if (string.Equals(error.ErrorCode, "FEATURE_DISABLED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Command acknowledgement skipped: cloud command API is disabled");
                return new CommandAckTransportResult
                {
                    Outcome = CommandAckTransportOutcome.FeatureDisabled,
                    ErrorCode = error.ErrorCode,
                    Message = error.Message
                };
            }

            if (string.Equals(error.ErrorCode, "COMMAND_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Command acknowledgement skipped: command {CommandId} no longer exists", commandId);
                return new CommandAckTransportResult
                {
                    Outcome = CommandAckTransportOutcome.CommandNotFound,
                    ErrorCode = error.ErrorCode,
                    Message = error.Message
                };
            }

            return new CommandAckTransportResult
            {
                Outcome = CommandAckTransportOutcome.TransportFailure,
                ErrorCode = error.ErrorCode,
                Message = error.Message
            };
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var error = await ReadErrorAsync(response, ct);
            _logger.LogWarning("Command ack conflict for {CommandId}: {ErrorCode} {Message}", commandId, error.ErrorCode, error.Message);
            return new CommandAckTransportResult
            {
                Outcome = CommandAckTransportOutcome.Conflict,
                ErrorCode = error.ErrorCode,
                Message = error.Message
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, ct);
            return new CommandAckTransportResult
            {
                Outcome = CommandAckTransportOutcome.TransportFailure,
                ErrorCode = error.ErrorCode,
                Message = error.Message
            };
        }

        var ack = await response.Content.ReadFromJsonAsync<CommandAckResponse>(JsonOptions, ct);
        return new CommandAckTransportResult
        {
            Outcome = ack?.Duplicate == true
                ? CommandAckTransportOutcome.DuplicateApplied
                : CommandAckTransportOutcome.Applied
        };
    }

    private async Task<string?> RefreshTokenAsync(CancellationToken ct)
    {
        try
        {
            return await _tokenProvider.RefreshTokenAsync(ct);
        }
        catch (RefreshTokenExpiredException)
        {
            await _registrationManager.MarkReprovisioningRequiredAsync(ct);
            return null;
        }
        catch (DeviceDecommissionedException)
        {
            await _registrationManager.MarkDecommissionedAsync(ct);
            return null;
        }
    }

    private static CommandAckRequest BuildAckRequest(AgentCommandExecutionResult execution)
        => execution.Outcome == LocalCommandAckOutcome.Failed
            ? new CommandAckRequest
            {
                CompletionStatus = AgentCommandCompletionStatus.FAILED,
                HandledAtUtc = DateTimeOffset.UtcNow,
                FailureCode = execution.FailureCode,
                FailureMessage = execution.FailureMessage,
                Result = execution.Result
            }
            : new CommandAckRequest
            {
                CompletionStatus = AgentCommandCompletionStatus.ACKED,
                HandledAtUtc = DateTimeOffset.UtcNow,
                Result = execution.Result
            };

    private static async Task<(string? ErrorCode, string? Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var errorCode =
                root.TryGetProperty("errorCode", out var code) ? code.GetString()
                : root.TryGetProperty("error", out var legacy) ? legacy.GetString()
                : null;
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : response.ReasonPhrase;
            return (errorCode, message);
        }
        catch
        {
            return (null, response.ReasonPhrase);
        }
    }
}
