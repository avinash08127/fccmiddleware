using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.Core.Runtime;

/// <summary>
/// Convenience entry point that delegates to <see cref="ServiceCollectionExtensions.AddAgentCore"/>.
/// Prefer calling <c>AddAgentCore</c> directly for clarity.
/// </summary>
public static class AgentHostBuilder
{
    /// <inheritdoc cref="ServiceCollectionExtensions.AddAgentCore"/>
    public static IServiceCollection AddAgentCoreServices(
        this IServiceCollection services,
        IConfiguration config)
        => services.AddAgentCore(config);
}
