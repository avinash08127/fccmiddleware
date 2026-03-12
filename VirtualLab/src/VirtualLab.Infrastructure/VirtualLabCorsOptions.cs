namespace VirtualLab.Infrastructure;

public sealed class VirtualLabCorsOptions
{
    public const string SectionName = "VirtualLab:Cors";
    public const string PolicyName = "VirtualLabUi";

    public string[] AllowedOrigins { get; set; } = [];
}
