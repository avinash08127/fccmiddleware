using MediatR;

namespace FccMiddleware.Application.MasterData;

public sealed class SyncPumpsCommand : IRequest<MasterDataSyncResult>
{
    public bool IsFullSnapshot { get; init; }
    public required List<PumpSyncItem> Items { get; init; }
}

public sealed class PumpSyncItem
{
    public Guid Id { get; init; }
    public string SiteCode { get; init; } = null!;
    public int PumpNumber { get; init; }
    public int? FccPumpNumber { get; init; }
    public List<NozzleSyncItem> Nozzles { get; init; } = [];
    public bool IsActive { get; init; }
}

public sealed class NozzleSyncItem
{
    public int NozzleNumber { get; init; }
    public int? FccNozzleNumber { get; init; }
    public string CanonicalProductCode { get; init; } = null!;
}
