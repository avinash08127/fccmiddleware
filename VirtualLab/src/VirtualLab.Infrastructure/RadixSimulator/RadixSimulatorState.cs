using System.Collections.Concurrent;

namespace VirtualLab.Infrastructure.RadixSimulator;

/// <summary>
/// In-memory state for the Radix FDC simulator.
/// Thread-safe — all collections use concurrent types.
/// </summary>
public sealed class RadixSimulatorState
{
    private readonly ConcurrentQueue<RadixSimulatedTransaction> _transactionBuffer = new();
    private readonly ConcurrentDictionary<int, RadixPreAuthSession> _activePreAuths = new();
    private readonly ConcurrentDictionary<int, RadixProductEntry> _productCatalog = new();
    private readonly ConcurrentQueue<RadixErrorInjection> _errorInjections = new();

    private long _tokenCounter;

    /// <summary>Current operating mode: ON_DEMAND or UNSOLICITED.</summary>
    public RadixOperatingMode Mode { get; set; } = RadixOperatingMode.OnDemand;

    /// <summary>Registered callback URL for unsolicited push mode.</summary>
    public string? UnsolicitedCallbackUrl { get; set; }

    /// <summary>Number of transactions currently buffered.</summary>
    public int TransactionCount => _transactionBuffer.Count;

    /// <summary>Number of active pre-authorization sessions.</summary>
    public int PreAuthCount => _activePreAuths.Count;

    /// <summary>Number of pending error injections.</summary>
    public int PendingErrorCount => _errorInjections.Count;

    /// <summary>Atomically generate the next token value.</summary>
    public long NextToken() => Interlocked.Increment(ref _tokenCounter);

    /// <summary>Get current token value without incrementing.</summary>
    public long CurrentToken => Interlocked.Read(ref _tokenCounter);

    // -----------------------------------------------------------------------
    // Transaction FIFO
    // -----------------------------------------------------------------------

    public void EnqueueTransaction(RadixSimulatedTransaction transaction)
    {
        _transactionBuffer.Enqueue(transaction);
    }

    public bool TryDequeueTransaction(out RadixSimulatedTransaction? transaction)
    {
        return _transactionBuffer.TryDequeue(out transaction);
    }

    public IReadOnlyList<RadixSimulatedTransaction> PeekAllTransactions()
    {
        return _transactionBuffer.ToArray();
    }

    // -----------------------------------------------------------------------
    // Pre-Auth sessions
    // -----------------------------------------------------------------------

    public bool TryAddPreAuth(int pumpNumber, RadixPreAuthSession session)
    {
        return _activePreAuths.TryAdd(pumpNumber, session);
    }

    public bool TryGetPreAuth(int pumpNumber, out RadixPreAuthSession? session)
    {
        return _activePreAuths.TryGetValue(pumpNumber, out session);
    }

    public bool TryRemovePreAuth(int pumpNumber, out RadixPreAuthSession? session)
    {
        return _activePreAuths.TryRemove(pumpNumber, out session);
    }

    public IReadOnlyDictionary<int, RadixPreAuthSession> GetAllPreAuths()
    {
        return new Dictionary<int, RadixPreAuthSession>(_activePreAuths);
    }

    // -----------------------------------------------------------------------
    // Product catalog
    // -----------------------------------------------------------------------

    public void SetProduct(int productId, RadixProductEntry product)
    {
        _productCatalog[productId] = product;
    }

    public IReadOnlyDictionary<int, RadixProductEntry> GetAllProducts()
    {
        return new Dictionary<int, RadixProductEntry>(_productCatalog);
    }

    // -----------------------------------------------------------------------
    // Error injection
    // -----------------------------------------------------------------------

    public void InjectError(RadixErrorInjection error)
    {
        _errorInjections.Enqueue(error);
    }

    public bool TryDequeueError(out RadixErrorInjection? error)
    {
        return _errorInjections.TryDequeue(out error);
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    public void Reset(int pumpCount)
    {
        while (_transactionBuffer.TryDequeue(out _)) { }
        _activePreAuths.Clear();
        _errorInjections.Clear();
        _tokenCounter = 0;
        Mode = RadixOperatingMode.OnDemand;
        UnsolicitedCallbackUrl = null;

        _productCatalog.Clear();
        _productCatalog[1] = new RadixProductEntry(1, "UNLEADED 95", "1.850");
        _productCatalog[2] = new RadixProductEntry(2, "UNLEADED 98", "2.050");
        _productCatalog[3] = new RadixProductEntry(3, "DIESEL", "1.650");
        _productCatalog[4] = new RadixProductEntry(4, "LPG", "0.950");
    }

    /// <summary>Snapshot of current state for diagnostics.</summary>
    public RadixStateSnapshot ToSnapshot()
    {
        return new RadixStateSnapshot
        {
            Mode = Mode.ToString(),
            UnsolicitedCallbackUrl = UnsolicitedCallbackUrl,
            TokenCounter = CurrentToken,
            BufferedTransactionCount = TransactionCount,
            BufferedTransactions = PeekAllTransactions(),
            ActivePreAuths = GetAllPreAuths(),
            ProductCatalog = GetAllProducts(),
            PendingErrorInjections = PendingErrorCount,
        };
    }
}

// -----------------------------------------------------------------------
// Supporting types
// -----------------------------------------------------------------------

public enum RadixOperatingMode
{
    OnDemand,
    Unsolicited,
}

public sealed record RadixSimulatedTransaction
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int PumpNumber { get; init; }
    public int NozzleNumber { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = "UNLEADED 95";
    public string Volume { get; init; } = "10.00";
    public string Amount { get; init; } = "18.50";
    public string Price { get; init; } = "1.850";
    public string FdcDate { get; init; } = DateTimeOffset.UtcNow.ToString("dd/MM/yyyy");
    public string FdcTime { get; init; } = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
    public string EfdId { get; init; } = "1";
    public string SaveNum { get; init; } = "1";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RadixPreAuthSession
{
    public int PumpNumber { get; init; }
    public string Amount { get; init; } = "50.00";
    public string CustomerName { get; init; } = "";
    public string CustomerTaxId { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RadixProductEntry(int Id, string Name, string Price);

public sealed record RadixErrorInjection
{
    /// <summary>Target endpoint: "transaction" or "auth".</summary>
    public string Target { get; init; } = "transaction";

    /// <summary>Error code to return (e.g. 251, 255, 256, 258, 260).</summary>
    public int ErrorCode { get; init; } = 255;

    /// <summary>Error message text.</summary>
    public string ErrorMessage { get; init; } = "Injected error";
}

public sealed class RadixStateSnapshot
{
    public string Mode { get; init; } = "OnDemand";
    public string? UnsolicitedCallbackUrl { get; init; }
    public long TokenCounter { get; init; }
    public int BufferedTransactionCount { get; init; }
    public IReadOnlyList<RadixSimulatedTransaction> BufferedTransactions { get; init; } = [];
    public IReadOnlyDictionary<int, RadixPreAuthSession> ActivePreAuths { get; init; } = new Dictionary<int, RadixPreAuthSession>();
    public IReadOnlyDictionary<int, RadixProductEntry> ProductCatalog { get; init; } = new Dictionary<int, RadixProductEntry>();
    public int PendingErrorInjections { get; init; }
}
