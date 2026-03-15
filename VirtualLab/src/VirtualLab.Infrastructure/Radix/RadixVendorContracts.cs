namespace VirtualLab.Infrastructure.Radix;

public sealed class RadixVendorOptions
{
    public const string SectionName = "Simulators:Radix";
    public bool Enabled { get; set; } = true;
    public string SharedSecret { get; set; } = "virtuallab-test-secret";
    public int DefaultUsnCode { get; set; } = 12345;
    public bool ValidateSignatures { get; set; } = false;
}

// Parsed from AUTH_DATA XML
public sealed record RadixSimAuthRequest(
    int Pump,
    int Fp,
    bool Authorize,
    int Product,
    string PresetVolume,
    string PresetAmount,
    string? CustomerName,
    int? CustomerIdType,
    string? CustomerId,
    string? MobileNumber,
    string Token);

// Parsed from HOST_REQ XML
public sealed record RadixSimHostRequest(
    int CmdCode,
    string CmdName,
    string Token,
    string? Mode,
    string? Signature);

// Management endpoint: inject transaction via JSON
public sealed record RadixSimInjectTransactionRequest(
    int? PumpNumber,
    int? NozzleNumber,
    int? ProductId,
    string? ProductName,
    string? Volume,
    string? Amount,
    string? Price);

// State view for management
public sealed record RadixSimStateView(
    string SiteCode,
    IReadOnlyDictionary<int, RadixSimulator.RadixPreAuthSession> ActivePreAuths,
    IReadOnlyList<RadixSimulator.RadixSimulatedTransaction> BufferedTransactions,
    IReadOnlyDictionary<int, RadixSimulator.RadixProductEntry> Products,
    string Mode,
    int PendingErrors);
