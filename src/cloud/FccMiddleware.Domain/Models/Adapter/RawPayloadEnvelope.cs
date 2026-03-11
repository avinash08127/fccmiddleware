using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Wraps a raw vendor payload with the context needed for adapter routing and validation.
/// The payload field carries the exact, unchanged bytes/string from the vendor.
/// </summary>
public sealed record RawPayloadEnvelope
{
    /// <summary>FCC vendor that produced this payload. Must match the resolved site config.</summary>
    public required FccVendor Vendor { get; init; }

    /// <summary>Adapter context key for mappings and validation.</summary>
    public required string SiteCode { get; init; }

    /// <summary>UTC timestamp when this payload reached the cloud or edge boundary.</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>
    /// MIME content type of the payload. MVP values: application/json, text/xml,
    /// application/octet-stream. DOMS uses application/json.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>Exact raw payload, unchanged. Serialized as a string for JSON and XML vendors.</summary>
    public required string Payload { get; init; }
}
