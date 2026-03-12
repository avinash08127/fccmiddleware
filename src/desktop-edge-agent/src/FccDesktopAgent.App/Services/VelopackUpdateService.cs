using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;

namespace FccDesktopAgent.App.Services;

/// <summary>
/// Velopack-backed auto-update service. Checks a configured releases URL for new versions,
/// downloads delta packages when available, and stages them for apply-on-restart.
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly IOptionsMonitor<AgentConfiguration> _config;

    public VelopackUpdateService(
        ILogger<VelopackUpdateService> logger,
        IOptionsMonitor<AgentConfiguration> config)
    {
        _logger = logger;
        _config = config;
    }

    public bool IsInstalled
    {
        get
        {
            try
            {
                // IsInstalled is a local filesystem check — the URL is not contacted.
                // Use the configured URL when available; otherwise a descriptive fallback.
                var url = _config.CurrentValue.UpdateUrl;
                var source = new SimpleWebSource(
                    !string.IsNullOrWhiteSpace(url) ? url : "https://updates.not-configured");
                var mgr = new UpdateManager(source);
                return mgr.IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var cfg = _config.CurrentValue;

        if (!cfg.AutoUpdateEnabled)
        {
            _logger.LogDebug("Auto-update is disabled via configuration");
            return new UpdateCheckResult(false, null, false, "Auto-update disabled");
        }

        if (string.IsNullOrWhiteSpace(cfg.UpdateUrl))
        {
            _logger.LogDebug("No update URL configured — skipping update check");
            return new UpdateCheckResult(false, null, false, "No update URL configured");
        }

        try
        {
            var source = new SimpleWebSource(cfg.UpdateUrl);
            var mgr = new UpdateManager(source);

            if (!mgr.IsInstalled)
            {
                _logger.LogDebug("App is not installed via Velopack — skipping update check");
                return new UpdateCheckResult(false, null, false, "Not installed (dev mode)");
            }

            var updateInfo = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);

            if (updateInfo is null)
            {
                _logger.LogDebug("No updates available");
                return new UpdateCheckResult(false, null, false, null);
            }

            _logger.LogInformation(
                "Update available: {TargetVersion} (current: {CurrentVersion})",
                updateInfo.TargetFullRelease.Version,
                mgr.CurrentVersion);

            await mgr.DownloadUpdatesAsync(updateInfo).ConfigureAwait(false);

            _logger.LogInformation("Update {Version} downloaded and staged for next restart",
                updateInfo.TargetFullRelease.Version);

            return new UpdateCheckResult(
                true,
                updateInfo.TargetFullRelease.Version.ToString(),
                true,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return new UpdateCheckResult(false, null, false, ex.Message);
        }
    }
}
