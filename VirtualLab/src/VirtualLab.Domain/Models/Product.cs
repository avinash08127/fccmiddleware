namespace VirtualLab.Domain.Models;

public sealed class Product
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#000000";
    public decimal UnitPrice { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public LabEnvironment LabEnvironment { get; set; } = null!;
    public ICollection<Nozzle> Nozzles { get; set; } = new List<Nozzle>();
}
