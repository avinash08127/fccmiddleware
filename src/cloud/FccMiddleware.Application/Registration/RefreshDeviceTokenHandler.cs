using System.Security.Cryptography;
using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class RefreshDeviceTokenHandler
    : IRequestHandler<RefreshDeviceTokenCommand, Result<RefreshDeviceTokenResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly IDeviceTokenService _tokenService;
    private readonly ILogger<RefreshDeviceTokenHandler> _logger;

    public RefreshDeviceTokenHandler(
        IRegistrationDbContext db,
        IDeviceTokenService tokenService,
        ILogger<RefreshDeviceTokenHandler> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<Result<RefreshDeviceTokenResult>> Handle(
        RefreshDeviceTokenCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Look up refresh token
        var tokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(request.RefreshToken);
        var existingToken = await _db.FindRefreshTokenByHashAsync(tokenHash, cancellationToken);

        if (existingToken is null)
            return Result<RefreshDeviceTokenResult>.Failure("REFRESH_TOKEN_INVALID",
                "Refresh token not found.");

        // BUG-010: Detect refresh token reuse — a revoked but not-yet-expired token
        // being presented again indicates the token may have been stolen.
        // Per RFC 6819 §5.2.2.3: revoke ALL active tokens for that device.
        if (existingToken.RevokedAt is not null && existingToken.ExpiresAt > now)
        {
            _logger.LogWarning(
                "Refresh token reuse detected for device {DeviceId} — revoking all tokens (potential compromise)",
                existingToken.DeviceId);

            var activeTokens = await _db.GetActiveRefreshTokensForDeviceAsync(
                existingToken.DeviceId, cancellationToken);
            foreach (var token in activeTokens)
                token.RevokedAt = now;

            var compromisedDevice = await _db.FindAgentByIdAsync(existingToken.DeviceId, cancellationToken);
            if (compromisedDevice is not null)
            {
                _db.AddAuditEvent(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now,
                    LegalEntityId = compromisedDevice.LegalEntityId,
                    EventType = "REFRESH_TOKEN_REUSE_DETECTED",
                    CorrelationId = Guid.NewGuid(),
                    SiteCode = compromisedDevice.SiteCode,
                    Source = "RefreshDeviceTokenHandler",
                    Payload = JsonSerializer.Serialize(new
                    {
                        DeviceId = existingToken.DeviceId,
                        ReusedTokenHash = tokenHash,
                        RevokedTokenCount = activeTokens.Count,
                        DetectedAt = now
                    })
                });
            }

            await _db.SaveChangesAsync(cancellationToken);

            return Result<RefreshDeviceTokenResult>.Failure("REFRESH_TOKEN_REUSE",
                "Refresh token has already been used. All device tokens have been revoked for security.");
        }

        if (!existingToken.IsValid(now))
            return Result<RefreshDeviceTokenResult>.Failure("REFRESH_TOKEN_EXPIRED",
                "Refresh token has expired or been revoked.");

        // 2. Verify device is still active
        var device = await _db.FindAgentByIdAsync(existingToken.DeviceId, cancellationToken);
        if (device is null || !device.IsActive)
            return Result<RefreshDeviceTokenResult>.Failure("DEVICE_DECOMMISSIONED",
                "Device has been decommissioned.");

        // 3. Revoke old refresh token (rotation)
        existingToken.RevokedAt = now;

        // 4. Issue new device JWT
        var (newDeviceToken, tokenExpiresAt) = _tokenService.GenerateDeviceToken(
            device.Id, device.SiteCode, device.LegalEntityId);

        // Update agent registration with new token hash
        device.TokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(newDeviceToken);
        device.TokenExpiresAt = tokenExpiresAt;
        device.UpdatedAt = now;

        // 5. Issue new refresh token
        var refreshTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawRefreshToken = GenerateBootstrapTokenHandler.Base64UrlEncode(refreshTokenBytes);
        var refreshTokenHash = GenerateBootstrapTokenHandler.ComputeSha256Hex(rawRefreshToken);

        var newRefreshToken = new DeviceRefreshToken
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            TokenHash = refreshTokenHash,
            ExpiresAt = now.AddDays(90),
            CreatedAt = now
        };

        _db.AddDeviceRefreshToken(newRefreshToken);

        // Audit: token refresh
        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = device.LegalEntityId,
            EventType = "DEVICE_TOKEN_REFRESHED",
            CorrelationId = Guid.NewGuid(),
            SiteCode = device.SiteCode,
            Source = "RefreshDeviceTokenHandler",
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = device.Id,
                RefreshedAt = now,
            })
        });

        // BUG-008: Use optimistic concurrency to prevent race condition.
        // RevokedAt is configured as a concurrency token — if another request already
        // revoked this token, TrySaveChangesAsync returns false instead of creating
        // two valid token pairs.
        var saved = await _db.TrySaveChangesAsync(cancellationToken);
        if (!saved)
        {
            _logger.LogWarning(
                "Concurrent token refresh detected for device {DeviceId} — request rejected",
                device.Id);
            return Result<RefreshDeviceTokenResult>.Failure("REFRESH_TOKEN_CONFLICT",
                "Token was already refreshed by another request.");
        }

        _logger.LogInformation("Token refreshed for device {DeviceId}", device.Id);

        return Result<RefreshDeviceTokenResult>.Success(new RefreshDeviceTokenResult
        {
            DeviceToken = newDeviceToken,
            RefreshToken = rawRefreshToken,
            TokenExpiresAt = tokenExpiresAt
        });
    }
}
