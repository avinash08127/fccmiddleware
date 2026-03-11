using FccMiddleware.Domain.Common;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FccMiddleware.UnitTests.Security;

public sealed class RedactingDestructuringPolicyTests
{
    [Fact]
    public void DestructuredSensitiveProperties_AreRedacted()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .Destructure.With<FccMiddleware.ServiceDefaults.Security.RedactingDestructuringPolicy>()
            .CreateLogger();

        logger.Information("payload {@Payload}", new SensitivePayload
        {
            SiteCode = "GH-001",
            RefreshToken = "refresh-token-value",
            CustomerTaxId = "TIN-123456"
        });

        var payload = sink.Events.Should().ContainSingle().Subject.Properties["Payload"] as StructureValue;
        payload.Should().NotBeNull();
        payload!.Properties.Single(property => property.Name == nameof(SensitivePayload.SiteCode))
            .Value.Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be("GH-001");
        payload.Properties.Single(property => property.Name == nameof(SensitivePayload.RefreshToken))
            .Value.Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be("[REDACTED]");
        payload.Properties.Single(property => property.Name == nameof(SensitivePayload.CustomerTaxId))
            .Value.Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be("[REDACTED]");
    }

    private sealed class SensitivePayload
    {
        public string SiteCode { get; init; } = string.Empty;

        [Sensitive]
        public string RefreshToken { get; init; } = string.Empty;

        [Sensitive]
        public string CustomerTaxId { get; init; } = string.Empty;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }
}
