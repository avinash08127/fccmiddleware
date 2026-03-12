using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Tests.Simulators;

/// <summary>
/// Shared test fixture that starts a single VirtualLab host for all simulator E2E tests.
/// Simulators bind to fixed network ports (TCP/HTTP), so only one host can run at a time.
/// All simulator test classes must use [Collection("Simulators")] to share this fixture
/// and run sequentially.
/// </summary>
public sealed class SimulatorTestFixture : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"vl-sim-tests-{Guid.NewGuid():N}.db");

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["VirtualLab:Persistence:ConnectionString"] = $"Data Source={_databasePath}",
                        ["VirtualLab:Seed:ApplyOnStartup"] = "true",
                        ["VirtualLab:Seed:ResetOnStartup"] = "true",
                        ["VirtualLab:Callbacks:WorkerPollIntervalMs"] = "100",
                        ["VirtualLab:Callbacks:MaxRetryCount"] = "2",
                        ["VirtualLab:Callbacks:RetryDelaysSeconds:0"] = "30",
                        ["VirtualLab:Callbacks:RetryDelaysSeconds:1"] = "30",
                        ["VirtualLab:Callbacks:RequestTimeoutSeconds"] = "2",
                    });
                });
            });

        // Trigger host startup so simulators start listening.
        Client = Factory.CreateClient();

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        try { File.Delete(_databasePath); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Reset all simulators to initial state between tests.
    /// </summary>
    public async Task ResetAllSimulatorsAsync()
    {
        await Client.PostAsync("/api/doms-jpl/reset", null);
        await Client.PostAsync("/api/radix/reset", null);
        await Client.PostAsync("/api/petronite/reset", null);
    }
}

[CollectionDefinition("Simulators")]
public class SimulatorCollection : ICollectionFixture<SimulatorTestFixture>
{
}
