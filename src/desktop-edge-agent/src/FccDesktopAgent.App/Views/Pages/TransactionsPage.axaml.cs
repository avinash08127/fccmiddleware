using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.App.Views.Pages;

public sealed partial class TransactionsPage : UserControl, IDisposable
{
    private const int PageSize = 50;
    private readonly IServiceProvider? _services;
    private readonly ILogger<TransactionsPage>? _logger;
    private readonly Timer _refreshTimer;

    private int _currentPage;
    private int _totalCount;

    // P-DSK-022: Cache total count, only refresh on filter change or explicit load
    private bool _totalCountDirty = true;

    // Filter state
    private SyncStatus? _statusFilter;
    private int? _pumpFilter;
    private DateTimeOffset? _dateFrom;
    private DateTimeOffset? _dateTo;

    public TransactionsPage()
    {
        InitializeComponent();
        _services = AgentAppContext.ServiceProvider;
        _logger = _services?.GetService<ILoggerFactory>()?.CreateLogger<TransactionsPage>();

        // Auto-refresh every 10 seconds
        _refreshTimer = new Timer(_ => Dispatcher.UIThread.Post(() => _ = LoadPageAsync()),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    // P-DSK-019: Pause the timer when the page is not visible to avoid unnecessary DB queries
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        base.OnDetachedFromVisualTree(e);
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadPageAsync()
    {
        if (_services is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            IQueryable<BufferedTransaction> query = db.Transactions.AsNoTracking();

            // Apply filters
            if (_statusFilter.HasValue)
                query = query.Where(t => t.SyncStatus == _statusFilter.Value);

            if (_pumpFilter.HasValue)
                query = query.Where(t => t.PumpNumber == _pumpFilter.Value);

            if (_dateFrom.HasValue)
                query = query.Where(t => t.CompletedAt >= _dateFrom.Value);

            if (_dateTo.HasValue)
                query = query.Where(t => t.CompletedAt <= _dateTo.Value);

            // P-DSK-022: Only refresh total count when filters change to avoid duplicate DB round-trip
            if (_totalCountDirty)
            {
                _totalCount = await query.CountAsync();
                _totalCountDirty = false;
            }

            var rows = await query
                .OrderByDescending(t => t.CompletedAt)
                .Skip(_currentPage * PageSize)
                .Take(PageSize)
                .Select(t => new TransactionRow
                {
                    Id = t.Id,
                    FccTransactionId = t.FccTransactionId,
                    SiteCode = t.SiteCode,
                    PumpNumber = t.PumpNumber,
                    NozzleNumber = t.NozzleNumber,
                    ProductCode = t.ProductCode,
                    VolumeMicrolitres = t.VolumeMicrolitres,
                    AmountMinorUnits = t.AmountMinorUnits,
                    CurrencyCode = t.CurrencyCode,
                    StartedAt = t.StartedAt,
                    CompletedAt = t.CompletedAt,
                    SyncStatus = t.SyncStatus.ToString(),
                    UploadAttempts = t.UploadAttempts,
                    LastUploadError = t.LastUploadError,
                    CorrelationId = t.CorrelationId,
                    RawPayloadJson = t.RawPayloadJson,
                })
                .ToListAsync();

            TransactionGrid.ItemsSource = rows;

            var totalPages = Math.Max(1, (int)Math.Ceiling((double)_totalCount / PageSize));
            PageInfoText.Text = $"Page {_currentPage + 1} of {totalPages} ({_totalCount:N0} total)";
            PrevButton.IsEnabled = _currentPage > 0;
            NextButton.IsEnabled = (_currentPage + 1) * PageSize < _totalCount;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading transactions page");
            PageInfoText.Text = "Error loading transactions";
        }
    }

    // ── Filter handlers ───────────────────────────────────────────────────────

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Don't auto-apply; user clicks "Apply"
    }

    private void OnDateFilterChanged(object? sender, DatePickerSelectedValueChangedEventArgs e)
    {
        // Don't auto-apply
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ApplyFilters();
    }

    private void OnApplyFilterClicked(object? sender, RoutedEventArgs e) => ApplyFilters();

    private void OnClearFilterClicked(object? sender, RoutedEventArgs e)
    {
        StatusFilter.SelectedIndex = 0;
        PumpFilter.Text = "";
        DateFromPicker.SelectedDate = null;
        DateToPicker.SelectedDate = null;
        _statusFilter = null;
        _pumpFilter = null;
        _dateFrom = null;
        _dateTo = null;
        _currentPage = 0;
        _totalCountDirty = true;
        _ = LoadPageAsync();
    }

    private void ApplyFilters()
    {
        // P-DSK-022: Mark total count dirty so it refreshes with new filters
        _totalCountDirty = true;

        // Sync status filter
        _statusFilter = StatusFilter.SelectedIndex switch
        {
            1 => SyncStatus.Pending,
            2 => SyncStatus.Uploaded,
            3 => SyncStatus.SyncedToOdoo,
            4 => SyncStatus.DuplicateConfirmed,
            _ => null
        };

        // Pump number
        _pumpFilter = int.TryParse(PumpFilter.Text, out var pump) ? pump : null;

        // Date range
        _dateFrom = DateFromPicker.SelectedDate.HasValue
            ? new DateTimeOffset(DateFromPicker.SelectedDate.Value.DateTime, DateTimeOffset.Now.Offset)
            : null;

        _dateTo = DateToPicker.SelectedDate.HasValue
            ? new DateTimeOffset(DateToPicker.SelectedDate.Value.DateTime.AddDays(1).AddTicks(-1), DateTimeOffset.Now.Offset)
            : null;

        _currentPage = 0;
        _ = LoadPageAsync();
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    private void OnPrevPageClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentPage > 0)
        {
            _currentPage--;
            _ = LoadPageAsync();
        }
    }

