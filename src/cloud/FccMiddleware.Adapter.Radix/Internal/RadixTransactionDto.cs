using System.Xml.Linq;

namespace FccMiddleware.Adapter.Radix.Internal;

/// <summary>
/// Deserialisation target for a Radix XML TRN element.
/// Populated by parsing the FDC transaction response XML.
///
/// Radix sends TRN data as XML **attributes** (not child elements):
///   &lt;TRN AMO="30000.0" VOL="15.54" PRICE="1930" FDC_NUM="100253410" ... /&gt;
///
/// Field names follow the Radix protocol spec (AMO, VOL, PRICE, NOZ, FDC_PROD, etc.)
/// </summary>
internal sealed class RadixTransactionDto
{
    public required string FdcNum { get; init; }
    public required string FdcSaveNum { get; init; }
    public required int Fp { get; init; }
    public required int PumpAddr { get; init; }
    public required int Nozzle { get; init; }
    /// <summary>Volume in litres as a decimal string (from VOL attribute).</summary>
    public required string Volume { get; init; }
    /// <summary>Amount in currency units as a decimal string (from AMO attribute).</summary>
    public required string Amount { get; init; }
    /// <summary>Unit price as a decimal string (from PRICE attribute).</summary>
    public required string UnitPrice { get; init; }
    public required string ProductCode { get; init; }
    /// <summary>Start time in FDC local format (composed from FDC_DATE + FDC_TIME).</summary>
    public required string StartTime { get; init; }
    /// <summary>End time in FDC local format (composed from RDG_DATE + RDG_TIME, falls back to FDC_DATE + FDC_TIME).</summary>
    public required string EndTime { get; init; }
    public required int Token { get; init; }
    public string? EfdId { get; init; }

    /// <summary>
    /// Parse a TRN XML element into a DTO.
    ///
    /// Radix sends all TRN fields as XML **attributes**, not child elements.
    /// Field names match the Radix protocol: AMO (not AMOUNT), VOL (not VOLUME),
    /// PRICE (not UNIT_PRICE), NOZ (not NOZZLE), FDC_PROD (not PRODUCT_CODE).
    /// </summary>
    internal static RadixTransactionDto FromXml(XElement trn)
    {
        // Helper: read attribute value or empty string
        static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";

        var fdcDate = Attr(trn, "FDC_DATE");
        var fdcTime = Attr(trn, "FDC_TIME");
        var rdgDate = Attr(trn, "RDG_DATE");
        var rdgTime = Attr(trn, "RDG_TIME");

        // Compose timestamps: "YYYY-MM-DD"+"HH:MM:SS" -> "YYYY-MM-DDThh:mm:ss"
        var startTime = !string.IsNullOrWhiteSpace(fdcDate) && !string.IsNullOrWhiteSpace(fdcTime)
            ? $"{fdcDate}T{fdcTime}"
            : "";
        var endTime = !string.IsNullOrWhiteSpace(rdgDate) && !string.IsNullOrWhiteSpace(rdgTime)
            ? $"{rdgDate}T{rdgTime}"
            : startTime; // Fall back to FDC time if RDG time not available

        return new RadixTransactionDto
        {
            FdcNum = Attr(trn, "FDC_NUM"),
            FdcSaveNum = Attr(trn, "FDC_SAVE_NUM"),
            Fp = int.TryParse(Attr(trn, "FP"), out var fp) ? fp : 0,
            PumpAddr = int.TryParse(Attr(trn, "PUMP_ADDR"), out var pa) ? pa : 0,
            Nozzle = int.TryParse(Attr(trn, "NOZ"), out var nz) ? nz : 0,
            Volume = Attr(trn, "VOL") is { Length: > 0 } vol ? vol : "0",
            Amount = Attr(trn, "AMO") is { Length: > 0 } amo ? amo : "0",
            UnitPrice = Attr(trn, "PRICE") is { Length: > 0 } price ? price : "0",
            ProductCode = Attr(trn, "FDC_PROD"),
            StartTime = startTime,
            EndTime = endTime,
            Token = int.TryParse(Attr(trn, "TOKEN"), out var tok) ? tok : 0,
            EfdId = Attr(trn, "EFD_ID") is { Length: > 0 } efdId ? efdId : null,
        };
    }
}
