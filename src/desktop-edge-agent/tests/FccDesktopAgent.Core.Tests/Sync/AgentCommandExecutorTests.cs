using System.Net;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.MasterData;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

public sealed class AgentCommandExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfigManager _configManager;
    private readonly IRegistrationManager _registrationManager;
    private readonly AgentCommandStateStore _stateStore;
    private readonly DesktopAgentCommandExecutor _executor;

    public AgentCommandExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fcc-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _configManager = Substitute.For<IConfigManager>();
        _registrationManager = Substitute.For<IRegistrationManager>();
        _registrationManager.IsDecommissioned.Returns(false);

        _stateStore = new AgentCommandStateStore(
            NullLogger<AgentCommandStateStore>.Instance,
            _tempDir);

        var tokenProvider = Substitute.For<IDeviceTokenProvider>();
        var configWorker = new ConfigPollWorker(
            new TestHttpClientFactory(FakeHandler.RespondStatus(HttpStatusCode.NotModified)),
            Options.Create(new AgentConfiguration { CloudBaseUrl = "https://cloud.test" }),
            _configManager,
            new AuthenticatedCloudRequestHandler(
                tokenProvider,
                _registrationManager,
                NullLogger<AuthenticatedCloudRequestHandler>.Instance),
            _registrationManager,
            NullLogger<ConfigPollWorker>.Instance);

        _executor = new DesktopAgentCommandExecutor(
            configWorker,
            _configManager,
            _registrationManager,
            _stateStore,
            Substitute.For<IServiceScopeFactory>(),
            new LocalOverrideManager(NullLogger<LocalOverrideManager>.Instance, _tempDir),
            new SiteDataManager(Substitute.For<IServiceScopeFactory>(), NullLogger<SiteDataManager>.Instance),
            Substitute.For<ICredentialStore>(),
            NullLogger<DesktopAgentCommandExecutor>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredCommand_ReturnsIgnoredExpired()
    {
        var command = new EdgeCommandItem
        {
            CommandId = Guid.NewGuid(),
            CommandType = AgentCommandType.FORCE_CONFIG_PULL,
            Status = AgentCommandStatus.PENDING,
            Reason = "expired",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        var result = await _executor.ExecuteAsync(command, DateTimeOffset.UtcNow, CancellationToken.None);

        result.Outcome.Should().Be(LocalCommandAckOutcome.IgnoredExpired);
        result.PostAckAction.Should().Be(PostAckAction.None);
    }

    [Fact]
    public async Task ExecuteAsync_ResetCommand_TracksPendingStateAndNotice()
    {
        var command = new EdgeCommandItem
        {
            CommandId = Guid.NewGuid(),
            CommandType = AgentCommandType.RESET_LOCAL_STATE,
            Status = AgentCommandStatus.PENDING,
            Reason = "reset",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };

        var result = await _executor.ExecuteAsync(command, DateTimeOffset.UtcNow, CancellationToken.None);
        var state = _stateStore.Load();

        result.Outcome.Should().Be(LocalCommandAckOutcome.Succeeded);
        result.PostAckAction.Should().Be(PostAckAction.FinalizeResetLocalState);
        state.PendingAction.Should().Be(PendingAgentAction.ResetLocalState);
        state.PendingCommandId.Should().Be(command.CommandId);
        state.PendingActionAcked.Should().BeFalse();
        state.NoticeKind.Should().Be(OperatorNoticeKind.ResetInProgress);
        state.NoticeMessage.Should().Contain("Reset in progress");
    }
}
