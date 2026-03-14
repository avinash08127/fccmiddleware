namespace FccMiddleware.Application.Notifications;

public sealed class EmailNotificationOptions
{
    public const string SectionName = "EmailNotifications";

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "None";
    public string FromAddress { get; set; } = string.Empty;
    public string[] AdminRecipients { get; set; } = [];
    public string AwsSesRegion { get; set; } = "eu-west-1";
    public string SendGridApiKey { get; set; } = string.Empty;
}
