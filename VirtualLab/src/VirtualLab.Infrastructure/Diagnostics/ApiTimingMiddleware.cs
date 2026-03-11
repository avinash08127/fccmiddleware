using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace VirtualLab.Infrastructure.Diagnostics;

public sealed class ApiTimingMiddleware
{
    private readonly RequestDelegate _next;

    public ApiTimingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ApiTimingStore timingStore)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await _next(context);
        stopwatch.Stop();

        string routeKey = ResolveRouteKey(context.Request.Path);
        timingStore.Record(routeKey, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static string ResolveRouteKey(PathString path)
    {
        if (path.StartsWithSegments("/api"))
        {
            return "api";
        }

        if (path.StartsWithSegments("/fcc"))
        {
            return "fcc";
        }

        if (path.StartsWithSegments("/callbacks"))
        {
            return "callbacks";
        }

        if (path.StartsWithSegments("/hubs"))
        {
            return "signalr";
        }

        return "other";
    }
}
