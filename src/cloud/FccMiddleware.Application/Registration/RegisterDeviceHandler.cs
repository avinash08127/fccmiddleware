using System.Security.Cryptography;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class RegisterDeviceHandler
    : IRequestHandler<RegisterDeviceCommand, Result<RegisterDeviceResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly IDeviceTokenService _tokenService;
    private readonly ILogger<RegisterDeviceHandler> _logger;

    public RegisterDeviceHandler(
        IRegistrationDbContext db,
        IDeviceTokenService tokenService,
        ILogger<RegisterDeviceHandler> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<Result<RegisterDeviceResult>> Handle(
        RegisterDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Validate bootstrap token
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

        // 2. Find site
        var site = await _db.FindSiteBySiteCodeAsync(request.SiteCode, cancellationToken);
        if (site is null)
            return Result<RegisterDeviceResult>.Failure("SITE_NOT_FOUND",
                $"Site '{request.SiteCode}' not found.");

        // 3. Check for existing active agent
        var existingAgent = await _db.FindActiveAgentForSiteAsync(site.Id, cancellationToken);
        if (existingAgent is not null)
        {
            if (!request.ReplacePreviousAgent)
                return Result<RegisterDeviceResult>.Failure("ACTIVE_AGENT_EXISTS",
                    "An active agent is already registered for this site. Set replacePreviousAgent=true to replace.");

            // Deactivate existing agent
            existingAgent.IsActive = false;
            existingAgent.DeactivatedAt = now;
            existingAgent.UpdatedAt = now;

            // Revoke all refresh tokens for old device
            var oldTokens = await _db.GetActiveRefreshTokensForDeviceAsync(existingAgent.Id, cancellationToken);
            foreach (var t in oldTokens)
                t.RevokedAt = now;

            _logger.LogInformation("Deactivated existing agent {OldDeviceId} for site {SiteCode} (replaced)",
                existingAgent.Id, request.SiteCode);
        }

        // 4. Create new agent registration
        var deviceId = Guid.NewGuid();
        var (deviceToken, tokenExpiresAt) = _tokenService.GenerateDeviceToken(
            deviceId, request.SiteCode, bootstrapToken.LegalEntityId);
        var deviceTokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(deviceToken);

        var registration = new AgentRegistration
        {
            Id = deviceId,
            SiteId = site.Id,
            LegalEntityId = bootstrapToken.LegalEntityId,
            SiteCode = request.SiteCode,
            DeviceSerialNumber = request.DeviceSerialNumber,
            DeviceModel = request.DeviceModel,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            IsActive = true,
            TokenHash = deviceTokenHash,
            TokenExpiresAt = tokenExpiresAt,
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.AddAgentRegistration(registration);

        // 5. Generate refresh token (opaque, 90 days)
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

        // 6. Mark bootstrap token as used
        bootstrapToken.Status = ProvisioningTokenStatus.USED;
        bootstrapToken.UsedAt = now;
        bootstrapToken.UsedByDeviceId = deviceId;

        await _db.SaveChangesAsync(cancellationToken);

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
}
