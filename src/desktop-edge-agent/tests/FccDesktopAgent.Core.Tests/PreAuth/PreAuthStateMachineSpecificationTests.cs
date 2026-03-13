using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.PreAuth;
using FluentAssertions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.PreAuth;

public sealed class PreAuthStateMachineSpecificationTests
{
    [Fact]
    public void SharedSpecification_MatchesDesktopStateMachine()
    {
        var spec = LoadSpecification();

        PreAuthStateMachine.ActiveStatuses
            .Select(status => status.ToString().ToUpperInvariant())
            .Should()
            .BeEquivalentTo(spec.ActiveStates);

        PreAuthStateMachine.TerminalStatuses
            .Select(status => status.ToString().ToUpperInvariant())
            .Should()
            .BeEquivalentTo(spec.TerminalStates);

        foreach (var from in Enum.GetValues<PreAuthStatus>())
        {
            var expectedTargets = spec.Transitions[from.ToString().ToUpperInvariant()];
            PreAuthStateMachine.GetAllowedTransitions(from)
                .Select(status => status.ToString().ToUpperInvariant())
                .Should()
                .BeEquivalentTo(expectedTargets);
        }
    }

    private static PreAuthStateMachineSpec LoadSpecification()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "schemas",
                "state-machines",
                "pre-auth-state-machine.json");

            if (File.Exists(candidate))
            {
                var spec = JsonSerializer.Deserialize<PreAuthStateMachineSpec>(
                    File.ReadAllText(candidate),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                return spec ?? throw new InvalidOperationException("Failed to deserialize pre-auth state machine spec.");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate schemas/state-machines/pre-auth-state-machine.json.");
    }

    private sealed record PreAuthStateMachineSpec(
        IReadOnlyList<string> States,
        IReadOnlyList<string> ActiveStates,
        IReadOnlyList<string> TerminalStates,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions);
}
