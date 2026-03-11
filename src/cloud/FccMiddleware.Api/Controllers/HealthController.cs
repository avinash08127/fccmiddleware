using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class HealthController : ControllerBase
{
    // Note: primary health endpoints are handled by MapHealthChecks middleware at /health and /health/ready.
    // This stub is kept for API explorer / Swagger documentation purposes only.
    [HttpGet("health-info")]
    public IActionResult HealthInfo() => Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow });
}
