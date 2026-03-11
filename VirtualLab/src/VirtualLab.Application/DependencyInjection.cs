using Microsoft.Extensions.DependencyInjection;
using VirtualLab.Application.Diagnostics;

namespace VirtualLab.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddVirtualLabApplication(this IServiceCollection services)
    {
        services.AddSingleton<DiagnosticProbeService>();
        return services;
    }
}
