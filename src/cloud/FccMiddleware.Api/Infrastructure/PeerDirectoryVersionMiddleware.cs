using FccMiddleware.Application.Registration;
using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Api.Infrastructure;

/// <summary>
/// Emits the <c>X-Peer-Directory-Version</c> header on every response to an authenticated agent.
/// Agents compare this value to their locally cached version to detect peer directory staleness.
/// Runs after <see cref="DeviceActiveCheckMiddleware"/> and reuses the resolved device stored in
/// <see cref="HttpContext.Items"/> to avoid a second agent lookup.
/// </summary>
public sealed class PeerDirectoryVersionMiddleware
{
    private readonly RequestDelegate _next;

    public PeerDirectoryVersionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IRegistrationDbContext db)
    {
        // Reuse the agent resolved by DeviceActiveCheckMiddleware (stored in HttpContext.Items).
        if (context.Items.TryGetValue(DeviceActiveCheckMiddleware.ResolvedDeviceKey, out var deviceObj)
            && deviceObj is AgentRegistration agent)
        {
            var site = await db.FindSiteBySiteCodeAsync(agent.SiteCode, context.RequestAborted);
            if (site is not null)
            {
                var version = site.PeerDirectoryVersion;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Peer-Directory-Version"] = version.ToString();
                    return Task.CompletedTask;
                });
            }
        }

        await _next(context);
    }
}

public static class PeerDirectoryVersionMiddlewareExtensions
{
    public static IApplicationBuilder UsePeerDirectoryVersionHeader(this IApplicationBuilder app)
        => app.UseMiddleware<PeerDirectoryVersionMiddleware>();
}
