using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Buffers unsolicited FCC operational-data events so they can be uploaded later.
/// </summary>
public sealed class OperationalDataEventSink : IFccEventListener
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationalDataEventSink> _logger;
    private int _priceSnapshotRequested;

    public OperationalDataEventSink(
        IServiceScopeFactory scopeFactory,
        ILogger<OperationalDataEventSink> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool ConsumePriceSnapshotRequested() =>
        Interlocked.Exchange(ref _priceSnapshotRequested, 0) == 1;

    public void OnBnaReport(BnaReport report)
    {
        _ = PersistBnaReportAsync(report);
    }

    public void OnPriceChanged(string? priceSetId)
    {
        Interlocked.Exchange(ref _priceSnapshotRequested, 1);
        _logger.LogInformation("FCC price change observed (priceSetId={PriceSetId})", priceSetId ?? "unknown");
    }

    public void OnPumpStatusChanged(int pumpNumber, PumpState newState, string? fccStatusCode) { }

    public void OnTransactionAvailable(TransactionNotification notification) { }

    public void OnFuellingUpdate(int pumpNumber, long volumeMicrolitres, long amountMinorUnits) { }

    public void OnConnectionLost(string reason) { }

    public void OnDispenserInstallData(DispenserInfo info) { }

    public void OnEptInfoReceived(EptTerminalInfo info) { }

    private async Task PersistBnaReportAsync(BnaReport report)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            db.BnaReports.Add(new BufferedBnaReport
            {
                TerminalId = report.TerminalId,
                NotesAccepted = report.NotesAccepted,
                ReportedAtUtc = report.ReportedAtUtc,
                IsSynced = false
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist unsolicited BNA report");
        }
    }
}
