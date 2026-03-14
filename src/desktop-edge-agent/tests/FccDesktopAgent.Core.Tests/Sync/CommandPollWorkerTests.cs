using System.Net;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync;
using FccDesktopAgent.Core.Sync.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

public sealed class CommandPollWorkerTests
{
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IAgentCommandExecutor _executor;
    private readonly IAgentCommandStateStore _stateStore;
    private readonly IRegistrationManager _registrationManager;

    public CommandPollWorkerTests()
    {
        _tokenProvider = Substitute.For<IDeviceTokenProvider>();
        _executor = Substitute.For<IAgentCommandExecutor>();
        _stateStore = Substitute.For<IAgentCommandStateStore>();
        _registrationManager = Substitute.For<IRegistrationManager>();

        _registrationManager.IsRegistered.Returns(true);
        _registrationManager.IsDecommissioned.Returns(false);
        _tokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test-jwt-token"));
    }

    [Fact]
    public async Task PollAsync_ResetCommandFinalizesOnlyAfterAckApplied()
    {
        var command = SampleCommand(AgentCommandType.RESET_LOCAL_STATE);
        _executor.ExecuteAsync(Arg.Any<EdgeCommandItem>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AgentCommandExecutionResult
            {
                Outcome = LocalCommandAckOutcome.Succeeded,
                Result = JsonSerializer.SerializeToElement(new { outcome = "Succeeded" }),
                PostAckAction = PostAckAction.FinalizeResetLocalState
            }));

        var handler = new SequencedHandler(
            pollResponse: BuildPollResponse(command),
            ackStatusCode: HttpStatusCode.OK,
            ackBody: JsonSerializer.Serialize(new CommandAckResponse
            {
                CommandId = command.CommandId,
                Status = AgentCommandStatus.ACKED,
                AcknowledgedAt = DateTimeOffset.UtcNow,
                Duplicate = false
            }));

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.CommandCount.Should().Be(1);
        result.AckedCount.Should().Be(1);
        result.HaltRequested.Should().BeTrue();
        await _executor.Received(1).CompletePostAckActionAsync(
            PostAckAction.FinalizeResetLocalState,
            command.CommandId,
            Arg.Any<CancellationToken>());
        await _stateStore.DidNotReceive().ClearPendingActionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ResetCommandConflictClearsPendingStateWithoutFinalizing()
    {
        var command = SampleCommand(AgentCommandType.RESET_LOCAL_STATE);
        _executor.ExecuteAsync(Arg.Any<EdgeCommandItem>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AgentCommandExecutionResult
            {
                Outcome = LocalCommandAckOutcome.Succeeded,
                Result = JsonSerializer.SerializeToElement(new { outcome = "Succeeded" }),
                PostAckAction = PostAckAction.FinalizeResetLocalState
            }));

        var handler = new SequencedHandler(
            pollResponse: BuildPollResponse(command),
            ackStatusCode: HttpStatusCode.Conflict,
            ackBody: """{"errorCode":"COMMAND_NOT_ACTIONABLE","message":"Command is already expired"}""");

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.CommandCount.Should().Be(1);
        result.AckedCount.Should().Be(0);
        result.HaltRequested.Should().BeFalse();
        await _stateStore.Received(1).ClearPendingActionAsync(Arg.Any<CancellationToken>());
        await _stateStore.Received(1).ClearNoticeAsync(Arg.Any<CancellationToken>());
        await _executor.DidNotReceive().CompletePostAckActionAsync(
            PostAckAction.FinalizeResetLocalState,
            command.CommandId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ResetCommandDuplicateAckStillFinalizes()
    {
        var command = SampleCommand(AgentCommandType.RESET_LOCAL_STATE);
        _executor.ExecuteAsync(Arg.Any<EdgeCommandItem>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AgentCommandExecutionResult
            {
                Outcome = LocalCommandAckOutcome.Succeeded,
                Result = JsonSerializer.SerializeToElement(new { outcome = "Succeeded" }),
                PostAckAction = PostAckAction.FinalizeResetLocalState
            }));

        var handler = new SequencedHandler(
            pollResponse: BuildPollResponse(command),
            ackStatusCode: HttpStatusCode.OK,
            ackBody: JsonSerializer.Serialize(new CommandAckResponse
            {
                CommandId = command.CommandId,
                Status = AgentCommandStatus.ACKED,
                AcknowledgedAt = DateTimeOffset.UtcNow,
                Duplicate = true
            }));

        var worker = CreateWorker(handler);
        var result = await worker.PollAsync(CancellationToken.None);

        result.AckedCount.Should().Be(1);
        result.HaltRequested.Should().BeTrue();
        await _executor.Received(1).CompletePostAckActionAsync(
            PostAckAction.FinalizeResetLocalState,
            command.CommandId,
            Arg.Any<CancellationToken>());
    }

    private CommandPollWorker CreateWorker(HttpMessageHandler handler)
    {
        return new CommandPollWorker(
            new TestHttpClientFactory(handler),
            Options.Create(new AgentConfiguration { CloudBaseUrl = "https://cloud.test" }),
            _tokenProvider,
            _executor,
            _stateStore,
            _registrationManager,
            NullLogger<CommandPollWorker>.Instance);
    }

    private static EdgeCommandItem SampleCommand(AgentCommandType commandType) => new()
    {
        CommandId = Guid.NewGuid(),
        CommandType = commandType,
        Status = AgentCommandStatus.PENDING,
        Reason = "test",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private static string BuildPollResponse(EdgeCommandItem command)
        => JsonSerializer.Serialize(new EdgeCommandPollResponse
        {
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Commands = [command]
        });

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private int _calls;
        private readonly string _pollResponse;
        private readonly HttpStatusCode _ackStatusCode;
        private readonly string _ackBody;

        public SequencedHandler(string pollResponse, HttpStatusCode ackStatusCode, string ackBody)
        {
            _pollResponse = pollResponse;
            _ackStatusCode = ackStatusCode;
            _ackBody = ackBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
                return Task.FromResult(FakeHandler.JsonResponse(_pollResponse));

            return Task.FromResult(FakeHandler.JsonResponse(_ackBody, _ackStatusCode));
        }
    }
}
