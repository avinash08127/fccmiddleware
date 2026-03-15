using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.App.Views.Pages;

public sealed partial class LogsPage : UserControl, IDisposable
{
    private const int MaxEntries = 100;
    private readonly IServiceProvider? _services;
    private readonly Timer _refreshTimer;

    private string? _eventTypeFilter;
    private string? _searchText;

    public LogsPage()
    {
        InitializeComponent();
        _services = AgentAppContext.ServiceProvider;

        // Auto-refresh every 10 seconds
        _refreshTimer = new Timer(_ => Dispatcher.UIThread.Post(() => _ = LoadLogsAsync()),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    // P-DSK-024: Pause the timer when the page is not visible to avoid unnecessary DB queries
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

    private async Task LoadLogsAsync()
    {
        if (_services is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            IQueryable<AuditLogEntry> query = db.AuditLog.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(_eventTypeFilter))
                query = query.Where(e => e.EventType == _eventTypeFilter);

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                var search = _searchText;
                query = query.Where(e =>
                    (e.PayloadJson != null && e.PayloadJson.Contains(search)) ||
                    e.EventType.Contains(search) ||
                    (e.EntityType != null && e.EntityType.Contains(search)) ||
                    (e.Actor != null && e.Actor.Contains(search)));
            }

            var entries = await query
                .OrderByDescending(e => e.CreatedAt)
                .Take(MaxEntries)
                .Select(e => new LogRow
                {
                    Id = e.Id,
                    EventType = e.EventType,
                    EntityType = e.EntityType,
                    EntityId = e.EntityId,
                    Actor = e.Actor ?? "",
                    PayloadJson = e.PayloadJson,
                    CreatedAt = e.CreatedAt,
                })
                .ToListAsync();

            LogGrid.ItemsSource = entries;
        }
        catch
        {
            // Non-fatal
        }
    }

    // ── Filter handlers ───────────────────────────────────────────────────────

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Don't auto-apply
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ApplyFilters();
    }

    private void OnSearchClicked(object? sender, RoutedEventArgs e) => ApplyFilters();

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        EventTypeFilter.SelectedIndex = 0;
        SearchBox.Text = "";
        _eventTypeFilter = null;
        _searchText = null;
        _ = LoadLogsAsync();
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        _ = LoadLogsAsync();
    }

    private void ApplyFilters()
    {
        _eventTypeFilter = EventTypeFilter.SelectedIndex > 0
            ? (EventTypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()
            : null;

        _searchText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
        _ = LoadLogsAsync();
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
    }
}

internal sealed class LogRow
{
    public long Id { get; init; }
    public string EventType { get; init; } = "";
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string Actor { get; init; } = "";
    public string? PayloadJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    // Computed display properties
    public string TimestampLocal => CreatedAt.LocalDateTime.ToString("G");
    public string EntityDisplay => EntityType is not null
        ? $"{EntityType}/{EntityId?[..Math.Min(8, EntityId?.Length ?? 0)]}"
        : "";
    public string PayloadPreview => PayloadJson is not null
        ? (PayloadJson.Length > 200 ? PayloadJson[..200] + "..." : PayloadJson)
        : "";
}
