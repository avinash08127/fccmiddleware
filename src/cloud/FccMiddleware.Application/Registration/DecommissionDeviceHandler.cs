using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
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

        // Audit: device decommissioned
        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = device.LegalEntityId,
            EventType = "DEVICE_DECOMMISSIONED",
            EntityId = device.Id,
            CorrelationId = Guid.NewGuid(),
            SiteCode = device.SiteCode,
            Source = "DecommissionDeviceHandler",
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = device.Id,
                SiteCode = device.SiteCode,
                DecommissionedBy = request.DecommissionedBy,
                Reason = request.Reason, // FM-S04: Audit reason for traceability
                RevokedTokenCount = tokens.Count,
                DeactivatedAt = now,
            })
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Device {DeviceId} decommissioned for site {SiteCode} by {DecommissionedBy}",
            device.Id, device.SiteCode, request.DecommissionedBy);

        return Result<DecommissionDeviceResult>.Success(new DecommissionDeviceResult
        {
            DeviceId = device.Id,
            DeactivatedAt = now
        });
    }
}
