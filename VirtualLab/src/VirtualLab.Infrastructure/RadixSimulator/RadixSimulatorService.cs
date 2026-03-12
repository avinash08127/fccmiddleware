using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualLab.Infrastructure.RadixSimulator;

/// <summary>
/// Dual-port HTTP server simulating a Radix FDC controller.
/// Port P+1: transaction management (CMD_CODE 10, 20, 55, 201).
/// Port P:   external authorization / pre-auth (AUTH_DATA XML).
/// </summary>
public sealed class RadixSimulatorService : BackgroundService
{
    private readonly RadixSimulatorOptions _options;
    private readonly RadixSimulatorState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RadixSimulatorService> _logger;

    public RadixSimulatorService(
        IOptions<RadixSimulatorOptions> options,
        RadixSimulatorState state,
        IHttpClientFactory httpClientFactory,
        ILogger<RadixSimulatorService> logger)
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
            "Radix FDC simulator starting — transaction port {TxPort}, auth port {AuthPort}, pumps {Pumps}",
            _options.TransactionPort, _options.AuthPort, _options.PumpCount);

        WebApplication transactionApp = BuildTransactionApp(stoppingToken);
        WebApplication authApp = BuildAuthApp(stoppingToken);

        await Task.WhenAll(
            transactionApp.RunAsync(stoppingToken),
            authApp.RunAsync(stoppingToken));
    }

    // -----------------------------------------------------------------------
    // Transaction port (P+1)
    // -----------------------------------------------------------------------

    private WebApplication BuildTransactionApp(CancellationToken stoppingToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [$"--urls=http://0.0.0.0:{_options.TransactionPort}"],
        });
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.MapPost("/", async (HttpContext httpContext) =>
        {
            string body = await ReadBodyAsync(httpContext.Request);
            _logger.LogDebug("Radix TX received: {Body}", body);

            if (!ValidateTransactionSignature(body))
            {
                string sigError = BuildTransactionResponse(251, "SIGNATURE_ERR", _state.NextToken(), null);
                return Results.Content(sigError, "application/xml", Encoding.UTF8);
            }

            // Check for injected error
            if (_state.TryDequeueError(out RadixErrorInjection? error) && error?.Target == "transaction")
            {
                string errXml = BuildTransactionResponse(error.ErrorCode, error.ErrorMessage, _state.NextToken(), null);
                return Results.Content(errXml, "application/xml", Encoding.UTF8);
            }

            int cmdCode = ExtractCmdCode(body);
            string responseXml = cmdCode switch
            {
                10 => HandleRequestTransaction(),
                201 => HandleAcknowledge(body),
                20 => HandleModeChange(body),
                55 => HandleProductRead(),
                _ => BuildTransactionResponse(255, "BAD_XML_FORMAT", _state.NextToken(), null),
            };

            return Results.Content(responseXml, "application/xml", Encoding.UTF8);
        });

        return app;
    }

    /// <summary>CMD_CODE=10 — Request next transaction from FIFO buffer.</summary>
    private string HandleRequestTransaction()
    {
        long token = _state.NextToken();

        if (_state.TryDequeueTransaction(out RadixSimulatedTransaction? transaction) && transaction is not null)
        {
            _logger.LogInformation("Radix TX dequeued transaction {Id} for pump {Pump}", transaction.Id, transaction.PumpNumber);
            return BuildTransactionResponse(201, "DATA", token, transaction);
        }

        return BuildTransactionResponse(205, "NO_DATA", token, null);
    }

    /// <summary>CMD_CODE=201 — Acknowledge receipt of a transaction.</summary>
    private string HandleAcknowledge(string body)
    {
        long token = _state.NextToken();
        string ackToken = ExtractAttributeValue(body, "TOKEN");
        _logger.LogDebug("Radix TX ACK received for token {Token}", ackToken);
        return BuildTransactionResponse(201, "ACK_OK", token, null);
    }

    /// <summary>CMD_CODE=20 — Switch between ON_DEMAND and UNSOLICITED mode.</summary>
    private string HandleModeChange(string body)
    {
        long token = _state.NextToken();
        string modeValue = ExtractAttributeValue(body, "MODE");

        if (string.Equals(modeValue, "UNSOLICITED", StringComparison.OrdinalIgnoreCase))
        {
            _state.Mode = RadixOperatingMode.Unsolicited;
            string callbackUrl = ExtractAttributeValue(body, "CALLBACK_URL");
            if (!string.IsNullOrWhiteSpace(callbackUrl))
            {
                _state.UnsolicitedCallbackUrl = callbackUrl;
            }

            _logger.LogInformation("Radix TX mode changed to UNSOLICITED, callback: {Url}", _state.UnsolicitedCallbackUrl);
        }
        else
        {
            _state.Mode = RadixOperatingMode.OnDemand;
            _logger.LogInformation("Radix TX mode changed to ON_DEMAND");
        }

        return BuildTransactionResponse(201, "MODE_OK", token, null);
    }

    /// <summary>CMD_CODE=55 — Product read / heartbeat.</summary>
    private string HandleProductRead()
    {
        long token = _state.NextToken();
        IReadOnlyDictionary<int, RadixProductEntry> products = _state.GetAllProducts();

        StringBuilder productElements = new();
        foreach (KeyValuePair<int, RadixProductEntry> kvp in products)
        {
            productElements.Append(
                $"""<PRODUCT ID="{kvp.Key}" NAME="{kvp.Value.Name}" PRICE="{kvp.Value.Price}" />""");
        }

        string tableContent =
            $"""<TABLE><ANS RESP_CODE="201" RESP_MSG="DATA" TOKEN="{token}" />{productElements}</TABLE>""";

        string signature = ComputeSha1(tableContent + _options.SharedSecret);
        return $"""<?xml version="1.0" encoding="UTF-8"?><FDC_RESP>{tableContent}<SIGNATURE>{signature}</SIGNATURE></FDC_RESP>""";
    }

    // -----------------------------------------------------------------------
    // Auth port (P)
    // -----------------------------------------------------------------------

    private WebApplication BuildAuthApp(CancellationToken stoppingToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [$"--urls=http://0.0.0.0:{_options.AuthPort}"],
        });
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.MapPost("/", async (HttpContext httpContext) =>
        {
            string body = await ReadBodyAsync(httpContext.Request);
            _logger.LogDebug("Radix AUTH received: {Body}", body);

            if (!ValidateAuthSignature(body))
            {
                string sigError = BuildAuthResponse(251, "SIGNATURE_ERR");
                return Results.Content(sigError, "application/xml", Encoding.UTF8);
            }

            // Check for injected error
            if (_state.TryDequeueError(out RadixErrorInjection? error) && error?.Target == "auth")
            {
                string errXml = BuildAuthResponse(error.ErrorCode, error.ErrorMessage);
                return Results.Content(errXml, "application/xml", Encoding.UTF8);
            }

            string responseXml = HandleAuthRequest(body);
            return Results.Content(responseXml, "application/xml", Encoding.UTF8);
        });

        return app;
    }

    /// <summary>Handle AUTH_DATA XML for pre-authorization.</summary>
    private string HandleAuthRequest(string body)
    {
        int pumpNumber = ParseIntAttribute(body, "PUMP_ADDR", 1);
        string amount = ExtractAttributeValue(body, "AMO");
        string customerName = ExtractAttributeValue(body, "CUST_NAME");
        string customerTaxId = ExtractAttributeValue(body, "CUST_ID");

        if (pumpNumber < 1 || pumpNumber > _options.PumpCount)
        {
            _logger.LogWarning("Radix AUTH: invalid pump {Pump}", pumpNumber);
            return BuildAuthResponse(258, "PUMP_NOT_READY");
        }

        RadixPreAuthSession session = new()
        {
            PumpNumber = pumpNumber,
            Amount = string.IsNullOrWhiteSpace(amount) ? "50.00" : amount,
            CustomerName = customerName,
            CustomerTaxId = customerTaxId,
        };

        if (_state.TryAddPreAuth(pumpNumber, session))
        {
            _logger.LogInformation("Radix AUTH: pre-auth created for pump {Pump}, amount {Amount}", pumpNumber, session.Amount);
            return BuildAuthResponse(0, "AUTH_OK");
        }

        _logger.LogWarning("Radix AUTH: pump {Pump} already has active pre-auth", pumpNumber);
        return BuildAuthResponse(258, "PUMP_NOT_READY");
    }

    // -----------------------------------------------------------------------
    // Unsolicited push
    // -----------------------------------------------------------------------

    /// <summary>
    /// Push a transaction to the registered unsolicited callback URL.
    /// Called internally when mode is UNSOLICITED and a transaction is injected.
    /// </summary>
    internal async Task PushUnsolicitedTransactionAsync(RadixSimulatedTransaction transaction, CancellationToken cancellationToken)
    {
        if (_state.Mode != RadixOperatingMode.Unsolicited || string.IsNullOrWhiteSpace(_state.UnsolicitedCallbackUrl))
        {
            return;
        }

        string responseXml = BuildTransactionResponse(30, "UNSOLICITED", _state.NextToken(), transaction);

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("RadixSimulator");
            using StringContent content = new(responseXml, Encoding.UTF8, "application/xml");
            HttpResponseMessage response = await client.PostAsync(_state.UnsolicitedCallbackUrl, content, cancellationToken);
            _logger.LogInformation(
                "Radix unsolicited push to {Url} returned {StatusCode}",
                _state.UnsolicitedCallbackUrl, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Radix unsolicited push to {Url} failed", _state.UnsolicitedCallbackUrl);
        }
    }

    // -----------------------------------------------------------------------
    // XML response builders
    // -----------------------------------------------------------------------

    private string BuildTransactionResponse(int respCode, string respMsg, long token, RadixSimulatedTransaction? transaction)
    {
        StringBuilder table = new();
        table.Append($"""<TABLE><ANS RESP_CODE="{respCode}" RESP_MSG="{respMsg}" TOKEN="{token}" />""");

        if (transaction is not null)
        {
            table.Append($"""<TRN AMO="{transaction.Amount}" EFD_ID="{transaction.EfdId}" """);
            table.Append($"""FDC_DATE="{transaction.FdcDate}" FDC_TIME="{transaction.FdcTime}" """);
            table.Append($"""FDC_NAME="RADIX_SIM" FDC_NUM="{_options.UsnCode}" """);
            table.Append($"""FDC_PROD="{transaction.ProductId}" FDC_PROD_NAME="{transaction.ProductName}" """);
            table.Append($"""FDC_SAVE_NUM="{transaction.SaveNum}" FDC_TANK="1" """);
            table.Append($"""FP="{transaction.PumpNumber}" NOZ="{transaction.NozzleNumber}" """);
            table.Append($"""PRICE="{transaction.Price}" PUMP_ADDR="{transaction.PumpNumber}" """);
            table.Append($"""RDG_DATE="{transaction.FdcDate}" RDG_TIME="{transaction.FdcTime}" """);
            table.Append($"""RDG_ID="{transaction.Id}" RDG_INDEX="1" """);
            table.Append($"""RDG_PROD="{transaction.ProductId}" RDG_SAVE_NUM="{transaction.SaveNum}" """);
            table.Append($"""REG_ID="1" ROUND_TYPE="0" VOL="{transaction.Volume}" />""");
        }

        table.Append("</TABLE>");

        string tableContent = table.ToString();
        string signature = ComputeSha1(tableContent + _options.SharedSecret);

        return $"""<?xml version="1.0" encoding="UTF-8"?><FDC_RESP>{tableContent}<SIGNATURE>{signature}</SIGNATURE></FDC_RESP>""";
    }

    private string BuildAuthResponse(int ackCode, string ackMsg)
    {
        string now = DateTimeOffset.UtcNow.ToString("dd/MM/yyyy");
        string time = DateTimeOffset.UtcNow.ToString("HH:mm:ss");

        string fdcAckContent =
            $"""<FDCACK><DATE>{now}</DATE><TIME>{time}</TIME><ACKCODE>{ackCode}</ACKCODE><ACKMSG>{ackMsg}</ACKMSG></FDCACK>""";

        string signature = ComputeSha1(fdcAckContent + _options.SharedSecret);

        return $"""<?xml version="1.0" encoding="UTF-8"?><FDCMS>{fdcAckContent}<FDCSIGNATURE>{signature}</FDCSIGNATURE></FDCMS>""";
    }

    // -----------------------------------------------------------------------
    // Signature validation
    // -----------------------------------------------------------------------

    private bool ValidateTransactionSignature(string xml)
    {
        string? reqContent = ExtractRawElement(xml, "REQ");
        string? signature = ExtractRawElementText(xml, "SIGNATURE");

        if (reqContent is null || signature is null)
        {
            return true; // No signature to validate — allow for simple test payloads
        }

        string expected = ComputeSha1(reqContent + _options.SharedSecret);
        return string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateAuthSignature(string xml)
    {
        string? authDataContent = ExtractRawElement(xml, "AUTH_DATA");
        string? signature = ExtractRawElementText(xml, "FDCSIGNATURE");

        if (authDataContent is null || signature is null)
        {
            return true; // No signature to validate — allow for simple test payloads
        }

        string expected = ComputeSha1(authDataContent + _options.SharedSecret);
        return string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ComputeSha1(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = SHA1.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static int ExtractCmdCode(string xml)
    {
        string value = ExtractAttributeValue(xml, "CMD_CODE");
        return int.TryParse(value, out int code) ? code : -1;
    }

    private static string ExtractAttributeValue(string xml, string attributeName)
    {
        string searchPattern = $"{attributeName}=\"";
        int startIdx = xml.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return string.Empty;

        startIdx += searchPattern.Length;
        int endIdx = xml.IndexOf('"', startIdx);
        if (endIdx < 0) return string.Empty;

        return xml[startIdx..endIdx];
    }

    private static int ParseIntAttribute(string xml, string attributeName, int defaultValue)
    {
        string value = ExtractAttributeValue(xml, attributeName);
        return int.TryParse(value, out int result) ? result : defaultValue;
    }

    private static string? ExtractRawElement(string xml, string tagName)
    {
        string openTag = $"<{tagName}";
        string closeTag = $"</{tagName}>";
        int startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        int endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;
        return xml[startIdx..(endIdx + closeTag.Length)];
    }

    private static string? ExtractRawElementText(string xml, string tagName)
    {
        string openTag = $"<{tagName}>";
        string closeTag = $"</{tagName}>";
        int startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        int endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;
        return xml[(startIdx + openTag.Length)..endIdx].Trim();
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using StreamReader reader = new(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
