using System.Text.Json;

namespace FccMiddleware.Contracts.Ingestion;

/// <summary>
/// Request body for POST /api/v1/transactions/ingest.
/// The FCC (or relay agent) sends one raw vendor payload envelope per request.
/// </summary>
public sealed record IngestRequest
{
    /// <summary>FCC vendor identifier (e.g., "DOMS", "RADIX").</summary>
    public required string FccVendor { get; init; }

    /// <summary>Globally-unique site code where the dispense occurred.</summary>
    public required string SiteCode { get; init; }

    /// <summary>UTC timestamp when the payload was captured at the FCC boundary.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Raw vendor payload as JSON. This may be a single transaction object or an object containing
    /// a transactions array for bulk push payloads.
    /// </summary>
    public required JsonElement RawPayload { get; init; }
}
