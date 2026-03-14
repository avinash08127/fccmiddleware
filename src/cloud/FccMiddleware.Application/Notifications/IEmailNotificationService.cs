namespace FccMiddleware.Application.Notifications;

public interface IEmailNotificationService
{
    Task SendBootstrapTokenGeneratedAsync(
        BootstrapTokenGeneratedEmail email,
        CancellationToken cancellationToken);
}

public sealed class BootstrapTokenGeneratedEmail
{
    public required string SiteCode { get; init; }
    public required Guid TokenId { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string GeneratedBy { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
