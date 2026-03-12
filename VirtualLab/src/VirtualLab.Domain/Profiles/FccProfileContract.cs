using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Profiles;

public sealed class FccProfileContract
{
    public List<FccEndpointDefinition> EndpointSurface { get; set; } = [];
    public FccAuthConfiguration Auth { get; set; } = new();
    public FccDeliveryCapabilities Capabilities { get; set; } = new();
    public PreAuthFlowMode PreAuthMode { get; set; }
    public List<FccTemplateDefinition> RequestTemplates { get; set; } = [];
    public List<FccTemplateDefinition> ResponseTemplates { get; set; } = [];
    public List<FccValidationRuleDefinition> ValidationRules { get; set; } = [];
    public List<FccFieldMappingDefinition> FieldMappings { get; set; } = [];
    public FccFailureSimulationDefinition FailureSimulation { get; set; } = new();
    public FccExtensionPointDefinition Extensions { get; set; } = new();
}

public sealed class FccEndpointDefinition
{
    public string Operation { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string PathTemplate { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = string.Empty;
}

public sealed class FccAuthConfiguration
{
    public SimulatedAuthMode Mode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
}

public sealed class FccDeliveryCapabilities
{
    public bool SupportsPush { get; set; }
    public bool SupportsPull { get; set; }
    public bool SupportsHybrid { get; set; }
    public bool SupportsPreAuthCancellation { get; set; } = true;
}

public sealed class FccTemplateDefinition
{
    public string Operation { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/json";
    public Dictionary<string, string> Headers { get; set; } = [];
    public string BodyTemplate { get; set; } = "{}";
}

public sealed class FccValidationRuleDefinition
{
    public string RuleKey { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public string ExpectedType { get; set; } = string.Empty;
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public List<string> AllowedValues { get; set; } = [];
}

public sealed class FccFieldMappingDefinition
{
    public string Scope { get; set; } = string.Empty;
    public string SourceField { get; set; } = string.Empty;
    public string TargetField { get; set; } = string.Empty;
    public string Direction { get; set; } = "Inbound";
    public string Transform { get; set; } = string.Empty;
}

public sealed class FccFailureSimulationDefinition
{
    public int SimulatedDelayMs { get; set; }
    public bool Enabled { get; set; }
    public int FailureRatePercent { get; set; }
    public int HttpStatusCode { get; set; } = 500;
    public string ErrorCode { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = string.Empty;
}

public sealed class FccExtensionPointDefinition
{
    public string ResolverKey { get; set; } = string.Empty;
    public Dictionary<string, string> Configuration { get; set; } = [];
}
