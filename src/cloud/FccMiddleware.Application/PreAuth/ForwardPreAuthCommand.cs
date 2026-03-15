using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Common;
using FccMiddleware.Domain.Enums;
using MediatR;

namespace FccMiddleware.Application.PreAuth;

/// <summary>
/// MediatR command to forward a pre-auth record from the Edge Agent to cloud.
/// Handles dedup on (odooOrderId, siteCode), status transitions, and event publishing.
/// </summary>
public sealed record ForwardPreAuthCommand : IRequest<Result<ForwardPreAuthResult>>
{
    public required Guid LegalEntityId { get; init; }
    public required string SiteCode { get; init; }
    public required string OdooOrderId { get; init; }
    public required int PumpNumber { get; init; }
    public required int NozzleNumber { get; init; }
    public required string ProductCode { get; init; }
    public required long RequestedAmountMinorUnits { get; init; }
    public required long UnitPriceMinorPerLitre { get; init; }
    public required string CurrencyCode { get; init; }
    public required PreAuthStatus Status { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? FccCorrelationId { get; init; }
    public string? FccAuthorizationCode { get; init; }
    public string? VehicleNumber { get; init; }
    public string? CustomerName { get; init; }
    [Sensitive]
    public string? CustomerTaxId { get; init; }
    public string? CustomerBusinessName { get; init; }
    public string? AttendantId { get; init; }
    public long? LeaderEpoch { get; init; }
    public required Guid CorrelationId { get; init; }
}
