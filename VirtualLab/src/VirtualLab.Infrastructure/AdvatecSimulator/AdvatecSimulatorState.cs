using System.Collections.Concurrent;

namespace VirtualLab.Infrastructure.AdvatecSimulator;

/// <summary>
/// In-memory state for the Advatec EFD simulator.
/// Thread-safe — all collections use concurrent types.
/// </summary>
public sealed class AdvatecSimulatorState
{
    private readonly ConcurrentQueue<AdvatecPendingReceipt> _pendingReceipts = new();
    private readonly ConcurrentDictionary<string, AdvatecGeneratedReceipt> _generatedReceipts = new();
    private readonly ConcurrentDictionary<int, AdvatecPumpConfig> _pumps = new();

    private int _globalCount;
    private int _dailyCount;
    private string _currentZDate = DateTime.UtcNow.ToString("yyyyMMdd");

    /// <summary>Registered webhook callback URL for Receipt webhooks.</summary>
    public string? WebhookCallbackUrl { get; set; }

    /// <summary>Webhook authentication token (sent as query parameter).</summary>
    public string? WebhookToken { get; set; }

    /// <summary>Error simulation mode.</summary>
    public AdvatecErrorMode ErrorMode { get; set; } = AdvatecErrorMode.None;

    /// <summary>Custom receipt delay override (null = use options default).</summary>
    public int? ReceiptDelayOverrideMs { get; set; }

    /// <summary>Company profile for receipt generation.</summary>
    public AdvatecCompanyProfile Company { get; set; } = AdvatecCompanyProfile.Default;

