namespace VirtualLab.Infrastructure.AdvatecSimulator;

public sealed class AdvatecSimulatorOptions
{
    public const string SectionName = "AdvatecSimulator";

    /// <summary>Port for the simulated Advatec EFD REST API (mirrors real Advatec default of 5560).</summary>
    public int Port { get; set; } = 5560;

    /// <summary>Delay in milliseconds before generating a receipt webhook after Customer submission.</summary>
    public int ReceiptDelayMs { get; set; } = 2000;

    /// <summary>Number of pumps to simulate.</summary>
    public int PumpCount { get; set; } = 3;

    /// <summary>Default unit price per litre in TZS (used for receipt generation).</summary>
    public decimal DefaultUnitPriceTzs { get; set; } = 3285.00m;

    /// <summary>TRA VAT rate (18% standard rate).</summary>
    public decimal VatRate { get; set; } = 0.18m;
}
