using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.StateMachines;

public static class PreAuthStateMachine
{
    public static IReadOnlyList<PreAuthStatus> ActiveStatuses { get; } =
    [
        PreAuthStatus.PENDING,
        PreAuthStatus.AUTHORIZED,
        PreAuthStatus.DISPENSING
    ];

    public static IReadOnlyList<PreAuthStatus> TerminalStatuses { get; } =
    [
        PreAuthStatus.COMPLETED,
        PreAuthStatus.CANCELLED,
        PreAuthStatus.EXPIRED,
        PreAuthStatus.FAILED
    ];

    private static readonly IReadOnlyDictionary<PreAuthStatus, HashSet<PreAuthStatus>> AllowedTransitions =
        new Dictionary<PreAuthStatus, HashSet<PreAuthStatus>>
        {
            [PreAuthStatus.PENDING] =
            [
                PreAuthStatus.AUTHORIZED,
                PreAuthStatus.CANCELLED,
                PreAuthStatus.EXPIRED,
                PreAuthStatus.FAILED
            ],
            [PreAuthStatus.AUTHORIZED] =
            [
                PreAuthStatus.DISPENSING,
                PreAuthStatus.COMPLETED,
                PreAuthStatus.CANCELLED,
                PreAuthStatus.EXPIRED,
                PreAuthStatus.FAILED
            ],
            [PreAuthStatus.DISPENSING] =
            [
                PreAuthStatus.COMPLETED,
                PreAuthStatus.CANCELLED,
                PreAuthStatus.EXPIRED,
                PreAuthStatus.FAILED
            ],
            [PreAuthStatus.COMPLETED] = [],
            [PreAuthStatus.CANCELLED] = [],
            [PreAuthStatus.EXPIRED] = [],
            [PreAuthStatus.FAILED] = []
        };

    public static bool IsActive(PreAuthStatus status) => ActiveStatuses.Contains(status);

    public static bool IsTerminal(PreAuthStatus status) => TerminalStatuses.Contains(status);

    public static bool CanTransition(PreAuthStatus from, PreAuthStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static IReadOnlyCollection<PreAuthStatus> GetAllowedTransitions(PreAuthStatus from) =>
        AllowedTransitions.TryGetValue(from, out var allowed)
            ? allowed
            : Array.Empty<PreAuthStatus>();
}
