using FccMiddleware.Application.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class DecommissionDeviceHandler
    : IRequestHandler<DecommissionDeviceCommand, Result<DecommissionDeviceResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly ILogger<DecommissionDeviceHandler> _logger;

    public DecommissionDeviceHandler(IRegistrationDbContext db, ILogger<DecommissionDeviceHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<DecommissionDeviceResult>> Handle(
        DecommissionDeviceCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var device = await _db.FindAgentByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
            return Result<DecommissionDeviceResult>.Failure("DEVICE_NOT_FOUND",
                $"Device '{request.DeviceId}' not found.");

        if (!device.IsActive)
            return Result<DecommissionDeviceResult>.Failure("DEVICE_ALREADY_DECOMMISSIONED",
                "Device is already decommissioned.");

        // Deactivate device
        device.IsActive = false;
        device.DeactivatedAt = now;
        device.UpdatedAt = now;

        // Revoke all refresh tokens
        var tokens = await _db.GetActiveRefreshTokensForDeviceAsync(device.Id, cancellationToken);
        foreach (var token in tokens)
            token.RevokedAt = now;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Device {DeviceId} decommissioned for site {SiteCode}",
            device.Id, device.SiteCode);

        return Result<DecommissionDeviceResult>.Success(new DecommissionDeviceResult
        {
            DeviceId = device.Id,
            DeactivatedAt = now
        });
    }
}
