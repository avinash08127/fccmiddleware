using System.Xml.Linq;

namespace FccMiddleware.Adapter.Radix.Internal;

/// <summary>
/// Deserialisation target for a Radix XML TRN element.
/// Populated by parsing the FDC transaction response XML.
/// </summary>
internal sealed class RadixTransactionDto
{
    public required string FdcNum { get; init; }
    public required string FdcSaveNum { get; init; }
    public required int Fp { get; init; }
    public required int PumpAddr { get; init; }
    public required int Nozzle { get; init; }
    /// <summary>Volume in litres as a decimal string.</summary>
    public required string Volume { get; init; }
    /// <summary>Amount in currency units as a decimal string.</summary>
    public required string Amount { get; init; }
    /// <summary>Unit price as a decimal string.</summary>
    public required string UnitPrice { get; init; }
    public required string ProductCode { get; init; }
    /// <summary>Start time in FDC local format.</summary>
    public required string StartTime { get; init; }
    /// <summary>End time in FDC local format.</summary>
    public required string EndTime { get; init; }
    public required int Token { get; init; }
    public string? EfdId { get; init; }

    /// <summary>Parse a TRN XML element into a DTO.</summary>
    internal static RadixTransactionDto FromXml(XElement trn)
    {
        return new RadixTransactionDto
        {
            FdcNum = trn.Element("FDC_NUM")?.Value ?? "",
            FdcSaveNum = trn.Element("FDC_SAVE_NUM")?.Value ?? "",
            Fp = int.TryParse(trn.Element("FP")?.Value, out var fp) ? fp : 0,
            PumpAddr = int.TryParse(trn.Element("PUMP_ADDR")?.Value, out var pa) ? pa : 0,
            Nozzle = int.TryParse(trn.Element("NOZZLE")?.Value, out var nz) ? nz : 0,
            Volume = trn.Element("VOLUME")?.Value ?? "0",
            Amount = trn.Element("AMOUNT")?.Value ?? "0",
            UnitPrice = trn.Element("UNIT_PRICE")?.Value ?? "0",
            ProductCode = trn.Element("PRODUCT_CODE")?.Value ?? "",
            StartTime = trn.Element("START_TIME")?.Value ?? "",
            EndTime = trn.Element("END_TIME")?.Value ?? "",
            Token = int.TryParse(trn.Element("TOKEN")?.Value, out var tok) ? tok : 0,
            EfdId = trn.Element("EFD_ID")?.Value,
        };
    }
}
