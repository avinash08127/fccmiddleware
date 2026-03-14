using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.Registration;

public sealed class ApproveSuspiciousDeviceHandler
    : IRequestHandler<ApproveSuspiciousDeviceCommand, Result<SuspiciousDeviceReviewResult>>
{
    private readonly IRegistrationDbContext _db;
    private readonly ILogger<ApproveSuspiciousDeviceHandler> _logger;

    public ApproveSuspiciousDeviceHandler(
        IRegistrationDbContext db,
        ILogger<ApproveSuspiciousDeviceHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<SuspiciousDeviceReviewResult>> Handle(
        ApproveSuspiciousDeviceCommand request,
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
                "Only pending or quarantined devices can be approved.");
        }

        var now = DateTimeOffset.UtcNow;
        device.ApprovalGrantedAt = now;
        device.ApprovalGrantedByActorId = request.ApprovedByActorId;
        device.ApprovalGrantedByActorDisplay = request.ApprovedByActorDisplay;
        device.UpdatedAt = now;

        _db.AddAuditEvent(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            LegalEntityId = device.LegalEntityId,
            EventType = AgentControlAuditEventTypes.SuspiciousRegistrationApproved,
            EntityId = device.Id,
            CorrelationId = Guid.NewGuid(),
            SiteCode = device.SiteCode,
            Source = nameof(ApproveSuspiciousDeviceHandler),
            Payload = JsonSerializer.Serialize(new
            {
                DeviceId = device.Id,
                device.Status,
                device.SuspensionReasonCode,
                device.SuspensionReason,
                request.Reason,
                ApprovedAt = now,
                request.ApprovedByActorId,
                request.ApprovedByActorDisplay
            })
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Suspicious registration approved for device {DeviceId} at site {SiteCode}",
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
