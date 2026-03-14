namespace FccDesktopAgent.Core.Sync;

public abstract record ConfigPollExecutionResult
{
    public sealed record Applied(int ConfigVersion) : ConfigPollExecutionResult;
    public sealed record Unchanged(int? CurrentConfigVersion) : ConfigPollExecutionResult;
    public sealed record Skipped(int ConfigVersion) : ConfigPollExecutionResult;
    public sealed record Rejected(int ConfigVersion, string Reason) : ConfigPollExecutionResult;
    public sealed record Decommissioned : ConfigPollExecutionResult;
    public sealed record TransportFailure(string Message) : ConfigPollExecutionResult;
    public sealed record Unavailable(string Reason) : ConfigPollExecutionResult;
}
