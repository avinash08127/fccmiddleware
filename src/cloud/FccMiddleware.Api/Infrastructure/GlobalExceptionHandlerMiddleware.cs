using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FccMiddleware.Contracts.Common;

namespace FccMiddleware.Api.Infrastructure;

/// <summary>
/// Catches unhandled exceptions, logs them as structured errors, and returns
/// a consistent <see cref="ErrorResponse"/> body. Registered after
/// <see cref="CorrelationIdMiddleware"/> so the correlation ID is available.
/// </summary>
public sealed class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — not an error
            context.Response.StatusCode = 499; // nginx-style "Client Closed Request"
        }
        catch (Exception ex)
        {
            var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

            _logger.LogError(ex,
                "Unhandled exception. CorrelationId={CorrelationId} Path={Path} Method={Method}",
                correlationId,
                context.Request.Path.Value,
                context.Request.Method);

            if (context.Response.HasStarted)
            {
                _logger.LogWarning(
                    "Response already started — cannot write error body. CorrelationId={CorrelationId}",
                    correlationId);
                throw;
            }

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var errorResponse = new ErrorResponse
            {
                ErrorCode = "INTERNAL.UNHANDLED_EXCEPTION",
                Message = "An unexpected error occurred. Please retry or contact support if the problem persists.",
                TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                Retryable = true,
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(errorResponse, JsonOptions));
        }
    }
}
