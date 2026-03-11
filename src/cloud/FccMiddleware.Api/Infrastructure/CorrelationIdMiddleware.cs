using System.Diagnostics;
using Serilog.Context;

namespace FccMiddleware.Api.Infrastructure;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context.Request.Headers[HeaderName]);
        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId.ToString();
        context.Response.Headers[HeaderName] = correlationId.ToString();

        Activity.Current?.SetTag("correlationId", correlationId.ToString());
        Activity.Current?.AddBaggage("correlationId", correlationId.ToString());

        using (LogContext.PushProperty("correlationId", correlationId))
        {
            await _next(context);
        }
    }

    internal static Guid ResolveCorrelationId(string? headerValue) =>
        Guid.TryParse(headerValue, out var parsed) ? parsed : Guid.CreateVersion7();

    public static Guid GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is Guid correlationId
            ? correlationId
            : Guid.Empty;
}
