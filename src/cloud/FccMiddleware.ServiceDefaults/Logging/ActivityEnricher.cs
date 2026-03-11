using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace FccMiddleware.ServiceDefaults.Logging;

internal sealed class ActivityEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("traceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("spanId", activity.SpanId.ToString()));
    }
}
