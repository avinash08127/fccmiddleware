using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow });
}
