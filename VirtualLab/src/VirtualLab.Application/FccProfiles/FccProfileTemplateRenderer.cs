using System.Text.RegularExpressions;

namespace VirtualLab.Application.FccProfiles;

public static partial class FccProfileTemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        return PlaceholderRegex().Replace(template, match =>
        {
            string key = match.Groups["key"].Value;
            return values.TryGetValue(key, out string? value) ? value : match.Value;
        });
    }

    public static IReadOnlyDictionary<string, string> Render(
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> values)
    {
        return headers.ToDictionary(pair => pair.Key, pair => Render(pair.Value, values), StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\{\{\s*(?<key>[A-Za-z0-9_\-\.]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
