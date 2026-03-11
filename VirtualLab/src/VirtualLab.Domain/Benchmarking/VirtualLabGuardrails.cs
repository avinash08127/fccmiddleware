namespace VirtualLab.Domain.Benchmarking;

public static class VirtualLabGuardrails
{
    public const int StartupReadyMinutes = 5;
    public const int DashboardLoadP95Ms = 2000;
    public const int SignalRUpdateP95Ms = 500;
    public const int FccEmulatorP95Ms = 300;
    public const int TransactionPullP95Ms = 250;
}
