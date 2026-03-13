using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.StateMachines;

namespace FccMiddleware.UnitTests.StateMachines;

public sealed class PreAuthStateMachineSpecificationTests
{
    [Fact]
    public void SharedSpecification_MatchesCloudStateMachine()
    {
        var spec = LoadSpecification();

        PreAuthStateMachine.ActiveStatuses
            .Select(status => status.ToString())
            .Should()
            .BeEquivalentTo(spec.ActiveStates);

        PreAuthStateMachine.TerminalStatuses
            .Select(status => status.ToString())
            .Should()
            .BeEquivalentTo(spec.TerminalStates);

        foreach (var from in Enum.GetValues<PreAuthStatus>())
        {
            var expectedTargets = spec.Transitions[from.ToString()];
            PreAuthStateMachine.GetAllowedTransitions(from)
                .Select(status => status.ToString())
                .Should()
                .BeEquivalentTo(expectedTargets);

            foreach (var to in Enum.GetValues<PreAuthStatus>())
            {
                var record = new PreAuthRecord
                {
                    Id = Guid.NewGuid(),
                    Status = from,
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                };

                var act = () => record.Transition(to);
                if (expectedTargets.Contains(to.ToString()))
                {
                    act.Should().NotThrow();
                }
                else
                {
                    act.Should().Throw<InvalidPreAuthTransitionException>();
                }
            }
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
