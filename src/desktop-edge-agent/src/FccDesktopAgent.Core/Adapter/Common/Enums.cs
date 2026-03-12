namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>Canonical transaction lifecycle status (matches Cloud Backend).</summary>
public enum TransactionStatus
{
    Pending,
    Synced,
    SyncedToOdoo,
    StalePending,
    Duplicate,
    Archived
}

/// <summary>Edge-side upload state for buffered transactions.</summary>
public enum SyncStatus
{
    Pending,
    Uploaded,
    DuplicateConfirmed,
    SyncedToOdoo,
    Archived
}

/// <summary>Pre-authorization lifecycle status.</summary>
public enum PreAuthStatus
{
    Pending,
    Authorized,
    Dispensing,
    Completed,
    Cancelled,
    Expired,
    Failed
}

/// <summary>How transactions flow into the middleware (site configuration).</summary>
public enum IngestionMode
{
    /// <summary>FCC pushes directly to cloud. Agent is safety-net LAN poller (catch-up only).</summary>
    CloudDirect,

    /// <summary>Agent is primary receiver. Polls FCC, buffers, uploads to cloud.</summary>
    Relay,

    /// <summary>Agent always buffers locally first, then uploads.</summary>
    BufferAlways
}

/// <summary>FCC protocol adapter vendor.</summary>
public enum FccVendor
{
    Doms,
    Radix
}

/// <summary>Origin of a transaction's ingestion into the middleware.</summary>
public enum IngestionSource
{
    FccPush,
    EdgeUpload,
    CloudPull
}

/// <summary>Pump nozzle state as reported by the FCC.</summary>
public enum PumpState
{
    Idle,
    Authorized,
    Calling,
    Dispensing,
    Paused,
    Completed,
    Error,
    Offline,
    Unknown
}

/// <summary>Source of a pump status reading.</summary>
public enum PumpStatusSource
{
    FccLive,
    EdgeSynthesized
}
