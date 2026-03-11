using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Static capability metadata returned by IFccAdapter.GetAdapterMetadata.
/// Used for adapter registration, diagnostics, and config validation.
/// </summary>
public sealed record AdapterInfo
{
    /// <summary>FCC vendor. Registration key for the adapter factory.</summary>
    public required FccVendor Vendor { get; init; }

    /// <summary>Semantic version of the adapter package (e.g., "1.0.0").</summary>
    public required string AdapterVersion { get; init; }

    /// <summary>
    /// Ingestion methods this adapter supports.
    /// Corresponds to the spec's supportedTransactionModes (PUSH/PULL/HYBRID).
    /// </summary>
    public required IReadOnlyList<IngestionMethod> SupportedIngestionMethods { get; init; }

    /// <summary>True when this adapter supports pre-authorization commands (edge-only for DOMS).</summary>
    public required bool SupportsPreAuth { get; init; }

    /// <summary>True when this adapter can return real-time pump status (edge-only capability).</summary>
    public required bool SupportsPumpStatus { get; init; }

    /// <summary>Transport protocol: REST, TCP, or SOAP. DOMS MVP = REST.</summary>
    public required string Protocol { get; init; }
}
