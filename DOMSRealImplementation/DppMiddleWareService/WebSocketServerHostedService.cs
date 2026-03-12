using DPPMiddleware.ForecourtTcpWorker;
using DPPMiddleware.Interface;
using DPPMiddleware.Models;
using DppMiddleWareService.Models;
using Fleck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DppMiddleWareService
{
    public class WebSocketServerHostedService : IHostedService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<WebSocketServerHostedService> _logger;
        private readonly ConcurrentDictionary<IWebSocketConnection, bool> _clients = new();
        private WebSocketServer? _server;
        private CancellationTokenSource? _cts;
        private readonly IConfiguration _configuration;
        private ForecourtClient? _forecourtClient;
        public IEnumerable<IWebSocketConnection> Clients => _clients.Keys;

        public WebSocketServerHostedService(IConfiguration configuration, IServiceProvider services, ILogger<WebSocketServerHostedService> logger, ForecourtClient forecourtClient)
        {
            _configuration = configuration;
            _services = services;
            _logger = logger;
            _forecourtClient = forecourtClient;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return WebSocketLoopAsync(_cts.Token);
        }

        private X509Certificate2 LoadOrInstallCertificate()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "C:\\certificates1\\New folder\\server_20251212_V1.pfx");
            // var path = Path.Combine(AppContext.BaseDirectory, "C:\\certificates1\\New folder\\server_20251212_V1.pfx");
            const string password = "5202ygreneamup!"; //5202ygreneamup!, Brain@123

            if (!File.Exists(path))
                throw new FileNotFoundException($"Certificate not found: {path}");

            _logger.LogInformation("🔐 Loading certificate from {Path}", path);

            var cert = new X509Certificate2(path, password,
                X509KeyStorageFlags.Exportable |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.UserKeySet);

            try
            {
                using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                rootStore.Open(OpenFlags.ReadWrite);

                bool alreadyInstalled = false;
                foreach (var existing in rootStore.Certificates)
                {
                    if (existing.Thumbprint?.Equals(cert.Thumbprint, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        alreadyInstalled = true;
                        break;
                    }
                }

                if (!alreadyInstalled)
                {
                    _logger.LogInformation("🪪 Installing certificate into Trusted Root store...");
                    rootStore.Add(cert);
                }

                rootStore.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Could not install certificate into Trusted Root. Continuing anyway.");
            }

            return cert;
        }
        private List<string> GetLocalIPv4Addresses()
        {
            var addresses = new List<string>();

            try
            {
                // Get hostname
                var hostName = Dns.GetHostName();
                var hostEntry = Dns.GetHostEntry(hostName);

                // Get all IPv4 addresses
                foreach (var ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        addresses.Add(ip.ToString());
                    }
                }

                // Also get network interface addresses (more reliable)
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                var addr = ip.Address.ToString();
                                if (!addresses.Contains(addr))
                                {
                                    addresses.Add(addr);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting local IP addresses");
            }

            return addresses;
        }

        private Task WebSocketLoopAsync(CancellationToken stoppingToken)
        {
            // 🔹 Load certificate using old working method
            var cert = LoadOrInstallCertificate();

            var bindAddress = _configuration["WebSocketServer:Host"];
            var port = int.Parse(_configuration["WebSocketServer:Port"]);

            //const int port = 8443;
            //var bindAddress = "10.175.1.2"; // Listen on all interfaces, old - 172.18.209.11
            //var bindAddress = "wss.middleware.localnet"; // Listen on all interfaces, old - 172.18.209.11

            _server = new WebSocketServer($"wss://{bindAddress}:{port}")
            {
                Certificate = cert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };

            _server.Start(connection =>
            {
                connection.OnOpen = async () =>
                {
                    var fullPath = connection.ConnectionInfo.Path ?? "";
                    var path = fullPath;
                    var queryString = "";

                    var queryIndex = fullPath.IndexOf('?');
                    if (queryIndex >= 0)
                    {
                        path = fullPath.Substring(0, queryIndex);
                        queryString = fullPath.Substring(queryIndex + 1);
                    }

                    _logger.LogInformation("✅ Client connected: {Client} ({Path})", connection.ConnectionInfo.ClientIpAddress, fullPath);
                    _clients.TryAdd(connection, true);

                    using var scope = _services.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ITransactionService>();

                    // pass server BroadcastToAllClients as callback
                    var socket = new FleckWebSocketAdapter(connection, service, BroadcastToAllClients, OnClientDisconnected, _forecourtClient, _services, this);

                    //// Pass a closure callback to adapter
                    try
                    {

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in WebSocket handler for {Path}", path);
                    }
                };

                connection.OnClose = () =>
                {
                    OnClientDisconnected(connection);
                };
            });

            // Get and log local IP addresses
            var localIps = GetLocalIPv4Addresses();
            _logger.LogInformation("WebSocket WSS server started on wss://{BindAddress}:{Port}", bindAddress, port);
            _logger.LogInformation("Clients can connect using any of these addresses:");

            if (localIps.Count > 0)
            {
                foreach (var ip in localIps)
                {
                    _logger.LogInformation("wss://{Ip}:{Port}", ip, port);
                }
            }
            else
            {
                _logger.LogWarning("Could not detect local IP addresses. Use the server's IP address manually.");
            }

            _logger.LogInformation("wss://localhost:{Port} (for local connections)", port);

            return Task.CompletedTask;
        }


        /// <summary>
        /// Broadcast payload typed as (type, data) to all connected clients.
        /// </summary>
        public async Task BroadcastToAllClients(string type, object data)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = type,
                data = ConvertToSnakeCase(data)
            });

            foreach (var client in _clients.Keys)
            {
                if (client.IsAvailable)
                    await client.Send(payload);
            }
        }
        private Dictionary<string, object> ConvertToSnakeCase(object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            return dict.ToDictionary(
                x => ToSnakeCase(x.Key),
                x => x.Value
            );
        }

        private string ToSnakeCase(string str)
        {
            return Regex.Replace(str, "([a-z])([A-Z])", "$1_$2").ToLower();
        }



        private void OnClientDisconnected(IWebSocketConnection connection)
        {
            if (_clients.TryRemove(connection, out _))
            {
                _logger.LogInformation("❌ Client disconnected: {Client}", connection.ConnectionInfo.ClientIpAddress);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping WebSocket server...");
            _cts?.Cancel();

            foreach (var client in _clients.Keys)
            {
                client.Close();
            }

            _server?.Dispose();
            return Task.CompletedTask;
        }
    }


    public class FleckWebSocketAdapter : WebSocket
    {
        private readonly IWebSocketConnection _connection;
        private readonly Channel<ArraySegment<byte>> _receiveChannel = Channel.CreateUnbounded<ArraySegment<byte>>();
        private readonly Action<IWebSocketConnection>? _onClosed;
        private bool _isClosed;
        private ITransactionService _service;
        private Timer? _broadcastTimer;
        private IServiceProvider _serviceprovider;
        private ForecourtClient _forecourt;
        private readonly WebSocketServerHostedService _serverRef;
        public event Action<int>? OnFpStatusUpdated;

        public FleckWebSocketAdapter(IWebSocketConnection connection, ITransactionService service, Func<string, object, Task>? broadcastCallback = null, Action<IWebSocketConnection>? onClosed = null, ForecourtClient forecourtClient = null, IServiceProvider serviceProvider = null, WebSocketServerHostedService serverRef = null)
        {
            _connection = connection;
            _onClosed = onClosed;
            _service = service;
            _forecourt = forecourtClient;
            _serviceprovider = serviceProvider;
            _serverRef = serverRef ?? throw new ArgumentNullException(nameof(serverRef));


            _connection.OnMessage = async message =>
            {
                Console.WriteLine($"Received WebSocket message: {message}");

                try
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
                    if (data == null)
                    {
                        await SendError("Invalid message format");
                        await service.HandleWebSocketRequest(_connection, "", null, null, null, "");
                        return;
                    }
                    string mode = data["mode"]?.ToString()?.ToLower() ?? "";
                    //_logger.LogInformation("📩 Received mode={Mode} from {IP}", mode, connection.ConnectionInfo.ClientIpAddress);

                    string? type = data.TryGetValue("mode", out var t) ? t?.ToString()?.ToLower() ?? "latest" : "latest";
                    //string type = mode;

                    switch (mode)
                    {
                        case "latest":
                            //await HandleAllLatest(service, connection, data);
                            //await HandleLatest(service, connection, data);
                            int? fpId = null;
                            if (data.TryGetValue("pump_id", out var f) && int.TryParse(f?.ToString(), out var pumpValue))
                                fpId = pumpValue;

                            int? nozzleId = null;
                            if (data.TryGetValue("nozzle_id", out var n) && int.TryParse(n?.ToString(), out var nozzleValue))
                                nozzleId = nozzleValue;

                            string? referenceId = data.TryGetValue("emp", out var r) ? r?.ToString() ?? "" : "";

                            DateTime? createdDate = null;
                            if (data.TryGetValue("CreatedDate", out var cd) && DateTime.TryParse(cd?.ToString(), out var dt))
                                createdDate = dt;

                            await service.HandleWebSocketRequest(_connection, "attendant", fpId, nozzleId, createdDate, referenceId);
                            break;

                        case "all":
                            await HandleAll(service, connection);
                            break;

                        case "manager_update":
                            await HandleUpdate(service, connection, data);
                            // OnFpStatusUpdated?.Invoke(3);
                            // _forecourt.CheckAndApplyPumpLimitAsync(0, _serviceprovider);

                            break;

                        case "add_transaction":
                            await HandleAddTransaction(service, connection, data);
                            break;


                        case "FuelPumpStatus":
                            await HandleFuelPumpStatus(service, connection, data);
                            break;


                        case "fp_unblock":
                            await HandleFpUnblock(service, connection, data);
                            break;

                        case "attendant_pump_count_update":
                            {
                                if (!data.TryGetValue("data", out var payload))
                                    break;

                                var json = payload.ToString();
                                var items = JsonSerializer.Deserialize<List<AttendantPumpCountUpdate>>(json);

                                if (items == null || items.Count == 0)
                                    break;

                                foreach (var item in items)
                                {
                                    bool updated = await service.UpdateAttendantPumpCountAsync(item);

                                    if (updated && connection.IsAvailable)
                                    {
                                        await connection.Send(JsonSerializer.Serialize(new
                                        {
                                            type = "attendant_pump_count_update_ack",
                                            data = new
                                            {
                                                pump_number = item.PumpNumber,
                                                emp_tag_no = item.EmpTagNo,
                                                max_limit = item.NewMaxTransaction,
                                                status = "updated"
                                            }
                                        }));
                                    }
                                }

                                break;
                            }
                        case "manager_manual_update":
                            await HandleIsDisCard(service, connection, data);
                            break;

                        default:
                            if (connection.IsAvailable)
                                await connection.Send(JsonSerializer.Serialize(new { status = "error", message = $"Unknown mode '{mode}'" }));
                            break;
                    }
                    if (type == "attendant_update")
                    // Console.WriteLine($"Received WebSocket message: {message}");
                    {
                        string? transactionId = data.TryGetValue("transaction_id", out var tx) ? tx?.ToString() : null;

                        if (data.TryGetValue("update", out var updateObj) && updateObj is JsonElement updateElement)
                        {
                            string? orderUuid = updateElement.TryGetProperty("order_uuid", out var ou) ? ou.GetString() : null;
                            int orderId = updateElement.TryGetProperty("order_id", out var ouid)
                            ? (ouid.ValueKind == JsonValueKind.Number
                            ? ouid.GetInt32()
                            : (int.TryParse(ouid.GetString(), out var parsed) ? parsed : 0))
                            : 0;

                            string? state = updateElement.TryGetProperty("state", out var StateResult) ? StateResult.GetString() : null;

                            //bool? addToCart = null;
                            //if (updateElement.TryGetProperty("add_to_cart", out var atc))
                            //{
                            //    if (atc.ValueKind == JsonValueKind.True || atc.ValueKind == JsonValueKind.False)
                            //        addToCart = atc.GetBoolean();
                            //}


                            bool? addToCart = null;

                            try
                            {
                                if (updateElement.TryGetProperty("add_to_cart", out var atc))
                                {
                                    if (atc.ValueKind == JsonValueKind.True || atc.ValueKind == JsonValueKind.False)
                                    {
                                        addToCart = atc.GetBoolean();
                                    }
                                    else
                                    {
                                        throw new Exception($"Invalid value type for add_to_cart: {atc.ValueKind}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ Error parsing add_to_cart: {ex.Message}");
                            }

                            string? paymentId = null;
                            if (updateElement.TryGetProperty("payment_id", out var paymentIdVal))
                            {
                                paymentId = paymentIdVal.ValueKind switch
                                {
                                    JsonValueKind.String => paymentIdVal.GetString(),
                                    JsonValueKind.Null => null,
                                    _ => paymentIdVal.ToString()
                                };
                            }

                            if (addToCart.HasValue)
                            {
                                Console.WriteLine($"🛒 AddToCart received = {addToCart.Value} for TxID = {transactionId}");

                                bool updated = await service.UpdateAddToCartAsync(transactionId, addToCart.Value, paymentId);

                                if (updated)
                                {
                                    Console.WriteLine($"✅ AddToCart updated in DB for {transactionId} = {addToCart.Value}");

                                    // Fetch the updated transaction to include all columns
                                    var allTx = await service.GetAllTransactions(); // should return List<PumpTransactions>
                                    var txList = allTx.Cast<PumpTransactions>().ToList(); // ensure proper type
                                    var updatedTx = txList.FirstOrDefault(t => t.TransactionId == transactionId);

                                    if (updatedTx != null)
                                    {
                                        // Broadcast full object
                                        var payload = JsonSerializer.Serialize(new
                                        {
                                            type = "transaction_update",
                                            data = updatedTx
                                        });

                                        foreach (var client in _serverRef.Clients)
                                        {
                                            if (client.IsAvailable)
                                                await client.Send(payload);
                                        }
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"⚠️ Failed to update AddToCart for {transactionId}");
                                }
                            }



                            if (!string.IsNullOrEmpty(transactionId) && !string.IsNullOrEmpty(orderUuid))
                            {
                                await service.UpdateOrderUuidAsync(transactionId, orderUuid, orderId, state);
                                Console.WriteLine($"✅ Updated OrderUuid for {transactionId} = {orderUuid}, paymentId={paymentId}");
                                // Fetch updated single record
                                // update in-memory object instead of calling DB
                                var allTx = await service.GetAllTransactions();
                                var jsonString = JsonSerializer.Serialize(allTx);
                                var txList = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(jsonString);

                                // Find matching transaction inside list
                                var updatedTx = txList.FirstOrDefault(t => t["transaction_id"]?.ToString() == transactionId);

                                if (updatedTx != null)
                                {
                                    updatedTx["order_uuid"] = orderUuid;
                                    updatedTx["order_id"] = orderId;
                                    updatedTx["state"] = state;
                                    updatedTx["payment_id"] = paymentId;
                                }


                                var payload = JsonSerializer.Serialize(new
                                {
                                    type = "transaction_update",
                                    data = updatedTx
                                });

                                // broadcast to all connected clients
                                foreach (var client in _serverRef.Clients)
                                {
                                    if (client.IsAvailable)
                                        await client.Send(payload);
                                }
                            }
                            else
                            {
                                Console.WriteLine("⚠️ Missing transaction_id or order_uuid in update message.");
                            }
                        }
                        return; // stop further processing
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ WebSocket message handling error: {ex.Message}");
                    await SendError("Internal server error");
                }
            };

            // Ensure closure callback triggers when Fleck connection closes
            _connection.OnClose = () =>
            {
                _isClosed = true;
                _onClosed?.Invoke(connection);
            };

            StartBroadcastingAtIntervals(TimeSpan.FromSeconds(3)); // Set the interval here

        }
        private void StartBroadcastingAtIntervals(TimeSpan interval)
        {
            _broadcastTimer = new Timer(async _ =>
            {
                await BroadcastFuelPumpStatusUpdateAsync();
            }, null, TimeSpan.Zero, interval);
        }

        public async Task BroadcastFuelPumpStatusUpdateAsync()
        {
            try
            {
                var latestFuelPumpStatus = await _service.GetAllFpStatus();

                if (latestFuelPumpStatus != null && latestFuelPumpStatus.Count > 0)
                {

                    foreach (var item in latestFuelPumpStatus)
                    {
                        string payload = JsonSerializer.Serialize(item, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = false
                        });

                        await _connection.Send(payload);

                        Console.WriteLine($"Broadcasted item: {payload}");
                    }
                }
                else
                {
                    Console.WriteLine("No new fuel pump status found to broadcast.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to broadcast fuel pump status update.");
            }
        }


        private async Task SendError(string message)
        {
            var error = JsonSerializer.Serialize(new { status = "error", message });

            await SafeSend(error);
        }
        private async Task SafeSend(string json)
        {
            if (_isClosed || !_connection.IsAvailable)
                return;

            try
            {
                await _connection.Send(json);
            }
            catch (Fleck.ConnectionNotAvailableException)
            {
                // connection is closing/closed — safely ignore
                _isClosed = true;
            }
            catch (Exception)
            {
                // optional: log
            }
        }

        private async Task HandleLatest(ITransactionService service, IWebSocketConnection connection, Dictionary<string, object> data)
        {
            // Safely extract and convert pumpId
            int pumpId = data.GetValueOrDefault("pump_id") switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
                int id => id,
                _ => -1 // Default to -1 if not a valid number (can be adjusted to a different fallback value)
            };

            // Safely extract and convert nozzleId
            int nozzleId = data.GetValueOrDefault("nozzle_id") switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
                int id => id,
                _ => -1 // Default to -1 if not a valid number (can be adjusted to a different fallback value)
            };

            // Safely extract and convert emp (string)
            string emp = data.GetValueOrDefault("emp")?.ToString();

            // Check if any parameter is invalid (i.e., -1 for int fallback, or empty string for emp)
            if (pumpId == -1 || nozzleId == -1 || string.IsNullOrEmpty(emp))
            {
                // Fallback to HandleAttendantSocket if parameters are invalid or missing
                //await service.HandleAttendantSocket(socket, queryString);
                return;
            }

            // If valid, proceed with fetching the latest transaction
            var latestTx = await service.GetLatestTransactions(pumpId, nozzleId, emp);
            var response = new { type = "latest_transaction", data = latestTx };

            await connection.Send(JsonSerializer.Serialize(response));
        }

        private async Task HandleAllLatest(ITransactionService service, IWebSocketConnection connection, object data)
        {
            var allTx = await service.GetAllLatestTransactions(data);
            var response = new { type = "all_transactions", data = allTx };

            Console.WriteLine($"Received WebSocket message: {response}");

            await connection.Send(JsonSerializer.Serialize(response));
        }
        private async Task HandleAll(ITransactionService service, IWebSocketConnection connection)
        {
            var allTx = await service.GetAllTransactions();
            var response = new { type = "all_transactions", data = allTx };

            Console.WriteLine($"Received WebSocket message: {response}");

            await connection.Send(JsonSerializer.Serialize(response));
        }

        private async Task HandleUpdate(ITransactionService service, IWebSocketConnection connection, Dictionary<string, object> data)
        {
            string txId = data.GetValueOrDefault("transaction_id")?.ToString() ?? "";
            var updateJson = data.GetValueOrDefault("update")?.ToString() ?? "{}";

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updateJson);
            var updateFields = new Dictionary<string, object>();

            if (parsed != null)
            {
                if (parsed.TryGetValue("state", out var stateVal))
                    updateFields["State"] = stateVal.ToString();

                if (parsed.TryGetValue("order_uuid", out var orderUuidVal))
                    updateFields["OrderUuid"] = orderUuidVal.ToString();
                // offline change

                if (parsed.TryGetValue("order_id", out var odooOrderIdVal))
                {
                    object val = odooOrderIdVal.ValueKind switch
                    {
                        JsonValueKind.Number when odooOrderIdVal.TryGetInt64(out long l) => l,
                        JsonValueKind.Number when odooOrderIdVal.TryGetDouble(out double d) => d,
                        _ => odooOrderIdVal.ToString()
                    };

                    updateFields["OdooOrderId"] = val;
                }

                if (parsed.TryGetValue("payment_id", out var paymentIdVal))
                {
                    var paymentId = paymentIdVal.ValueKind == JsonValueKind.String
                        ? paymentIdVal.GetString()
                        : paymentIdVal.ToString();

                    updateFields["PaymentId"] = paymentId;
                    Console.WriteLine($"💳 PaymentId parsed and set: {paymentId}");
                }



                //if (parsed.TryGetValue("order_uuid", out var orderUuidElem))
                //{
                //    var orderUuid = orderUuidElem.GetString();
                //    updateFields["OrderUuid"] = orderUuid;

                //    if (!parsed.TryGetValue("order_id", out var odooOrderIdVal))
                //    {
                //        updateFields["OdooOrderId"] = orderUuid;
                //    }
                //}


                if (parsed.TryGetValue("status_sync", out var statusSyncVal))
                {
                    bool sync = statusSyncVal.ValueKind == JsonValueKind.True;
                    updateFields["SyncStatus"] = sync ? 1 : 0;
                }

                if (parsed.TryGetValue("add_to_cart", out var addToCartVal))
                {
                    bool addCartFlag = addToCartVal.ValueKind == JsonValueKind.True;
                    updateFields["AddToCart"] = addCartFlag ? 1 : 0;

                    Console.WriteLine($"🔄 AddToCart updated to {(addCartFlag ? 1 : 0)} for {txId}");
                }
            }

            // ✅ Detect AddToCart-only update
            bool isOnlyAddToCartUpdate =
                updateFields.Count == 1 &&
                updateFields.ContainsKey("AddToCart");

            // -------------------------------------------------------------
            // OFFLINE SYNC (UNCHANGED)
            // -------------------------------------------------------------
            var pendingOffline = await service.SyncOfflineTransactionsAsync();
            if (pendingOffline != null && pendingOffline.Count > 0)
            {
                await service.MarkTransactionsSyncedAsync(pendingOffline);

                foreach (var offTxn in pendingOffline)
                {
                    if (connection.IsAvailable)
                    {
                        //string paymentId = updateFields.ContainsKey("PaymentId")
                        //? updateFields["PaymentId"]?.ToString() ?? ""
                        //: "";
                        await connection.Send(JsonSerializer.Serialize(new
                        {
                            type = "transaction_update",
                            data = new
                            {
                                transaction_id = offTxn.TransactionId,
                                odoo_order_id = offTxn.OdooOrderId,
                                sync_status = 1,
                                state = "approved",
                                add_to_cart = 0,
                                payment_id = offTxn.PaymentId
                            }
                        }));
                    }
                }
            }
            // -------------------------------------------------------------

            bool updated = await service.UpdateTransaction(txId, updateFields);
            if (!updated)
                return;

            // 🛑 IMPORTANT FIX
            // Do NOT broadcast AddToCart-only updates
            if (isOnlyAddToCartUpdate)
            {
                // DB is updated, FE already knows about cart action
                // Broadcasting here causes reorder + missing PumpId/NozzleId
                return;
            }

            // ---------------- EXISTING BROADCAST (UNCHANGED) ----------------
            if (connection.IsAvailable)
            {
                try
                {
                    await connection.Send(JsonSerializer.Serialize(new
                    {
                        type = "transaction_update",
                        data = new
                        {
                            transaction_id = txId,
                            state = "Attendant Order"
                        }
                    }));
                }
                catch { }

                try
                {
                    int syncStatus = updateFields.ContainsKey("SyncStatus")
                        ? Convert.ToInt32(updateFields["SyncStatus"])
                        : 1;

                    string orderUuid = updateFields.ContainsKey("OrderUuid")
                        ? updateFields["OrderUuid"]?.ToString() ?? ""
                        : "";

                    await connection.Send(JsonSerializer.Serialize(new
                    {
                        type = "transaction_update",
                        data = new
                        {
                            transaction_id = txId,
                            state = "approved",
                            status_sync = syncStatus == 1,
                            order_uuid = orderUuid
                        }
                    }));
                }
                catch { }
            }
        }


        private async Task HandleFpUnblock(ITransactionService service, IWebSocketConnection connection, Dictionary<string, object> data)
        {
            try
            {
                int fpId = 0;
                if (data.TryGetValue("fp_id", out var fpObj) && fpObj != null)
                {
                    fpId = Convert.ToInt32(fpObj);
                }

                var limits = await service.GetTransactionLimitCountByFpId(fpId);

                if (limits == null || !limits.Any())
                {
                    if (connection.IsAvailable)
                    {
                        var response = new Dictionary<string, object>
                        {
                            ["type"] = "fp_unblock",
                            ["status"] = "error",
                            ["fp_id"] = fpId,
                            ["message"] = "Fuel pump record not found"
                        };

                        await connection.Send(JsonSerializer.Serialize(response));
                    }
                    return;
                }

                var limit = limits.First();
                string status = limit.Status?.Trim().ToLower() ?? "";

                if (status == "unavailable")
                {
                    await service.FpLimitReset(fpId);

                    await _forecourt.CheckAndApplyPumpLimitAsync(fpId, _serviceprovider, false, false);

                    if (connection.IsAvailable)
                    {
                        var dataResp = new Dictionary<string, object>
                        {
                            ["fp_id"] = fpId,
                            ["state"] = "unblocked",
                            ["previous_status"] = "unavailable",
                            ["message"] = "Pump limit reset and unblocked successfully"
                        };

                        var response = new Dictionary<string, object>
                        {
                            ["type"] = "fp_unblock",
                            ["data"] = dataResp
                        };

                        await connection.Send(JsonSerializer.Serialize(response));
                    }
                }
                else
                {
                    if (connection.IsAvailable)
                    {
                        var dataResp = new Dictionary<string, object>
                        {
                            ["fp_id"] = fpId,
                            ["state"] = "available",
                            ["message"] = "Fuel pump already available, nothing to unblock"
                        };

                        var response = new Dictionary<string, object>
                        {
                            ["type"] = "fp_unblock",
                            ["data"] = dataResp
                        };

                        await connection.Send(JsonSerializer.Serialize(response));
                    }
                }
            }
            catch (Exception ex)
            {
                if (connection.IsAvailable)
                {
                    var response = new Dictionary<string, object>
                    {
                        ["type"] = "fp_unblock",
                        ["status"] = "error",
                        ["message"] = ex.Message
                    };

                    await connection.Send(JsonSerializer.Serialize(response));
                }
            }
        }




        private async Task HandleAddTransaction(ITransactionService service, IWebSocketConnection connection, Dictionary<string, object> data)
        {
            var newTx = data.GetValueOrDefault("data");
            await service.AddTransaction(newTx);

            var broadcastData = JsonSerializer.Serialize(new { type = "new_transaction", data = newTx });

            //foreach (var client in _clients)
            //{
            //    await client.Send(broadcastData);
            //}

            //_logger.LogInformation("📢 Broadcasted new transaction");
        }

        private async Task HandleFuelPumpStatus(ITransactionService service, IWebSocketConnection connection, Dictionary<string, object> data)
        {
            var newTx = data.GetValueOrDefault("data");
            List<FuelPumpStatusDto> FpStatus = await service.GetAllFpStatus();

            var response = JsonSerializer.Serialize(new { type = "FuelPumpStatus", data = FpStatus });

            //foreach (var client in _clients)
            //{
            //    await client.Send(broadcastData);
            //}

            //_logger.LogInformation("📢 Broadcasted new transaction");

            Console.WriteLine($"Received WebSocket message: {response}");

            await connection.Send(JsonSerializer.Serialize(response));
        }

        public async Task HandleIsDisCard(ITransactionService service, IWebSocketConnection connection, Dictionary<string, object> data)
        {
            string txId = data.GetValueOrDefault("transaction_id")?.ToString() ?? "";
            var updateJson = data.GetValueOrDefault("update")?.ToString() ?? "{}";

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updateJson);
            var updateFields = new Dictionary<string, object>();

            if (parsed != null)
            {
                if (parsed.TryGetValue("state", out var stateVal))
                    updateFields["State"] = stateVal.ToString();

                if (parsed.TryGetValue("manual_approved", out var manualApprovedVal))
                    updateFields["ManualApproved"] = manualApprovedVal.ToString();

                if (parsed.TryGetValue("manual_approved", out _))
                {
                    var transactionDiscard = new TransactionDiscard
                    {
                        TransactionId = txId,
                        Status = parsed.TryGetValue("state", out var stateValue) ? stateValue.ToString() : "approved",
                        IsDiscard = true
                    };

                    await service.UpdateIsDiscard(transactionDiscard);
                }
            }

            if (connection.IsAvailable)
            {
                try
                {
                    //  await service.HandleWebSocketRequest(_connection, "", null, null, null, "");
                    await connection.Send(JsonSerializer.Serialize(new
                    {
                        type = "transaction_update",
                        data = new
                        {
                            transaction_id = txId,
                            state = "approved",
                            manual_approved = "yes",
                            // message = "Transaction updated successfully"
                        }
                    }));
                }
                catch
                {
                }
            }
        }



        //public Task StopAsync(CancellationToken cancellationToken)
        //{
        //    _logger.LogInformation(" Stopping WebSocket server...");
        //    _cts?.Cancel();
        //    _server?.Dispose();
        //    return Task.CompletedTask;
        //}
        //vijat implematation done 

        public override WebSocketCloseStatus? CloseStatus => _isClosed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _isClosed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => _connection.Close();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _isClosed = true;
            _connection.Close();
            _onClosed?.Invoke(_connection);
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _isClosed = true;
            _connection.Close();
            _onClosed?.Invoke(_connection);
            return Task.CompletedTask;
        }
        public override void Dispose()
        {
            _isClosed = true;
            _connection.Close();
            _onClosed?.Invoke(_connection);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (await _receiveChannel.Reader.WaitToReadAsync(cancellationToken) &&
                _receiveChannel.Reader.TryRead(out var msg))
            {
                var count = Math.Min(buffer.Count, msg.Count);
                msg.AsSpan(0, count).CopyTo(buffer);
                return new WebSocketReceiveResult(count, WebSocketMessageType.Text, true);
            }

            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var msg = System.Text.Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            _connection.Send(msg);
            return Task.CompletedTask;
        }
    }
}
