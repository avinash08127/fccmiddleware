using Microsoft.Extensions.Configuration;

namespace FccMiddleware.Api.Infrastructure;

public static class SecurityConfigurationValidator
{
    public static void Validate(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment() || configuration is not IConfigurationRoot root)
        {
            return;
        }

        var disallowedJsonKeys = new[]
        {
            "DeviceJwt:SigningKey",
            "PortalJwt:SigningKey"
        };

        foreach (var key in disallowedJsonKeys)
        {
            if (TryGetJsonValue(root, key, out _))
            {
                throw new InvalidOperationException(
                    $"Sensitive configuration key '{key}' must not be sourced from appsettings JSON outside Development.");
            }
        }

        var fccClientSecrets = root.GetSection(FccMiddleware.Api.Auth.FccHmacAuthOptions.SectionName)
            .GetSection("Clients")
            .GetChildren()
            .Select((section, index) => new { section, index });

        foreach (var client in fccClientSecrets)
        {
            var key = $"{FccMiddleware.Api.Auth.FccHmacAuthOptions.SectionName}:Clients:{client.index}:Secret";
            if (TryGetJsonValue(root, key, out _))
            {
                throw new InvalidOperationException(
                    $"Sensitive configuration key '{key}' must not be sourced from appsettings JSON outside Development.");
            }
        }
    }

    private static bool TryGetJsonValue(IConfigurationRoot root, string key, out string? value)
    {
        foreach (var provider in root.Providers)
        {
            if (!provider.TryGet(key, out value))
            {
                continue;
            }

            if (provider.GetType().FullName?.Contains("JsonConfigurationProvider", StringComparison.Ordinal) == true)
            {
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = null;
        return false;
    }
}
