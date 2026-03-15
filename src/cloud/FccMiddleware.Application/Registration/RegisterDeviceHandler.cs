using System.Security.Cryptography;
using System.Text.Json;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.Common;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Application.Registration;

public sealed class RegisterDeviceHandler
    : IRequestHandler<RegisterDeviceCommand, Result<RegisterDeviceResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly IAgentConfigDbContext _agentConfigDb;
    private readonly IDeviceTokenService _tokenService;
    private readonly ILogger<RegisterDeviceHandler> _logger;
    private readonly SuspiciousDeviceWorkflowOptions _workflowOptions;

    public RegisterDeviceHandler(
        IRegistrationDbContext db,
        IAgentConfigDbContext agentConfigDb,
        IDeviceTokenService tokenService,
        IOptions<SuspiciousDeviceWorkflowOptions> workflowOptions,
        ILogger<RegisterDeviceHandler> logger)
    {
        _db = db;
        _agentConfigDb = agentConfigDb;
        _tokenService = tokenService;
        _logger = logger;
        _workflowOptions = workflowOptions.Value;
    }

    public async Task<Result<RegisterDeviceResult>> Handle(
        RegisterDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var tokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(request.ProvisioningToken);
        var bootstrapToken = await _db.FindBootstrapTokenByHashAsync(tokenHash, cancellationToken);

        if (bootstrapToken is null)
            return Result<RegisterDeviceResult>.Failure("BOOTSTRAP_TOKEN_INVALID",
                "Bootstrap token not found.");

        if (bootstrapToken.Status == ProvisioningTokenStatus.USED || bootstrapToken.UsedAt.HasValue)
            return Result<RegisterDeviceResult>.Failure("BOOTSTRAP_TOKEN_ALREADY_USED",
                "Bootstrap token has already been used.");

        if (bootstrapToken.Status == ProvisioningTokenStatus.REVOKED)
            return Result<RegisterDeviceResult>.Failure("BOOTSTRAP_TOKEN_REVOKED",
                "Bootstrap token has been revoked.");

        if (bootstrapToken.ExpiresAt <= now)
            return Result<RegisterDeviceResult>.Failure("BOOTSTRAP_TOKEN_EXPIRED",
                "Bootstrap token has expired.");

        if (!string.Equals(bootstrapToken.SiteCode, request.SiteCode, StringComparison.OrdinalIgnoreCase))
            return Result<RegisterDeviceResult>.Failure("SITE_MISMATCH",
                "Site code does not match the bootstrap token.");

        var site = await _db.FindSiteBySiteCodeAsync(request.SiteCode, cancellationToken);
        if (site is null)
            return Result<RegisterDeviceResult>.Failure("SITE_NOT_FOUND",
                $"Site '{request.SiteCode}' not found.");

        if (site.LegalEntityId != bootstrapToken.LegalEntityId)
            return Result<RegisterDeviceResult>.Failure("SITE_MISMATCH",
                "Bootstrap token LegalEntityId does not match the site's LegalEntityId.");

        var existingConfig = await _agentConfigDb.GetFccConfigWithSiteDataAsync(
            request.SiteCode,
            bootstrapToken.LegalEntityId,
            cancellationToken);

        if (existingConfig is null)
            return Result<RegisterDeviceResult>.Failure("CONFIG_NOT_FOUND",
                "No active FCC configuration found for this site. Registration is blocked until site configuration is published.");

        if (!TryResolveHaMetadata(request, out var haMetadata, out var haError))
        {
            return Result<RegisterDeviceResult>.Failure(haError!.Value.Code, haError.Value.Message);
        }

        var activeAgents = await _db.FindActiveAgentsForSiteAsync(site.Id, cancellationToken);
        var sameSerialActiveAgent = activeAgents.FirstOrDefault(agent =>
            SameSerial(agent.DeviceSerialNumber, request.DeviceSerialNumber));
        var replacementTarget = ResolveReplacementTarget(activeAgents, sameSerialActiveAgent, request.ReplacePreviousAgent);
        var suspendedRegistration = await _db.FindSuspendedAgentForSiteAndSerialAsync(
            site.Id,
            request.DeviceSerialNumber,
            cancellationToken);

        if (suspendedRegistration is not null)
        {
            UpdateHeldRegistrationSnapshot(
                suspendedRegistration,
                haMetadata,
                request,
                replacementTarget?.Id,
                now);

            if (suspendedRegistration.ApprovalGrantedAt is null)
            {
                await PersistHeldRegistrationAsync(
                    suspendedRegistration,
                    "Repeated registration attempt blocked while operator review is still pending.",
                    cancellationToken);

                return SuspiciousFailure(
                    suspendedRegistration.Status,
                    suspendedRegistration.SuspensionReason);
            }
        }

        var approvalGranted = suspendedRegistration?.ApprovalGrantedAt is not null;
        if (!approvalGranted)
        {
            var disposition = EvaluateDisposition(replacementTarget, request);
            if (disposition is not null)
            {
                var heldRegistration = suspendedRegistration
                    ?? CreateHeldRegistration(site, bootstrapToken.LegalEntityId, haMetadata, request, disposition, replacementTarget?.Id, now);

                ApplyHeldRegistrationState(
                    heldRegistration,
                    haMetadata,
                    request,
                    disposition,
                    replacementTarget?.Id,
                    now);

                if (suspendedRegistration is null)
                {
                    _db.AddAgentRegistration(heldRegistration);
                }

                await PersistHeldRegistrationAsync(heldRegistration, disposition.AuditMessage, cancellationToken);
                return Result<RegisterDeviceResult>.Failure(disposition.ErrorCode, disposition.ClientMessage);
            }
        }

        if (replacementTarget is not null && replacementTarget.Id != sameSerialActiveAgent?.Id)
        {
            await DeactivateExistingAgent(replacementTarget, now, cancellationToken);
            site.PeerDirectoryVersion++;

            // P2-07: Enqueue REFRESH_CONFIG for remaining active agents after deactivation
            foreach (var peer in activeAgents.Where(a => a.Id != replacementTarget.Id && a.IsActive))
            {
                _db.AddAgentCommand(new AgentCommand
                {
                    Id = Guid.NewGuid(),
                    DeviceId = peer.Id,
                    LegalEntityId = site.LegalEntityId,
                    SiteCode = request.SiteCode,
                    CommandType = AgentCommandType.REFRESH_CONFIG,
                    Reason = $"Peer directory changed: agent {replacementTarget.Id} deactivated",
                    Status = AgentCommandStatus.PENDING,
                    CreatedAt = now,
                    ExpiresAt = now.AddMinutes(10),
                    UpdatedAt = now,
                });
            }

            _logger.LogInformation(
                "Deactivated existing agent {OldDeviceId} for site {SiteCode} (approvedReplacement={ApprovedReplacement})",
                replacementTarget.Id,
                request.SiteCode,
                approvalGranted);
        }

        var deviceId = sameSerialActiveAgent?.Id ?? suspendedRegistration?.Id ?? Guid.NewGuid();
        var (deviceToken, tokenExpiresAt) = _tokenService.GenerateDeviceToken(
            deviceId,
            request.SiteCode,
            bootstrapToken.LegalEntityId);

        var deviceTokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(deviceToken);
        var registration = sameSerialActiveAgent ?? suspendedRegistration ?? new AgentRegistration
        {
            Id = deviceId,
            SiteId = site.Id,
            LegalEntityId = bootstrapToken.LegalEntityId,
            SiteCode = request.SiteCode,
            CreatedAt = now
        };

        registration.SiteId = site.Id;
        registration.LegalEntityId = bootstrapToken.LegalEntityId;
        registration.SiteCode = request.SiteCode;
        registration.DeviceSerialNumber = request.DeviceSerialNumber;
        registration.DeviceModel = request.DeviceModel;
        registration.OsVersion = request.OsVersion;
        registration.AgentVersion = request.AgentVersion;
        registration.DeviceClass = haMetadata.DeviceClass;
        registration.RoleCapability = haMetadata.RoleCapability;
        registration.SiteHaPriority = haMetadata.Priority;
        registration.CurrentRole = null;
        registration.CapabilitiesJson = SerializeCapabilities(haMetadata.Capabilities);
        registration.PeerApiBaseUrl = haMetadata.PeerApiBaseUrl;
        registration.PeerApiAdvertisedHost = haMetadata.PeerApiAdvertisedHost;
        registration.PeerApiPort = haMetadata.PeerApiPort;
        registration.PeerApiTlsEnabled = haMetadata.PeerApiTlsEnabled;
        registration.LeaderEpochSeen ??= 0;
        registration.Status = AgentRegistrationStatus.ACTIVE;
        registration.IsActive = true;
        registration.TokenHash = deviceTokenHash;
        registration.TokenExpiresAt = tokenExpiresAt;
        registration.RegisteredAt = now;
        registration.DeactivatedAt = null;
        registration.SuspensionReasonCode = null;
        registration.SuspensionReason = null;
        registration.ReplacementForDeviceId = null;
        registration.UpdatedAt = now;

        if (sameSerialActiveAgent is null && suspendedRegistration is null)
        {
            _db.AddAgentRegistration(registration);
        }

        await RevokeRefreshTokensAsync(deviceId, now, cancellationToken);

        var refreshTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawRefreshToken = GenerateBootstrapTokenHandler.Base64UrlEncode(refreshTokenBytes);
        var refreshTokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(rawRefreshToken);

        var refreshToken = new DeviceRefreshToken
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            TokenHash = refreshTokenHash,
            ExpiresAt = now.AddDays(90),
            CreatedAt = now
        };

        _db.AddDeviceRefreshToken(refreshToken);

        bootstrapToken.Status = ProvisioningTokenStatus.USED;
        bootstrapToken.UsedAt = now;
        bootstrapToken.UsedByDeviceId = deviceId;

        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = bootstrapToken.LegalEntityId,
            EventType = "DEVICE_REGISTERED",
            EntityId = deviceId,
            CorrelationId = Guid.NewGuid(),
            SiteCode = request.SiteCode,
            Source = nameof(RegisterDeviceHandler),
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = deviceId,
                SiteCode = request.SiteCode,
                DeviceClass = haMetadata.DeviceClass,
                DeviceModel = request.DeviceModel,
                AgentVersion = request.AgentVersion,
                RoleCapability = haMetadata.RoleCapability,
                SiteHaPriority = haMetadata.Priority,
                Capabilities = haMetadata.Capabilities,
                PeerApi = new
                {
                    haMetadata.PeerApiBaseUrl,
                    haMetadata.PeerApiAdvertisedHost,
                    haMetadata.PeerApiPort,
                    haMetadata.PeerApiTlsEnabled
                },
                ReusedExistingIdentity = sameSerialActiveAgent is not null,
                ReplacedPreviousAgent = replacementTarget is not null && replacementTarget.Id != sameSerialActiveAgent?.Id,
                ReplacedDeviceId = replacementTarget is not null && replacementTarget.Id != sameSerialActiveAgent?.Id
                    ? (Guid?)replacementTarget.Id
                    : null,
                ActiveAgentCountAtRegistration = activeAgents.Count,
                RegisteredAt = now,
                SuspiciousRegistrationApprovedAt = suspendedRegistration?.ApprovalGrantedAt
            })
        });

        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = bootstrapToken.LegalEntityId,
            EventType = AgentControlAuditEventTypes.BootstrapTokenUsed,
            CorrelationId = Guid.NewGuid(),
            SiteCode = request.SiteCode,
            Source = nameof(RegisterDeviceHandler),
            EntityId = bootstrapToken.Id,
            Payload = JsonSerializer.Serialize(new
            {
                TokenId = bootstrapToken.Id,
                DeviceId = deviceId,
                SiteCode = request.SiteCode,
                UsedAt = now
            })
        });

        site.PeerDirectoryVersion++;

        // P2-07: Enqueue REFRESH_CONFIG for all other active agents at the site
        // so they detect the new peer directory and refresh their config.
        foreach (var peer in activeAgents.Where(a => a.Id != deviceId && a.IsActive))
        {
            _db.AddAgentCommand(new AgentCommand
            {
                Id = Guid.NewGuid(),
                DeviceId = peer.Id,
                LegalEntityId = site.LegalEntityId,
                SiteCode = request.SiteCode,
                CommandType = AgentCommandType.REFRESH_CONFIG,
                Reason = $"Peer directory changed: new agent {deviceId} registered",
                Status = AgentCommandStatus.PENDING,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(10),
                UpdatedAt = now,
            });
        }

        var saved = await _db.TrySaveChangesAsync(cancellationToken);
        if (!saved)
        {
            _logger.LogWarning(
                "Concurrent registration race detected for bootstrap token (site {SiteCode}). Another request consumed the token first.",
                request.SiteCode);

            return Result<RegisterDeviceResult>.Failure("BOOTSTRAP_TOKEN_ALREADY_USED",
                "Bootstrap token has already been used.");
        }

        _logger.LogInformation("Device {DeviceId} registered for site {SiteCode}",
            deviceId, request.SiteCode);

        return Result<RegisterDeviceResult>.Success(new RegisterDeviceResult
        {
            DeviceId = deviceId,
            DeviceToken = deviceToken,
            RefreshToken = rawRefreshToken,
            TokenExpiresAt = tokenExpiresAt,
            SiteCode = request.SiteCode,
            LegalEntityId = bootstrapToken.LegalEntityId,
            RegisteredAt = now
        });
    }

    private SuspiciousRegistrationDisposition? EvaluateDisposition(
        AgentRegistration? existingAgent,
        RegisterDeviceCommand request)
    {
        if (!_workflowOptions.Enabled)
        {
            return null;
        }

        var sameSerial = existingAgent is not null
            && SameSerial(existingAgent.DeviceSerialNumber, request.DeviceSerialNumber);

        if (existingAgent is not null && !sameSerial)
        {
            if (request.ReplacePreviousAgent && _workflowOptions.HoldUnexpectedSerialReplacement)
            {
                return new SuspiciousRegistrationDisposition(
                    AgentRegistrationStatus.PENDING_APPROVAL,
                    "DEVICE_PENDING_APPROVAL",
                    "UNEXPECTED_SERIAL_REPLACEMENT",
                    "Registration held for operator approval because the site is switching to a different device serial number.",
                    "Held suspicious registration because replacePreviousAgent targeted a different device serial.");
            }
        }

        if (_workflowOptions.QuarantineSecurityRuleMismatch)
        {
            var securityMismatch = EvaluateSecurityRuleMismatch(request);
            if (securityMismatch is not null)
            {
                return securityMismatch;
            }
        }

        return null;
    }

    private SuspiciousRegistrationDisposition? EvaluateSecurityRuleMismatch(RegisterDeviceCommand request)
    {
        if (_workflowOptions.AllowedDeviceModels.Count > 0
            && !_workflowOptions.AllowedDeviceModels.Contains(request.DeviceModel, StringComparer.OrdinalIgnoreCase))
        {
            return new SuspiciousRegistrationDisposition(
                AgentRegistrationStatus.QUARANTINED,
                "DEVICE_QUARANTINED",
                "SECURITY_RULE_DEVICE_MODEL_MISMATCH",
                $"Registration quarantined because device model '{request.DeviceModel}' is not allowed by policy.",
                "Quarantined registration because the device model is outside the configured allow-list.");
        }

        if (_workflowOptions.AllowedSerialPrefixes.Count > 0
            && !_workflowOptions.AllowedSerialPrefixes.Any(prefix =>
                request.DeviceSerialNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new SuspiciousRegistrationDisposition(
                AgentRegistrationStatus.QUARANTINED,
                "DEVICE_QUARANTINED",
                "SECURITY_RULE_SERIAL_PREFIX_MISMATCH",
                "Registration quarantined because the device serial number does not match the approved serial prefix policy.",
                "Quarantined registration because the device serial prefix is outside the configured allow-list.");
        }

        if (!string.IsNullOrWhiteSpace(_workflowOptions.MinimumAgentVersion)
            && !IsVersionAtLeast(request.AgentVersion, _workflowOptions.MinimumAgentVersion))
        {
            return new SuspiciousRegistrationDisposition(
                AgentRegistrationStatus.QUARANTINED,
                "DEVICE_QUARANTINED",
                "SECURITY_RULE_MIN_AGENT_VERSION",
                $"Registration quarantined because agent version '{request.AgentVersion}' is below the minimum approved version '{_workflowOptions.MinimumAgentVersion}'.",
                "Quarantined registration because the agent version is below the configured minimum.");
        }

        return null;
    }

    private async Task PersistHeldRegistrationAsync(
        AgentRegistration registration,
        string auditMessage,
        CancellationToken cancellationToken)
    {
        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = registration.UpdatedAt,
            LegalEntityId = registration.LegalEntityId,
            EventType = AgentControlAuditEventTypes.SuspiciousRegistrationHeld,
            EntityId = registration.Id,
            CorrelationId = Guid.NewGuid(),
            SiteCode = registration.SiteCode,
            Source = nameof(RegisterDeviceHandler),
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = registration.Id,
                registration.Status,
                registration.SuspensionReasonCode,
                registration.SuspensionReason,
                registration.ReplacementForDeviceId,
                registration.ApprovalGrantedAt,
                Message = auditMessage
            })
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static Result<RegisterDeviceResult> SuspiciousFailure(
        AgentRegistrationStatus status,
        string? message = null) =>
        Result<RegisterDeviceResult>.Failure(
            status.ToDeviceAccessErrorCode(),
            message ?? status.ToDeviceAccessErrorMessage());

    private static AgentRegistration CreateHeldRegistration(
        Site site,
        Guid legalEntityId,
        ResolvedHaRegistrationMetadata haMetadata,
        RegisterDeviceCommand request,
        SuspiciousRegistrationDisposition disposition,
        Guid? replacementForDeviceId,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            LegalEntityId = legalEntityId,
            SiteCode = request.SiteCode,
            DeviceSerialNumber = request.DeviceSerialNumber,
            DeviceModel = request.DeviceModel,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            DeviceClass = haMetadata.DeviceClass,
            RoleCapability = haMetadata.RoleCapability,
            SiteHaPriority = haMetadata.Priority,
            CapabilitiesJson = SerializeCapabilities(haMetadata.Capabilities),
            PeerApiBaseUrl = haMetadata.PeerApiBaseUrl,
            PeerApiAdvertisedHost = haMetadata.PeerApiAdvertisedHost,
            PeerApiPort = haMetadata.PeerApiPort,
            PeerApiTlsEnabled = haMetadata.PeerApiTlsEnabled,
            Status = disposition.Status,
            IsActive = false,
            TokenHash = null,
            TokenExpiresAt = null,
            RegisteredAt = now,
            DeactivatedAt = null,
            SuspensionReasonCode = disposition.ReasonCode,
            SuspensionReason = disposition.ClientMessage,
            ReplacementForDeviceId = replacementForDeviceId,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static void ApplyHeldRegistrationState(
        AgentRegistration registration,
        ResolvedHaRegistrationMetadata haMetadata,
        RegisterDeviceCommand request,
        SuspiciousRegistrationDisposition disposition,
        Guid? replacementForDeviceId,
        DateTimeOffset now)
    {
        registration.SiteCode = request.SiteCode;
        registration.DeviceSerialNumber = request.DeviceSerialNumber;
        registration.DeviceModel = request.DeviceModel;
        registration.OsVersion = request.OsVersion;
        registration.AgentVersion = request.AgentVersion;
        registration.DeviceClass = haMetadata.DeviceClass;
        registration.RoleCapability = haMetadata.RoleCapability;
        registration.SiteHaPriority = haMetadata.Priority;
        registration.CapabilitiesJson = SerializeCapabilities(haMetadata.Capabilities);
        registration.PeerApiBaseUrl = haMetadata.PeerApiBaseUrl;
        registration.PeerApiAdvertisedHost = haMetadata.PeerApiAdvertisedHost;
        registration.PeerApiPort = haMetadata.PeerApiPort;
        registration.PeerApiTlsEnabled = haMetadata.PeerApiTlsEnabled;
        registration.Status = disposition.Status;
        registration.IsActive = false;
        registration.TokenHash = null;
        registration.TokenExpiresAt = null;
        registration.DeactivatedAt = null;
        registration.SuspensionReasonCode = disposition.ReasonCode;
        registration.SuspensionReason = disposition.ClientMessage;
        registration.ReplacementForDeviceId = replacementForDeviceId;
        registration.UpdatedAt = now;
    }

    private static void UpdateHeldRegistrationSnapshot(
        AgentRegistration registration,
        ResolvedHaRegistrationMetadata haMetadata,
        RegisterDeviceCommand request,
        Guid? replacementForDeviceId,
        DateTimeOffset now)
    {
        registration.DeviceModel = request.DeviceModel;
        registration.OsVersion = request.OsVersion;
        registration.AgentVersion = request.AgentVersion;
        registration.DeviceClass = haMetadata.DeviceClass;
        registration.RoleCapability = haMetadata.RoleCapability;
        registration.SiteHaPriority = haMetadata.Priority;
        registration.CapabilitiesJson = SerializeCapabilities(haMetadata.Capabilities);
        registration.PeerApiBaseUrl = haMetadata.PeerApiBaseUrl;
        registration.PeerApiAdvertisedHost = haMetadata.PeerApiAdvertisedHost;
        registration.PeerApiPort = haMetadata.PeerApiPort;
        registration.PeerApiTlsEnabled = haMetadata.PeerApiTlsEnabled;
        registration.ReplacementForDeviceId = replacementForDeviceId;
        registration.UpdatedAt = now;
    }

    private async Task DeactivateExistingAgent(
        AgentRegistration existingAgent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        existingAgent.Status = AgentRegistrationStatus.DEACTIVATED;
        existingAgent.IsActive = false;
        existingAgent.DeactivatedAt = now;
        existingAgent.UpdatedAt = now;

        var oldTokens = await _db.GetActiveRefreshTokensForDeviceAsync(existingAgent.Id, cancellationToken);
        foreach (var token in oldTokens)
        {
            token.RevokedAt = now;
        }
    }

    private async Task RevokeRefreshTokensAsync(
        Guid deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeTokens = await _db.GetActiveRefreshTokensForDeviceAsync(deviceId, cancellationToken);
        foreach (var token in activeTokens)
        {
            token.RevokedAt = now;
        }
    }

    private static AgentRegistration? ResolveReplacementTarget(
        IReadOnlyList<AgentRegistration> activeAgents,
        AgentRegistration? sameSerialActiveAgent,
        bool replacePreviousAgent)
    {
        if (!replacePreviousAgent)
        {
            return null;
        }

        if (sameSerialActiveAgent is not null)
        {
            return sameSerialActiveAgent;
        }

        return activeAgents.Count == 1 ? activeAgents[0] : null;
    }

    private static bool TryResolveHaMetadata(
        RegisterDeviceCommand request,
        out ResolvedHaRegistrationMetadata? metadata,
        out (string Code, string Message)? error)
    {
        metadata = null;
        error = null;

        var deviceClass = NormalizeDeviceClass(request.DeviceClass);
        if (deviceClass is null)
        {
            error = ("INVALID_DEVICE_CLASS",
                $"Device class '{request.DeviceClass}' is not supported. Expected ANDROID or DESKTOP.");
            return false;
        }

        var requestedRoleCapability = NormalizeRoleCapability(request.RoleCapability);
        if (!string.IsNullOrWhiteSpace(request.RoleCapability) && requestedRoleCapability is null)
        {
            error = ("INVALID_ROLE_CAPABILITY",
                $"Role capability '{request.RoleCapability}' is not supported.");
            return false;
        }
        var roleCapability = requestedRoleCapability ?? DefaultRoleCapabilityFor(deviceClass);

        var priority = request.SiteHaPriority ?? DefaultPriorityFor(deviceClass);
        if (priority <= 0)
        {
            error = ("INVALID_SITE_HA_PRIORITY",
                "siteHaPriority must be greater than zero when provided.");
            return false;
        }

        metadata = new ResolvedHaRegistrationMetadata(
            DeviceClass: deviceClass,
            RoleCapability: roleCapability!,
            Priority: priority,
            Capabilities: ResolveCapabilities(request.Capabilities, deviceClass),
            PeerApiBaseUrl: NormalizeOptional(request.PeerApiBaseUrl),
            PeerApiAdvertisedHost: NormalizeOptional(request.PeerApiAdvertisedHost),
            PeerApiPort: request.PeerApiPort,
            PeerApiTlsEnabled: request.PeerApiTlsEnabled);
        return true;
    }

    private static bool SameSerial(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string SerializeCapabilities(IReadOnlyList<string> capabilities) =>
        JsonSerializer.Serialize(capabilities);

    private static string[] ResolveCapabilities(IReadOnlyList<string> requestedCapabilities, string deviceClass)
    {
        if (requestedCapabilities.Count > 0)
        {
            return requestedCapabilities
                .Select(capability => capability.Trim().ToUpperInvariant())
                .Where(capability => !string.IsNullOrWhiteSpace(capability))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return deviceClass switch
        {
            "DESKTOP" => ["FCC_CONTROL", "PEER_API", "TRANSACTION_BUFFER", "TELEMETRY"],
            _ => ["FCC_CONTROL", "LOCALHOST_API", "LAN_PROXY", "TRANSACTION_BUFFER", "TELEMETRY"]
        };
    }

    private static string? NormalizeDeviceClass(string? rawDeviceClass) =>
        rawDeviceClass?.Trim().ToUpperInvariant() switch
        {
            "ANDROID" => "ANDROID",
            "DESKTOP" => "DESKTOP",
            _ => null
        };

    private static string? NormalizeRoleCapability(string? rawRoleCapability) =>
        rawRoleCapability?.Trim().ToUpperInvariant() switch
        {
            "PRIMARY_ELIGIBLE" => "PRIMARY_ELIGIBLE",
            "STANDBY_ONLY" => "STANDBY_ONLY",
            "READ_ONLY" => "READ_ONLY",
            null => null,
            "" => null,
            _ => null
        };

    private static string DefaultRoleCapabilityFor(string deviceClass) =>
        deviceClass switch
        {
            "DESKTOP" => "PRIMARY_ELIGIBLE",
            _ => "PRIMARY_ELIGIBLE"
        };

    private static int DefaultPriorityFor(string deviceClass) =>
        deviceClass switch
        {
            "DESKTOP" => 10,
            _ => 100
        };

    private static string? NormalizeOptional(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static bool IsVersionAtLeast(string currentVersion, string? minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(minimumVersion))
        {
            return true;
        }

        var normalizedCurrent = NormalizeVersion(currentVersion);
        var normalizedMinimum = NormalizeVersion(minimumVersion);
        if (normalizedCurrent is null || normalizedMinimum is null)
        {
            return true;
        }

        return normalizedCurrent >= normalizedMinimum;
    }

    private static Version? NormalizeVersion(string rawVersion)
    {
        var cleaned = rawVersion.Split('-', '+')[0];
        return Version.TryParse(cleaned, out var parsed) ? parsed : null;
    }

    private sealed record ResolvedHaRegistrationMetadata(
        string DeviceClass,
        string RoleCapability,
        int Priority,
        string[] Capabilities,
        string? PeerApiBaseUrl,
        string? PeerApiAdvertisedHost,
        int? PeerApiPort,
        bool PeerApiTlsEnabled);

    private sealed record SuspiciousRegistrationDisposition(
        AgentRegistrationStatus Status,
        string ErrorCode,
        string ReasonCode,
        string ClientMessage,
        string AuditMessage);
}
