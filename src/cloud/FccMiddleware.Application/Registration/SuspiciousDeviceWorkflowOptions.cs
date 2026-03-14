namespace FccMiddleware.Application.Registration;

public sealed class SuspiciousDeviceWorkflowOptions
{
    public const string SectionName = "SuspiciousDeviceWorkflow";

    public bool Enabled { get; set; }
    public bool HoldUnexpectedSerialReplacement { get; set; } = true;
    public bool HoldSiteOccupancyWithoutApproval { get; set; } = true;
    public bool QuarantineSecurityRuleMismatch { get; set; } = true;
    public string? MinimumAgentVersion { get; set; }
    public List<string> AllowedDeviceModels { get; set; } = [];
    public List<string> AllowedSerialPrefixes { get; set; } = [];
}
