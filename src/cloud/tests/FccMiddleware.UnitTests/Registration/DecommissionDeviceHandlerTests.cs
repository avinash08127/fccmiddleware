using FccMiddleware.Application.Registration;
using FccMiddleware.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FccMiddleware.UnitTests.Registration;

public sealed class DecommissionDeviceHandlerTests
{
    private readonly IRegistrationDbContext _db = Substitute.For<IRegistrationDbContext>();

    [Fact]
    public async Task Handle_WhenSaveConflicts_ReturnsAlreadyDecommissionedFailure()
    {
        var deviceId = Guid.NewGuid();
        var legalEntityId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var device = new AgentRegistration
        {
            Id = deviceId,
            SiteId = siteId,
            LegalEntityId = legalEntityId,
            SiteCode = "SITE-1",
            DeviceSerialNumber = "SN-001",
            DeviceModel = "Urovo i9100",
            OsVersion = "Android 12",
            AgentVersion = "1.0.0",
            IsActive = true,
            TokenHash = "token-hash",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(12),
            RegisteredAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var token = new DeviceRefreshToken
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            TokenHash = "refresh-hash",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        _db.FindAgentByIdAsync(deviceId, Arg.Any<CancellationToken>())
            .Returns(device);
        _db.GetActiveRefreshTokensForDeviceAsync(deviceId, Arg.Any<CancellationToken>())
            .Returns([token]);
        _db.TrySaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new DecommissionDeviceHandler(_db, NullLogger<DecommissionDeviceHandler>.Instance);

        var result = await sut.Handle(new DecommissionDeviceCommand
        {
            DeviceId = deviceId,
            DecommissionedBy = "portal-admin",
            Reason = "Concurrent admin request"
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("DEVICE_ALREADY_DECOMMISSIONED");
        result.Error.Message.Should().Contain("already decommissioned");
        await _db.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
