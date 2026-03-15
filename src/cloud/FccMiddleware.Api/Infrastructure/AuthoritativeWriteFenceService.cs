using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Domain.Constants;

namespace FccMiddleware.Api.Infrastructure;

public interface IAuthoritativeWriteFenceService
{
    Task<AuthoritativeWriteFenceResult> ValidateAsync(
        string? deviceIdClaim,
        string siteCode,
        long? requestLeaderEpoch,
        CancellationToken cancellationToken);
}

/// <summary>
/// P2-15: Agent elections are authoritative — the cloud is a passive observer, not an arbiter.
///
/// Epoch-based write fencing rules:
/// - requestEpoch > siteMaxEpoch: accept, update the site's recorded leader and epoch
/// - requestEpoch == siteMaxEpoch AND requestDevice == recordedLeader: accept
/// - requestEpoch == siteMaxEpoch AND requestDevice != recordedLeader: reject (stale writer)
/// - requestEpoch &lt; siteMaxEpoch: reject (stale epoch)
///
/// Elections work when cloud is unreachable — agents register the new epoch on next contact.
/// </summary>
public sealed class AuthoritativeWriteFenceService : IAuthoritativeWriteFenceService
{
    private readonly IAgentConfigDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthoritativeWriteFenceService> _logger;

    public AuthoritativeWriteFenceService(
        IAgentConfigDbContext db,
        IConfiguration configuration,
        ILogger<AuthoritativeWriteFenceService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthoritativeWriteFenceResult> ValidateAsync(
        string? deviceIdClaim,
        string siteCode,
        long? requestLeaderEpoch,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("EdgeAgentDefaults:SiteHa:Enabled", false))
        {
            return AuthoritativeWriteFenceResult.Allow();
        }

        if (requestLeaderEpoch is null or <= 0)
        {
            return AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status400BadRequest,
                "VALIDATION.LEADER_EPOCH_REQUIRED",
                "Authoritative writes must include a positive leaderEpoch when site HA fencing is enabled.");
        }

        if (!Guid.TryParse(deviceIdClaim, out var deviceId))
        {
            return AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status400BadRequest,
                "VALIDATION.INVALID_DEVICE_ID",
                "Device JWT 'sub' claim must be a valid UUID when site HA fencing is enabled.");
        }

        var currentAgent = await _db.FindAgentByDeviceIdAsync(deviceId, cancellationToken);
        if (currentAgent is null)
        {
            return AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status401Unauthorized,
                "DEVICE_NOT_FOUND",
                "Device not found. It may have been deleted.");
        }

        if (!string.Equals(currentAgent.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase))
        {
            return AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN.DEVICE_SITE_SCOPE",
                "Authenticated device is not permitted to write for the requested site.",
                new
                {
                    deviceId,
                    registeredSiteCode = currentAgent.SiteCode,
                    requestSiteCode = siteCode
                });
        }

        // Load the site to get the recorded HA leader state
        var site = await _db.GetSiteByIdAsync(currentAgent.SiteId, cancellationToken);
        if (site is null)
        {
            return AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status409Conflict,
                "CONFLICT.SITE_NOT_FOUND",
                "Site not found in the database.");
        }

        var siteMaxEpoch = site.HaLeaderEpoch;
        var recordedLeaderId = site.HaLeaderAgentId;

        // ── P2-15: Epoch-based fencing (agent elections are authoritative) ──

        if (requestLeaderEpoch.Value > siteMaxEpoch)
        {
            // Higher epoch — this agent won a new election. Accept and record.
            _logger.LogInformation(
                "[{EventType}] New leader epoch accepted: device {DeviceId} at epoch {NewEpoch} (was {OldEpoch}) for site {SiteCode}",
                FailoverAuditEventTypes.HaEpochIncremented,
                deviceId, requestLeaderEpoch.Value, siteMaxEpoch, siteCode);

            await _db.UpdateSiteHaLeaderAsync(
                currentAgent.SiteId, deviceId, requestLeaderEpoch.Value, cancellationToken);

            return AuthoritativeWriteFenceResult.Allow();
        }

        if (requestLeaderEpoch.Value == siteMaxEpoch)
        {
            // Same epoch — only the recorded leader may write
            if (recordedLeaderId.HasValue && recordedLeaderId.Value == deviceId)
            {
                return AuthoritativeWriteFenceResult.Allow();
            }

            // Cold start: no leader recorded yet at this epoch — accept from first writer
            if (!recordedLeaderId.HasValue)
            {
                await _db.UpdateSiteHaLeaderAsync(
                    currentAgent.SiteId, deviceId, requestLeaderEpoch.Value, cancellationToken);
                return AuthoritativeWriteFenceResult.Allow();
            }

            // Different agent at same epoch — stale writer
            _logger.LogWarning(
                "[{EventType}] Non-leader write rejected: device {DeviceId} at epoch {Epoch}, leader is {LeaderId} for site {SiteCode}",
                FailoverAuditEventTypes.HaStaleWriterRejected,
                deviceId, requestLeaderEpoch.Value, recordedLeaderId.Value, siteCode);

            return AuthoritativeWriteFenceResult.Reject(
                StatusCodes.Status409Conflict,
                "CONFLICT.NON_LEADER_WRITE",
                "Authoritative write rejected because this device is not the current primary for the site.",
                new
                {
                    requestLeaderEpoch,
                    currentLeaderEpoch = siteMaxEpoch,
                    currentLeaderAgentId = recordedLeaderId,
                    deviceId
                });
        }

        // requestEpoch < siteMaxEpoch — stale epoch
        _logger.LogWarning(
            "[{EventType}] Stale epoch write rejected: device {DeviceId} epoch {RequestEpoch} < site max {SiteMaxEpoch} for site {SiteCode}",
            FailoverAuditEventTypes.HaStaleWriterRejected,
            deviceId, requestLeaderEpoch.Value, siteMaxEpoch, siteCode);

        return AuthoritativeWriteFenceResult.Reject(
            StatusCodes.Status409Conflict,
            "CONFLICT.STALE_LEADER_EPOCH",
            "Authoritative write rejected because the supplied leaderEpoch is stale.",
            new
            {
                requestLeaderEpoch,
                currentLeaderEpoch = siteMaxEpoch,
                currentLeaderAgentId = recordedLeaderId
            });
    }
}

public sealed record AuthoritativeWriteFenceResult(
    bool IsAllowed,
    int StatusCode,
    string? ErrorCode,
    string? Message,
    object? Details)
{
    public static AuthoritativeWriteFenceResult Allow() => new(true, StatusCodes.Status200OK, null, null, null);

    public static AuthoritativeWriteFenceResult Reject(
        int statusCode,
        string errorCode,
        string message,
        object? details = null) =>
        new(false, statusCode, errorCode, message, details);
}
