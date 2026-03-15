namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that receive peripheral device events
/// (EPT terminals, BNA devices, dispenser install data).
/// Ported from legacy DPP port 5006 peripheral message handling.
/// </summary>
public interface IFccPeripheralMonitor
{
    /// <summary>
    /// Request current peripheral device inventory from FCC.
    /// </summary>
    Task<PeripheralInventory> GetPeripheralInventoryAsync(CancellationToken ct);
}

/// <summary>Inventory of peripheral devices connected to the FCC.</summary>
public sealed record PeripheralInventory(
    IReadOnlyList<DispenserInfo> Dispensers,
    IReadOnlyList<EptTerminalInfo> EptTerminals);

/// <summary>Dispenser installation data. Ported from legacy DispenserInstallData.</summary>
public sealed record DispenserInfo(string DispenserId, string Model);

/// <summary>EPT (Electronic Payment Terminal) info. Ported from legacy EptInfo.</summary>
public sealed record EptTerminalInfo(string TerminalId, string Version);

/// <summary>BNA (Banknote Acceptor) report. Ported from legacy EptBnaReport.</summary>
public sealed record BnaReport(string TerminalId, int NotesAccepted, DateTimeOffset ReportedAtUtc);
