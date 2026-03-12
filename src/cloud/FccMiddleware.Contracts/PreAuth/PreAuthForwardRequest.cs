using System.ComponentModel.DataAnnotations;
using FccMiddleware.Domain.Common;

namespace FccMiddleware.Contracts.PreAuth;

/// <summary>
/// Request body for POST /api/v1/preauth.
/// Edge Agent forwards pre-auth record to cloud for lifecycle tracking and reconciliation.
/// Matches PreAuthForwardRequest in cloud-api.yaml.
/// </summary>
public sealed record PreAuthForwardRequest
{
    public required string SiteCode { get; init; }
    public required string OdooOrderId { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public required string ProductCode { get; init; }
    public required long RequestedAmount { get; init; }
    public required long UnitPrice { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? FccCorrelationId { get; init; }
    public string? FccAuthorizationCode { get; init; }
    public string? VehicleNumber { get; init; }
    [Sensitive]
    public string? CustomerName { get; init; }
    [Sensitive]
    [StringLength(100, MinimumLength = 1)]
    public string? CustomerTaxId { get; init; }
    [StringLength(200, MinimumLength = 1)]
    public string? CustomerBusinessName { get; init; }
    public string? AttendantId { get; init; }
}
