namespace FccMiddleware.Api.AgentControl;

public interface IAgentPushHintSender
{
    Task<PushHintProviderResult> SendAsync(
        string registrationToken,
        PushHintRequest request,
        CancellationToken cancellationToken);
}
