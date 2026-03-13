using FccDesktopAgent.Core.Adapter.Common;

namespace FccDesktopAgent.Core.PreAuth;

internal static class PreAuthStateMachine
{
    internal static IReadOnlyList<PreAuthStatus> ActiveStatuses { get; } =
    [
        PreAuthStatus.Pending,
        PreAuthStatus.Authorized,
        PreAuthStatus.Dispensing
    ];

    internal static IReadOnlyList<PreAuthStatus> TerminalStatuses { get; } =
    [
        PreAuthStatus.Completed,
        PreAuthStatus.Cancelled,
        PreAuthStatus.Expired,
        PreAuthStatus.Failed
    ];

    private static readonly IReadOnlyDictionary<PreAuthStatus, HashSet<PreAuthStatus>> AllowedTransitions =
        new Dictionary<PreAuthStatus, HashSet<PreAuthStatus>>
        {
            [PreAuthStatus.Pending] =
            [
                PreAuthStatus.Authorized,
                PreAuthStatus.Cancelled,
                PreAuthStatus.Expired,
                PreAuthStatus.Failed
            ],
            [PreAuthStatus.Authorized] =
            [
                PreAuthStatus.Dispensing,
                PreAuthStatus.Completed,
                PreAuthStatus.Cancelled,
                PreAuthStatus.Expired,
                PreAuthStatus.Failed
            ],
            [PreAuthStatus.Dispensing] =
            [
                PreAuthStatus.Completed,
                PreAuthStatus.Cancelled,
                PreAuthStatus.Expired,
                PreAuthStatus.Failed
            ],
            [PreAuthStatus.Completed] = [],
            [PreAuthStatus.Cancelled] = [],
            [PreAuthStatus.Expired] = [],
            [PreAuthStatus.Failed] = []
        };

    internal static IReadOnlyList<string> ActiveStatusNames { get; } =
        ActiveStatuses.Select(status => status.ToString()).ToArray();

    internal static bool IsActive(PreAuthStatus status) => ActiveStatuses.Contains(status);

    internal static bool IsTerminal(PreAuthStatus status) => TerminalStatuses.Contains(status);

    internal static bool CanTransition(PreAuthStatus from, PreAuthStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    internal static IReadOnlyCollection<PreAuthStatus> GetAllowedTransitions(PreAuthStatus from) =>
        AllowedTransitions.TryGetValue(from, out var allowed)
            ? allowed
            : Array.Empty<PreAuthStatus>();
}
