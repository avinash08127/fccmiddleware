using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Api.AgentControl;

public interface IAgentPushHintDispatcher
{
    Task<PushHintDispatchSummary> SendCommandPendingHintAsync(
        AgentCommand command,
        CancellationToken cancellationToken);

    Task<PushHintDispatchSummary> SendConfigChangedHintsForSiteAsync(
        Guid legalEntityId,
        string siteCode,
        int configVersion,
        CancellationToken cancellationToken);
}
