using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class FccSimulatorProfile
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ProfileKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string VendorFamily { get; set; } = string.Empty;
    public SimulatedAuthMode AuthMode { get; set; }
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public PreAuthFlowMode PreAuthMode { get; set; }
    public string EndpointBasePath { get; set; } = "/fcc";
    public string EndpointSurfaceJson { get; set; } = "[]";
    public string AuthConfigurationJson { get; set; } = "{}";
    public string CapabilitiesJson { get; set; } = "{}";
    public string RequestTemplatesJson { get; set; } = "{}";
    public string ResponseTemplatesJson { get; set; } = "{}";
    public string ValidationRulesJson { get; set; } = "[]";
    public string FieldMappingsJson { get; set; } = "{}";
    public string FailureSimulationJson { get; set; } = "{}";
    public string ExtensionConfigurationJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public LabEnvironment LabEnvironment { get; set; } = null!;
    public ICollection<Site> Sites { get; set; } = new List<Site>();
}
