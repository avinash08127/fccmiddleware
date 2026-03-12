namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Built-in cloud environment map.
/// v2 provisioning references an environment key (e.g. "PRODUCTION") instead of a raw URL.
/// Mirrors the Android <c>CloudEnvironments.kt</c> definition.
/// </summary>
public static class CloudEnvironments
{
    public sealed record CloudEnv(string BaseUrl, string DisplayName);

    public static readonly IReadOnlyDictionary<string, CloudEnv> Environments =
        new Dictionary<string, CloudEnv>(StringComparer.OrdinalIgnoreCase)
        {
            ["PRODUCTION"]  = new("https://api.fccmiddleware.io", "Production"),
            ["STAGING"]     = new("https://api-staging.fccmiddleware.io", "Staging"),
            ["DEVELOPMENT"] = new("https://api-dev.fccmiddleware.io", "Development"),
            ["LOCAL"]       = new("https://localhost:5001", "Local"),
        };

    /// <summary>All environment keys in display order.</summary>
    public static readonly IReadOnlyList<string> Keys = ["PRODUCTION", "STAGING", "DEVELOPMENT", "LOCAL"];

    /// <summary>Display names in the same order as <see cref="Keys"/>.</summary>
    public static readonly IReadOnlyList<string> DisplayNames =
        Keys.Select(k => Environments[k].DisplayName).ToList();

    /// <summary>
    /// Resolve an environment key to its base URL, or <c>null</c> if unknown.
    /// </summary>
    public static string? Resolve(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return null;
        return Environments.TryGetValue(env, out var entry) ? entry.BaseUrl : null;
    }
}
