using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Reads/writes <c>registration.json</c> from the agent data directory.
/// Also implements <see cref="IPostConfigureOptions{T}"/> to overlay identity fields
/// (DeviceId, SiteId, CloudBaseUrl) from registration state into <see cref="AgentConfiguration"/>
/// so that all workers see the correct identity without manual wiring.
/// </summary>
public sealed class RegistrationManager : IRegistrationManager, IPostConfigureOptions<AgentConfiguration>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<RegistrationManager> _logger;
    private readonly string? _baseDirectoryOverride;
    private readonly object _lock = new();
    private RegistrationState? _cached;

    public RegistrationManager(ILogger<RegistrationManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Test constructor that overrides the data directory.</summary>
    internal RegistrationManager(ILogger<RegistrationManager> logger, string baseDirectory)
    {
        _logger = logger;
        _baseDirectoryOverride = baseDirectory;
    }

    // ── IRegistrationManager ─────────────────────────────────────────────────

    public RegistrationState LoadState()
    {
        lock (_lock)
        {
            if (_cached is not null)
                return _cached;
        }

        var path = GetFilePath();
        if (!File.Exists(path))
        {
            _logger.LogDebug("No registration.json found — returning default unregistered state");
            var defaultState = new RegistrationState();
            lock (_lock) _cached = defaultState;
            return defaultState;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<RegistrationState>(json, JsonOptions)
                        ?? new RegistrationState();
            lock (_lock) _cached = state;

            if (state.IsDecommissioned)
                _logger.LogWarning("Device is decommissioned (deviceId={DeviceId})", state.DeviceId);
            else if (state.IsRegistered)
                _logger.LogInformation("Device is registered (deviceId={DeviceId}, site={SiteCode})",
                    state.DeviceId, state.SiteCode);

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read registration.json — treating as unregistered");
            var fallback = new RegistrationState();
            lock (_lock) _cached = fallback;
            return fallback;
        }
    }

    public async Task SaveStateAsync(RegistrationState state, CancellationToken ct = default)
    {
        var path = GetFilePath();
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);

        lock (_lock) _cached = state;

        _logger.LogInformation("Registration state saved (registered={IsRegistered}, deviceId={DeviceId})",
            state.IsRegistered, state.DeviceId);
    }

    public async Task MarkDecommissionedAsync(CancellationToken ct = default)
    {
        var state = LoadState();
        state.IsDecommissioned = true;
        await SaveStateAsync(state, ct);
        _logger.LogWarning("Device marked as decommissioned");
    }

    // ── IPostConfigureOptions<AgentConfiguration> ────────────────────────────

    public void PostConfigure(string? name, AgentConfiguration options)
    {
        var state = LoadState();
        if (!state.IsRegistered) return;

        if (!string.IsNullOrEmpty(state.DeviceId))
            options.DeviceId = state.DeviceId;

        if (!string.IsNullOrEmpty(state.SiteCode))
            options.SiteId = state.SiteCode;

        if (!string.IsNullOrEmpty(state.CloudBaseUrl))
            options.CloudBaseUrl = state.CloudBaseUrl;

        if (!string.IsNullOrEmpty(state.LegalEntityId))
            options.LegalEntityId = state.LegalEntityId;
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private string GetFilePath() =>
        Path.Combine(_baseDirectoryOverride ?? AgentDataDirectory.Resolve(), "registration.json");
}
