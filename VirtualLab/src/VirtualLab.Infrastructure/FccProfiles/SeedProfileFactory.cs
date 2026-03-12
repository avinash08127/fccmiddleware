using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Profiles;

namespace VirtualLab.Infrastructure.FccProfiles;

internal static class SeedProfileFactory
{
    public static FccProfileContract Create(
        SimulatedAuthMode authMode,
        TransactionDeliveryMode deliveryMode,
        PreAuthFlowMode preAuthMode)
    {
        bool supportsPush = deliveryMode is TransactionDeliveryMode.Push or TransactionDeliveryMode.Hybrid;
        bool supportsPull = deliveryMode is TransactionDeliveryMode.Pull or TransactionDeliveryMode.Hybrid;

        return new FccProfileContract
        {
            Auth = new FccAuthConfiguration
            {
                Mode = authMode,
                ApiKeyHeaderName = authMode == SimulatedAuthMode.ApiKey ? "X-Api-Key" : string.Empty,
                ApiKeyValue = authMode == SimulatedAuthMode.ApiKey ? "demo-profile-key" : string.Empty,
                BasicAuthUsername = authMode == SimulatedAuthMode.BasicAuth ? "demo" : string.Empty,
                BasicAuthPassword = authMode == SimulatedAuthMode.BasicAuth ? "demo-password" : string.Empty,
            },
            Capabilities = new FccDeliveryCapabilities
            {
                SupportsPush = supportsPush,
                SupportsPull = supportsPull,
                SupportsHybrid = supportsPush && supportsPull,
                SupportsPreAuthCancellation = true,
            },
            PreAuthMode = preAuthMode,
            EndpointSurface =
            [
                new() { Operation = "health", Method = "GET", PathTemplate = "/health", Description = "Profile health check." },
                new() { Operation = "preauth-create", Method = "POST", PathTemplate = "/preauth/create", Description = "Create preauth reservation." },
                new() { Operation = "preauth-authorize", Method = "POST", PathTemplate = "/preauth/authorize", Description = "Authorize existing preauth.", Enabled = preAuthMode == PreAuthFlowMode.CreateThenAuthorize },
                new() { Operation = "preauth-cancel", Method = "POST", PathTemplate = "/preauth/cancel", Description = "Cancel preauth reservation." },
                new() { Operation = "transactions-pull", Method = "GET", PathTemplate = "/transactions", Description = "Pull transactions.", Enabled = supportsPull },
                new() { Operation = "transactions-ack", Method = "POST", PathTemplate = "/transactions/ack", Description = "Acknowledge pulled transactions.", Enabled = supportsPull },
                new() { Operation = "transactions-push", Method = "POST", PathTemplate = "/transactions/push", Description = "Push transactions.", Enabled = supportsPush },
                new() { Operation = "pump-status", Method = "GET", PathTemplate = "/pump-status", Description = "Read live pump state." },
            ],
            RequestTemplates =
            [
                Template("health", "{}"),
                Template("preauth-create", """{"siteCode":"{{siteCode}}","pump":{{pumpNumber}},"nozzle":{{nozzleNumber}},"amount":{{amount}},"correlationId":"{{correlationId}}"}"""),
                Template("preauth-authorize", """{"siteCode":"{{siteCode}}","preauthId":"{{preauthId}}","amount":{{amount}},"correlationId":"{{correlationId}}"}"""),
                Template("preauth-cancel", """{"siteCode":"{{siteCode}}","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}"}"""),
                Template("transactions-pull", "{}"),
                Template("transactions-ack", """{"siteCode":"{{siteCode}}","transactionId":"{{transactionId}}"}"""),
                Template("transactions-push", """{"siteCode":"{{siteCode}}","transactionId":"{{transactionId}}","volume":{{volume}},"amount":{{amount}},"correlationId":"{{correlationId}}"}"""),
                Template("pump-status", "{}"),
            ],
            ResponseTemplates =
            [
                Template("health", """{"status":"ok","profile":"{{profileKey}}"}"""),
                Template("preauth-create", """{"status":"{{status}}","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}","expiresAtUtc":"{{expiresAtUtc}}"}"""),
                Template("preauth-authorize", """{"status":"{{status}}","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}","expiresAtUtc":"{{expiresAtUtc}}"}"""),
                Template("preauth-cancel", """{"status":"{{status}}","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}"}"""),
                Template("transactions-pull", """{"transactions":[{"transactionId":"{{transactionId}}","siteCode":"{{siteCode}}","volume":{{volume}},"amount":{{amount}}}]}"""),
                Template("transactions-ack", """{"status":"acknowledged","transactionId":"{{transactionId}}"}"""),
                Template("transactions-push", """{"accepted":true,"transactionId":"{{transactionId}}"}"""),
                Template("pump-status", """{"siteCode":"{{siteCode}}","pump":{{pumpNumber}},"status":"idle"}"""),
            ],
            ValidationRules =
            [
                Rule("transaction.raw.transaction-id", "transaction.raw", "$.transactionId", "transactionId is required.", expectedType: "string"),
                Rule("transaction.raw.correlation-id", "transaction.raw", "$.correlationId", "correlationId is required.", expectedType: "string"),
                Rule("transaction.raw.site-code", "transaction.raw", "$.siteCode", "siteCode is required.", expectedType: "string"),
                Rule("transaction.raw.pump-number", "transaction.raw", "$.pumpNumber", "pumpNumber must be at least 1.", expectedType: "integer", minimum: 1),
                Rule("transaction.raw.nozzle-number", "transaction.raw", "$.nozzleNumber", "nozzleNumber must be at least 1.", expectedType: "integer", minimum: 1),
                Rule("transaction.raw.product-code", "transaction.raw", "$.productCode", "productCode is required.", expectedType: "string"),
                Rule("transaction.raw.volume", "transaction.raw", "$.volume", "volume must be greater than zero.", expectedType: "number", minimum: 0.001m),
                Rule("transaction.raw.amount", "transaction.raw", "$.amount", "amount must be greater than zero.", expectedType: "number", minimum: 0.01m),
                Rule("transaction.raw.unit-price", "transaction.raw", "$.unitPrice", "unitPrice must be greater than zero.", expectedType: "number", minimum: 0.01m),
                Rule("transaction.raw.currency", "transaction.raw", "$.currencyCode", "currencyCode must be a three-letter ISO code.", expectedType: "string", pattern: "^[A-Z]{3}$"),
                Rule("transaction.raw.occurred-at", "transaction.raw", "$.occurredAtUtc", "occurredAtUtc must be a valid ISO 8601 timestamp.", expectedType: "date-time"),
                Rule("transaction.canonical.transaction-id", "transaction.canonical", "$.fccTransactionId", "fccTransactionId is required.", expectedType: "string"),
                Rule("transaction.canonical.correlation-id", "transaction.canonical", "$.correlationId", "correlationId is required.", expectedType: "string"),
                Rule("transaction.canonical.site-code", "transaction.canonical", "$.siteCode", "siteCode is required.", expectedType: "string"),
                Rule("transaction.canonical.pump-number", "transaction.canonical", "$.pumpNumber", "pumpNumber must be at least 1.", expectedType: "integer", minimum: 1),
                Rule("transaction.canonical.nozzle-number", "transaction.canonical", "$.nozzleNumber", "nozzleNumber must be at least 1.", expectedType: "integer", minimum: 1),
                Rule("transaction.canonical.product-code", "transaction.canonical", "$.productCode", "productCode is required.", expectedType: "string"),
                Rule("transaction.canonical.volume", "transaction.canonical", "$.volumeMicrolitres", "volumeMicrolitres must be greater than zero.", expectedType: "integer", minimum: 1),
                Rule("transaction.canonical.amount", "transaction.canonical", "$.amountMinorUnits", "amountMinorUnits must be greater than zero.", expectedType: "integer", minimum: 1),
                Rule("transaction.canonical.unit-price", "transaction.canonical", "$.unitPriceMinorPerLitre", "unitPriceMinorPerLitre must be greater than zero.", expectedType: "integer", minimum: 1),
                Rule("transaction.canonical.currency", "transaction.canonical", "$.currencyCode", "currencyCode must be a three-letter ISO code.", expectedType: "string", pattern: "^[A-Z]{3}$"),
                Rule("transaction.canonical.started-at", "transaction.canonical", "$.startedAt", "startedAt must be a valid ISO 8601 timestamp.", expectedType: "date-time"),
                Rule("transaction.canonical.completed-at", "transaction.canonical", "$.completedAt", "completedAt must be a valid ISO 8601 timestamp.", expectedType: "date-time"),
                Rule("transaction.canonical.vendor", "transaction.canonical", "$.fccVendor", "fccVendor must map to a supported vendor.", expectedType: "string", allowedValues: ["DOMS", "RADIX", "ADVATEC", "PETRONITE"]),
                Rule("preauth.request.raw.correlation-id", "preauth.request.raw", "$.correlationId", "correlationId is required.", expectedType: "string"),
                Rule("preauth.request.raw.pump", "preauth.request.raw", "$.pump", "pump is required for the simulated create request.", expectedType: "integer", minimum: 1),
                Rule("preauth.request.raw.nozzle", "preauth.request.raw", "$.nozzle", "nozzle is required for the simulated create request.", expectedType: "integer", minimum: 1),
                Rule("preauth.request.raw.amount", "preauth.request.raw", "$.amount", "amount must be greater than zero.", expectedType: "number", minimum: 0.01m),
                Rule("preauth.request.canonical.preauth-id", "preauth.request.canonical", "$.preAuthId", "preAuthId is required.", expectedType: "string"),
                Rule("preauth.request.canonical.site-code", "preauth.request.canonical", "$.siteCode", "siteCode is required.", expectedType: "string"),
                Rule("preauth.request.canonical.correlation-id", "preauth.request.canonical", "$.correlationId", "correlationId is required.", expectedType: "string"),
                Rule("preauth.request.canonical.pump-number", "preauth.request.canonical", "$.pumpNumber", "pumpNumber must be at least 1.", expectedType: "integer", minimum: 1),
                Rule("preauth.request.canonical.nozzle-number", "preauth.request.canonical", "$.nozzleNumber", "nozzleNumber must be at least 1.", expectedType: "integer", minimum: 1),
                Rule("preauth.request.canonical.product-code", "preauth.request.canonical", "$.productCode", "productCode is required.", expectedType: "string"),
                Rule("preauth.request.canonical.amount-minor", "preauth.request.canonical", "$.requestedAmountMinorUnits", "requestedAmountMinorUnits must be greater than zero.", expectedType: "integer", minimum: 1),
                Rule("preauth.request.canonical.unit-price-minor", "preauth.request.canonical", "$.unitPriceMinorPerLitre", "unitPriceMinorPerLitre must be greater than zero.", expectedType: "integer", minimum: 1),
                Rule("preauth.request.canonical.currency", "preauth.request.canonical", "$.currencyCode", "currencyCode must be a three-letter ISO code.", expectedType: "string", pattern: "^[A-Z]{3}$"),
                Rule("preauth.request.canonical.status", "preauth.request.canonical", "$.status", "status is required.", expectedType: "string", allowedValues: ["PENDING", "AUTHORIZED", "DISPENSING", "COMPLETED", "CANCELLED", "EXPIRED", "FAILED"]),
                Rule("preauth.request.canonical.requested-at", "preauth.request.canonical", "$.requestedAt", "requestedAt must be a valid ISO 8601 timestamp.", expectedType: "date-time"),
                Rule("preauth.request.canonical.expires-at", "preauth.request.canonical", "$.expiresAt", "expiresAt must be a valid ISO 8601 timestamp.", expectedType: "date-time"),
                Rule("preauth.response.raw.status", "preauth.response.raw", "$.status", "status is required.", expectedType: "string"),
                Rule("preauth.response.raw.preauth-id", "preauth.response.raw", "$.preauthId", "preauthId is required.", expectedType: "string"),
                Rule("preauth.response.raw.correlation-id", "preauth.response.raw", "$.correlationId", "correlationId is required.", expectedType: "string"),
                Rule("preauth.response.canonical.status", "preauth.response.canonical", "$.status", "status is required.", expectedType: "string", allowedValues: ["PENDING", "AUTHORIZED", "DISPENSING", "COMPLETED", "CANCELLED", "EXPIRED", "FAILED"]),
                Rule("preauth.response.canonical.preauth-id", "preauth.response.canonical", "$.preAuthId", "preAuthId is required.", expectedType: "string"),
                Rule("preauth.response.canonical.correlation-id", "preauth.response.canonical", "$.correlationId", "correlationId is required.", expectedType: "string"),
            ],
            FieldMappings =
            [
                Mapping("transaction", "$.transactionId", "$.fccTransactionId"),
                Mapping("transaction", "$.correlationId", "$.correlationId"),
                Mapping("transaction", "$.siteCode", "$.siteCode"),
                Mapping("transaction", "$.pumpNumber", "$.pumpNumber"),
                Mapping("transaction", "$.nozzleNumber", "$.nozzleNumber"),
                Mapping("transaction", "$.productCode", "$.productCode"),
                Mapping("transaction", "$.volume", "$.volumeMicrolitres", transform: "litres-to-microlitres"),
                Mapping("transaction", "$.amount", "$.amountMinorUnits", transform: "major-to-minor"),
                Mapping("transaction", "$.unitPrice", "$.unitPriceMinorPerLitre", transform: "major-to-minor"),
                Mapping("transaction", "$.currencyCode", "$.currencyCode"),
                Mapping("preauth.request", "$.correlationId", "$.correlationId"),
                Mapping("preauth.request", "$.pump", "$.pumpNumber"),
                Mapping("preauth.request", "$.nozzle", "$.nozzleNumber"),
                Mapping("preauth.request", "$.amount", "$.requestedAmountMinorUnits", transform: "major-to-minor"),
                Mapping("preauth.request", "$.customerName", "$.customerName"),
                Mapping("preauth.request", "$.customerTaxId", "$.customerTaxId"),
                Mapping("preauth.response", "$.status", "$.status", transform: "uppercase"),
                Mapping("preauth.response", "$.preauthId", "$.preAuthId"),
                Mapping("preauth.response", "$.correlationId", "$.correlationId"),
            ],
            FailureSimulation = new FccFailureSimulationDefinition
            {
                SimulatedDelayMs = 0,
                Enabled = false,
                FailureRatePercent = 0,
                HttpStatusCode = 503,
                ErrorCode = "SIMULATED_FAILURE",
                MessageTemplate = "Simulated failure for {{operation}}",
            },
            Extensions = new FccExtensionPointDefinition
            {
                Configuration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["preAuthExpirySeconds"] = "300",
                },
            },
        };
    }

    private static FccTemplateDefinition Template(string operation, string bodyTemplate)
    {
        return new FccTemplateDefinition
        {
            Operation = operation,
            Name = operation,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "application/json",
            },
            BodyTemplate = bodyTemplate,
        };
    }

    private static FccValidationRuleDefinition Rule(
        string ruleKey,
        string scope,
        string expression,
        string message,
        string expectedType = "",
        decimal? minimum = null,
        decimal? maximum = null,
        string pattern = "",
        IReadOnlyList<string>? allowedValues = null)
    {
        return new FccValidationRuleDefinition
        {
            RuleKey = ruleKey,
            Scope = scope,
            Expression = expression,
            Message = message,
            ExpectedType = expectedType,
            Minimum = minimum,
            Maximum = maximum,
            Pattern = pattern,
            AllowedValues = allowedValues?.ToList() ?? [],
        };
    }

    private static FccFieldMappingDefinition Mapping(
        string scope,
        string sourceField,
        string targetField,
        string transform = "")
    {
        return new FccFieldMappingDefinition
        {
            Scope = scope,
            SourceField = sourceField,
            TargetField = targetField,
            Direction = "Inbound",
            Transform = transform,
        };
    }
}
