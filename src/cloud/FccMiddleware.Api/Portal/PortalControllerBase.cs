using System.Diagnostics;
using FccMiddleware.Contracts.Common;
using Microsoft.AspNetCore.Mvc;

namespace FccMiddleware.Api.Portal;

public abstract class PortalControllerBase : ControllerBase
{
    protected ErrorResponse BuildError(string errorCode, string message, bool retryable = false) =>
        new()
        {
            ErrorCode = errorCode,
            Message = message,
            Details = null,
            TraceId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Retryable = retryable
        };

    protected IActionResult ForbidOrUnauthorized(PortalAccess access) =>
        access.IsValid ? Forbid() : Unauthorized();
}
