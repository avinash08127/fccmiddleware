using System.Collections.Concurrent;

namespace VirtualLab.Infrastructure.DomsJpl;

/// <summary>
/// In-memory simulation state for the DOMS JPL simulator.
/// Thread-safe for concurrent access from the TCP listener and REST management endpoints.
/// </summary>
public sealed class DomsJplSimulatorState
{
    private readonly object _lock = new();

    /// <summary>
    /// Pump states keyed by pump number (1-based). Values are DOMS protocol state codes (0-13).
    /// </summary>
    public ConcurrentDictionary<int, DomsPumpState> PumpStates { get; } = new();

    /// <summary>
    /// Buffered transactions waiting to be read by FpSupTrans_read_req.
    /// </summary>
    private readonly List<SimulatedDomsTransaction> _transactions = [];

    /// <summary>
    /// Active pre-auth sessions keyed by pump number.
    /// </summary>
    public ConcurrentDictionary<int, SimulatedDomsPreAuth> ActivePreAuths { get; } = new();

    /// <summary>
    /// Unsupervised transactions waiting to be read by FpUnsupTrans_read_req.
    /// </summary>
    private readonly List<SimulatedDomsTransaction> _unsupervisedTransactions = [];

    /// <summary>
    /// Current fuel price set for the simulated site.
    /// </summary>
    public SimulatedPriceSet PriceSet { get; set; } = new();

    /// <summary>
    /// Accumulated pump totals keyed by pump number.
    /// </summary>
    public ConcurrentDictionary<int, SimulatedPumpTotals> PumpTotals { get; } = new();

    /// <summary>
    /// Error injection configuration.
    /// </summary>
    public DomsErrorInjection ErrorInjection { get; set; } = new();

    /// <summary>
    /// Number of connected TCP clients.
    /// </summary>
    private int _connectedClientCount;
    public int ConnectedClientCount => _connectedClientCount;

    /// <summary>
    /// When true, the simulator has been started and is listening.
    /// </summary>
    public bool IsListening { get; set; }

    /// <summary>
    /// Total messages processed since last reset.
    /// </summary>
    private long _totalMessagesProcessed;
    public long TotalMessagesProcessed => _totalMessagesProcessed;

    public void IncrementConnectedClients() => Interlocked.Increment(ref _connectedClientCount);
    public void DecrementConnectedClients() => Interlocked.Decrement(ref _connectedClientCount);
    public void IncrementMessagesProcessed() => Interlocked.Increment(ref _totalMessagesProcessed);

    public void Initialize(int pumpCount)
    {
        lock (_lock)
        {
            PumpStates.Clear();
            _transactions.Clear();
            _unsupervisedTransactions.Clear();
            ActivePreAuths.Clear();
            PriceSet = new SimulatedPriceSet();
            PumpTotals.Clear();
            ErrorInjection = new DomsErrorInjection();
            _connectedClientCount = 0;
            _totalMessagesProcessed = 0;

            for (int i = 1; i <= pumpCount; i++)
            {
                PumpStates[i] = DomsPumpState.Idle;
                PumpTotals[i] = new SimulatedPumpTotals { PumpNumber = i };
            }
        }
    }

    public void InjectTransaction(SimulatedDomsTransaction transaction)
    {
        lock (_lock)
        {
            _transactions.Add(transaction);
        }
    }

    public IReadOnlyList<SimulatedDomsTransaction> GetTransactions()
    {
        lock (_lock)
        {
            return [.. _transactions];
        }
    }

    public int ClearTransactions()
    {
        lock (_lock)
        {
            int count = _transactions.Count;
            _transactions.Clear();
            return count;
        }
    }

    public int ClearTransactions(IReadOnlyList<string> transactionIds)
    {
        lock (_lock)
        {
            HashSet<string> ids = new(transactionIds, StringComparer.OrdinalIgnoreCase);
            return _transactions.RemoveAll(t => ids.Contains(t.TransactionId));
        }
    }

    public void InjectUnsupervisedTransaction(SimulatedDomsTransaction transaction)
    {
        lock (_lock)
        {
            _unsupervisedTransactions.Add(transaction);
        }
    }

    public IReadOnlyList<SimulatedDomsTransaction> GetUnsupervisedTransactions()
    {
        lock (_lock)
        {
            return [.. _unsupervisedTransactions];
        }
    }

    public int ClearUnsupervisedTransactions()
    {
        lock (_lock)
        {
            int count = _unsupervisedTransactions.Count;
            _unsupervisedTransactions.Clear();
            return count;
        }
    }

    public void SetPumpState(int pumpNumber, DomsPumpState state)
    {
        PumpStates[pumpNumber] = state;
    }

