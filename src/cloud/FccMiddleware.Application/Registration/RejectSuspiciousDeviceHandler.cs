using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class RejectSuspiciousDeviceHandler
    : IRequestHandler<RejectSuspiciousDeviceCommand, Result<SuspiciousDeviceReviewResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly ILogger<RejectSuspiciousDeviceHandler> _logger;

    public RejectSuspiciousDeviceHandler(
        IRegistrationDbContext db,
        ILogger<RejectSuspiciousDeviceHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<SuspiciousDeviceReviewResult>> Handle(
        RejectSuspiciousDeviceCommand request,
        CancellationToken cancellationToken)
    {
        var device = await _db.FindAgentByIdAsync(request.DeviceId, cancellationToken);
        if (device is null)
        {
            return Result<SuspiciousDeviceReviewResult>.Failure("DEVICE_NOT_FOUND",
                $"Device '{request.DeviceId}' not found.");
        }

        if (!device.Status.IsSuspended())
        {
            return Result<SuspiciousDeviceReviewResult>.Failure("DEVICE_NOT_SUSPENDED",
                "Only pending or quarantined devices can be rejected.");
        }

        var now = DateTimeOffset.UtcNow;
        var previousStatus = device.Status.ToString();
        device.Status = AgentRegistrationStatus.DEACTIVATED;
        device.IsActive = false;
        device.DeactivatedAt = now;
        device.UpdatedAt = now;

        var refreshTokens = await _db.GetActiveRefreshTokensForDeviceAsync(device.Id, cancellationToken);
        foreach (var token in refreshTokens)
        {
            token.RevokedAt = now;
        }

        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = device.LegalEntityId,
            EventType = AgentControlAuditEventTypes.SuspiciousRegistrationRejected,
            EntityId = device.Id,
            CorrelationId = Guid.NewGuid(),
            SiteCode = device.SiteCode,
            Source = nameof(RejectSuspiciousDeviceHandler),
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = device.Id,
                PreviousStatus = previousStatus,
                request.Reason,
                RejectedAt = now,
                request.RejectedByActorId,
                request.RejectedByActorDisplay,
                RevokedTokenCount = refreshTokens.Count
            })
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Suspicious registration rejected for device {DeviceId} at site {SiteCode}",
            device.Id,
            device.SiteCode);

        return Result<SuspiciousDeviceReviewResult>.Success(new SuspiciousDeviceReviewResult
        {
            DeviceId = device.Id,
            Status = device.Status.ToString(),
            UpdatedAt = now
        });
    }
}
