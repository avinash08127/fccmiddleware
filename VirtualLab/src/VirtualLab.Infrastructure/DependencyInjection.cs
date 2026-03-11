using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Infrastructure.Diagnostics;

namespace VirtualLab.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVirtualLabInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VirtualLabPersistenceOptions>(configuration.GetSection(VirtualLabPersistenceOptions.SectionName));
        services.Configure<VirtualLabSeedOptions>(configuration.GetSection(VirtualLabSeedOptions.SectionName));

        services.AddDbContext<VirtualLabDbContext>((serviceProvider, options) =>
        {
            VirtualLabPersistenceOptions persistenceOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualLabPersistenceOptions>>()
                .Value;

            if (!string.Equals(persistenceOptions.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported VirtualLab provider '{persistenceOptions.Provider}'.");
            }

            options.UseSqlite(
                persistenceOptions.ConnectionString,
                sqliteOptions => sqliteOptions.MigrationsAssembly(typeof(VirtualLabDbContext).Assembly.FullName));
        });

        services.AddSingleton<ApiTimingStore>();
        services.AddScoped<IVirtualLabSeedService, VirtualLabSeedService>();
        services.AddScoped<IFccProfileService, FccProfileService>();
        return services;
    }
}
