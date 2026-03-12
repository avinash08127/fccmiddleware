// NOTE: Requires NuGet package Microsoft.Extensions.Http.Polly (added to FccMiddleware.Infrastructure.csproj).
// The existing CloudFccAdapterFactoryRegistration uses a bare AddHttpClient() call — it does not
// register named/typed clients for Odoo or Databricks individually. Those external calls flow
// through the adapter factory's IHttpClientFactory.CreateClient() (unnamed). Named clients can be
// introduced later; this extension is designed for both named and unnamed builders.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;

namespace FccMiddleware.Infrastructure.Resilience;

/// <summary>
/// Extension methods for adding circuit breaker resilience to external HTTP calls.
/// Follows the Android agent's CircuitBreaker.kt pattern: 20 failures -> open,
/// exponential backoff 1s->60s, 5 min half-open window.
/// </summary>
public static class HttpClientResilienceExtensions
{
    /// <summary>
    /// Adds a circuit breaker policy to the named HTTP client registration.
    /// Opens after <paramref name="failureThreshold"/> consecutive failures,
    /// stays open for <paramref name="durationOfBreakSeconds"/> seconds.
    /// </summary>
    public static IHttpClientBuilder AddCircuitBreakerPolicy(
        this IHttpClientBuilder builder,
        int failureThreshold = 20,
        int durationOfBreakSeconds = 300,
        string? clientName = null)
    {
        return builder.AddPolicyHandler((services, _) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger($"CircuitBreaker[{clientName ?? "HttpClient"}]");

            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: failureThreshold,
                    durationOfBreak: TimeSpan.FromSeconds(durationOfBreakSeconds),
                    onBreak: (outcome, breakDelay) =>
                    {
                        logger.LogWarning(
                            "Circuit breaker OPEN for {ClientName}. Break duration: {BreakSeconds}s. Last error: {Error}",
                            clientName, breakDelay.TotalSeconds, outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                    },
                    onReset: () =>
                    {
                        logger.LogInformation("Circuit breaker CLOSED for {ClientName}", clientName);
                    },
                    onHalfOpen: () =>
                    {
                        logger.LogInformation("Circuit breaker HALF-OPEN for {ClientName}", clientName);
                    });
        });
    }

    /// <summary>
    /// Adds a retry + circuit breaker policy combination for resilient external calls.
    /// </summary>
    public static IHttpClientBuilder AddResiliencePolicies(
        this IHttpClientBuilder builder,
        string clientName,
        int retryCount = 3,
        int circuitBreakerThreshold = 20,
        int circuitBreakerBreakSeconds = 300)
    {
        // Retry policy (exponential backoff: 1s, 2s, 4s)
        builder.AddPolicyHandler((services, _) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>()
                .CreateLogger($"Retry[{clientName}]");

            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(
                    retryCount,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
                    onRetry: (outcome, delay, attempt, _) =>
                    {
                        logger.LogWarning(
                            "Retry {Attempt}/{MaxRetries} for {ClientName} after {DelaySeconds}s. Error: {Error}",
                            attempt, retryCount, clientName, delay.TotalSeconds,
                            outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                    });
        });

        // Circuit breaker (wraps retries)
        builder.AddCircuitBreakerPolicy(circuitBreakerThreshold, circuitBreakerBreakSeconds, clientName);

        return builder;
    }
}
