namespace FccMiddleware.Contracts.PreAuth;

/// <summary>
/// Response body for POST /api/v1/preauth.
/// Matches PreAuthForwardResponse in cloud-api.yaml.
/// </summary>
public sealed record PreAuthForwardResponse
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required string SiteCode { get; init; }
    public required string OdooOrderId { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
