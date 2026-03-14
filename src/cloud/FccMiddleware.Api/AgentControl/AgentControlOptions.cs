namespace FccMiddleware.Api.AgentControl;

public sealed class AgentCommandsOptions
{
    public const string SectionName = "AgentCommands";

    public bool Enabled { get; set; }
    public bool FcmHintsEnabled { get; set; }
    public int DefaultCommandTtlHours { get; set; } = 24;
}

public sealed class BootstrapTokensOptions
{
    public const string SectionName = "BootstrapTokens";

    public bool HistoryApiEnabled { get; set; }
}

public sealed class FirebaseMessagingOptions
{
    public const string SectionName = "Firebase:Messaging";

    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string BaseUrl { get; set; } = "https://fcm.googleapis.com";
    public string? ProjectId { get; set; }
    public string? ClientEmail { get; set; }
    public string? PrivateKey { get; set; }
}
