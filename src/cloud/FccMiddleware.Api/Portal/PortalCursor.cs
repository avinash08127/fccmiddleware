using System.Text;

namespace FccMiddleware.Api.Portal;

internal static class PortalCursor
{
    public static string Encode(DateTimeOffset timestamp, Guid id)
    {
        var raw = $"{timestamp:O}|{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset timestamp, out Guid id)
    {
        timestamp = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };

            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separatorIndex = raw.IndexOf('|');
            if (separatorIndex < 0)
            {
                return false;
            }

            return DateTimeOffset.TryParse(
                       raw[..separatorIndex],
                       null,
                       System.Globalization.DateTimeStyles.RoundtripKind,
                       out timestamp)
                   && Guid.TryParse(raw[(separatorIndex + 1)..], out id);
        }
        catch
        {
            return false;
        }
    }
}
