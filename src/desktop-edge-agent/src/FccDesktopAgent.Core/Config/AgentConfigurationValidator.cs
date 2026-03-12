using FccDesktopAgent.Core.Adapter.Common;

namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Validates that <see cref="AgentConfiguration"/> has the minimum required fields
/// for the selected ingestion mode. Called at startup to reject incomplete runtime config.
/// </summary>
public static class AgentConfigurationValidator
{
    /// <summary>
    /// Returns null if valid, or an error message describing the missing fields.
    /// </summary>
    public static string? Validate(AgentConfiguration config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.DeviceId))
            errors.Add("DeviceId is not set");

        if (string.IsNullOrWhiteSpace(config.SiteId))
            errors.Add("SiteId is not set");

        // FCC URL is required for Relay and BufferAlways modes
        if (config.IngestionMode is IngestionMode.Relay or IngestionMode.BufferAlways)
        {
            if (string.IsNullOrWhiteSpace(config.FccBaseUrl))
                errors.Add($"FccBaseUrl is required for ingestion mode {config.IngestionMode}");

            if (!DesktopFccRuntimeConfiguration.IsSupported(config.FccVendor))
                errors.Add($"FccVendor '{config.FccVendor}' is not supported on desktop");
        }

        // Cloud URL is required for CloudDirect and Relay modes
        if (config.IngestionMode is IngestionMode.CloudDirect or IngestionMode.Relay)
        {
            if (string.IsNullOrWhiteSpace(config.CloudBaseUrl))
                errors.Add($"CloudBaseUrl is required for ingestion mode {config.IngestionMode}");
        }

        return errors.Count > 0
            ? $"Agent configuration is incomplete: {string.Join("; ", errors)}"
            : null;
    }
}
