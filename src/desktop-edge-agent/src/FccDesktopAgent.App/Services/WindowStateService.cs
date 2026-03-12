using System.Text.Json;
using System.Text.Json.Serialization;

namespace FccDesktopAgent.App.Services;

/// <summary>
/// Persists and restores window position, size, and maximized state across sessions.
/// Stored as JSON in the user's LocalApplicationData folder.
/// </summary>
internal static class WindowStateService
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FccDesktopAgent",
        "window-state.json");

    public static WindowState? Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize(json, WindowStateJsonContext.Default.WindowState);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WindowState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(state, WindowStateJsonContext.Default.WindowState);
            File.WriteAllText(StatePath, json);
        }
        catch
        {
            // Non-fatal — window state is a convenience feature
        }
    }
}

internal sealed class WindowState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

[JsonSerializable(typeof(WindowState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class WindowStateJsonContext : JsonSerializerContext
{
}
