using System.Security.Cryptography;
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

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Token refreshed for device {DeviceId}", device.Id);

        return Result<RefreshDeviceTokenResult>.Success(new RefreshDeviceTokenResult
        {
            DeviceToken = newDeviceToken,
            RefreshToken = rawRefreshToken,
            TokenExpiresAt = tokenExpiresAt
        });
    }
}
