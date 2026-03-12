namespace VirtualLab.Domain.Models;

public sealed class Pump
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public int PumpNumber { get; set; }
    public int FccPumpNumber { get; set; }
    public int LayoutX { get; set; }
    public int LayoutY { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Site Site { get; set; } = null!;
    public ICollection<Nozzle> Nozzles { get; set; } = new List<Nozzle>();
}
