using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.HealthChecks;

/// <summary>
/// Integration tests for /health (liveness) and /health/ready (readiness) endpoints.
/// Uses Testcontainers to spin up real PostgreSQL and Redis containers.
/// </summary>
public sealed class HealthCheckTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:FccMiddleware"] = _postgres.GetConnectionString(),
                        ["ConnectionStrings:Redis"]         = _redis.GetConnectionString()
                    });
                });
            });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    [Fact]
    public async Task GetHealth_AlwaysReturns200_WithStructuredJson()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("HEALTHY");
        json.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        json.TryGetProperty("dependencies", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealthReady_WhenDependenciesHealthy_Returns200WithDependencyStatuses()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("HEALTHY");

        var deps = json.GetProperty("dependencies");
        deps.GetProperty("postgres").GetProperty("status").GetString().Should().Be("HEALTHY");
        deps.GetProperty("redis").GetProperty("status").GetString().Should().Be("HEALTHY");
    }

    [Fact]
    public async Task GetHealthReady_WhenDependenciesDown_Returns503()
    {
        // Use connection strings that immediately refuse — no real servers on these ports
        await using var badFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:FccMiddleware"] =
                            "Host=127.0.0.1;Port=59997;Database=bad;Username=bad;Password=bad;Timeout=1;CommandTimeout=1",
                        ["ConnectionStrings:Redis"] =
                            "127.0.0.1:59998,connectTimeout=100,syncTimeout=100,abortConnect=false"
                    });
                });
            });

        var client = badFactory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("UNHEALTHY");
    }
}
