using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Advatec;

/// <summary>
/// Fiscalization service that generates TRA-compliant fiscal receipts via an Advatec EFD device (ADV-7.2).
///
/// Used in Scenario A where the primary FCC (DOMS/Radix) controls pumps and provides
/// transaction data, and Advatec is a secondary localhost device used solely for fiscal
/// receipt generation. The flow is:
///   1. Primary FCC transaction is ingested and buffered
///   2. This service POSTs Customer data to Advatec with transaction details
///   3. Advatec generates a TRA fiscal receipt and sends it via webhook
///   4. The fiscal receipt data is attached to the original transaction
///
/// Thread safety: A <see cref="SemaphoreSlim"/> serializes fiscalization requests because
/// Advatec is a single-threaded localhost device processing requests sequentially.
/// Receipt correlation uses a signal-based approach: after Customer data submission,
/// the service waits for the next Receipt webhook to arrive on the queue.
/// </summary>
public sealed class AdvatecFiscalizationService : IFiscalizationService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Default time to wait for a Receipt webhook after Customer data submission.</summary>
    private const int DefaultReceiptTimeoutSeconds = 30;

    private const int DefaultWebhookListenerPort = 8091;
    private const int DefaultDevicePort = 5560;
    private const int DefaultHeartbeatTimeoutMs = 5000;

    private readonly FccConnectionConfig _config;
    private readonly ILogger<AdvatecFiscalizationService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Resolved fiscal receipt timeout from config or default.</summary>
    private TimeSpan ReceiptTimeout => _config.FiscalReceiptTimeoutSeconds is > 0
        ? TimeSpan.FromSeconds(_config.FiscalReceiptTimeoutSeconds.Value)
        : TimeSpan.FromSeconds(DefaultReceiptTimeoutSeconds);

    /// <summary>Resolved heartbeat timeout from config or default.</summary>
    private int HeartbeatTimeoutMs => (_config.ApiRequestTimeoutSeconds ?? 5) * 1000 is > 0 and var ms
        ? ms
        : DefaultHeartbeatTimeoutMs;

    // ── API client ────────────────────────────────────────────────────────────

    private AdvatecApiClient? _apiClient;
    private readonly object _apiClientLock = new();

    // ── Webhook listener ─────────────────────────────────────────────────────

    private AdvatecWebhookListener? _webhookListener;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // ── Receipt queue & signal ────────────────────────────────────────────────
    // When a Receipt webhook arrives, it's parsed and enqueued. The signal
    // semaphore is released to wake up the waiting SubmitForFiscalizationAsync.

    private readonly ConcurrentQueue<AdvatecReceiptData> _receiptQueue = new();
    private readonly SemaphoreSlim _receiptSignal = new(0);

    // ── Serialize fiscalization requests ──────────────────────────────────────
    // Advatec is a sequential localhost device — only one fiscalization at a time.

    private readonly SemaphoreSlim _fiscalizeLock = new(1, 1);

    public AdvatecFiscalizationService(
        FccConnectionConfig config,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdvatecFiscalizationService>();
    }

    // ── IFiscalizationService ─────────────────────────────────────────────────

    public async Task<FiscalizationResult> SubmitForFiscalizationAsync(
        CanonicalTransaction transaction,
        FiscalizationContext context,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        await _fiscalizeLock.WaitAsync(ct);
        try
        {
            // Drain any stale receipts from prior calls
            while (_receiptQueue.TryDequeue(out _)) { }
            while (_receiptSignal.CurrentCount > 0)
                _receiptSignal.Wait(0);

            var client = GetOrCreateApiClient();

            // Convert volume back to litres for Advatec Dose field
            var doseLitres = (decimal)transaction.VolumeMicrolitres / 1_000_000m;
            var custIdType = context.CustomerIdType ?? _config.AdvatecCustIdType ?? 6;

            // Build payment from transaction amount (convert minor → major units)
            var currencyFactor = CurrencyHelper.GetCurrencyFactor(transaction.CurrencyCode);
            var amountMajor = currencyFactor > 0
                ? transaction.AmountMinorUnits / currencyFactor
                : 0m;

            var request = new AdvatecCustomerRequest
            {
                DataType = "Customer",
                Data = new AdvatecCustomerData
                {
                    Pump = transaction.PumpNumber,
                    Dose = Math.Round(doseLitres, 4),
                    CustIdType = custIdType,
                    CustomerId = context.CustomerTaxId ?? "",
                    CustomerName = context.CustomerName ?? "",
                    Payments =
                    [
                        new AdvatecPaymentItem
                        {
                            PaymentType = context.PaymentType ?? "CASH",
                            PaymentAmount = amountMajor,
                        },
                    ],
                },
            };

            _logger.LogInformation(
                "Fiscalization: submitting to Advatec (Pump={Pump}, Dose={Dose}L, CustIdType={CustIdType})",
                transaction.PumpNumber, doseLitres, custIdType);

            var submitResult = await client.SubmitCustomerDataAsync(request, ct);

            if (!submitResult.Success)
            {
                _logger.LogWarning(
                    "Fiscalization submission failed: HTTP {StatusCode}: {Body}",
                    submitResult.StatusCode, submitResult.ResponseBody);

                return new FiscalizationResult(
                    Success: false,
                    ErrorMessage: submitResult.ErrorMessage
                        ?? $"Advatec returned HTTP {submitResult.StatusCode}");
            }

            // Wait for receipt webhook with timeout
            _logger.LogDebug("Fiscalization: waiting for receipt webhook (timeout={Timeout}s)",
                ReceiptTimeout.TotalSeconds);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ReceiptTimeout);

            try
            {
                await _receiptSignal.WaitAsync(timeoutCts.Token);

                if (_receiptQueue.TryDequeue(out var receipt))
                {
                    _logger.LogInformation(
                        "Fiscalization: receipt received — ReceiptCode={ReceiptCode}, TxId={TxId}",
                        receipt.ReceiptCode, receipt.TransactionId);

                    return new FiscalizationResult(
                        Success: true,
                        ReceiptCode: receipt.ReceiptCode,
                        ReceiptVCodeUrl: receipt.ReceiptVCodeUrl,
                        TotalTaxAmount: receipt.TotalTaxAmount);
                }

                return new FiscalizationResult(
                    Success: false,
                    ErrorMessage: "Receipt signal received but queue was empty");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Fiscalization: timed out waiting for receipt ({Timeout}s)",
                    ReceiptTimeout.TotalSeconds);

                return new FiscalizationResult(
                    Success: false,
                    ErrorMessage: $"Timed out waiting for fiscal receipt ({ReceiptTimeout.TotalSeconds}s)");
            }
        }
        finally
        {
            _fiscalizeLock.Release();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        var host = _config.AdvatecDeviceAddress ?? "127.0.0.1";
        var port = _config.AdvatecDevicePort ?? DefaultDevicePort;
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(HeartbeatTimeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var port = _config.AdvatecWebhookListenerPort ?? DefaultWebhookListenerPort;
            try
            {
                _webhookListener = new AdvatecWebhookListener(
                    port,
                    _config.SiteCode,
                    _config.AdvatecWebhookToken,
                    _loggerFactory.CreateLogger<AdvatecWebhookListener>());

                _webhookListener.Start(OnWebhookReceivedAsync, ct);
                _logger.LogInformation(
                    "Fiscalization webhook listener started on port {Port}", port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Fiscalization webhook listener failed to start on port {Port}", port);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Webhook callback: parse Receipt JSON, enqueue the data, and signal the waiter.
    /// </summary>
    private Task OnWebhookReceivedAsync(RawPayloadEnvelope payload, CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<AdvatecWebhookEnvelope>(payload.RawJson, JsonOpts);
            if (envelope?.Data is not null
                && string.Equals(envelope.DataType, "Receipt", StringComparison.OrdinalIgnoreCase))
            {
                _receiptQueue.Enqueue(envelope.Data);
                _receiptSignal.Release();
                _logger.LogDebug("Fiscalization: receipt enqueued from webhook");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fiscalization: failed to parse webhook payload");
        }

        return Task.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private AdvatecApiClient GetOrCreateApiClient()
    {
        if (_apiClient is not null) return _apiClient;

        lock (_apiClientLock)
        {
            if (_apiClient is not null) return _apiClient;

            var host = _config.AdvatecDeviceAddress ?? "127.0.0.1";
            var port = _config.AdvatecDevicePort ?? DefaultDevicePort;
            var httpClient = new HttpClient();
            var apiTimeout = _config.ApiRequestTimeoutSeconds ?? 10;
            httpClient.Timeout = TimeSpan.FromSeconds(apiTimeout);
            _apiClient = new AdvatecApiClient(httpClient, host, port, _logger);
            return _apiClient;
        }
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_webhookListener is not null)
        {
            await _webhookListener.StopAsync();
            _webhookListener.Dispose();
            _webhookListener = null;
        }

        _apiClient = null;

        _initLock.Dispose();
        _fiscalizeLock.Dispose();
        _receiptSignal.Dispose();
    }
}
