using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Domain.Interfaces;

/// <summary>
/// Cloud-side adapter interface for a single FCC vendor.
/// Each vendor ships as a separate adapter project implementing this interface.
/// Selection is config-driven by FccVendor via IFccAdapterFactory.
///
/// Matches the cloud .NET adapter contract defined in §5.1 of
/// docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md.
/// </summary>
public interface IFccAdapter
{
    /// <summary>
    /// Parses one vendor payload object and produces a valid canonical transaction.
    /// Must preserve the source payload reference and apply configured mappings.
    /// The caller must invoke ValidatePayload first; behaviour is undefined for invalid payloads.
    /// Multi-item payloads (arrays) must be split by the caller before calling this method.
    /// </summary>
    CanonicalTransaction NormalizeTransaction(RawPayloadEnvelope rawPayload);

    /// <summary>
    /// Performs structural and vendor-rule validation of a raw payload.
    /// No persistence or dedup checks are performed here.
    /// Returns ValidationResult.IsValid=false when normalization must not be attempted.
    /// </summary>
    ValidationResult ValidatePayload(RawPayloadEnvelope rawPayload);

    /// <summary>
    /// Pull-mode fetch against a cloud-reachable FCC endpoint.
    /// Returns zero or more normalized canonical transactions together with cursor
    /// and batch-completeness metadata.
    /// This method is side-effect free on vendor state except for vendor-defined
    /// cursor acknowledgment implicit in the request parameters.
    /// </summary>
    Task<TransactionBatch> FetchTransactionsAsync(
        FetchCursor cursor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns static capability metadata used for registration, diagnostics,
    /// and config validation at startup.
    /// </summary>
    AdapterInfo GetAdapterMetadata();
}
