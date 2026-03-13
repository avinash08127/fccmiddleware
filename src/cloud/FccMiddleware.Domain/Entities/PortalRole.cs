namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Lookup entity for portal user roles. Seeded at migration time — not user-editable.
/// </summary>
public class PortalRole
{
    public const short FccAdmin = 1;
    public const short FccUser = 2;
    public const short FccViewer = 3;

    public short Id { get; set; }
    public string Name { get; set; } = null!;
}
