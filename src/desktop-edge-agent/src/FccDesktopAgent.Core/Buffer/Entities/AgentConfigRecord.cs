namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Single-row table (Id always = 1) storing the agent configuration JSON received from cloud.
/// </summary>
public sealed class AgentConfigRecord
{
    /// <summary>Always 1. Single-row sentinel.</summary>
    public int Id { get; set; } = 1;

    /// <summary>Full configuration payload as JSON received from cloud.</summary>
    public string ConfigJson { get; set; } = "{}";

    public string? ConfigVersion { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
