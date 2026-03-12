using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class Nozzle
{
    public Guid Id { get; set; }
    public Guid PumpId { get; set; }
    public Guid ProductId { get; set; }
    public int NozzleNumber { get; set; }
    public int FccNozzleNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public NozzleState State { get; set; }
    public string SimulationStateJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Pump Pump { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
