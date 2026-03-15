namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>
/// BNA (Banknote Acceptor) report buffered for cloud sync.
/// Ported from legacy EptBnaReport peripheral message.
/// </summary>
public sealed class BufferedBnaReport
{
    public int Id { get; set; }
    public string TerminalId { get; set; } = "";
    public int NotesAccepted { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; }
    public bool IsSynced { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