    /// <summary>Product catalog for receipt items.</summary>
    public ConcurrentDictionary<string, AdvatecProduct> Products { get; } = new(StringComparer.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // Counters
    // -----------------------------------------------------------------------

    public int GetNextGlobalCount()
    {
        string today = DateTime.UtcNow.ToString("yyyyMMdd");
        if (today != _currentZDate)
        {
            _currentZDate = today;
            Interlocked.Exchange(ref _dailyCount, 0);
        }

        Interlocked.Increment(ref _dailyCount);
        return Interlocked.Increment(ref _globalCount);
    }

    public int CurrentDailyCount => _dailyCount;
    public int CurrentGlobalCount => _globalCount;
    public string CurrentZNumber => _currentZDate;

    // -----------------------------------------------------------------------
    // Pending receipts (Customer submission → receipt generation queue)
    // -----------------------------------------------------------------------

    public void EnqueuePendingReceipt(AdvatecPendingReceipt pending)
    {
        _pendingReceipts.Enqueue(pending);
    }

    public bool TryDequeuePendingReceipt(out AdvatecPendingReceipt? pending)
    {
        return _pendingReceipts.TryDequeue(out pending);
    }

    public int PendingReceiptCount => _pendingReceipts.Count;

    // -----------------------------------------------------------------------
    // Generated receipts
    // -----------------------------------------------------------------------

    public void AddGeneratedReceipt(AdvatecGeneratedReceipt receipt)
    {
        _generatedReceipts[receipt.TransactionId] = receipt;
    }

    public IReadOnlyList<AdvatecGeneratedReceipt> GetAllReceipts()
    {
        return _generatedReceipts.Values
            .OrderByDescending(r => r.GeneratedAtUtc)
            .ToList();
    }

    public int GeneratedReceiptCount => _generatedReceipts.Count;

    // -----------------------------------------------------------------------
    // Pumps
    // -----------------------------------------------------------------------

    public AdvatecPumpConfig? GetPump(int pumpNumber)
    {
        return _pumps.TryGetValue(pumpNumber, out AdvatecPumpConfig? pump) ? pump : null;
    }

    public IReadOnlyList<AdvatecPumpConfig> GetAllPumps()
    {
        return _pumps.Values.OrderBy(p => p.PumpNumber).ToList();
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    public void Reset(int pumpCount)
    {
        while (_pendingReceipts.TryDequeue(out _)) { }
        _generatedReceipts.Clear();
        _pumps.Clear();
        WebhookCallbackUrl = null;
        WebhookToken = null;
        ErrorMode = AdvatecErrorMode.None;
        ReceiptDelayOverrideMs = null;
        Interlocked.Exchange(ref _globalCount, 0);
        Interlocked.Exchange(ref _dailyCount, 0);
        _currentZDate = DateTime.UtcNow.ToString("yyyyMMdd");

        Company = AdvatecCompanyProfile.Default;
        Products.Clear();
        Products["TANGO"] = new AdvatecProduct { Code = "TANGO", Name = "TANGO", TaxCode = "1", UnitPriceTzs = 3285.00m };
        Products["DIESEL"] = new AdvatecProduct { Code = "DIESEL", Name = "DIESEL", TaxCode = "1", UnitPriceTzs = 3427.00m };

        for (int i = 1; i <= pumpCount; i++)
        {
            _pumps[i] = new AdvatecPumpConfig
            {
                PumpNumber = i,
                ProductCode = i <= 2 ? "TANGO" : "DIESEL",
            };
        }
    }

    /// <summary>Snapshot of current state for diagnostics.</summary>
    public AdvatecStateSnapshot ToSnapshot()
    {
        return new AdvatecStateSnapshot
        {
            WebhookCallbackUrl = WebhookCallbackUrl,
            WebhookToken = WebhookToken,
            ErrorMode = ErrorMode,
            ReceiptDelayOverrideMs = ReceiptDelayOverrideMs,
            PendingReceiptCount = PendingReceiptCount,
            GeneratedReceiptCount = GeneratedReceiptCount,
            GlobalCount = CurrentGlobalCount,
            DailyCount = CurrentDailyCount,
            ZNumber = CurrentZNumber,
            Company = Company,
            Products = Products.Values.OrderBy(p => p.Code).ToList(),
            Pumps = GetAllPumps(),
            RecentReceipts = _generatedReceipts.Values
                .OrderByDescending(r => r.GeneratedAtUtc)
                .Take(10)
                .ToList(),
        };
    }
}

// -----------------------------------------------------------------------
// Supporting types
// -----------------------------------------------------------------------

public enum AdvatecErrorMode
{
    None,
    TraOffline,
    DeviceBusy,
}

public sealed record AdvatecCompanyProfile
{
    public string TIN { get; init; } = string.Empty;
    public string VRN { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string Street { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string Mobile { get; init; } = string.Empty;
    public string TaxOffice { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string RegistrationId { get; init; } = string.Empty;
    public string UIN { get; init; } = string.Empty;

    public static AdvatecCompanyProfile Default => new()
    {
        TIN = "100-123-456",
        VRN = "10-0123456-B",
        Name = "ADVATECH COMPANY LIMITED",
        City = "DAR ES SALAAM",
        Region = "DAR ES SALAAM",
        Street = "MSIMBAZI",
        Country = "TZ",
        Mobile = "+255712345678",
        TaxOffice = "ILALA TAX REGION",
        SerialNumber = "10TZ100625",
        RegistrationId = "TZ0100-0000625",
        UIN = "WEB0625",
    };
}

public sealed record AdvatecProduct
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TaxCode { get; init; } = "1";
    public decimal UnitPriceTzs { get; init; }
}

public sealed record AdvatecPumpConfig
{
    public int PumpNumber { get; init; }
    public string ProductCode { get; init; } = "TANGO";
}

public sealed record AdvatecPendingReceipt
{
    public int Pump { get; init; }
    public decimal Dose { get; init; }
    public int CustIdType { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AdvatecGeneratedReceipt
{
    public string TransactionId { get; init; } = string.Empty;
    public string ReceiptCode { get; init; } = string.Empty;
    public int Pump { get; init; }
    public decimal Volume { get; init; }
    public decimal AmountInclusive { get; init; }
    public string ProductCode { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool WebhookSent { get; set; }
}

public sealed class AdvatecStateSnapshot
{
    public string? WebhookCallbackUrl { get; init; }
    public string? WebhookToken { get; init; }
    public AdvatecErrorMode ErrorMode { get; init; }
    public int? ReceiptDelayOverrideMs { get; init; }
    public int PendingReceiptCount { get; init; }
    public int GeneratedReceiptCount { get; init; }
    public int GlobalCount { get; init; }
    public int DailyCount { get; init; }
    public string ZNumber { get; init; } = string.Empty;
    public AdvatecCompanyProfile Company { get; init; } = AdvatecCompanyProfile.Default;
    public IReadOnlyList<AdvatecProduct> Products { get; init; } = [];
    public IReadOnlyList<AdvatecPumpConfig> Pumps { get; init; } = [];
    public IReadOnlyList<AdvatecGeneratedReceipt> RecentReceipts { get; init; } = [];
}
