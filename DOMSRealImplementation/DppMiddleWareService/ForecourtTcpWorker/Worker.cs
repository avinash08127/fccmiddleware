using DPPMiddleware.Interface;
using DPPMiddleware.IRepository;
using DPPMiddleware.Models;
using DppMiddleWareService;
using Fleck;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Ocsp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace DPPMiddleware.ForecourtTcpWorker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IServiceProvider _services;
        private readonly WorkerOptions _options;
        private List<Attendant> _attendants = new();
        private readonly PopupService _popupService;
        private ForecourtClient? _forecourtClient;
        public event Action<int>? OnFpStatusUpdated;
        public Worker(ILogger<Worker> logger, IServiceProvider services, IOptions<WorkerOptions> options, ForecourtClient forecourtClient, PopupService popupService)
        {
            _logger = logger;
            _services = services;
            _options = options.Value;
            _logger.LogInformation("Forecourt TCP Worker initialized at {StartTime}", DateTime.Now);
            _forecourtClient = forecourtClient;
            _popupService = popupService;
            NativePopup.OnPopupResult += (result) =>
            {
                if (result)
                {
                    _logger.LogInformation("User clicked YES. (true)");
                    // HANDLE YES LOGIC HERE
                }
                else
                {
                    _logger.LogWarning("User clicked NO. (false)");
                    // HANDLE NO LOGIC HERE
                }
            };



        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting Forecourt TCP client worker...");
            string host = _options.Host;
            int port = _options.JplPort ?? 8888;
            string fcAccessCode = _options.FcAccessCode ?? "Invalid FcAccessCode";
            bool windowStarted = false;
            AttendantMonitorWindow? PopupWindow = null;


            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_forecourtClient.IsConnected)
                    {
                        _logger.LogInformation("Attempting to connect to forecourt controller {host}:{port}", host, port);
                        bool connected = _forecourtClient.Connect(host, port);

                        if (connected)
                        {
                            _logger.LogInformation("Connected to forecourt controller, sending FC_LOGON...");
                            _forecourtClient.FcLogon(
                                ipAddress: host,
                                fcAccessCode: _options.FcAccessCode,
                                posId: Convert.ToInt32(_options.posId),
                                countryCode: _options.countryCode,
                                posVersionId: _options.posVersionId,
                                unsolicitedApcList: new List<int> { 2 }
                            );
                            await Task.Delay(10000);
                        }
                        else
                        {
                            _logger.LogWarning("Connection attempt failed. Retrying in 5s...");
                            await Task.Delay(10000, stoppingToken);
                            continue;
                        }
                    }

                    if (!_forecourtClient.IsLoggedOn)
                    {
                        _logger.LogWarning("Still not logged on. Waiting for FC_LOGON response...");

                    }


                    _forecourtClient.CheckAndApplyPumpLimitAsync(0, _services, false, false);
                    _forecourtClient.StartFpStatusPolling(stoppingToken);

                    await Task.Delay(5000, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in worker main loop. Retrying in 5 seconds...");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            _forecourtClient?.Disconnect();
            _logger.LogInformation("Forecourt TCP Worker stopped.");
        }
    }


    public class ForecourtClient
    {
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const int JPL_PORT = 8888;
        private readonly ILogger<ForecourtClient> _logger;
        private readonly IServiceProvider _services;
        private readonly WorkerOptions _options;

        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private bool _running;
        private Thread? _recvThread;
        private Thread? _heartbeatThread;
        private byte[] _receiveBuffer = Array.Empty<byte>();
        private DateTime _lastHeartbeat = DateTime.Now;
        private bool _isLoggedOn;
        private string _host = "";
        private int _port;
        private static readonly Dictionary<int, int> _fpNozzleCache = new();
        //private DomsPriceSet _latestPriceSet = null;
        private static string _currentMasterResetId = string.Empty;
        public static string CurrentMasterResetId => _currentMasterResetId;
        public bool IsConnected => _tcpClient?.Connected ?? false;

        public bool IsLoggedOn => _isLoggedOn;
        public event Action<int>? OnFpStatusUpdated;
        public ForecourtClient(ILogger<ForecourtClient> logger, IServiceProvider services, IOptions<WorkerOptions> options)
        {
            _logger = logger;
            _services = services;
            _options = options.Value;
        }

        public bool Connect(string host, int port = JPL_PORT)
        {
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(host, port);
                _stream = _tcpClient.GetStream();
                _running = true;
                _host = host;
                _port = port;
                _lastHeartbeat = DateTime.Now;

                _recvThread = new Thread(ReceiveLoop) { IsBackground = true };
                _recvThread.Start();

                _heartbeatThread = new Thread(SendHeartbeats) { IsBackground = true };
                _heartbeatThread.Start();

                _logger.LogInformation("✓ Connected to {host}:{port}", host, port);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to {host}:{port}", host, port);
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;
            _isLoggedOn = false;

            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _logger.LogInformation("Disconnected from forecourt controller.");
        }

        private void SendHeartbeats()
        {
            while (_running)
            {
                try
                {
                    Thread.Sleep(30000);
                    if (_stream == null) continue;

                    var elapsed = (DateTime.Now - _lastHeartbeat).TotalSeconds;
                    if (elapsed > 60)
                        _logger.LogWarning("⚠ Heartbeat timeout ({elapsed:F1}s)", elapsed);

                    byte[] heartbeat = { STX, ETX };
                    _stream.Write(heartbeat, 0, heartbeat.Length);
                    _logger.LogDebug("💓 Heartbeat sent at {time}", DateTime.Now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Heartbeat thread error");
                    break;
                }
            }
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            while (_running)
            {
                try
                {
                    if (_stream == null) continue;
                    int bytesRead = _stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        _logger.LogWarning("Connection closed by server.");
                        Reconnect();
                        continue;
                    }

                    var chunk = new byte[bytesRead];
                    Array.Copy(buffer, chunk, bytesRead);
                    _receiveBuffer = Combine(_receiveBuffer, chunk);
                    _lastHeartbeat = DateTime.Now;

                    ProcessReceived();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Receive loop error. Reconnecting...");
                    Reconnect();
                }
            }
        }

        private void Reconnect()
        {
            try
            {
                Disconnect();
                Thread.Sleep(5000);

                _receiveBuffer = Array.Empty<byte>();

                if (Connect(_host, _port))
                {
                    _logger.LogInformation("Reconnected. Sending FC_LOGON...");
                    _isLoggedOn = false;
                    //LAB
                    //FcLogon(
                    //    ipAddress: _host,
                    //    fcAccessCode: "POS,RI,APPL_ID=13,UNSO_FPSTA_3,UNSO_TRBUFSTA_1", //POS,RI,APPL_ID=50,UNSO_FPSTA_3,UNSO_TRBUFSTA_1
                    //    posId: 13,
                    //    countryCode: "0045",
                    //    posVersionId: "1234",
                    //    unsolicitedApcList: new List<int> { 2 }
                    //);

                    //MALAWI
                    FcLogon(
                        ipAddress: _host,
                        fcAccessCode: _options.FcAccessCode,
                        posId: Convert.ToInt32(_options.posId),
                        countryCode: _options.countryCode,
                        posVersionId: _options.posVersionId,
                        unsolicitedApcList: new List<int> { 2 }
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnection attempt failed.");
            }
        }

        //JPL message builder for Grade Price.
        //    public bool RequestGradePriceFromDoms()
        //    {
        //        if (_stream == null) return false;

        //        var msg = new Dictionary<string, object>
        //{
        //    { "name", "GradePrice_req" },   // <-- DOMS JPL name
        //    { "subCode", "00H" },
        //    { "data", new Dictionary<string, object>() }
        //};

        //        return SendJplMessage(msg);
        //    }
        public bool RequestFcPriceSet()
        {
            if (_stream == null) return false;

            var msg = new Dictionary<string, object>
            {
                { "name", "FcPriceSet_req" },
                { "extCode", "000D" },     // 0DH from spec
                { "subCode", "02H" },      // Extended price
                { "data", new Dictionary<string, object>
                    {
                        { "PriceSetType", "00H" }   // Current price set
                    }
                }
            };

            return SendJplMessage(msg);
        }
        public void HandlePriceSetRequest(string respone)
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
            repo.HandlePriceSetRequest(respone);
        }
        private void ProcessReceived()
        {
            while (true)
            {
                int stxPos = Array.IndexOf(_receiveBuffer, STX);
                if (stxPos == -1) break;

                int etxPos = Array.IndexOf(_receiveBuffer, ETX, stxPos + 1);
                if (etxPos == -1) break;

                byte[] msgBytes = new byte[etxPos - stxPos - 1];
                Array.Copy(_receiveBuffer, stxPos + 1, msgBytes, 0, msgBytes.Length);

                var remaining = new byte[_receiveBuffer.Length - etxPos - 1];
                Array.Copy(_receiveBuffer, etxPos + 1, remaining, 0, remaining.Length);
                _receiveBuffer = remaining;

                if (msgBytes.Length == 0)
                {
                    _lastHeartbeat = DateTime.Now;
                    continue;
                }

                string json = Encoding.UTF8.GetString(msgBytes);
                try
                {
                    var message = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    HandleMessage(message!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse JPL JSON: {json}", json);
                }
            }
        }
        private void HandleFcLogonResponse(Dictionary<string, object> message)
        {
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("✓ FC_LOGON SUCCESSFUL!");
            Console.WriteLine(new string('=', 60));
            _isLoggedOn = true;
        }
        private void HandleRejectMessage(Dictionary<string, object> message)
        {
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("✗ MESSAGE REJECTED!");
            Console.WriteLine(new string('=', 60));
            _isLoggedOn = false;
        }
        private void HandleMessage(Dictionary<string, object> message)
        {
            try
            {
                string name = message.ContainsKey("name") ? message["name"]?.ToString() ?? "" : "";
                string prettyJson = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true });

                using var doc = JsonDocument.Parse(prettyJson);
                var data = doc.RootElement.GetProperty("data");
                string fpId = string.Empty;
                JsonElement[] transArray = null;
                _logger.LogInformation("\n" + new string('=', 60));
                _logger.LogInformation("📥 RECEIVED MESSAGE:");
                _logger.LogInformation($"Name: {name}");
                _logger.LogInformation(JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true }));
                _logger.LogInformation(new string('=', 60) + "\n");

                if (name == "FcLogon_resp")
                {
                    HandleFcLogonResponse(message);
                    
                    //Price update
                    SendDynamicPriceUpdate();
                    RequestFcPriceSet();   // requesting a gradepraice 
                }
                else if (name == "FcPriceSet_resp")
                {
                    HandlePriceSetRequest(prettyJson);

                }
                else if(name == "FcStatus_resp")
                {
                    if (data.TryGetProperty("FcMasterResetDateAndTime", out var resetProp))
                    {
                        _currentMasterResetId = resetProp.GetString() ?? "";
                        _logger.LogInformation("Master Reset ID Updated: {id}", _currentMasterResetId);
                    }
                }
                else if (name == "RejectMessage_resp")
                {
                    HandleRejectMessage(message);
                }
                if (data.TryGetProperty("TransInSupBuffer", out var TransInSupBuffer))
                {
                    transArray = TransInSupBuffer.EnumerateArray().ToArray();
                }

                if (data.TryGetProperty("FpId", out var fpIdProp))
                {
                    fpId = fpIdProp.GetString();
                }

                _logger.LogInformation("📥 Message received: {name}", name);
                if (transArray != null)
                {
                    foreach (var t in transArray)
                    {

                        string seq = t.TryGetProperty("TransSeqNo", out var seqProp) ? seqProp.GetString() : "-";
                        string volStr = t.TryGetProperty("Vol_e", out var volProp) ? volProp.GetString() : "0";
                        string moneyStr = t.TryGetProperty("MoneyDue_e", out var monProp) ? monProp.GetString() : "0";
                        string grade = t.TryGetProperty("FcGradeId", out var gProp) ? gProp.GetString() : "-";
                        int nozzle = _fpNozzleCache.ContainsKey(Convert.ToInt32(fpId)) ? _fpNozzleCache[Convert.ToInt32(fpId)] : 0;
                        Console.WriteLine($"🔗 Mapped Nozzle for Transaction: {nozzle}");
                        decimal.TryParse(volStr, out var vol);
                        decimal.TryParse(moneyStr, out var money);
                        Console.WriteLine($"→ Pump: {fpId}, Seq: {seq}, Volume: {vol}, Money: {money}, Grade: {grade}");

                        //RequestLockAndReadTransaction(fpId, seq);
                        var txnId = Guid.NewGuid().ToString();
                        var txn = new TransactionEntity
                        {
                            TransactionId = txnId,
                            HexMessage = BitConverter.ToString(Encoding.UTF8.GetBytes(prettyJson)).Replace("-", " "),
                            ParsedJson = prettyJson,
                            Port = _port.ToString(),
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                            ReferenceId = seq,
                            EventDetails = new EventDetailEntity
                            {
                                TransactionId = txnId,
                                ReferenceId = seq,
                                FpId = int.TryParse(fpId, out var idVal) ? idVal : 0,
                                Vol = vol,
                                Money = money,
                                Classification = "Fuel",
                                SubCode = "01H",
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                                NozzleId = nozzle,
                            }
                        };

                        var dppMessage = new DppMessage
                        {
                            Name = "FpSupTransBufStatus_resp",
                            SubCode = "01H",
                            Solicited = false,
                            Data = new
                            {
                                FpId = fpId,
                                TransSeqNo = seq,
                                Volume = vol,
                                MoneyDue = money,
                                GradeId = grade,
                            }
                        };

                        try
                        {
                            using var scope = _services.CreateScope();
                            var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
                            //_logger.LogInformation("Before Inserting in DB: {Payload} FCCM", JsonSerializer.Serialize(dppMessage));
                            string str = repo.InsertTransactions(txn, dppMessage,_currentMasterResetId);
                            // 🧹 CLEAR AFTER SUCCESS
                            //ClearSupervisedTransaction(fpId, seq, volStr, moneyStr);
                            //_logger.LogInformation("After Inserting in DB: {Payload} FCCM", str);

                        }
                        catch (Exception dbEx)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            _logger.LogInformation($"⚠️ DB Insert failed: {dbEx.Message + " , " + dbEx.InnerException + " , " + dbEx.StackTrace}");
                            Console.ResetColor();
                        }
                    }
                }
                //_logger.LogInformation($"After Insertin DB PumpStatusUpdate : {prettyJson}");
                switch (name)
                {
                    case "FpStatus_resp":
                        PrintPumpStatusAsync(prettyJson);

                        break;

                    case "MultiMessage_resp":
                        HandleMultiMessageAsync(prettyJson);
                        //if (data.TryGetProperty("messages", out var messages))
                        //{
                        //    foreach (var msg in messages.EnumerateArray())
                        //    {
                        //        string innerName = msg.GetProperty("name").GetString();

                        //        _logger.LogInformation("📦 Inner Message: {name}", innerName);
                        //        if (innerName == "FpStatus_resp")
                        //        {
                        //            string innerJson = msg.GetRawText();
                        //            PrintPumpStatusAsync(innerJson);
                        //        }
                        //        if (innerName == "FpSupTrans_resp")
                        //        {
                        //            string innerJson = msg.GetRawText();
                        //            HandleLockedTransaction(innerJson);
                        //        }
                        //    }
                        //}
                        break;
                    //case "FpSupTrans_resp":
                    //    HandleLockedTransaction(prettyJson);
                    //    break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                _logger.LogInformation($"⚠️ DB Insert failed: {ex.Message + " ," + ex.InnerException + " ," + ex.StackTrace}");
                Console.ResetColor();
            }
        }
        private void HandleLockedTransaction(string prettyJson)
        {
            _logger.LogInformation("Received HandleLockedTransaction ");

            using var doc = JsonDocument.Parse(prettyJson);
            var data = doc.RootElement.GetProperty("data");

            string fpId = data.GetProperty("FpId").GetString();
            string seq = data.GetProperty("TransSeqNo").GetString();
            string volStr = data.GetProperty("Vol_e").GetString();
            string moneyStr = data.GetProperty("MoneyDue_e").GetString();
            string grade = data.GetProperty("FcGradeId").GetString();

            decimal.TryParse(volStr, out var vol);
            decimal.TryParse(moneyStr, out var money);

            int nozzle = _fpNozzleCache.ContainsKey(Convert.ToInt32(fpId))
                ? _fpNozzleCache[Convert.ToInt32(fpId)]
                : 0;

            var txnId = Guid.NewGuid().ToString();

            var txn = new TransactionEntity
            {
                TransactionId = txnId,
                HexMessage = BitConverter.ToString(Encoding.UTF8.GetBytes(prettyJson)).Replace("-", " "),
                ParsedJson = prettyJson,
                Port = _port.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ReferenceId = seq,
                EventDetails = new EventDetailEntity
                {
                    TransactionId = txnId,
                    ReferenceId = seq,
                    FpId = int.TryParse(fpId, out var idVal) ? idVal : 0,
                    Vol = vol,
                    Money = money,
                    Classification = "Fuel",
                    SubCode = "01H",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    NozzleId = nozzle,
                }
            };

            var dppMessage = new DppMessage
            {
                Name = "FpSupTrans_resp",
                SubCode = "01H",
                Solicited = true,
                Data = new
                {
                    FpId = fpId,
                    TransSeqNo = seq,
                    Volume = vol,
                    MoneyDue = money,
                    GradeId = grade,
                }
            };

            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

            repo.InsertTransactions(txn, dppMessage,_currentMasterResetId);

            // Clear only after successful insert
            ClearSupervisedTransaction(fpId, seq, volStr, moneyStr);
        }
        public bool FcLogon(string ipAddress, string fcAccessCode, int posId, string countryCode, string posVersionId, List<int> unsolicitedApcList)
        {
            if (_stream == null) return false;

            var msg = new Dictionary<string, object>
            {
                { "name", "FcLogon_req" },
                { "subCode", "00H" },
                { "data", new Dictionary<string, object>
                    {
                        { "FcAccessCode", fcAccessCode },
                        { "CountryCode", countryCode },
                        { "PosVersionId", posVersionId },
                        { "UnsolicitedApcList", unsolicitedApcList }
                    }
                }
            };

            return SendJplMessage(msg);
        }
        public bool SendDynamicPriceUpdate()
        {
            // Hardcoded Grade + Price
            var gradeIds = new List<string> { "01", "02" };

            var prices = new List<string>
            {
                "004945",
                "004965",//004965
            };

            var msg = new Dictionary<string, object>
            {
                { "name", "change_FcPriceSet_req" },
                { "subCode", "02H" },
                {
                    "data", new Dictionary<string, object>
                    {
                        { "FcPriceSetId", "92" },
                        { "NoFcPriceGroups", "1" },
                        { "NoFcGrades", gradeIds.Count },
                        { "FcPriceGroupId", new List<string> { "01" } },
                        { "FcGradeId", gradeIds },
                        { "FcPriceGroups", new List<List<string>> { prices } },
                        { "PriceSetActivationDateAndTime", "20260217095113" }
                    }
                }
            };

            _logger.LogInformation("📤 Sending hardcoded price update → {0} grades updated", gradeIds.Count);

            return SendJplMessage(msg);
        }
        private bool IsValidJson(string json)
        {
            try
            {
                JsonDocument.Parse(json);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        private bool SendJplMessage(Dictionary<string, object> message)
        {
            try
            {
                string json = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true });

                if (!IsValidJson(json))
                {
                    _logger.LogError("Invalid JSON message: {json}", json);
                    return false;
                }

                _logger.LogInformation("📤 Sent message:\n{json}", json);

                byte[] data = Combine(new byte[] { STX }, Encoding.UTF8.GetBytes(json), new byte[] { ETX });
                _stream!.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send JPL message");
                return false;
            }
        }

        private static byte[] Combine(params byte[][] arrays)
        {
            int length = arrays.Sum(a => a.Length);
            var result = new byte[length];
            int offset = 0;
            foreach (var a in arrays)
            {
                Buffer.BlockCopy(a, 0, result, offset, a.Length);
                offset += a.Length;
            }
            return result;
        }

        private async Task PrintPumpStatusAsync(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string subCode = root.GetProperty("subCode").GetString();
                var data = root.GetProperty("data");

                string id = data.GetProperty("FpId").GetString();
                string stateValue = data.GetProperty("FpMainState").GetProperty("value").GetString();

                string stateName =
                    data.GetProperty("FpMainState")
                        .GetProperty("enum")
                        .EnumerateObject()
                        .First(p => p.Value.GetString() == stateValue)
                        .Name;

                string nozzleId = "0";
                decimal vol = 0;
                decimal money = 0;
                int FpGradeOptionNo = 0;
                bool isOnlineFlag = false;

                if (data.TryGetProperty("FpSupplStatusPars", out var suppl))
                {
                    //if (suppl.TryGetProperty("NozzleId", out JsonElement NozzleId))
                    //{
                    //    nozzleId = NozzleId.GetString();

                    //}
                    if (suppl.TryGetProperty("NozzleId", out JsonElement NozzleIdElement))
                    {
                        int nozzle = Convert.ToInt32(NozzleIdElement.GetString());
                        _fpNozzleCache[Convert.ToInt32(id)] = nozzle;
                        nozzleId = NozzleIdElement.GetString();
                        Console.WriteLine($"🔄 Cached nozzle FP:{id} Nozzle:{nozzle}");
                    }


                    string volRaw = null;
                    if (suppl.TryGetProperty("FuellingDataVol_e", out JsonElement volElement))
                    {
                        volRaw = volElement.GetString();

                        if (decimal.TryParse(volRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var volVal) && volVal > 0)
                            vol = volVal / 100;
                        else
                            vol = 0;
                    }

                    string moneyRaw = "";
                    if (suppl.TryGetProperty("FuellingDataMon_e", out JsonElement moneyElement))
                    {
                        moneyRaw = moneyElement.GetString();
                        money = Convert.ToDecimal(moneyRaw) / 10;

                    }

                    //if (suppl.TryGetProperty("FpGradeOptionNo", out JsonElement fpGrade))
                    //if (data.TryGetProperty("FcGradeId", out JsonElement fpGrade))
                    //{
                    //    FpGradeOptionNo = fpGrade.GetInt32();
                    //}
                    
                }
                if (data.TryGetProperty("FcGradeId", out JsonElement fpGrade))
                {
                    if (fpGrade.ValueKind == JsonValueKind.String)
                    {
                        int.TryParse(fpGrade.GetString(), out FpGradeOptionNo);
                    }
                    else if (fpGrade.ValueKind == JsonValueKind.Number)
                    {
                        FpGradeOptionNo = fpGrade.GetInt32();
                    }
                }
                if (data.TryGetProperty("FpSubStates", out JsonElement fpSubStates))
                {
                    if (fpSubStates.TryGetProperty("bits", out JsonElement bits))
                    {
                        if (bits.TryGetProperty("IsOnline", out var isOnline))
                        {
                            isOnlineFlag = true;
                        }
                        else
                        {
                            isOnlineFlag = false;
                        }
                    }
                }

                FpSatus status = new FpSatus
                {
                    FpId = Convert.ToInt32(id),
                    FpStatus = stateName,
                    NozzleId = Convert.ToInt32(nozzleId),
                    Volume = vol.ToString(CultureInfo.InvariantCulture),
                    Money = money.ToString(CultureInfo.InvariantCulture),
                    FpGradeOptionNo = FpGradeOptionNo,
                    IsOnline = isOnlineFlag

                };

                _logger.LogInformation($" FP Status : {status} | IsOnline : {isOnlineFlag} | FpId : {id} ");
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

                if (status.FpId != 0)
                    repo.UpdateFpStatusById(status);
                CheckAndApplyPumpLimitAsync(0, _services, false, false);

                //if (status.FpId != 0)
                //{
                //    OnFpStatusUpdated?.Invoke(status.FpId);
                //}

            }
            catch (Exception ex)
            {
                _logger.LogInformation($"⚠️ Pump status parse failed: {ex.Message}");
            }
        }

        private async Task HandleMultiMessageAsync(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data))
                return;

            if (!data.TryGetProperty("messages", out var messages))
                return;

            foreach (var msg in messages.EnumerateArray())
            {
                string innerName = msg.GetProperty("name").GetString();

                // Rebuild JSON so it matches normal message format
                var normalized = JsonSerializer.Serialize(new
                {
                    name = innerName,
                    subCode = msg.GetProperty("subCode").GetString(),
                    solicited = false,
                    data = msg.GetProperty("data")
                });

                switch (innerName)
                {
                    case "FpStatus_resp":
                        await PrintPumpStatusAsync(normalized);
                        break;

                    // future-proof
                    default:
                        break;
                }
            }
        }
        public void StartFpStatusPolling(CancellationToken token)
        {
            //Task.Run(async () =>
            //{
            //while (!token.IsCancellationRequested)
            //{
            try
            {
                // 0 means "refresh all"
                OnFpStatusUpdated?.Invoke(0);
                CheckAndApplyPumpLimitAsync_IsAllowed(0, _services, true, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising FP status update");
            }

            //        await Task.Delay(5000, token);
            //    }
            //}, token);
        }

        // Price update code starts 

        //    public bool SendDynamicPriceUpdate(List<(string GradeId, string NewPrice)> updates)
        //    {
        //        var gradeIds = updates.Select(u => u.GradeId).ToList();
        //        var prices = updates.Select(u => u.NewPrice).ToList();

        //        var msg = new Dictionary<string, object>
        //{
        //    { "name", "change_FcPriceSet_req" },
        //    { "subCode", "02H" },
        //    {
        //        "data", new Dictionary<string, object>
        //        {
        //            { "FcPriceSetId", "92" },
        //            { "NoFcPriceGroups", "1" },
        //            { "NoFcGrades", gradeIds.Count.ToString() },
        //            { "FcPriceGroupId", new List<string> { "01" } },
        //            { "FcGradeId", gradeIds },
        //            { "FcPriceGroups", new List<List<string>> { prices } },
        //            { "PriceSetActivationDateAndTime", "00000000000000" }
        //        }
        //    }
        //};

        //        _logger.LogInformation("📤 Sending dynamic price update → {0} grades updated", updates.Count);

        //        return SendJplMessage(msg);
        //    }





        //    public void HandleFcPriceSetResp(dynamic domsData)
        //    {
        //        var prices = ((List<object>)domsData.FcPriceGroups[0])
        //            .Select(p => p.ToString())
        //            .ToList();

        //        var gradeIds = ((List<object>)domsData.FcGradeId)
        //            .Select(g => g.ToString())
        //            .ToList();

        //        _latestPriceSet = new DomsPriceSet
        //        {
        //            PriceSetId = domsData.FcPriceSetId.ToString(),
        //            PriceGroupIds = ((List<object>)domsData.FcPriceGroupId).Select(x => x.ToString()).ToList(),
        //            GradeIds = gradeIds,
        //            CurrentPrices = gradeIds.Zip(prices, (g, p) => new { g, p })
        //                                    .ToDictionary(x => x.g, x => x.p)
        //        };

        //        _logger.LogInformation("💾 Stored latest DOMS Price Set (ID: {0})", _latestPriceSet.PriceSetId);
        //    }


        //    public bool SendDynamicPriceUpdate(List<(string GradeId, string NewPrice)> updates)
        //    {
        //        if (_latestPriceSet == null)
        //        {
        //            _logger.LogError("❌ No stored DOMS Price Set — request FcPriceSet_req first.");
        //            return false;
        //        }

        //        // 1️⃣ Load dynamic values from DOMS
        //        var priceSetId = _latestPriceSet.PriceSetId;
        //        var priceGroupIds = _latestPriceSet.PriceGroupIds;

        //        // 2️⃣ Merge old and new prices
        //        var updatedPrices = new List<string>();

        //        foreach (var grade in _latestPriceSet.GradeIds)
        //        {
        //            // If update exists → use new price
        //            var update = updates.FirstOrDefault(u => u.GradeId == grade);

        //            if (update.GradeId != null)
        //                updatedPrices.Add(update.NewPrice);   // use new price
        //            else
        //                updatedPrices.Add(_latestPriceSet.CurrentPrices[grade]); // keep old price
        //        }

        //        // 3️⃣ Build message
        //        var msg = new Dictionary<string, object>
        //{
        //    { "name", "change_FcPriceSet_req" },
        //    { "subCode", "02H" },
        //    {
        //        "data", new Dictionary<string, object>
        //        {
        //            { "FcPriceSetId", priceSetId },
        //            { "NoFcPriceGroups", priceGroupIds.Count.ToString() },
        //            { "NoFcGrades", _latestPriceSet.GradeIds.Count.ToString() },
        //            { "FcPriceGroupId", priceGroupIds },
        //            { "FcGradeId", _latestPriceSet.GradeIds },
        //            { "FcPriceGroups", new List<List<string>> { updatedPrices } },
        //            { "PriceSetActivationDateAndTime", "00000000000000" }
        //        }
        //    }
        //};

        //        _logger.LogInformation("📤 DYNAMIC DOMS PRICE UPDATE SENT → {0} grades updated", updates.Count);

        //        return SendJplMessage(msg);
        //    }




        // Price update code ends

        public bool RequestAllPumpStatus()
        {
            var msg = new Dictionary<string, object>
                        {
                            {"name", "FpStatus_req"},
                            {"subCode", "00H"},
                            {"data", new Dictionary<string, object> {{"FpId", "01"}}}
                        };
            Console.WriteLine($"📨 Requesting status for all pumps: {string.Join(", ", "01")}");
            return SendJplMessage(msg);
        }

        public bool RequestLockAndReadTransaction(string fpId, string transSeqNo)
        {
            var msg = new Dictionary<string, object>
            {
                { "name", "FpSupTrans_req" },
                { "subCode", "00H" },
                { "data", new Dictionary<string, object>
                    {
                        { "FpId", fpId },
                        { "TransSeqNo", transSeqNo },
                        { "PosId", _options.posId },
                        { "TransParId", new List<string> { "63" } }
                    }
                }
            };

            _logger.LogInformation("🔐 Locking transaction FpId:{fpId}, TransSeqNo:{transSeqNo}", fpId, transSeqNo);
            return SendJplMessage(msg);
        }

        public bool ClearSupervisedTransaction(string fpId, string transSeqNo, string volume, string money)
        {
            var msg = new Dictionary<string, object>
            {
                { "name", "clear_FpSupTrans_req" },
                { "subCode", "00H" },
                { "data", new Dictionary<string, object>
                    {
                        { "FpId", fpId },
                        { "PosId", _options.posId },
                        { "TransSeqNo", transSeqNo },
                        { "Vol", volume },
                        { "Money", money }
                    }
                }
            };

            _logger.LogInformation("🧹 Clearing transaction FpId:{fpId}, TransSeqNo:{transSeqNo}", fpId, transSeqNo);
            return SendJplMessage(msg);
        }

        public async Task HandleFpUnblock(IServiceProvider service, List<UnblockRequest> requests)
        {
            try
            {
                using var scope = _services.CreateScope();
                var _trans = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();


                foreach (var req in requests)
                {
                    int fpId = Convert.ToInt32(req.FpId);
                    int inputLimit = req.Limit;

                    var limits = await _trans.GetTransactionLimitCountByFpId(fpId);

                    if (limits == null || !limits.Any())
                        continue;

                    var limit = limits.First();
                    string status = limit.Status?.Trim().ToLower() ?? "";

                    if (status == "unavailable")
                    {
                        // 🔴 PASS THE INPUT VALUE TO DB
                        await _trans.FpLimitReset(fpId, inputLimit);

                        // OR if this method uses it internally
                        await CheckAndApplyPumpLimitAsync(fpId, _services, false, false);
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.LogInformation("Issue in Unblocking Pump", ex);
            }
        }

        private static readonly ConcurrentDictionary<int, DateTime> _fpLastUpdatedUtc
        = new ConcurrentDictionary<int, DateTime>();
        private static bool ShouldSkipFpAction(int fpId, int cooldownSeconds = 3)
        {
            var now = DateTime.UtcNow;

            if (_fpLastUpdatedUtc.TryGetValue(fpId, out var lastTime))
            {
                if ((now - lastTime).TotalSeconds < cooldownSeconds)
                {
                    return true; // ⛔ skip
                }
            }

            // ✅ allow and update timestamp
            _fpLastUpdatedUtc[fpId] = now;
            return false;
        }

        public async Task<bool> CheckAndApplyPumpLimitAsync_IsAllowed(int fpId, IServiceProvider serviceProvider, bool isAllowedChange, bool isAllowed)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

                //if (isAllowedChange)
                //{
                //    await repo.UpdateIsAllowedAsync(fpId, isAllowed);
                //}
                var limits = await repo.GetTransactionLimitCountByFpId(fpId);

                if (limits == null || !limits.Any())
                {
                    _logger.LogWarning("No limit records found for FP={fpId}", fpId);
                    return false;
                }

                bool anyActionTaken = false;

                foreach (var limit in limits)
                {
                    //if (ShouldSkipFpAction(limit.FpId))
                    //{
                    //    _logger.LogInformation(
                    //        "Skipping FP={fpId} — action within last 3 seconds",
                    //        limit.FpId
                    //    );
                    //    continue;
                    //}
                    string status = limit.Status?.Trim().ToLower() ?? "";

                    _logger.LogInformation(
                        "FP={fpId} | MaxLimit={Max} | Current={Current} | Status={Status} | IsAllowed={IsAllowed}",
                        limit.FpId, limit.MaxLimit, limit.CurrentCount, limit.Status, limit.IsAllowed
                    );

                    // 🔴 OVERRIDE LOGIC — IsAllowed = 1
                    if (limit.IsAllowed)
                    {
                        //if (status == "unavailable")
                        if (status.ToLower().Contains("unavailable"))
                        {
                            _logger.LogInformation(
                                "OVERRIDE ACTIVE — Unblocking FP={fpId} (IsAllowed=1)",
                                limit.FpId
                            );

                            UnblockPump(limit.FpId.ToString("00"));
                            await repo.InsertBlockUnbloclHistory(
                                limit.FpId,
                                "Unblock",
                                "Middleware",
                                "IsAllowed override"
                            );

                            GetFpStatus(limit.FpId.ToString());
                            anyActionTaken = true;
                        }

                        // ⛔ Skip limit checks completely
                        continue;
                    }


                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pump limit for FP={fpId}", fpId);
                return false;
            }
        }

        public async Task<bool> CheckAndApplyPumpLimitAsync(int fpId, IServiceProvider serviceProvider, bool isAllowedChange, bool isAllowed)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

                //if (isAllowedChange)
                //{
                //    await repo.UpdateIsAllowedAsync(fpId, isAllowed);
                //}
                var limits = await repo.GetTransactionLimitCountByFpId_Block(fpId);

                if (limits == null || !limits.Any())
                {
                    _logger.LogWarning("No limit records found for FP={fpId}", fpId);
                    return false;
                }

                bool anyActionTaken = false;

                foreach (var limit in limits)
                {
                    //if (ShouldSkipFpAction(limit.FpId))
                    //{
                    //    _logger.LogInformation(
                    //        "Skipping FP={fpId} — action within last 3 seconds",
                    //        limit.FpId
                    //    );
                    //    continue;
                    //}
                    string status = limit.Status?.Trim().ToLower() ?? "";

                    _logger.LogInformation(
                        "FP={fpId} | MaxLimit={Max} | Current={Current} | Status={Status} | IsAllowed={IsAllowed}",
                        limit.FpId, limit.MaxLimit, limit.CurrentCount, limit.Status, limit.IsAllowed
                    );

                    // 🔴 OVERRIDE LOGIC — IsAllowed = 1
                    if (limit.IsAllowed)
                    {
                        //if (status == "unavailable")
                        if (status.ToLower().Contains("unavailable"))
                        {
                            _logger.LogInformation(
                                "OVERRIDE ACTIVE — Unblocking FP={fpId} (IsAllowed=1)",
                                limit.FpId
                            );

                            UnblockPump(limit.FpId.ToString("00"));
                            await repo.InsertBlockUnbloclHistory(
                                limit.FpId,
                                "Unblock",
                                "Middleware",
                                "IsAllowed override"
                            );

                            GetFpStatus(limit.FpId.ToString());
                            anyActionTaken = true;
                        }

                        // ⛔ Skip limit checks completely
                        continue;
                    }

                    if (limit.CurrentCount >= limit.MaxLimit &&  !status.ToLower().Contains("unavailable"))
                    {
                        _logger.LogWarning("LIMIT REACHED — Blocking FP={fpId}", limit.FpId);

                        EmergencyBlock(limit.FpId.ToString("00"));
                        await repo.InsertBlockUnbloclHistory(
                            limit.FpId,
                            "Blocked",
                            "Middleware",
                            "Limit reached"
                        );

                        GetFpStatus(limit.FpId.ToString());
                        anyActionTaken = true;
                    }
                    else if (limit.CurrentCount < limit.MaxLimit && status.ToLower().Contains("unavailable"))
                    {
                        _logger.LogInformation("LIMIT AVAILABLE — Unblocking FP={fpId}", limit.FpId);

                        UnblockPump(limit.FpId.ToString("00"));
                        await repo.InsertBlockUnbloclHistory(
                            limit.FpId,
                            "Unblock",
                            "Middleware",
                            "Limit available"
                        );

                        GetFpStatus(limit.FpId.ToString());
                        anyActionTaken = true;
                    }
                }

                return anyActionTaken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pump limit for FP={fpId}", fpId);
                return false;
            }
        }
        //public async Task<bool> CheckAndApplyPumpLimitAsync(int fpId, IServiceProvider serviceProvider)
        //{
        //    try
        //    {
        //        using var scope = serviceProvider.CreateScope();
        //        var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

        //        var limits = await repo.GetTransactionLimitCountByFpId(fpId); // now returns List<FpLimitDto>

        //        if (limits == null || !limits.Any())
        //        {
        //            _logger.LogWarning("No limit records found for FP={fpId}", fpId);
        //            return false;
        //        }

        //        bool anyActionTaken = false;

        //        foreach (var limit in limits)   // regardless of fpId or 0, iterate list
        //        {
        //            string status = limit.Status?.Trim().ToLower() ?? "";

        //            _logger.LogInformation("FP={fpId} | MaxLimit={Max} | Current={Current} | Status={Status}",
        //                limit.FpId, limit.MaxLimit, limit.CurrentCount, limit.Status);

        //            if (limit.CurrentCount >= limit.MaxLimit && status != "unavailable")
        //            {
        //                _logger.LogWarning("LIMIT REACHED — Blocking FP={fpId}", limit.FpId);
        //                EmergencyBlock(limit.FpId.ToString("00"));
        //                await repo.InsertBlockUnbloclHistory(limit.FpId, "Blocked", "Middleware", "Request from Middleware");
        //                GetFpStatus(limit.FpId.ToString());
        //                anyActionTaken = true;
        //            }
        //            else if (limit.CurrentCount < limit.MaxLimit && status == "unavailable")
        //            {
        //                _logger.LogInformation("LIMIT AVAILABLE — Unblocking FP={fpId}", limit.FpId);
        //                UnblockPump(limit.FpId.ToString("00"));
        //                await repo.InsertBlockUnbloclHistory(limit.FpId, "Unblock", "Middleware", "Request from Middleware");
        //                GetFpStatus(limit.FpId.ToString());
        //                anyActionTaken = true;
        //            }
        //        }

        //        return anyActionTaken;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error checking pump limit for FP={fpId}", fpId);
        //        return false;
        //    }
        //}

        public bool EmergencyBlock(string fpId = "01")
        {
            var msg = new Dictionary<string, object>
                      {
                          { "name", "estop_Fp_req" },
                          { "subCode", "00H" },
                          { "data", new Dictionary<string, object>
                              {
                                  { "FpId", fpId },
                                  { "PosId", "00" }   // no POS lock needed
                              }
                          }
                      };

            _logger.LogInformation("EMERGENCY STOP SENT → FP={0}", fpId);

            return SendJplMessage(msg);
        }
        public bool UnblockPump(string fpId = "01")
        {
            var msg = new Dictionary<string, object>
                    {
                        { "name", "cancel_FpEstop_req" },
                        { "subCode", "00H" },
                        { "data", new Dictionary<string, object>
                            {
                                { "FpId", fpId },
                                { "PosId", "00" }
                            }
                        }
                    };

            _logger.LogInformation("UNBLOCK (cancel estop) sent for FP={0}", fpId);
            return SendJplMessage(msg);
        }

        public bool SoftLock(string fpId = "01")
        {
            var msg = new Dictionary<string, object>
                        {
                            { "name", "close_Fp_req" },
                            { "subCode", "00H" },
                            { "data", new Dictionary<string, object>
                                {
                                    { "FpId", fpId }
                                }
                            }
                        };

            _logger.LogInformation("Soft Lock (Close) Fuelling Point FP={0}", fpId);

            return SendJplMessage(msg); // Just send, no response handling
        }

        public bool Unlock(string fpId = "01")
        {
            var msg = new Dictionary<string, object>
                        {
                            { "name", "open_Fp_req" },
                            { "subCode", "00H" },
                            { "data", new Dictionary<string, object>
                                {
                                    { "FpId", fpId }
                                }
                            }
                        };

            _logger.LogInformation("🔓 Unlocking Fuelling Point FP={0}", fpId);

            return SendJplMessage(msg); // Just send, no response handling
        }
        public bool GetFpStatus(string fpId = "01")
        {
            var msg = new Dictionary<string, object>
                    {
                        { "name", "FpStatus_req" },
                        { "subCode", "00H" },
                        { "data", new Dictionary<string, object>
                            {
                                { "FpId", fpId }
                            }
                        }
                    };

            _logger.LogInformation("📤 Sending FpStatus request for FP={0}", fpId);

            return SendJplMessage(msg);
        }
    }

    public class WorkerOptions
    {
        public string Host { get; set; } /*= "10.156.55.15";*/
        public int? JplPort { get; set; } = 8888;
        public string? FcAccessCode { get; set; }
        public string? countryCode { get; set; }
        public string? posVersionId { get; set; }
        public string? posId { get; set; }
        public List<int> Ports { get; set; } = new() { 8888 };

    }

    public static class NativePopup
    {
        private const uint MB_YESNO = 0x00000004;
        private const uint MB_ICONQUESTION = 0x00000020;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        // Callback event
        public static event Action<bool> OnPopupResult;

        // THIS version matches your signature but is non-blocking
        public static bool Ask(string title, string message)
        {
            // Immediately return false (or true)—your worker will NOT block.
            // The REAL popup is shown on a background thread.
            ShowPopupAsync(title, message);
            return false; // Worker continues immediately
        }

        private static void ShowPopupAsync(string title, string message)
        {
            Thread t = new Thread(() =>
            {
                int result = MessageBox(IntPtr.Zero, message, title, MB_YESNO | MB_ICONQUESTION);
                bool accepted = (result == 6);

                // Fire callback
                OnPopupResult?.Invoke(accepted);
            });

            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }


}
