namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// Single-row cursor tracking how far the diagnostic log uploader has scanned.
/// </summary>
public sealed class DiagnosticLogCursorRecord
{
    public int Id { get; set; } = 1;
    public string? FilePath { get; set; }
    public int LastProcessedLineNumber { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
