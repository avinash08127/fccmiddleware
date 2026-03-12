namespace VirtualLab.Infrastructure.RadixSimulator;

public sealed class RadixSimulatorOptions
{
    public const string SectionName = "RadixSimulator";

    /// <summary>Transaction management port (P+1). Handles CMD_CODE requests.</summary>
    public int TransactionPort { get; set; } = 5001;

    /// <summary>External authorization port (P). Handles AUTH_DATA pre-auth requests.</summary>
    public int AuthPort { get; set; } = 5000;

    /// <summary>Shared secret for SHA-1 signature computation and validation.</summary>
    public string SharedSecret { get; set; } = "test-secret";

    /// <summary>USN_CODE identifying this FDC unit in protocol messages.</summary>
    public int UsnCode { get; set; } = 123456;

    /// <summary>Number of pumps to simulate.</summary>
    public int PumpCount { get; set; } = 4;
}
