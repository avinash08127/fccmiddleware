using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class CallbackTarget
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public Guid? SiteId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Uri CallbackUrl { get; set; } = new("https://localhost/");
    public SimulatedAuthMode AuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public LabEnvironment LabEnvironment { get; set; } = null!;
    public Site? Site { get; set; }
    public ICollection<CallbackAttempt> CallbackAttempts { get; set; } = new List<CallbackAttempt>();
}
