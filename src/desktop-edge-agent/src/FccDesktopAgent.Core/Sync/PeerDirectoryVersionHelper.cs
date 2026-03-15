using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Shared helper for extracting X-Peer-Directory-Version from cloud HTTP responses
/// and triggering immediate config polls when the local version is stale.
/// </summary>
internal static class PeerDirectoryVersionHelper
{
    private const string HeaderName = "X-Peer-Directory-Version";

    /// <summary>
    /// Extracts the peer directory version from the response header.
    /// If the cloud version is newer than the local version, updates the local
    /// version and invokes <see cref="IConfigManager.OnPeerDirectoryStale"/> to
    /// request an immediate config poll.
    /// </summary>
    public static void CheckAndTrigger(
        HttpResponseMessage response,
        IConfigManager configManager,
        ILogger logger)
    {
        if (!response.Headers.TryGetValues(HeaderName, out var values))
            return;

        if (!long.TryParse(values.FirstOrDefault(), out var cloudVersion))
            return;

        if (!configManager.IsPeerDirectoryStale(cloudVersion))
        {
            configManager.UpdatePeerDirectoryVersion(cloudVersion);
            return;
        }

        logger.LogInformation(
            "Peer directory stale: cloud version {CloudVersion} > local {LocalVersion} — requesting immediate config poll",
            cloudVersion, configManager.CurrentPeerDirectoryVersion);

        configManager.UpdatePeerDirectoryVersion(cloudVersion);
        configManager.OnPeerDirectoryStale?.Invoke();
    }
}
