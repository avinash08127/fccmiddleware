using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualLab.Infrastructure.AdvatecSimulator;

/// <summary>
/// HTTP server simulating an Advatec TRA-compliant EFD/VFD device.
/// Accepts Customer data submissions on POST /api/v2/incoming and generates
/// TRA fiscal receipt webhooks after a configurable delay.
/// </summary>
public sealed class AdvatecSimulatorService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AdvatecSimulatorOptions _options;
    private readonly AdvatecSimulatorState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AdvatecSimulatorService> _logger;

    public AdvatecSimulatorService(
        IOptions<AdvatecSimulatorOptions> options,
        AdvatecSimulatorState state,
        IHttpClientFactory httpClientFactory,
        ILogger<AdvatecSimulatorService> logger)
    {
        _options = options.Value;
        _state = state;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.Reset(_options.PumpCount);
        _logger.LogInformation(
            "Advatec EFD simulator starting — port {Port}, pumps {Pumps}",
            _options.Port, _options.PumpCount);

        WebApplication app = BuildApp(stoppingToken);
        await app.RunAsync(stoppingToken);
    }

    private WebApplication BuildApp(CancellationToken stoppingToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [$"--urls=http://0.0.0.0:{_options.Port}"],
        });
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        // POST /api/v2/incoming — Accept Customer data submissions (Advatec protocol)
        app.MapPost("/api/v2/incoming", async (HttpContext httpContext) =>
        {
            AdvatecIncomingRequest? request = await ReadJsonBodyAsync<AdvatecIncomingRequest>(httpContext);
            if (request is null)
            {
                return Results.BadRequest(new { error = "Invalid request body." });
            }

            // Simulate device busy
            if (_state.ErrorMode == AdvatecErrorMode.DeviceBusy)
            {
                _logger.LogWarning("Advatec simulator: device busy (error simulation)");
                return Results.Json(new { error = "Device is busy. Please try again later." }, statusCode: 503);
            }

            if (!string.Equals(request.DataType, "Customer", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"Unsupported DataType '{request.DataType}'. Expected 'Customer'." });
            }

            if (request.Data is null)
            {
                return Results.BadRequest(new { error = "Data field is required." });
            }

            _logger.LogInformation(
                "Advatec Customer submission received — Pump {Pump}, Dose {Dose}, Customer {CustomerId}",
                request.Data.Pump, request.Data.Dose, request.Data.CustomerId);

            AdvatecPendingReceipt pending = new()
            {
                Pump = request.Data.Pump,
                Dose = request.Data.Dose,
                CustIdType = request.Data.CustIdType,
                CustomerId = request.Data.CustomerId ?? string.Empty,
                CustomerName = request.Data.CustomerName ?? string.Empty,
            };

            _state.EnqueuePendingReceipt(pending);

            // Schedule receipt generation after delay (simulates TRA processing)
            if (_state.ErrorMode != AdvatecErrorMode.TraOffline)
            {
                int delayMs = _state.ReceiptDelayOverrideMs ?? _options.ReceiptDelayMs;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delayMs, stoppingToken);
                    await GenerateAndSendReceiptAsync(pending, stoppingToken);
                }, stoppingToken);
            }
            else
            {
                _logger.LogWarning("Advatec simulator: TRA offline — receipt will NOT be generated");
            }

            return Results.Ok(new
            {
                status = "accepted",
                message = "Customer data received successfully.",
                pump = request.Data.Pump,
            });
        });

        return app;
    }

    // -----------------------------------------------------------------------
    // Receipt generation & webhook delivery
    // -----------------------------------------------------------------------

    private async Task GenerateAndSendReceiptAsync(AdvatecPendingReceipt pending, CancellationToken cancellationToken)
    {
        int globalCount = _state.GetNextGlobalCount();
        string transactionId = $"TRSD1INV{globalCount:000}";
        string receiptCode = GenerateReceiptCode();
        string now = DateTimeOffset.UtcNow.AddHours(3).ToString("yyyy-MM-dd"); // EAT
        string time = DateTimeOffset.UtcNow.AddHours(3).ToString("HH:mm:ss");

        // Resolve product from pump config
        AdvatecPumpConfig? pumpConfig = _state.GetPump(pending.Pump);
        string productCode = pumpConfig?.ProductCode ?? "TANGO";
        AdvatecProduct product = _state.Products.TryGetValue(productCode, out AdvatecProduct? p)
            ? p
            : new AdvatecProduct { Code = productCode, Name = productCode, TaxCode = "1", UnitPriceTzs = _options.DefaultUnitPriceTzs };

        decimal quantity = pending.Dose;
        decimal unitPrice = product.UnitPriceTzs;
        decimal amountInclusive = quantity * unitPrice;
        decimal taxRate = _options.VatRate;
        decimal amountExclusive = Math.Round(amountInclusive / (1 + taxRate), 2);
        decimal taxAmount = amountInclusive - amountExclusive;
        decimal itemTaxAmount = taxAmount;

        object receiptPayload = new
        {
            DataType = "Receipt",
            Data = new
            {
                TransactionId = transactionId,
                ReceiptCode = receiptCode,
                Date = now,
                Time = time,
                AmountInclusive = amountInclusive,
                AmountExclusive = amountExclusive,
                TotalTaxAmount = taxAmount,
                Discount = 0m,
                CustIdType = pending.CustIdType,
                CustomerId = pending.CustomerId,
                CustomerName = pending.CustomerName,
                ReceiptVCodeURL = $"https://virtual.tra.go.tz/efdmsrctverify/{receiptCode}_{time.Replace(":", "")}",
                ZNumber = _state.CurrentZNumber,
                DailyCount = _state.CurrentDailyCount,
                GlobalCount = globalCount,
                Items = new[]
                {
                    new
                    {
                        Price = unitPrice,
                        Amount = amountInclusive,
                        TaxCode = product.TaxCode,
                        Quantity = quantity,
                        TaxAmount = itemTaxAmount,
                        Product = product.Name,
                        TaxId = "A-18.00",
                        DiscountAmount = 0m,
                        TaxRate = taxRate * 100,
                    },
                },
                Payments = new[]
                {
                    new
                    {
                        PaymentType = "CASH",
                        PaymentAmount = amountInclusive,
                    },
                },
                Company = _state.Company,
            },
        };

        AdvatecGeneratedReceipt receipt = new()
        {
            TransactionId = transactionId,
            ReceiptCode = receiptCode,
            Pump = pending.Pump,
            Volume = quantity,
            AmountInclusive = amountInclusive,
            ProductCode = productCode,
        };

        _state.AddGeneratedReceipt(receipt);
        _logger.LogInformation(
            "Advatec receipt generated — TransactionId {TransactionId}, ReceiptCode {ReceiptCode}, Amount {Amount} TZS",
            transactionId, receiptCode, amountInclusive);

        await SendWebhookAsync(receiptPayload, receipt, cancellationToken);
    }

    /// <summary>Send Receipt webhook to registered callback URL.</summary>
    internal async Task SendWebhookAsync(object receiptPayload, AdvatecGeneratedReceipt receipt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_state.WebhookCallbackUrl))
        {
            _logger.LogDebug("Advatec webhook skipped — no callback URL registered");
            return;
        }

        string payloadJson = JsonSerializer.Serialize(receiptPayload, JsonOptions);

        // Build webhook URL with optional token parameter
        string webhookUrl = _state.WebhookCallbackUrl;
        if (!string.IsNullOrWhiteSpace(_state.WebhookToken))
        {
            char separator = webhookUrl.Contains('?') ? '&' : '?';
            webhookUrl = $"{webhookUrl}{separator}token={Uri.EscapeDataString(_state.WebhookToken)}";
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("AdvatecSimulator");
            using HttpRequestMessage request = new(HttpMethod.Post, webhookUrl);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Webhook-Event", "receipt");

            if (!string.IsNullOrWhiteSpace(_state.WebhookToken))
            {
                request.Headers.Add("X-Webhook-Token", _state.WebhookToken);
            }

            HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            receipt.WebhookSent = true;
            _logger.LogInformation(
                "Advatec webhook sent to {Url} — {StatusCode}, TransactionId {TransactionId}",
                _state.WebhookCallbackUrl, (int)response.StatusCode, receipt.TransactionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advatec webhook delivery to {Url} failed", _state.WebhookCallbackUrl);
        }
    }

    /// <summary>
    /// Generates and sends a receipt webhook for a directly injected receipt
    /// (via management API, without Customer submission).
    /// </summary>
    internal async Task InjectReceiptAsync(AdvatecInjectReceiptRequest request, CancellationToken cancellationToken)
    {
        int pumpNumber = request.PumpNumber ?? 1;
        decimal dose = request.Volume ?? 10.0m;

        AdvatecPendingReceipt pending = new()
        {
            Pump = pumpNumber,
            Dose = dose,
            CustIdType = request.CustIdType ?? 6,
            CustomerId = request.CustomerId ?? string.Empty,
            CustomerName = request.CustomerName ?? string.Empty,
        };

        await GenerateAndSendReceiptAsync(pending, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string GenerateReceiptCode()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(6);
        return Convert.ToHexStringLower(bytes)[..11];
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext httpContext) where T : class
    {
        try
        {
            return await httpContext.Request.ReadFromJsonAsync<T>();
        }
        catch
        {
            return null;
        }
    }
}

// -----------------------------------------------------------------------
// Internal request contracts (Advatec protocol DTOs)
// -----------------------------------------------------------------------

internal sealed class AdvatecIncomingRequest
{
    public string DataType { get; set; } = string.Empty;
    public AdvatecIncomingData? Data { get; set; }
}

internal sealed class AdvatecIncomingData
{
    public int Pump { get; set; }
    public decimal Dose { get; set; }
    public int CustIdType { get; set; } = 1;
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public List<AdvatecIncomingPayment>? Payments { get; set; }
}

internal sealed class AdvatecIncomingPayment
{
    public string PaymentType { get; set; } = "CASH";
    public decimal PaymentAmount { get; set; }
}
