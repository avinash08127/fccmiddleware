namespace FccMiddleware.Application.Common;

/// <summary>
/// Minimal strict SemVer parser/comparer for x.y.z versions.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(segments[0], out var major)
            || !int.TryParse(segments[1], out var minor)
            || !int.TryParse(segments[2], out var patch)
            || major < 0
            || minor < 0
            || patch < 0)
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        if (minorComparison != 0)
        {
            return minorComparison;
        }

        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
