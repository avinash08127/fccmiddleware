using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.PreAuth;
using VirtualLab.Infrastructure.RadixSimulator;

namespace VirtualLab.Infrastructure.Radix;

/// <summary>
/// Vendor-faithful Radix XML protocol endpoints for the Virtual Lab.
/// Route-based alternative to the dual-port RadixSimulatorService.
/// Auth endpoint: POST /radix/{siteCode}/auth (pre-auth XML)
/// Transaction endpoint: POST /radix/{siteCode}/txn (fetch/ack/mode/product XML)
/// All pre-auth operations bridge to the shared PreAuthSimulationService.
/// </summary>
public static class RadixSimulatorEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapRadixSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /radix/{siteCode}/auth — Pre-auth XML (mirrors auth port P)
        app.MapPost("/radix/{siteCode}/auth", async (
            string siteCode,
            IPreAuthSimulationService preAuthService,
            RadixSimulatorState state,
            IOptions<RadixVendorOptions> vendorOptions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string body = await ReadBodyAsync(httpContext.Request);
            RadixVendorOptions opts = vendorOptions.Value;

            // Validate signature if configured
            if (opts.ValidateSignatures && !RadixXmlHelper.ValidateAuthSignature(body, opts.SharedSecret))
            {
                string sigError = RadixXmlHelper.BuildAuthResponse(251, "SIGNATURE_ERR", opts.SharedSecret);
                return Results.Content(sigError, "application/xml", Encoding.UTF8);
            }

            // Check for injected error
            if (state.TryDequeueError(out RadixErrorInjection? error) && error?.Target == "auth")
            {
                string errXml = RadixXmlHelper.BuildAuthResponse(error.ErrorCode, error.ErrorMessage, opts.SharedSecret);
                return Results.Content(errXml, "application/xml", Encoding.UTF8);
            }

            // Parse AUTH_DATA
            RadixSimAuthRequest authRequest = RadixXmlHelper.ParseAuthRequest(body);

            if (authRequest.Authorize)
            {
                // Create pre-auth
                string preauthId = $"RDX-{authRequest.Token}";
                decimal amount = 0;
                if (decimal.TryParse(authRequest.PresetAmount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal parsedAmount))
                {
                    amount = parsedAmount;
                }

                string requestBody = JsonSerializer.Serialize(new
                {
                    preauthId,
                    correlationId = preauthId,
                    pump = authRequest.Pump,
                    nozzle = authRequest.Fp,
                    amount,
                    customerTaxId = authRequest.CustomerId,
                    customerName = authRequest.CustomerName,
                }, JsonOptions);

                Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["siteCode"] = siteCode,
                    ["preauthId"] = preauthId,
                    ["correlationId"] = preauthId,
                    ["pump"] = authRequest.Pump.ToString(),
                    ["nozzle"] = authRequest.Fp.ToString(),
                    ["amount"] = amount.ToString(),
                };

                if (!string.IsNullOrWhiteSpace(authRequest.CustomerId))
                    fields["customerTaxId"] = authRequest.CustomerId;
                if (!string.IsNullOrWhiteSpace(authRequest.CustomerName))
                    fields["customerName"] = authRequest.CustomerName;

                PreAuthSimulationResponse response = await preAuthService.HandleAsync(
                    new PreAuthSimulationRequest(
                        siteCode,
                        "preauth-create",
                        HttpMethods.Post,
                        httpContext.Request.Path,
                        httpContext.TraceIdentifier,
                        requestBody,
                        fields),
                    cancellationToken);

                if (response.StatusCode is >= 200 and < 300)
                {
                    // Also store in Radix state for transaction correlation
                    state.TryAddPreAuth(authRequest.Pump, new RadixPreAuthSession
                    {
                        PumpNumber = authRequest.Pump,
                        Amount = amount.ToString("F2"),
                        CustomerName = authRequest.CustomerName ?? "",
                        CustomerTaxId = authRequest.CustomerId ?? "",
                    });

                    string okXml = RadixXmlHelper.BuildAuthResponse(0, "SUCCESS", opts.SharedSecret);
                    return Results.Content(okXml, "application/xml", Encoding.UTF8);
                }

                int ackCode = response.StatusCode == 409 ? 258 : 255;
                string failXml = RadixXmlHelper.BuildAuthResponse(ackCode, "REJECTED", opts.SharedSecret);
                return Results.Content(failXml, "application/xml", Encoding.UTF8);
            }
            else
            {
                // Cancel pre-auth (AUTH=FALSE)
                string preauthId = $"RDX-{authRequest.Token}";

                string requestBody = JsonSerializer.Serialize(new
                {
                    preauthId,
                    correlationId = preauthId,
                }, JsonOptions);

                Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["siteCode"] = siteCode,
                    ["preauthId"] = preauthId,
                    ["correlationId"] = preauthId,
                };

                await preAuthService.HandleAsync(
                    new PreAuthSimulationRequest(
                        siteCode,
                        "preauth-cancel",
                        HttpMethods.Post,
                        httpContext.Request.Path,
                        httpContext.TraceIdentifier,
                        requestBody,
                        fields),
                    cancellationToken);

                state.TryRemovePreAuth(authRequest.Pump, out _);

                string cancelXml = RadixXmlHelper.BuildAuthResponse(0, "DEAUTH_OK", opts.SharedSecret);
                return Results.Content(cancelXml, "application/xml", Encoding.UTF8);
            }
        });

        // POST /radix/{siteCode}/txn — Transaction management XML (mirrors txn port P+1)
        app.MapPost("/radix/{siteCode}/txn", async (
            string siteCode,
            RadixSimulatorState state,
            IOptions<RadixVendorOptions> vendorOptions,
            IForecourtSimulationService forecourtService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string body = await ReadBodyAsync(httpContext.Request);
            RadixVendorOptions opts = vendorOptions.Value;

            // Validate signature if configured
            if (opts.ValidateSignatures && !RadixXmlHelper.ValidateTransactionSignature(body, opts.SharedSecret))
            {
                string sigError = RadixXmlHelper.BuildTransactionResponse(251, "SIGNATURE_ERR", state.NextToken(), opts.SharedSecret);
                return Results.Content(sigError, "application/xml", Encoding.UTF8);
            }

            // Check for injected error
            if (state.TryDequeueError(out RadixErrorInjection? error) && error?.Target == "transaction")
            {
                string errXml = RadixXmlHelper.BuildTransactionResponse(error.ErrorCode, error.ErrorMessage, state.NextToken(), opts.SharedSecret);
                return Results.Content(errXml, "application/xml", Encoding.UTF8);
            }

            RadixSimHostRequest hostRequest = RadixXmlHelper.ParseHostRequest(body);
            long token = state.NextToken();

            string responseXml = hostRequest.CmdCode switch
            {
                10 => HandleRequestTransaction(state, token, opts),
                201 => HandleAcknowledge(token, opts),
                20 => HandleModeChange(body, state, token, opts),
                55 => HandleProductRead(state, token, opts),
                _ => RadixXmlHelper.BuildTransactionResponse(255, "BAD_XML_FORMAT", token, opts.SharedSecret),
            };

            return Results.Content(responseXml, "application/xml", Encoding.UTF8);
        });

        // GET /radix/{siteCode}/state — Management: view state (JSON)
        app.MapGet("/radix/{siteCode}/state", (
            string siteCode,
            RadixSimulatorState state) =>
        {
            return Results.Json(new RadixSimStateView(
                siteCode,
                state.GetAllPreAuths(),
                state.PeekAllTransactions(),
                state.GetAllProducts(),
                state.Mode.ToString(),
                state.PendingErrorCount), JsonOptions);
        });

        // POST /radix/{siteCode}/inject-transaction — Management: add transaction to FIFO (JSON)
        app.MapPost("/radix/{siteCode}/inject-transaction", (
            string siteCode,
            RadixSimInjectTransactionRequest request,
            RadixSimulatorState state) =>
        {
            RadixSimulatedTransaction transaction = new()
            {
                PumpNumber = request.PumpNumber ?? 1,
                NozzleNumber = request.NozzleNumber ?? 1,
                ProductId = request.ProductId ?? 1,
                ProductName = request.ProductName ?? "UNLEADED 95",
                Volume = request.Volume ?? "10.00",
                Amount = request.Amount ?? "18.50",
                Price = request.Price ?? "1.850",
            };

            state.EnqueueTransaction(transaction);

            return Results.Created($"/radix/{siteCode}/state", new
            {
                message = "Transaction injected via vendor endpoint.",
                transactionId = transaction.Id,
                bufferDepth = state.TransactionCount,
            });
        });

        // POST /radix/{siteCode}/reset — Management: reset state (JSON)
        app.MapPost("/radix/{siteCode}/reset", (
            string siteCode,
            RadixSimulatorState state,
            IOptions<RadixSimulatorOptions> existingOptions) =>
        {
            state.Reset(existingOptions.Value.PumpCount);

            return Results.Ok(new
            {
                message = $"Radix simulator state reset for site '{siteCode}'.",
                mode = state.Mode.ToString(),
                bufferDepth = state.TransactionCount,
                preAuthCount = state.PreAuthCount,
            });
        });

        return app;
    }

    // -----------------------------------------------------------------------
    // Transaction command handlers
    // -----------------------------------------------------------------------

    private static string HandleRequestTransaction(RadixSimulatorState state, long token, RadixVendorOptions opts)
    {
        if (state.TryDequeueTransaction(out RadixSimulatedTransaction? transaction) && transaction is not null)
        {
            return RadixXmlHelper.BuildTransactionResponse(201, "DATA", token, opts.SharedSecret,
                transaction, opts.DefaultUsnCode);
        }

        return RadixXmlHelper.BuildTransactionResponse(205, "NO_DATA", token, opts.SharedSecret);
    }

    private static string HandleAcknowledge(long token, RadixVendorOptions opts)
    {
        return RadixXmlHelper.BuildTransactionResponse(201, "ACK_OK", token, opts.SharedSecret);
    }

    private static string HandleModeChange(string body, RadixSimulatorState state, long token, RadixVendorOptions opts)
    {
        string modeValue = RadixXmlHelper.ExtractAttributeValue(body, "MODE");

        if (string.Equals(modeValue, "UNSOLICITED", StringComparison.OrdinalIgnoreCase))
        {
            state.Mode = RadixOperatingMode.Unsolicited;
            string callbackUrl = RadixXmlHelper.ExtractAttributeValue(body, "CALLBACK_URL");
            if (!string.IsNullOrWhiteSpace(callbackUrl))
            {
                state.UnsolicitedCallbackUrl = callbackUrl;
            }
        }
        else
        {
            state.Mode = RadixOperatingMode.OnDemand;
        }

        return RadixXmlHelper.BuildTransactionResponse(201, "MODE_OK", token, opts.SharedSecret);
    }

    private static string HandleProductRead(RadixSimulatorState state, long token, RadixVendorOptions opts)
    {
        return RadixXmlHelper.BuildProductResponse(token, opts.SharedSecret, state.GetAllProducts());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using StreamReader reader = new(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
