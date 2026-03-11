namespace FccMiddleware.Contracts.Registration;

public sealed class DecommissionResponse
{
    public Guid DeviceId { get; set; }
    public DateTimeOffset DeactivatedAt { get; set; }
}
