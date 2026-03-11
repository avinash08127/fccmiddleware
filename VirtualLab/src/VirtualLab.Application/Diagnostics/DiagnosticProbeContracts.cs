namespace VirtualLab.Application.Diagnostics;

public sealed record DiagnosticProbeResult(
    string ProfileName,
    string ReplaySignature,
    GuardrailThresholds Thresholds,
    DiagnosticMeasurements Measurements);

public sealed record GuardrailThresholds(
    int StartupReadyMinutes,
    int DashboardLoadP95Ms,
    int SignalRUpdateP95Ms,
    int FccEmulatorP95Ms,
    int TransactionPullP95Ms);

public sealed record DiagnosticMeasurements(
    double DashboardQueryP95Ms,
    double SiteLoadP95Ms,
    double SignalRBroadcastP95Ms,
    double FccHealthP95Ms,
    double TransactionPullP95Ms,
    int SampleCount);
