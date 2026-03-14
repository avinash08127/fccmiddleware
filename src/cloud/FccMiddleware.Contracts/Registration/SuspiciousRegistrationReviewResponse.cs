namespace FccMiddleware.Contracts.Registration;

public sealed class SuspiciousRegistrationReviewResponse
{
    public Guid DeviceId { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
}
