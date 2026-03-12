using VirtualLab.Application.PreAuth;

namespace VirtualLab.Api;

public sealed class LabPreAuthActionRequest
{
    public string Action { get; init; } = "create";
    public string? PreAuthId { get; init; }
    public string? CorrelationId { get; init; }
    public int? PumpNumber { get; init; }
    public int? NozzleNumber { get; init; }
    public decimal? Amount { get; init; }
    public int? ExpiresInSeconds { get; init; }
    public bool SimulateFailure { get; init; }
    public int? FailureStatusCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? FailureCode { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerTaxId { get; init; }
    public string? CustomerTaxOffice { get; init; }
}

public sealed record LabPreAuthActionResult(
    int StatusCode,
    string Action,
    string Message,
    string SiteCode,
    string CorrelationId,
    string ResponseBody,
    PreAuthSessionSummary? Session);