    public DomsSimulatorSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new DomsSimulatorSnapshot
            {
                IsListening = IsListening,
                ConnectedClientCount = ConnectedClientCount,
                TotalMessagesProcessed = TotalMessagesProcessed,
                PumpStates = PumpStates.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
                TransactionCount = _transactions.Count,
                Transactions = [.. _transactions],
                UnsupervisedTransactionCount = _unsupervisedTransactions.Count,
                UnsupervisedTransactions = [.. _unsupervisedTransactions],
                ActivePreAuths = ActivePreAuths.ToDictionary(kv => kv.Key, kv => kv.Value),
                PriceSet = PriceSet,
                PumpTotals = PumpTotals.ToDictionary(kv => kv.Key, kv => kv.Value),
                ErrorInjection = ErrorInjection,
            };
        }
    }
}

/// <summary>
/// DOMS pump states matching the real protocol state codes.
/// </summary>
public enum DomsPumpState
{
    Idle = 0,
    Calling = 1,
    Authorized = 2,
    Started = 3,
    Fuelling = 4,
    EndOfTransaction = 5,
    Locked = 6,
    Released = 7,
    Closed = 8,
    Offline = 9,
    Inoperative = 10,
    ManualMode = 11,
    Suspended = 12,
    Error = 13,
}

public sealed class SimulatedDomsTransaction
{
    public string TransactionId { get; init; } = Guid.NewGuid().ToString("N");
    public int PumpNumber { get; init; } = 1;
    public int NozzleNumber { get; init; } = 1;
    public string ProductCode { get; init; } = "UNL95";
    public decimal Volume { get; init; } = 25.00m;
    public decimal Amount { get; init; } = 100.00m;
    public decimal UnitPrice { get; init; } = 4.00m;
    public string CurrencyCode { get; init; } = "TRY";
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int TransactionSequence { get; init; } = 1;
    public string? AttendantId { get; init; }
    public string? ReceiptText { get; init; }
}

public sealed class SimulatedDomsPreAuth
{
    public int PumpNumber { get; init; }
    public decimal AuthorizedAmount { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public DateTimeOffset AuthorizedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class DomsErrorInjection
{
    /// <summary>Response delay in milliseconds. 0 = no delay.</summary>
    public int ResponseDelayMs { get; set; }

    /// <summary>If true, the next response will be a malformed frame (missing ETX).</summary>
    public bool SendMalformedFrame { get; set; }

    /// <summary>If true, the next connection will be dropped after logon.</summary>
    public bool DropConnectionAfterLogon { get; set; }

    /// <summary>If true, heartbeat responses will not be sent (simulates timeout).</summary>
    public bool SuppressHeartbeats { get; set; }

    /// <summary>If true, FcLogon will be rejected regardless of access code.</summary>
    public bool RejectLogon { get; set; }

    /// <summary>If true, authorize requests will be rejected.</summary>
    public bool RejectAuthorize { get; set; }

    /// <summary>Number of times the error injection fires before auto-clearing. 0 = unlimited.</summary>
    public int ShotCount { get; set; }

    /// <summary>Internal counter for shot-limited errors.</summary>
    internal int ShotsRemaining { get; set; }
}

public sealed class DomsSimulatorSnapshot
{
    public bool IsListening { get; init; }
    public int ConnectedClientCount { get; init; }
    public long TotalMessagesProcessed { get; init; }
    public Dictionary<int, string> PumpStates { get; init; } = [];
    public int TransactionCount { get; init; }
    public List<SimulatedDomsTransaction> Transactions { get; init; } = [];
    public int UnsupervisedTransactionCount { get; init; }
    public List<SimulatedDomsTransaction> UnsupervisedTransactions { get; init; } = [];
    public Dictionary<int, SimulatedDomsPreAuth> ActivePreAuths { get; init; } = [];
    public SimulatedPriceSet PriceSet { get; init; } = new();
    public Dictionary<int, SimulatedPumpTotals> PumpTotals { get; init; } = [];
    public DomsErrorInjection ErrorInjection { get; init; } = new();
}

/// <summary>
/// Simulated fuel price set maintained by the simulator.
/// </summary>
public sealed class SimulatedPriceSet
{
    public string PriceSetId { get; set; } = "01";
    public Dictionary<string, SimulatedGradePrice> GradePrices { get; set; } = new()
    {
        ["01"] = new SimulatedGradePrice { GradeId = "01", GradeName = "UNL95", PriceMinorUnits = 4500, CurrencyCode = "TRY" },
        ["02"] = new SimulatedGradePrice { GradeId = "02", GradeName = "DIESEL", PriceMinorUnits = 5000, CurrencyCode = "TRY" },
    };
    public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SimulatedGradePrice
{
    public string GradeId { get; set; } = "";
    public string GradeName { get; set; } = "";
    public long PriceMinorUnits { get; set; }
    public string CurrencyCode { get; set; } = "TRY";
}

/// <summary>
/// Accumulated pump totals for shift reconciliation.
/// </summary>
public sealed class SimulatedPumpTotals
{
    public int PumpNumber { get; init; }
    public long TotalVolumeMicrolitres { get; set; }
    public long TotalAmountMinorUnits { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
