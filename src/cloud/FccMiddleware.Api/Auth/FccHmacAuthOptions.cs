using Microsoft.AspNetCore.Authentication;

namespace FccMiddleware.Api.Auth;

public sealed class FccHmacAuthOptions : AuthenticationSchemeOptions
{
    public const string SectionName = "FccHmac";
    public const string SchemeName = "FccHmac";
    public const string PolicyName = "FccHmac";
    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string SignatureHeaderName = "X-Signature";
    public const string TimestampHeaderName = "X-Timestamp";

    public int AllowedClockSkewMinutes { get; set; } = 5;
    public List<FccHmacClient> Clients { get; init; } = [];
}

public sealed class FccHmacClient
{
    public string ApiKeyId { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public string? SiteCode { get; init; }
    public Guid? LegalEntityId { get; init; }
    public bool Active { get; init; } = true;
}
