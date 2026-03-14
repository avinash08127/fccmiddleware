using FccMiddleware.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Messaging;

public sealed class NoOpEmailService : IEmailNotificationService
{
    private readonly ILogger<NoOpEmailService> _logger;

    public NoOpEmailService(ILogger<NoOpEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendBootstrapTokenGeneratedAsync(
        BootstrapTokenGeneratedEmail email, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Email notifications disabled. Skipping bootstrap token notification for site {SiteCode}, token {TokenId}",
            email.SiteCode, email.TokenId);
        return Task.CompletedTask;
    }
}
