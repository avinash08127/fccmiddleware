using FccMiddleware.Application.Registration;

namespace FccMiddleware.Api.Infrastructure;

/// <summary>
/// Rejects requests from decommissioned devices by checking <see cref="Domain.Entities.AgentRegistration.IsActive"/>
/// in the database. Runs after authentication, before authorization.
/// Only applies to requests authenticated with a device JWT (identified by the presence of a "site" claim).
/// </summary>
public sealed class DeviceActiveCheckMiddleware
{
    private readonly RequestDelegate _next;

    public DeviceActiveCheckMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IRegistrationDbContext db)
    {
        // Only check device JWTs — portal tokens don't carry the "site" claim.
        if (context.User.Identity is { IsAuthenticated: true }
            && context.User.HasClaim(c => c.Type == "site"))
        {
            var sub = context.User.FindFirst("sub")?.Value;
            if (Guid.TryParse(sub, out var deviceId))
            {
                var device = await db.FindAgentByIdAsync(deviceId, context.RequestAborted);

                // H-12: Also reject when device is not found in the database.
                // A valid JWT with a deleted/non-existent device ID must not pass through.
                if (device is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        errorCode = "DEVICE_NOT_FOUND",
                        message = "Device not found. It may have been deleted.",
                    });
                    return;
                }

                if (!device.IsActive)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        errorCode = "DEVICE_DECOMMISSIONED",
                        message = "This device has been decommissioned.",
                    });
                    return;
                }
            }
        }

        await _next(context);
    }
}
