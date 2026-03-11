namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>Factory for creating FCC adapter instances by vendor type.</summary>
public interface IFccAdapterFactory
{
    IFccAdapter Create(FccVendor vendor, FccConnectionConfig config);
}
