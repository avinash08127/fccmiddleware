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
                Template("preauth-create", """{"status":"created","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}"}"""),
                Template("preauth-authorize", """{"status":"authorized","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}"}"""),
                Template("preauth-cancel", """{"status":"cancelled","preauthId":"{{preauthId}}","correlationId":"{{correlationId}}"}"""),
                Template("transactions-pull", """{"transactions":[{"transactionId":"{{transactionId}}","siteCode":"{{siteCode}}","volume":{{volume}},"amount":{{amount}}}]}"""),
                Template("transactions-ack", """{"status":"acknowledged","transactionId":"{{transactionId}}"}"""),
                Template("transactions-push", """{"accepted":true,"transactionId":"{{transactionId}}"}"""),
                Template("pump-status", """{"siteCode":"{{siteCode}}","pump":{{pumpNumber}},"status":"idle"}"""),
            ],
            ValidationRules =
            [
                new() { RuleKey = "site-code-required", Scope = "request", Expression = "$.siteCode", Message = "siteCode is required." },
                new() { RuleKey = "transaction-id-required", Scope = "transaction", Expression = "$.transactionId", Message = "transactionId is required." },
            ],
            FieldMappings =
            [
                new() { SourceField = "siteCode", TargetField = "siteCode", Direction = "Inbound" },
                new() { SourceField = "transactionId", TargetField = "transactionId", Direction = "Inbound" },
                new() { SourceField = "pump", TargetField = "pumpNumber", Direction = "Inbound" },
                new() { SourceField = "nozzle", TargetField = "nozzleNumber", Direction = "Inbound" },
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
}
