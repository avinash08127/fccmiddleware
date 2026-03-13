using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/portal")]
[Authorize(Policy = "PortalUser")]
public sealed class PortalLogsController : ControllerBase
{
    private readonly ILogger<PortalLogsController> _logger;

    public PortalLogsController(ILogger<PortalLogsController> logger)
    {
        _logger = logger;
    }

    [HttpPost("client-logs")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult IngestClientLog([FromBody] ClientLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Msg))
        {
            return BadRequest();
        }

        _logger.Log(
            MapLevel(entry.Lvl),
            "Portal client log [{Source}]: {Message} | url={Url}",
            entry.Source ?? "unknown",
            entry.Msg,
            entry.Url ?? "-");

        return NoContent();
    }

    private static LogLevel MapLevel(string? level) => level?.ToUpperInvariant() switch
    {
        "ERROR" => LogLevel.Error,
        "WARN" => LogLevel.Warning,
        "INFO" => LogLevel.Information,
        "DEBUG" => LogLevel.Debug,
        _ => LogLevel.Information
    };
}

public sealed record ClientLogEntry
{
    public string? Ts { get; init; }
    public string? Lvl { get; init; }
    public string? Source { get; init; }
    public string? Msg { get; init; }
    public object? Extra { get; init; }
    public string? Url { get; init; }
}