    private void OnNextPageClicked(object? sender, RoutedEventArgs e)
    {
        if ((_currentPage + 1) * PageSize < _totalCount)
        {
            _currentPage++;
            _ = LoadPageAsync();
        }
    }

    // ── Detail panel ──────────────────────────────────────────────────────────

    private void OnTransactionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (TransactionGrid.SelectedItem is TransactionRow row)
            ShowDetail(row);
    }

    private void ShowDetail(TransactionRow row)
    {
        DetailPanel.IsVisible = true;
        DetailId.Text = row.Id;
        DetailFccId.Text = row.FccTransactionId;
        DetailSiteCode.Text = row.SiteCode;
        DetailStarted.Text = row.StartedAt.LocalDateTime.ToString("G");
        DetailCompleted.Text = row.CompletedAt.LocalDateTime.ToString("G");
        DetailCurrency.Text = row.CurrencyCode;
        DetailUploadAttempts.Text = row.UploadAttempts.ToString();
        DetailLastError.Text = row.LastUploadError ?? "None";
        DetailCorrelationId.Text = row.CorrelationId ?? "N/A";
        // S-DSK-028: Store raw payload but keep it hidden until explicitly revealed.
        DetailRawPayload.Text = row.RawPayloadJson ?? "";
        DetailRawPayload.IsVisible = false;
        ToggleRawPayloadButton.Content = "Show";
    }

    // S-DSK-028: Toggle visibility of raw payload to prevent inadvertent PII exposure.
    private void OnToggleRawPayloadClicked(object? sender, RoutedEventArgs e)
    {
        var isVisible = DetailRawPayload.IsVisible;
        DetailRawPayload.IsVisible = !isVisible;
        ToggleRawPayloadButton.Content = isVisible ? "Show" : "Hide";
    }

    private void OnCloseDetailClicked(object? sender, RoutedEventArgs e)
    {
        DetailPanel.IsVisible = false;
        TransactionGrid.SelectedItem = null;
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
    }
}

/// <summary>Lightweight row model for the DataGrid binding.</summary>
internal sealed class TransactionRow
{
    public string Id { get; init; } = "";
    public string FccTransactionId { get; init; } = "";
    public string SiteCode { get; init; } = "";
    public int PumpNumber { get; init; }
    public int NozzleNumber { get; init; }
    public string ProductCode { get; init; } = "";
    public long VolumeMicrolitres { get; init; }
    public long AmountMinorUnits { get; init; }
    public string CurrencyCode { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string SyncStatus { get; init; } = "";
    public int UploadAttempts { get; init; }
    public string? LastUploadError { get; init; }
    public string? CorrelationId { get; init; }
    public string? RawPayloadJson { get; init; }

    // Computed display properties
    public string CompletedAtLocal => CompletedAt.LocalDateTime.ToString("g");
    public string VolumeDisplay => (VolumeMicrolitres / 1_000_000.0).ToString("F2");
    public string AmountDisplay => (AmountMinorUnits / 100.0).ToString("F2");
}
