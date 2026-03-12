namespace FccDesktopAgent.Core.Config;

/// <summary>Outcome of a <see cref="IConfigManager.ApplyConfigAsync"/> call.</summary>
public enum ConfigApplyOutcome
{
    /// <summary>Config was validated and applied (hot-reload and/or restart-required changes).</summary>
    Applied,
    /// <summary>Config failed validation and was rejected.</summary>
    Rejected,
    /// <summary>Config version was not strictly greater than the current version.</summary>
    StaleVersion,
    /// <summary>Config <c>effectiveAtUtc</c> is in the future; deferred to next poll.</summary>
    NotYetEffective,
}

/// <summary>Result returned by <see cref="IConfigManager.ApplyConfigAsync"/>.</summary>
public sealed record ConfigApplyResult(
    ConfigApplyOutcome Outcome,
    int ConfigVersion,
    IReadOnlyList<string>? HotReloadedSections = null,
    IReadOnlyList<string>? RestartRequiredSections = null);

/// <summary>Raised by <see cref="IConfigManager"/> when config changes are applied.</summary>
public sealed class ConfigChangedEventArgs : EventArgs
{
    public required int ConfigVersion { get; init; }
    public required IReadOnlyList<string> HotReloadedSections { get; init; }
    public required IReadOnlyList<string> RestartRequiredSections { get; init; }
}
