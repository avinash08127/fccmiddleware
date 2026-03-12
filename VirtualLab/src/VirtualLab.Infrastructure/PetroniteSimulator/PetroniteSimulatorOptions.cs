namespace VirtualLab.Infrastructure.PetroniteSimulator;

public sealed class PetroniteSimulatorOptions
{
    public const string SectionName = "PetroniteSimulator";

    /// <summary>Port for the simulated Petronite REST API.</summary>
    public int Port { get; set; } = 6000;

    /// <summary>OAuth2 client ID for Basic authentication.</summary>
    public string ClientId { get; set; } = "test-client";

    /// <summary>OAuth2 client secret for Basic authentication.</summary>
    public string ClientSecret { get; set; } = "test-secret";

    /// <summary>Shared secret for webhook HMAC signatures.</summary>
    public string WebhookSecret { get; set; } = "test-webhook-secret";

    /// <summary>OAuth2 token lifetime in seconds.</summary>
    public int TokenExpiresInSeconds { get; set; } = 3600;

    /// <summary>Delay in milliseconds before auto-sending the webhook after authorize.</summary>
    public int AutoWebhookDelayMs { get; set; } = 2000;

    /// <summary>Number of pumps to simulate.</summary>
    public int PumpCount { get; set; } = 4;
}
