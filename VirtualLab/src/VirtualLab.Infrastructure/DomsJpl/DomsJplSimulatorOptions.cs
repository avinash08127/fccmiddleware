namespace VirtualLab.Infrastructure.DomsJpl;

public sealed class DomsJplSimulatorOptions
{
    public const string SectionName = "DomsJplSimulator";

    public int ListenPort { get; set; } = 4001;
    public string AcceptedAccessCode { get; set; } = "test-access-code";
    public int PumpCount { get; set; } = 4;
    public int HeartbeatTimeoutSeconds { get; set; } = 90;
    public bool EnableUnsolicitedPush { get; set; } = false;
}
