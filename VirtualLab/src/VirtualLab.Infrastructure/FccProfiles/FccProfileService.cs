using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Domain.Profiles;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.FccProfiles;

public sealed class FccProfileService(VirtualLabDbContext dbContext) : IFccProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<FccProfileSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.FccSimulatorProfiles
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new FccProfileSummary(
                x.Id,
                x.ProfileKey,
                x.Name,
                x.VendorFamily,
                x.AuthMode,
                x.DeliveryMode,
                x.PreAuthMode,
                x.IsActive,
                x.IsDefault))
            .ToListAsync(cancellationToken);
    }

    public async Task<FccProfileRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        FccSimulatorProfile? entity = await dbContext.FccSimulatorProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<ResolvedFccProfile?> ResolveBySiteCodeAsync(string siteCode, CancellationToken cancellationToken = default)
    {
        var resolved = await dbContext.Sites
            .AsNoTracking()
            .Where(x => x.SiteCode == siteCode && x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.SiteCode,
                Profile = x.ActiveFccSimulatorProfile,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (resolved is null || !resolved.Profile.IsActive)
        {
            return null;
        }

        FccProfileRecord record = ToRecord(resolved.Profile);
        return new ResolvedFccProfile
        {
            SiteId = resolved.Id,
            SiteCode = resolved.SiteCode,
            ProfileId = resolved.Profile.Id,
            ProfileKey = resolved.Profile.ProfileKey,
            ProfileName = resolved.Profile.Name,
            VendorFamily = resolved.Profile.VendorFamily,
            DeliveryMode = resolved.Profile.DeliveryMode,
            PreAuthMode = resolved.Profile.PreAuthMode,
            Contract = record.Contract,
        };
    }

    public async Task<FccProfileValidationResult> ValidateAsync(FccProfileRecord record, CancellationToken cancellationToken = default)
    {
        List<FccProfileValidationMessage> messages = [];

        if (record.LabEnvironmentId == Guid.Empty)
        {
            messages.Add(new("labEnvironmentId", "Lab environment is required.", "Error"));
        }

        if (string.IsNullOrWhiteSpace(record.ProfileKey))
        {
            messages.Add(new("profileKey", "Profile key is required.", "Error"));
        }

        if (string.IsNullOrWhiteSpace(record.Name))
        {
            messages.Add(new("name", "Name is required.", "Error"));
        }

        if (record.Contract.EndpointSurface.Count == 0)
        {
            messages.Add(new("contract.endpointSurface", "At least one endpoint definition is required.", "Error"));
        }

        if (!record.Contract.EndpointSurface.Any(x => string.Equals(x.Operation, "health", StringComparison.OrdinalIgnoreCase) && x.Enabled))
        {
            messages.Add(new("contract.endpointSurface", "A health endpoint is required for profile diagnostics.", "Error"));
        }

        ValidateAuth(record.Contract.Auth, messages);
        ValidateCapabilities(record, messages);
        ValidatePreAuth(record, messages);
        ValidateTemplates(record, messages);
        ValidateMappings(record, messages);
        ValidateFailureSimulation(record.Contract.FailureSimulation, messages);

        bool profileKeyConflict = await dbContext.FccSimulatorProfiles
            .AsNoTracking()
            .AnyAsync(
                x => x.LabEnvironmentId == record.LabEnvironmentId &&
                     x.ProfileKey == record.ProfileKey &&
                     (!record.Id.HasValue || x.Id != record.Id.Value),
                cancellationToken);

        if (profileKeyConflict)
        {
            messages.Add(new("profileKey", $"Profile key '{record.ProfileKey}' already exists in this environment.", "Error"));
        }

        return new FccProfileValidationResult(messages.All(x => x.Severity != "Error"), messages);
    }

    public async Task<FccProfilePreviewResult> PreviewAsync(FccProfilePreviewRequest request, CancellationToken cancellationToken = default)
    {
        FccProfileRecord? record = request.Draft;
        if (record is null && request.ProfileId.HasValue)
        {
            record = await GetAsync(request.ProfileId.Value, cancellationToken);
        }

        if (record is null)
        {
            throw new InvalidOperationException("Profile preview requires a draft or existing profile id.");
        }

        FccProfileValidationResult validation = await ValidateAsync(record, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Profile preview blocked by validation errors: {string.Join("; ", validation.Messages.Where(x => x.Severity == "Error").Select(x => x.Message))}");
        }

        FccTemplateDefinition requestTemplate = record.Contract.RequestTemplates
            .SingleOrDefault(x => string.Equals(x.Operation, request.Operation, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Missing request template for operation '{request.Operation}'.");

        FccTemplateDefinition responseTemplate = record.Contract.ResponseTemplates
            .SingleOrDefault(x => string.Equals(x.Operation, request.Operation, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Missing response template for operation '{request.Operation}'.");

        Dictionary<string, string> sampleValues = BuildSampleValues(record, request.Operation, request.SampleValues);

        return new FccProfilePreviewResult(
            request.Operation,
            FccProfileTemplateRenderer.Render(requestTemplate.BodyTemplate, sampleValues),
            FccProfileTemplateRenderer.Render(requestTemplate.Headers, sampleValues),
            FccProfileTemplateRenderer.Render(responseTemplate.BodyTemplate, sampleValues),
            FccProfileTemplateRenderer.Render(responseTemplate.Headers, sampleValues),
            sampleValues);
    }

    public async Task<FccProfileRecord> CreateAsync(FccProfileRecord record, CancellationToken cancellationToken = default)
    {
        FccProfileValidationResult validation = await ValidateAsync(record, cancellationToken);
        if (!validation.IsValid)
        {
            throw new FccProfileValidationException(validation);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        FccSimulatorProfile entity = ToEntity(record, new FccSimulatorProfile
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        dbContext.FccSimulatorProfiles.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<FccProfileRecord?> UpdateAsync(Guid id, FccProfileRecord record, CancellationToken cancellationToken = default)
    {
        FccSimulatorProfile? entity = await dbContext.FccSimulatorProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        record.Id = id;
        FccProfileValidationResult validation = await ValidateAsync(record, cancellationToken);
        if (!validation.IsValid)
        {
            throw new FccProfileValidationException(validation);
        }

        ToEntity(record, entity);
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    internal static FccProfileRecord ToRecord(FccSimulatorProfile entity)
    {
        return new FccProfileRecord
        {
            Id = entity.Id,
            LabEnvironmentId = entity.LabEnvironmentId,
            ProfileKey = entity.ProfileKey,
            Name = entity.Name,
            VendorFamily = entity.VendorFamily,
            DeliveryMode = entity.DeliveryMode,
            IsActive = entity.IsActive,
            IsDefault = entity.IsDefault,
            Contract = new FccProfileContract
            {
                EndpointSurface = Deserialize<List<FccEndpointDefinition>>(entity.EndpointSurfaceJson, []),
                Auth = Deserialize<FccAuthConfiguration>(entity.AuthConfigurationJson, new FccAuthConfiguration { Mode = entity.AuthMode }),
                Capabilities = Deserialize<FccDeliveryCapabilities>(entity.CapabilitiesJson, new FccDeliveryCapabilities()),
                PreAuthMode = entity.PreAuthMode,
                RequestTemplates = Deserialize<List<FccTemplateDefinition>>(entity.RequestTemplatesJson, []),
                ResponseTemplates = Deserialize<List<FccTemplateDefinition>>(entity.ResponseTemplatesJson, []),
                ValidationRules = Deserialize<List<FccValidationRuleDefinition>>(entity.ValidationRulesJson, []),
                FieldMappings = Deserialize<List<FccFieldMappingDefinition>>(entity.FieldMappingsJson, []),
                FailureSimulation = Deserialize<FccFailureSimulationDefinition>(entity.FailureSimulationJson, new FccFailureSimulationDefinition()),
                Extensions = Deserialize<FccExtensionPointDefinition>(entity.ExtensionConfigurationJson, new FccExtensionPointDefinition()),
            },
        };
    }

    internal static FccSimulatorProfile ToEntity(FccProfileRecord record, FccSimulatorProfile entity)
    {
        entity.LabEnvironmentId = record.LabEnvironmentId;
        entity.ProfileKey = record.ProfileKey.Trim();
        entity.Name = record.Name.Trim();
        entity.VendorFamily = record.VendorFamily.Trim();
        entity.DeliveryMode = record.DeliveryMode;
        entity.AuthMode = record.Contract.Auth.Mode;
        entity.PreAuthMode = record.Contract.PreAuthMode;
        entity.EndpointBasePath = "/fcc";
        entity.EndpointSurfaceJson = Serialize(record.Contract.EndpointSurface);
        entity.AuthConfigurationJson = Serialize(record.Contract.Auth);
        entity.CapabilitiesJson = Serialize(record.Contract.Capabilities);
        entity.RequestTemplatesJson = Serialize(record.Contract.RequestTemplates);
        entity.ResponseTemplatesJson = Serialize(record.Contract.ResponseTemplates);
        entity.ValidationRulesJson = Serialize(record.Contract.ValidationRules);
        entity.FieldMappingsJson = Serialize(record.Contract.FieldMappings);
        entity.FailureSimulationJson = Serialize(record.Contract.FailureSimulation);
        entity.ExtensionConfigurationJson = Serialize(record.Contract.Extensions);
        entity.IsActive = record.IsActive;
        entity.IsDefault = record.IsDefault;
        return entity;
    }

    private static void ValidateAuth(FccAuthConfiguration auth, ICollection<FccProfileValidationMessage> messages)
    {
        if (auth.Mode == SimulatedAuthMode.ApiKey)
        {
            if (string.IsNullOrWhiteSpace(auth.ApiKeyHeaderName))
            {
                messages.Add(new("contract.auth.apiKeyHeaderName", "API key header name is required when auth mode is API_KEY.", "Error"));
            }

            if (string.IsNullOrWhiteSpace(auth.ApiKeyValue))
            {
                messages.Add(new("contract.auth.apiKeyValue", "API key value is required when auth mode is API_KEY.", "Error"));
            }
        }

        if (auth.Mode == SimulatedAuthMode.BasicAuth)
        {
            if (string.IsNullOrWhiteSpace(auth.BasicAuthUsername))
            {
                messages.Add(new("contract.auth.basicAuthUsername", "Basic auth username is required when auth mode is BASIC_AUTH.", "Error"));
            }

            if (string.IsNullOrWhiteSpace(auth.BasicAuthPassword))
            {
                messages.Add(new("contract.auth.basicAuthPassword", "Basic auth password is required when auth mode is BASIC_AUTH.", "Error"));
            }
        }
    }

    private static void ValidateCapabilities(FccProfileRecord record, ICollection<FccProfileValidationMessage> messages)
    {
        FccDeliveryCapabilities capabilities = record.Contract.Capabilities;
        if (!capabilities.SupportsPush && !capabilities.SupportsPull)
        {
            messages.Add(new("contract.capabilities", "Profile must support push, pull, or both.", "Error"));
        }

        switch (record.DeliveryMode)
        {
            case TransactionDeliveryMode.Push when !capabilities.SupportsPush:
                messages.Add(new("deliveryMode", "Delivery mode PUSH requires push capability.", "Error"));
                break;
            case TransactionDeliveryMode.Pull when !capabilities.SupportsPull:
                messages.Add(new("deliveryMode", "Delivery mode PULL requires pull capability.", "Error"));
                break;
            case TransactionDeliveryMode.Hybrid when !(capabilities.SupportsPush && capabilities.SupportsPull):
                messages.Add(new("deliveryMode", "Delivery mode HYBRID requires both push and pull capability.", "Error"));
                break;
        }
    }

    private static void ValidatePreAuth(FccProfileRecord record, ICollection<FccProfileValidationMessage> messages)
    {
        bool hasCreate = record.Contract.EndpointSurface.Any(x => x.Enabled && string.Equals(x.Operation, "preauth-create", StringComparison.OrdinalIgnoreCase));
        bool hasAuthorize = record.Contract.EndpointSurface.Any(x => x.Enabled && string.Equals(x.Operation, "preauth-authorize", StringComparison.OrdinalIgnoreCase));

        if (!hasCreate)
        {
            messages.Add(new("contract.endpointSurface", "Profiles must expose a preauth-create operation.", "Error"));
        }

        if (record.Contract.PreAuthMode == PreAuthFlowMode.CreateThenAuthorize && !hasAuthorize)
        {
            messages.Add(new("contract.endpointSurface", "CREATE_THEN_AUTHORIZE profiles must expose a preauth-authorize operation.", "Error"));
        }
    }

    private static void ValidateTemplates(FccProfileRecord record, ICollection<FccProfileValidationMessage> messages)
    {
        HashSet<string> enabledOperations = record.Contract.EndpointSurface
            .Where(x => x.Enabled)
            .Select(x => x.Operation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string operation in enabledOperations)
        {
            if (!record.Contract.RequestTemplates.Any(x => string.Equals(x.Operation, operation, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add(new($"contract.requestTemplates[{operation}]", $"Missing request template for '{operation}'.", "Error"));
            }

            if (!record.Contract.ResponseTemplates.Any(x => string.Equals(x.Operation, operation, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add(new($"contract.responseTemplates[{operation}]", $"Missing response template for '{operation}'.", "Error"));
            }
        }
    }

    private static void ValidateMappings(FccProfileRecord record, ICollection<FccProfileValidationMessage> messages)
    {
        if (!record.Contract.FieldMappings.Any(x => string.Equals(x.TargetField, "transactionId", StringComparison.OrdinalIgnoreCase)))
        {
            messages.Add(new("contract.fieldMappings", "Field mappings must include transactionId.", "Error"));
        }

        if (!record.Contract.FieldMappings.Any(x => string.Equals(x.TargetField, "siteCode", StringComparison.OrdinalIgnoreCase)))
        {
            messages.Add(new("contract.fieldMappings", "Field mappings must include siteCode.", "Error"));
        }
    }

    private static void ValidateFailureSimulation(FccFailureSimulationDefinition failureSimulation, ICollection<FccProfileValidationMessage> messages)
    {
        if (failureSimulation.SimulatedDelayMs < 0)
        {
            messages.Add(new("contract.failureSimulation.simulatedDelayMs", "Simulated delay cannot be negative.", "Error"));
        }

        if (failureSimulation.FailureRatePercent is < 0 or > 100)
        {
            messages.Add(new("contract.failureSimulation.failureRatePercent", "Failure rate must be between 0 and 100.", "Error"));
        }
    }

    private static Dictionary<string, string> BuildSampleValues(FccProfileRecord record, string operation, IReadOnlyDictionary<string, string>? overrides)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["profileKey"] = record.ProfileKey,
            ["profileName"] = record.Name,
            ["vendorFamily"] = record.VendorFamily,
            ["siteCode"] = "VL-MW-BT001",
            ["correlationId"] = "corr-preview-0001",
            ["transactionId"] = "TX-PREVIEW-0001",
            ["preauthId"] = "PA-PREVIEW-0001",
            ["pumpNumber"] = "1",
            ["nozzleNumber"] = "1",
            ["amount"] = "15000",
            ["volume"] = "42.113",
            ["operation"] = operation,
        };

        if (overrides is not null)
        {
            foreach (KeyValuePair<string, string> pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return values;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

public sealed class FccProfileValidationException(FccProfileValidationResult validationResult) : Exception("FCC profile validation failed.")
{
    public FccProfileValidationResult ValidationResult { get; } = validationResult;
}
