using DPPMiddleware.Helpers;
using DPPMiddleware.IRepository;
using DPPMiddleware.Models;
using DppMiddleWareService.Models;
using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using static DPPMiddleware.Helpers.TableHelper;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

public class TransactionRepository : ITransactionRepository
{
    private readonly AppDbContext _context;

    public TransactionRepository(AppDbContext context)
    {
        _context = context;
    }

    public void InsertTransaction(TransactionEntity txn, DppMessage dppMessage)
    {
        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_InsertEvent", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        // Common parameters
        cmd.Parameters.AddWithValue("@TransactionId", txn.TransactionId);
        cmd.Parameters.AddWithValue("@HexMessage", txn.HexMessage);
        cmd.Parameters.AddWithValue("@ParsedJson", txn.ParsedJson ?? "{}");
        cmd.Parameters.AddWithValue("@Port", (object?)txn.Port ?? DBNull.Value);

        cmd.Parameters.AddWithValue("@EventName", dppMessage.Name);
        cmd.Parameters.AddWithValue("@ExtCode", dppMessage.EXTC);
        cmd.Parameters.AddWithValue("@SubCode", dppMessage.SubCode);
        cmd.Parameters.AddWithValue("@FpId", dppMessage.Data?.GetType().GetProperty("FpId")?.GetValue(dppMessage.Data)?.ToString() ?? (object)DBNull.Value);

        // Conditional: add event-specific params
        if (dppMessage.Name == "FpStatus_resp" && dppMessage.Data != null)
        {
            dynamic data = dppMessage.Data;
            cmd.Parameters.AddWithValue("@SmId", data.SmId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpMainStateValue", data.FpMainState?.Value ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpMainStateName", data.FpMainState?.Name ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpSubStates", data.FpSubStates ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpLockId", data.FpLockId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FcGradeId", data.FcGradeId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpSubStates2", data.Supplemental?.FpSubStates2 ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpGradeOptionNo", data.Supplemental?.FpGradeOptionNo ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FuellingDataVol_e", data.Supplemental?.FuellingDataVol_e ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FuellingDataMon_e", data.Supplemental?.FuellingDataMon_e ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@AttendantAccountId", data.Supplemental?.AttendantAccountId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpBlockingStatus", data.Supplemental?.FpBlockingStatus ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpOperationModeNo", data.Supplemental?.FpOperationModeNo ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@PgId", data.Supplemental?.PgId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@NozzleTagReaderId", data.Supplemental?.NozzleTagReaderId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpSubStates3", data.Supplemental?.FpSubStates3 ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpAlarmStatus", data.Supplemental?.FpAlarmStatus ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FpSubStates4", data.Supplemental?.FpSubStates4 ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@AvailableSms", BuildFpAvailableSmsTable(data.Supplemental))
        .SqlDbType = SqlDbType.Structured;
            cmd.Parameters["@AvailableSms"].TypeName = "dbo.Tvp_FpAvailableSms";

            cmd.Parameters.AddWithValue("@AvailableGrades", BuildFpAvailableGradesTable(data.Supplemental))
                .SqlDbType = SqlDbType.Structured;
            cmd.Parameters["@AvailableGrades"].TypeName = "dbo.Tvp_FpAvailableGrades";

            cmd.Parameters.AddWithValue("@NozzleIds", BuildFpNozzleIdTable(data.Supplemental))
                .SqlDbType = SqlDbType.Structured;
            cmd.Parameters["@NozzleIds"].TypeName = "dbo.Tvp_FpNozzleId";

            cmd.Parameters.AddWithValue("@MinPresetValues", BuildMinPresetValuesTable(data.Supplemental))
                .SqlDbType = SqlDbType.Structured;
            cmd.Parameters["@MinPresetValues"].TypeName = "dbo.Tvp_FpMinPresetValues";
        }
        else if (dppMessage.Name == "FpTotals_resp" && dppMessage.Data != null)
        {
            dynamic data = dppMessage.Data;
            cmd.Parameters.AddWithValue("@TotalVol", data.TotalVol ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@TotalMoney", data.TotalMoney ?? (object)DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }
    public void HandlePriceSetRequest(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;

        // 🔥 Go inside "data"
        var data = root.GetProperty("data");

        var gradeIds = data.GetProperty("FcGradeId")
                           .EnumerateArray()
                           .Select(x => Convert.ToInt32(x.GetString()))
                           .ToList();

        var priceGroups = data
    .GetProperty("FcPriceGroups")
    .EnumerateArray()     // first level array
    .First()              // take first group
    .EnumerateArray()     // inner array of prices
    .Select(x => x.GetString())
    .ToList();

        var table = new DataTable();
        table.Columns.Add("Grade", typeof(int));
        table.Columns.Add("Price", typeof(decimal));

        for (int i = 0; i < gradeIds.Count; i++)
        {
            int gradeId = gradeIds[i];
            int unitPrice = Convert.ToInt32(priceGroups[i]); // raw FCC value

            table.Rows.Add(gradeId, unitPrice);
        }

        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_UpsertGradePrice_Bulk", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        var tvp = cmd.Parameters.Add("@GradePrices", SqlDbType.Structured);
        tvp.TypeName = "dbo.Tvp_GradePrice";
        tvp.Value = table;

        cmd.ExecuteNonQuery();
    }

    
    public string InsertTransactions(TransactionEntity txn, DppMessage dppMessage, string MasterResetKey)
    {
        //if (dppMessage != null)
        {
            switch (dppMessage.Name)
            {
                case "FpStatus_resp":
                    InsertEvent_FpStatus(txn, dppMessage);
                    break;
                case "FpSupTransBufStatus_resp":
                   return InsertEvent_FpSupTransBufStatus(txn, dppMessage,MasterResetKey);
                    break;
                default:
                    InsertEventBase(txn, dppMessage);
                    break;
            }
        }

        return string.Empty;

    }
    private void InsertEventBase(TransactionEntity txn, DppMessage dppMessage)
    {
        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_InsertEventBase", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@TransactionId", txn.TransactionId);
        cmd.Parameters.AddWithValue("@HexMessage", txn.HexMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ParsedJson", txn.ParsedJson ?? "{}");
        cmd.Parameters.AddWithValue("@Port", txn.Port ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@EventName", dppMessage.Name);
        cmd.Parameters.AddWithValue("@ExtCode", dppMessage.EXTC ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@SubCode", dppMessage.SubCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpId", dppMessage.Data?.GetType().GetProperty("FpId")?.GetValue(dppMessage.Data)?.ToString() ?? (object)DBNull.Value);

        // Output param for EventDetailsId if needed
        var outputParam = new SqlParameter("@EventDetailsId", SqlDbType.Int)
        {
            Direction = ParameterDirection.Output
        };
        cmd.Parameters.Add(outputParam);

        cmd.ExecuteNonQuery();

        // Optionally read EventDetailsId for further processing
        int eventDetailsId = (int)(outputParam.Value ?? 0);
    }
    private void InsertEvent_FpStatus(TransactionEntity txn, DppMessage dppMessage)
    {
        if (dppMessage.Data == null) return;

        dynamic data = dppMessage.Data;

        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_InsertEvent_FpStatus", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        // Common params
        cmd.Parameters.AddWithValue("@TransactionId", txn.TransactionId);
        cmd.Parameters.AddWithValue("@HexMessage", txn.HexMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ParsedJson", txn.ParsedJson ?? "{}");
        cmd.Parameters.AddWithValue("@Port", txn.Port ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@EventName", dppMessage.Name);
        cmd.Parameters.AddWithValue("@ExtCode", dppMessage.EXTC ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@SubCode", dppMessage.SubCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpId", data.FpId ?? (object)DBNull.Value);

        // Event-specific params
        cmd.Parameters.AddWithValue("@SmId", data.SmId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpMainStateValue", data.FpMainState?.Value ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpMainStateName", data.FpMainState?.Name ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpSubStates", data.FpSubStates ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpLockId", data.FpLockId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FcGradeId", data.FcGradeId ?? (object)DBNull.Value);

        cmd.Parameters.AddWithValue("@FpSubStates2", data.Supplemental?.FpSubStates2 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpGradeOptionNo", data.Supplemental?.FpGradeOptionNo ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FuellingDataVol_e", data.Supplemental?.FuellingDataVol_e ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FuellingDataMon_e", data.Supplemental?.FuellingDataMon_e ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AttendantAccountId", data.Supplemental?.AttendantAccountId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpBlockingStatus", data.Supplemental?.FpBlockingStatus ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpOperationModeNo", data.Supplemental?.FpOperationModeNo ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@PgId", data.Supplemental?.PgId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@NozzleTagReaderId", data.Supplemental?.NozzleTagReaderId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpSubStates3", data.Supplemental?.FpSubStates3 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpAlarmStatus", data.Supplemental?.FpAlarmStatus ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpSubStates4", data.Supplemental?.FpSubStates4 ?? (object)DBNull.Value);

        // TVPs
        AddTvps(cmd, "@AvailableSms", "dbo.Tvp_FpAvailableSms", BuildFpAvailableSmsTable(data.Supplemental));
        AddTvps(cmd, "@AvailableGrades", "dbo.Tvp_FpAvailableGrades", BuildFpAvailableGradesTable(data.Supplemental));
        AddTvps(cmd, "@NozzleIds", "dbo.Tvp_FpNozzleId", BuildFpNozzleIdTable(data.Supplemental));
        AddTvps(cmd, "@MinPresetValues", "dbo.Tvp_FpMinPresetValues", BuildMinPresetValuesTable(data.Supplemental));

        cmd.ExecuteNonQuery();
    }

    private string InsertEvent_FpSupTransBufStatus(TransactionEntity txn, DppMessage dppMessage,string MasterResetKey)
    {
       // string loggerstr = string.Empty;
        var log = new StringBuilder();
        log.AppendLine("========== FpSupTransBufStatus_resp ==========");
        if (dppMessage?.Data == null)
            return "";

        Console.WriteLine("FCC Raw Data:");
        Console.WriteLine(JsonSerializer.Serialize(dppMessage.Data));

        // Convert Data to JSON
        var data = JsonSerializer.Serialize(dppMessage.Data);
        
        // Parse original transaction JSON
        using var doc = JsonDocument.Parse(txn.ParsedJson);
        var root = doc.RootElement;

        // Deserialize FCC Device DTO
        var dto = JsonSerializer.Deserialize<FpSupDataDto>(data);
        if (dto == null)
            return "";

        log.AppendLine($"RAW dto.MoneyDue : {dto.MoneyDue}");
        log.AppendLine($"RAW dto.Volume   : {dto.Volume}");
        // FCC sends Volume & MoneyDue multiplied by 100 (520 => 5.20)
        decimal formattedMoneyDue = 0m;
        //string formattedMoneyDue = null;
        if (!string.IsNullOrWhiteSpace(dto.MoneyDue.ToString()) &&
            decimal.TryParse(dto.MoneyDue.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var moneyDueRaw))
        {
            if (moneyDueRaw != 0)
            {
                // FCC sends *100
                formattedMoneyDue = moneyDueRaw;
            }
            else
            {
                formattedMoneyDue = 0;

            }

        }
        log.AppendLine($"RAW dto.MoneyDue : {dto.MoneyDue}");
        log.AppendLine($"RAW dto.Volume   : {dto.Volume}");
        decimal formattedVolume = 0m;
        if (dto.Volume != 0)
        {
            if (decimal.TryParse(dto.Volume.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var volumeRaw))
            {
                //formattedVolume = (volumeRaw/100).ToString("0.00", CultureInfo.InvariantCulture);
                formattedVolume = volumeRaw/100m;
            }
        }
        else
        {
            formattedVolume = 0;

        }

        log.AppendLine($"formattedMoneyDue : {formattedMoneyDue}");
        log.AppendLine($"formattedVolume   : {formattedVolume}");

        // Create final response object
        var currentTrns = new FpSupTransBufStatusResponse
        {
            FpId = dto.FpId ?? "",

            TransInSupBuffer = new List<TransInSupBuffer>
    {
        new TransInSupBuffer
        {
            TransSeqNo = dto.TransSeqNo ?? "",
            SmId = root.TryGetProperty("SmId", out var sm) ? sm.GetString() : string.Empty,
            TransLockId = root.TryGetProperty("TransLockId", out var tl) ? tl.GetString() : string.Empty,

            TransInfoMask = new TransInfoMaskDto
            {
                Value = string.Empty,
                Bits = new Dictionary<string, int>()
            },

            MoneyDue = formattedMoneyDue,
            Vol = formattedVolume,
            FcGradeId = dto.GradeId,
            UnitPrice = dto.UnitPrice
        }
    }
        };

        // Save into DB
        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_InsertEvent_FpSupTransBufStatus", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        // Common parameters
        cmd.Parameters.AddWithValue("@TransactionId", currentTrns.TransInSupBuffer[0].TransSeqNo);
        cmd.Parameters.AddWithValue("@HexMessage", txn.HexMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ParsedJson", txn.ParsedJson ?? "{}");
        cmd.Parameters.AddWithValue("@Port", txn.Port ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@EventName", dppMessage.Name);
        cmd.Parameters.AddWithValue("@ExtCode", dppMessage.EXTC ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@SubCode", dppMessage.SubCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FpId", currentTrns.FpId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@MasterResetKey", MasterResetKey ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@NozzleId", txn.EventDetails.NozzleId ?? (object)DBNull.Value);
        // TVP for child records
        var transBufferTable = BuildTransInSupBufferTable(currentTrns);

        foreach (DataRow r in transBufferTable.Rows)
        {
            log.AppendLine("---- TVP Row ----");
            log.AppendLine($"MoneyDue : {r["MoneyDue"]}");
            log.AppendLine($"Vol      : {r["Vol"]}");
        }
        var tvpParam = cmd.Parameters.AddWithValue("@TransInSupBuffer", transBufferTable);
        tvpParam.SqlDbType = SqlDbType.Structured;
        tvpParam.TypeName = "dbo.Tvp_TransInSupBuffer";

        cmd.ExecuteNonQuery();
        log.AppendLine("=============================================");
        return log.ToString();
    }


    private void AddTvps(SqlCommand cmd, string paramName, string typeName, DataTable table)
    {
        var param = cmd.Parameters.AddWithValue(paramName, table ?? new DataTable());
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName = typeName;
    }
    public void InsertTransactionLogging(TransactionEntity txn)
    {
        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_InsertTransaction", conn);
        cmd.CommandType = System.Data.CommandType.StoredProcedure;

        cmd.Parameters.AddWithValue("@TransactionId", txn.TransactionId);
        cmd.Parameters.AddWithValue("@HexMessage", txn.HexMessage);
        cmd.Parameters.AddWithValue("@ParsedJson", txn.ParsedJson ?? "{}");
        cmd.Parameters.AddWithValue("@Port", (object?)txn.Port ?? DBNull.Value);

        cmd.ExecuteNonQuery();
    }
    public void UpdateTransactionSyncStatus(string transactionId, bool isSynced, int retryCount, DateTime lastAttempt, string? odooAckId)
    {
        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_UpdateTransactionSyncStatus", conn);
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@TransactionId", transactionId);
        cmd.Parameters.AddWithValue("@IsSynced", isSynced);
        cmd.Parameters.AddWithValue("@RetryCount", retryCount);
        cmd.Parameters.AddWithValue("@LastAttempt", lastAttempt);
        cmd.Parameters.AddWithValue("@OdooAckId", (object?)odooAckId ?? DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    public IEnumerable<TransactionEntity> GetPendingTransactions()
    {
        var txns = new List<TransactionEntity>();

        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_GetPendingTransactions", conn);
        cmd.CommandType = System.Data.CommandType.StoredProcedure;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            txns.Add(new TransactionEntity
            {
                Id = Convert.ToInt32(reader["Id"]),
                TransactionId = reader["TransactionId"].ToString()!,
                HexMessage = reader["HexMessage"].ToString()!,
                ParsedJson = reader["ParsedJson"].ToString()!,
                IsSynced = Convert.ToBoolean(reader["IsSynced"]),
                RetryCount = Convert.ToInt32(reader["RetryCount"]),
                LastAttempt = reader["LastAttempt"] == DBNull.Value ? null : Convert.ToDateTime(reader["LastAttempt"]),
                OdooAckId = reader["OdooAckId"]?.ToString(),
                CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                UpdatedAt = Convert.ToDateTime(reader["UpdatedAt"])
            });
        }

        return txns;
    }
    private DataTable BuildTransInSupBufferTable(FpSupTransBufStatusResponse data)
    {
        var table = new DataTable();
        table.Columns.Add("TransSeqNo", typeof(string));
        table.Columns.Add("SmId", typeof(string));
        table.Columns.Add("TransLockId", typeof(string));
        table.Columns.Add("TransInfoMaskValue", typeof(string));
        table.Columns.Add("TransInfoMaskBits", typeof(string));
        table.Columns.Add("MoneyDue", typeof(decimal));
        table.Columns.Add("Vol", typeof(decimal));
        table.Columns.Add("FcGradeId", typeof(string));
        table.Columns.Add("UnitPrice", typeof(decimal));

        foreach (var t in data.TransInSupBuffer)
        {
            decimal moneyRaw = 0m;
            decimal volRaw = 0m;

            decimal.TryParse(t.MoneyDue.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out moneyRaw);
            //decimal.TryParse(t.Vol.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out volRaw);

            // FCC sends *100
            decimal money = moneyRaw / 10m;
            decimal vol = volRaw;

            decimal unitPriceRaw = 0m;
            decimal.TryParse(t.UnitPrice.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out unitPriceRaw);
            decimal unitPrice = unitPriceRaw / 100m;   // FCC rule

            table.Rows.Add(
                t.TransSeqNo,
                t.SmId,
                t.TransLockId,
                t.TransInfoMask?.Value,
                JsonSerializer.Serialize(t.TransInfoMask?.Bits ?? new Dictionary<string, int>()),
                money,          // ✅ decimal
                t.Vol,            // ✅ decimal
                t.FcGradeId,
                t.UnitPrice     // ✅ decimal
            );
        }

        return table;
    }
    public IEnumerable<SocketViewModel> GetByReferenceId(string referenceId)
    {
        var results = new List<SocketViewModel>();

        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("sp_GetTransactionByReferenceId", conn);
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@refId", referenceId);

        using var reader = cmd.ExecuteReader();

        // Map by FpStatusCallId
        var socketMap = new Dictionary<int, SocketViewModel>();

        // 1. FpStatus base row
        while (reader.Read())
        {
            if (reader["FpStatusCallId"] == DBNull.Value)
                continue;

            var fpStatus = new FpStatusResponse
            {
                FpId = reader["FpId"]?.ToString(),
                SmId = reader["SmId"]?.ToString(),
                FpMainState = new FpMainStateDto
                {
                    Value = reader["FpMainStateValue"]?.ToString(),
                    Name = reader["FpMainStateName"]?.ToString()
                },
                FpSubStates = reader["FpSubStates"]?.ToString(),
                FpLockId = reader["FpLockId"]?.ToString(),
                FcGradeId = reader["FcGradeId"]?.ToString(),
                Supplemental = new SupplementalStatus
                {
                    FpSubStates2 = reader["FpSubStates2"]?.ToString(),
                    FpGradeOptionNo = reader["FpGradeOptionNo"]?.ToString(),
                    FuellingDataVol_e = reader["FuellingDataVol_e"]?.ToString(),
                    FuellingDataMon_e = reader["FuellingDataMon_e"]?.ToString(),
                    AttendantAccountId = reader["AttendantAccountId"]?.ToString(),
                    FpBlockingStatus = reader["FpBlockingStatus"]?.ToString(),
                    FpOperationModeNo = reader["FpOperationModeNo"]?.ToString(),
                    PgId = reader["PgId"]?.ToString(),
                    NozzleTagReaderId = reader["NozzleTagReaderId"]?.ToString(),
                    FpSubStates3 = reader["FpSubStates3"]?.ToString(),
                    FpAlarmStatus = reader["FpAlarmStatus"]?.ToString(),
                    FpSubStates4 = reader["FpSubStates4"]?.ToString()
                }
            };

            var socket = new SocketViewModel
            {
                FpStatusResponse = fpStatus
            };

            int callId = (int)reader["FpStatusCallId"];
            socketMap[callId] = socket;
            results.Add(socket);
        }

        // 2. FpAvailableSms
        if (reader.NextResult())
        {
            while (reader.Read())
            {
                int callId = (int)reader["FpStatusCallId"];
                if (socketMap.TryGetValue(callId, out var socket))
                {
                    var sms = new FpAvailableSmsDto
                    {
                        SmsId = reader["SmsId"]?.ToString(),
                        SmId = reader["SmId"]?.ToString()
                    };

                    // socket.FpAvailableSms = sms;
                    socket.FpStatusResponse!.Supplemental.FpAvailableSms = sms;
                }
            }
        }

        // 3. FpAvailableGrades
        if (reader.NextResult())
        {
            while (reader.Read())
            {
                int callId = (int)reader["FpStatusCallId"];
                if (socketMap.TryGetValue(callId, out var socket))
                {
                    if (socket.FpStatusResponse!.Supplemental.FpAvailableGrades == null)
                    {
                        socket.FpStatusResponse!.Supplemental.FpAvailableGrades = new FpAvailableGradesDto
                        {
                            Count = reader["Count"]?.ToString(),
                            GradeIds = new List<string>()
                        };
                        //socket.FpStatusResponse!.Supplemental.FpAvailableGrades = socket.FpAvailableGrades;
                    }
                }
            }
        }

        // 4. NozzleId
        if (reader.NextResult())
        {
            while (reader.Read())
            {
                int callId = (int)reader["FpStatusCallId"];
                if (socketMap.TryGetValue(callId, out var socket))
                {
                    var nozzle = new NozzleIdDto
                    {
                        Id = reader["NozzleId"]?.ToString(),
                        AsciiCode = reader["AsciiCode"]?.ToString(),
                        AsciiChar = reader["AsciiChar"]?.ToString()
                    };

                    //socket.NozzleId = nozzle;
                    socket.FpStatusResponse!.Supplemental.NozzleId = nozzle;
                }
            }
        }

        // 5. MinPresetValues
        if (reader.NextResult())
        {
            while (reader.Read())
            {
                int callId = (int)reader["FpStatusCallId"];
                if (socketMap.TryGetValue(callId, out var socket))
                {
                    var preset = new MinPresetValue
                    {
                        FcGradeId = reader["FcGradeId"]?.ToString(),
                        MinMoneyPreset_e = reader["MinMoneyPreset_e"]?.ToString(),
                        MinVolPreset_e = reader["MinVolPreset_e"]?.ToString()
                    };

                    //socket.MinPresetValues.Add(preset);
                    socket.FpStatusResponse!.Supplemental.MinPresetValues.Add(preset);
                }
            }
        }

        return results;
    }





    public void UpdateFpStatusById(FpSatus fpSatus)
    {
        using var conn = _context.CreateConnection();
        conn.Open();

        using var cmd = new SqlCommand("Sp_UpdateFuelPumbStatusByFpId", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@FpId", fpSatus.FpId);
        cmd.Parameters.AddWithValue("@Status", fpSatus.FpStatus);
        cmd.Parameters.AddWithValue("@Nozzle", fpSatus.NozzleId);
        cmd.Parameters.AddWithValue("@Vol", fpSatus.Volume);
        cmd.Parameters.AddWithValue("@Money", fpSatus.Money);
        cmd.Parameters.AddWithValue("@FpGradeOptionNo", fpSatus.FpGradeOptionNo);
        cmd.Parameters.AddWithValue("@IsOnline", fpSatus.IsOnline);


        cmd.ExecuteNonQuery();

        // Optionally read EventDetailsId for further processing
    }

    public async Task<List<FuelPumpStatusDto>> GetAllFpStatus()
    {
        try
        {
            List<FuelPumpStatusDto> fpStatus = new List<FuelPumpStatusDto>();

            using var conn = _context.CreateConnection();
            await conn.OpenAsync();

            using var cmd = new SqlCommand("Sp_GetFpLatestStatus", conn);
            cmd.CommandType = CommandType.StoredProcedure;



            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var status = reader["Status"]?.ToString() ?? string.Empty;
                fpStatus.Add(new FuelPumpStatusDto
                {
                    pump_number = reader["FpId"] != DBNull.Value ? Convert.ToInt32(reader["FpId"]) : 0,
                    nozzle_number = reader["Nozzle"] != DBNull.Value ? Convert.ToInt32(reader["Nozzle"]) : 0,
                    volume = reader["Vol"] != DBNull.Value ? Convert.ToDecimal(reader["Vol"]) : 0,
                    amount = reader["Money"] != DBNull.Value ? Convert.ToDecimal(reader["Money"]) : 0,
                    status = status,
                    attendant = reader["attendantId"]?.ToString() ?? string.Empty,
                    count = reader["Count"] != DBNull.Value ? Convert.ToInt32(reader["Count"]) : 0,
                    FpGradeOptionNo = reader["FpGradeOptionNo"] != DBNull.Value ? Convert.ToInt32(reader["FpGradeOptionNo"]) : 0,
                    unit_price = reader["UnitPrice"] != DBNull.Value ? Convert.ToDecimal(reader["UnitPrice"]) : 0,
                    // isOnline = reader["IsOnline"] != DBNull.Value ? Convert.ToBoolean(reader["IsOnline"]) : false
                    isOnline = status.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
                                    ? true
                                    : (reader["IsOnline"] != DBNull.Value && Convert.ToBoolean(reader["IsOnline"]))


                });
            }

            return fpStatus;
        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching Fuel Pump Status", ex);
        }
    }


    public IEnumerable<PumpTransactions> GetAllPumpTransactions()
    {
        try
        {
            var list = new List<PumpTransactions>();

            using var conn = _context.CreateConnection();
            conn.Open();

            using var cmd = new SqlCommand("sp_GetAllPumpTransactions", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<int, PumpTransactions>();

            while (reader.Read())
            {
                int callId = (int)reader["PumpTransactionCallId"];

                var pumpTransaction = new PumpTransactions
                {
                    TransactionId = reader["TransactionId"]?.ToString() ?? string.Empty,
                    PumpId = reader["PumpId"] != DBNull.Value ? Convert.ToInt32(reader["PumpId"]) : 0,
                    NozzleId = reader["NozzleId"] != DBNull.Value ? Convert.ToInt32(reader["NozzleId"]) : 0,
                    Attendant = reader["Attendant"]?.ToString() ?? string.Empty,
                    ProductId = reader["ProductId"]?.ToString() ?? string.Empty,
                    Quantity = reader["Quantity"] != DBNull.Value ? Convert.ToDecimal(reader["Quantity"]) : 0,
                    UnitPrice = reader["UnitPrice"] != DBNull.Value ? Convert.ToDecimal(reader["UnitPrice"]) : 0,
                    Total = reader["Total"] != DBNull.Value ? Convert.ToDecimal(reader["Total"]) : 0,
                    State = reader["State"]?.ToString() ?? string.Empty,
                    StartTime = reader["StartTime"] != DBNull.Value ? Convert.ToDateTime(reader["StartTime"]) : DateTime.MinValue,
                    EndTime = reader["EndTime"] != DBNull.Value ? Convert.ToDateTime(reader["EndTime"]) : DateTime.MinValue,
                    OrderUuid = reader["OrderUuid"]?.ToString() ?? string.Empty
                };

                map[callId] = pumpTransaction; // Store the parent row by its ID
                list.Add(pumpTransaction);
            }

            return list;
        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching pump transactions data", ex);
        }
    }

    public async Task<List<PumpTransactions>> GetUnsyncedTransactionsAsync(string type, int? fpId, int? nozzleId, DateTime? createdDate, string? referenceId)
    {
        try
        {
            List<PumpTransactions> transactions = new List<PumpTransactions>();

            using var conn = _context.CreateConnection();
            await conn.OpenAsync();

            using var cmd = new SqlCommand("sp_GetUnsyncedTransactions", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            // Add parameters for the filters
            cmd.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = string.IsNullOrEmpty(type) ? DBNull.Value : type;
            cmd.Parameters.Add("@FpId", SqlDbType.Int).Value = fpId ?? (object)DBNull.Value;
            cmd.Parameters.Add("@NozzleId", SqlDbType.Int).Value = nozzleId ?? (object)DBNull.Value;
            cmd.Parameters.AddWithValue("@CreatedDate", createdDate ?? (object)DBNull.Value);
            cmd.Parameters.Add("@ReferenceId", SqlDbType.NVarChar, 100).Value = string.IsNullOrEmpty(referenceId) ? DBNull.Value : referenceId;


            Console.WriteLine($"mode={type}, pump_id={fpId}, nozzle_id={nozzleId}, created_date={createdDate}, emp={referenceId}");

            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                transactions.Add(new PumpTransactions
                {
                    TransactionId = reader["transaction_id"]?.ToString() ?? string.Empty,
                    PumpId = reader["pump_id"] != DBNull.Value ? Convert.ToInt32(reader["pump_id"]) : 0,
                    NozzleId = reader["nozzle_id"] != DBNull.Value ? Convert.ToInt32(reader["nozzle_id"]) : 0,
                    Attendant = reader["attendant"]?.ToString() ?? string.Empty,
                    ProductId = reader["product_id"]?.ToString() ?? string.Empty,
                    Quantity = reader["qty"] != DBNull.Value ? Convert.ToDecimal(reader["qty"]) : 0,
                    UnitPrice = reader["unit_price"] != DBNull.Value ? Convert.ToDecimal(reader["unit_price"]) : 0,
                    Total = reader["total"] != DBNull.Value ? Convert.ToDecimal(reader["total"]) : 0,
                    State = reader["state"]?.ToString() ?? string.Empty,
                    StartTime = reader["start_time"] != DBNull.Value ? Convert.ToDateTime(reader["start_time"]) : DateTime.MinValue,
                    EndTime = reader["end_time"] != DBNull.Value ? Convert.ToDateTime(reader["end_time"]) : DateTime.MinValue,
                    OrderUuid = reader["order_uuid"]?.ToString() ?? string.Empty
                });
            }

            return transactions;
        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching unsynced transactions", ex);
        }
    }



    public async Task<List<PumpTransactions>> GetOfflineTransactionsForSyncAsync()
    {
        var list = new List<PumpTransactions>();

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_GetOfflineTransactionsForSync", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            list.Add(new PumpTransactions
            {
                Id = Convert.ToInt32(reader["Id"]),
                TransactionId = reader["TransactionId"]?.ToString() ?? "",
                PumpId = Convert.ToInt32(reader["PumpId"]),
                NozzleId = Convert.ToInt32(reader["NozzleId"]),
                Attendant = reader["Attendant"]?.ToString() ?? "",
                ProductId = reader["ProductId"]?.ToString() ?? "",
                Quantity = reader["Quantity"] != DBNull.Value ? Convert.ToDecimal(reader["Quantity"]) : 0,
                UnitPrice = reader["UnitPrice"] != DBNull.Value ? Convert.ToDecimal(reader["UnitPrice"]) : 0,
                Total = reader["Total"] != DBNull.Value ? Convert.ToDecimal(reader["Total"]) : 0,
                State = reader["State"]?.ToString() ?? "",
                StartTime = reader["StartTime"] != DBNull.Value ? Convert.ToDateTime(reader["StartTime"]) : DateTime.MinValue,
                EndTime = reader["EndTime"] != DBNull.Value ? Convert.ToDateTime(reader["EndTime"]) : DateTime.MinValue,
                OrderUuid = reader["OrderUuid"]?.ToString() ?? ""
            });
        }

        return list;
    }



    public async Task<bool> MarkTransactionsSyncedAsync(List<PumpTransactions> txns)
    {
        if (txns == null || txns.Count == 0)
            return false;

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        var ids = txns.Select(t => t.Id).ToList();
        var idList = string.Join(",", ids);

        using var cmd = new SqlCommand("sp_MarkTransactionsSynced", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@IdList", idList);

        int rows = await cmd.ExecuteNonQueryAsync();

        return rows > 0;
    }




    public async Task UpdateOrderUuidAsync(string transactionId, string orderUuid, int orderid, string state)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_UpdateOrderUuid", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@TransactionId", transactionId);
        cmd.Parameters.AddWithValue("@OrderUuid", (object?)orderUuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OrderId", orderid);
        cmd.Parameters.AddWithValue("@State", state);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateIsDiscard(TransactionDiscard parameters)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("UpdatePumpTransactionStatus", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@TransactionId", parameters.TransactionId);
        cmd.Parameters.AddWithValue("@Status", parameters.Status);
        cmd.Parameters.AddWithValue("@IsDiscard", parameters.IsDiscard);

        // Execute the stored procedure
        await cmd.ExecuteNonQueryAsync();
    }

    public IEnumerable<FpEntity> GetAllFpStatusWithEvents(string? fpId = null, string? nozzleId = null, DateTime? createdDate = null, string? referenceId = null)
    {
        try
        {
            var transactions = new List<FpEntity>();

            using var conn = _context.CreateConnection();
            conn.Open();

            using var cmd = new SqlCommand("sp_GetAllFpStatusWithEvents", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            // 🔹 Pass filter parameters to SP
            cmd.Parameters.AddWithValue("@FpId", (object?)fpId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NozzleId", (object?)nozzleId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedDate", (object?)createdDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            var statusMap = new Dictionary<int, SupplementalStatus>();

            while (reader.Read())
            {
                var fpStatusCallId = (int)reader["FpStatusCallId"];

                var txn = new FpEntity
                {
                    FpStatusResponse = new FpStatusResponse
                    {
                        FpId = reader["FpId"]?.ToString() ?? string.Empty,
                        SmId = reader["SmId"]?.ToString() ?? string.Empty,
                        FpMainState = new FpMainStateDto
                        {
                            Value = reader["FpMainStateValue"]?.ToString() ?? string.Empty,
                            Name = reader["FpMainStateName"]?.ToString() ?? string.Empty
                        },
                        FpSubStates = reader["FpSubStates"]?.ToString() ?? string.Empty,
                        FpLockId = reader["FpLockId"]?.ToString() ?? string.Empty,
                        FcGradeId = reader["FcGradeId"]?.ToString() ?? string.Empty,
                        Supplemental = new SupplementalStatus
                        {
                            FpSubStates2 = reader["FpSubStates2"]?.ToString() ?? string.Empty,
                            FpAvailableSms = new FpAvailableSmsDto
                            {
                                SmsId = string.Empty,
                                SmId = string.Empty
                            },
                            FpAvailableGrades = new FpAvailableGradesDto
                            {
                                Count = "0",
                                GradeIds = new List<string>()
                            },
                            FpGradeOptionNo = reader["FpGradeOptionNo"]?.ToString() ?? string.Empty,
                            FuellingDataVol_e = reader["FuellingDataVol_e"]?.ToString() ?? string.Empty,
                            FuellingDataMon_e = reader["FuellingDataMon_e"]?.ToString() ?? string.Empty,
                            AttendantAccountId = reader["AttendantAccountId"]?.ToString() ?? string.Empty,
                            FpBlockingStatus = reader["FpBlockingStatus"]?.ToString() ?? string.Empty,
                            NozzleId = new NozzleIdDto
                            {
                                Id = reader["NozzleId"]?.ToString() ?? string.Empty,
                                AsciiCode = reader["AsciiCode"]?.ToString() ?? string.Empty,
                                AsciiChar = reader["AsciiChar"]?.ToString() ?? string.Empty
                            },
                            FpOperationModeNo = reader["FpOperationModeNo"]?.ToString() ?? string.Empty,
                            PgId = reader["PgId"]?.ToString() ?? string.Empty,
                            NozzleTagReaderId = reader["NozzleTagReaderId"]?.ToString() ?? string.Empty,
                            FpSubStates3 = reader["FpSubStates3"]?.ToString() ?? string.Empty,
                            FpAlarmStatus = reader["FpAlarmStatus"]?.ToString() ?? string.Empty,
                            MinPresetValues = new List<MinPresetValue>(),
                            FpSubStates4 = reader["FpSubStates4"]?.ToString() ?? string.Empty,
                            ReferenceId = reader["ReferenceId"]?.ToString() ?? string.Empty
                        }
                    }
                };

                transactions.Add(txn);
                statusMap[fpStatusCallId] = txn.FpStatusResponse.Supplemental;
            }

            if (reader.NextResult())
            {
                while (reader.Read())
                {
                    int callId = (int)reader["FpStatusCallId"];
                    if (statusMap.TryGetValue(callId, out var supplemental) && supplemental.FpAvailableSms != null)
                    {
                        supplemental.FpAvailableSms.SmsId = reader["SmsId"]?.ToString() ?? string.Empty;
                        supplemental.FpAvailableSms.SmId = reader["SmId"]?.ToString() ?? string.Empty;
                    }
                }
            }

            if (reader.NextResult())
            {
                while (reader.Read())
                {
                    int callId = (int)reader["FpStatusCallId"];
                    if (statusMap.TryGetValue(callId, out var supplemental) && supplemental.FpAvailableGrades != null)
                    {
                        supplemental.FpAvailableGrades.Count = reader["Count"]?.ToString() ?? "0";
                        supplemental.FpAvailableGrades.GradeIds ??= new List<string>();
                        supplemental.FpAvailableGrades.GradeIds.Add(reader["GradeId"]?.ToString() ?? string.Empty);
                    }
                }
            }

            if (reader.NextResult())
            {
                while (reader.Read())
                {
                    int callId = (int)reader["FpStatusCallId"];
                    if (statusMap.TryGetValue(callId, out var supplemental))
                    {
                        supplemental.MinPresetValues.Add(new MinPresetValue
                        {
                            FcGradeId = reader["FcGradeId"]?.ToString() ?? string.Empty,
                            MinMoneyPreset_e = reader["MinMoneyPreset_e"]?.ToString() ?? string.Empty,
                            MinVolPreset_e = reader["MinVolPreset_e"]?.ToString() ?? string.Empty
                        });
                    }
                }
            }
            return transactions;

        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching FpStatus with events", ex);
        }
    }
    public IEnumerable<FpSupTransBufStatusResponse> GetAllFpSupTransBufStatus()
    {
        try
        {
            var results = new List<FpSupTransBufStatusResponse>();

            using var conn = _context.CreateConnection();
            conn.Open();

            using var cmd = new SqlCommand("sp_GetAllFpSupTransBufStatus", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            using var reader = cmd.ExecuteReader();
            var fpMap = new Dictionary<string, FpSupTransBufStatusResponse>();

            while (reader.Read())
            {
                var fpId = reader["FpId"]?.ToString() ?? string.Empty;

                if (!fpMap.ContainsKey(fpId))
                {
                    var response = new FpSupTransBufStatusResponse
                    {
                        FpId = fpId,
                        TransInSupBuffer = new List<TransInSupBuffer>()
                    };
                    fpMap[fpId] = response;
                    results.Add(response);
                }

                if (reader["TransSeqNo"] != DBNull.Value)
                {
                    var transBuffer = new TransInSupBuffer
                    {
                        TransSeqNo = reader["TransSeqNo"]?.ToString() ?? string.Empty,
                        SmId = reader["SmId"]?.ToString() ?? string.Empty,
                        TransLockId = reader["TransLockId"]?.ToString() ?? string.Empty,
                        MoneyDue = Convert.ToDecimal(reader["MoneyDue"]),
                        Vol = Convert.ToDecimal(reader["Vol"]?.ToString()),
                        FcGradeId = reader["FcGradeId"]?.ToString(),
                        TransInfoMask = new TransInfoMaskDto
                        {
                            Value = reader["TransInfoMaskValue"]?.ToString() ?? string.Empty,
                            Bits = new Dictionary<string, int>()
                        }
                    };

                    var bitsJson = reader["TransInfoMaskBits"]?.ToString();
                    if (!string.IsNullOrEmpty(bitsJson))
                    {
                        try
                        {
                            transBuffer.TransInfoMask.Bits = JsonSerializer.Deserialize<Dictionary<string, int>>(bitsJson) ?? new Dictionary<string, int>();
                        }
                        catch
                        {
                            // If deserialization fails, leave as empty dictionary
                        }
                    }

                    fpMap[fpId].TransInSupBuffer.Add(transBuffer);
                }
            }

            if (reader.NextResult())
            {
                while (reader.Read())
                {
                    var fpId = reader["FpId"]?.ToString() ?? string.Empty;
                    if (fpMap.TryGetValue(fpId, out var response))
                    {
                        var transBuffer = new TransInSupBuffer
                        {
                            TransSeqNo = reader["TransSeqNo"]?.ToString() ?? string.Empty,
                            SmId = reader["SmId"]?.ToString() ?? string.Empty,
                            TransLockId = reader["TransLockId"]?.ToString() ?? string.Empty,
                            MoneyDue = Convert.ToDecimal(reader["MoneyDue"].ToString()),
                            Vol = Convert.ToDecimal(reader["Vol"]?.ToString()),
                            FcGradeId = reader["FcGradeId"]?.ToString(),
                            TransInfoMask = new TransInfoMaskDto
                            {
                                Value = reader["TransInfoMaskValue"]?.ToString() ?? string.Empty,
                                Bits = new Dictionary<string, int>()
                            }
                        };

                        var bitsJson = reader["TransInfoMaskBits"]?.ToString();
                        if (!string.IsNullOrEmpty(bitsJson))
                        {
                            try
                            {
                                transBuffer.TransInfoMask.Bits = JsonSerializer.Deserialize<Dictionary<string, int>>(bitsJson) ?? new Dictionary<string, int>();
                            }
                            catch
                            {
                                // If deserialization fails, leave as empty dictionary
                            }
                        }

                        response.TransInSupBuffer.Add(transBuffer);
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching FpSupTransBufStatus", ex);
        }
    }
    public async Task<IEnumerable<PumpTransactions>> GetLatestTransactionsAsync(int pumpId, int nozzleId, string emp)
    {
        var transactions = new List<PumpTransactions>();

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_GetLatestTransactions", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@PumpId", pumpId);
        cmd.Parameters.AddWithValue("@NozzleId", nozzleId);
        cmd.Parameters.AddWithValue("@Emp", string.IsNullOrEmpty(emp) ? DBNull.Value : emp);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transactions.Add(new PumpTransactions
            {
                TransactionId = reader["transaction_id"]?.ToString() ?? string.Empty,
                PumpId = reader["pump_id"] != DBNull.Value ? Convert.ToInt32(reader["pump_id"]) : 0,
                NozzleId = reader["nozzle_id"] != DBNull.Value ? Convert.ToInt32(reader["nozzle_id"]) : 0,
                Attendant = reader["attendant"]?.ToString() ?? string.Empty,
                ProductId = reader["product_id"]?.ToString() ?? string.Empty,
                Quantity = reader["qty"] != DBNull.Value ? Convert.ToDecimal(reader["qty"]) : 0,
                UnitPrice = reader["unit_price"] != DBNull.Value ? Convert.ToDecimal(reader["unit_price"]) : 0,
                Total = reader["total"] != DBNull.Value ? Convert.ToDecimal(reader["total"]) : 0,
                State = reader["state"]?.ToString() ?? string.Empty,
                StartTime = reader["start_time"] != DBNull.Value ? Convert.ToDateTime(reader["start_time"]) : DateTime.MinValue,
                EndTime = reader["end_time"] != DBNull.Value ? Convert.ToDateTime(reader["end_time"]) : DateTime.MinValue,
                OrderUuid = reader["order_uuid"]?.ToString() ?? string.Empty
            });
        }

        return transactions;
    }
    public async Task<IEnumerable<PumpTransactions>> GetAllLatestTransactionsAsync(object data)
    {
        var transactions = new List<PumpTransactions>();

        string json = System.Text.Json.JsonSerializer.Serialize(data);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        int? pumpId = root.TryGetProperty("pump_id", out var p1) ? p1.GetInt32() : (int?)null;
        int? nozzleId = root.TryGetProperty("nozzle_id", out var p2) ? p2.GetInt32() : (int?)null;
        string emp = root.TryGetProperty("emp", out var p3) ? p3.GetString() : null;

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_GetUnsyncedLatestTransactions", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@Type", "All");
        cmd.Parameters.AddWithValue("@FpId", (object?)pumpId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NozzleId", (object?)nozzleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Emp", (object?)emp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedDate", DBNull.Value);
        cmd.Parameters.AddWithValue("@ReferenceId", DBNull.Value);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transactions.Add(new PumpTransactions
            {
                TransactionId = reader["transaction_id"]?.ToString() ?? string.Empty,
                PumpId = reader["pump_id"] != DBNull.Value ? Convert.ToInt32(reader["pump_id"]) : 0,
                NozzleId = reader["nozzle_id"] != DBNull.Value ? Convert.ToInt32(reader["nozzle_id"]) : 0,
                Attendant = reader["attendant"]?.ToString() ?? string.Empty,
                ProductId = reader["product_id"]?.ToString() ?? string.Empty,
                Quantity = reader["qty"] != DBNull.Value ? Convert.ToDecimal(reader["qty"]) : 0,
                UnitPrice = reader["unit_price"] != DBNull.Value ? Convert.ToDecimal(reader["unit_price"]) : 0,
                Total = reader["total"] != DBNull.Value ? Convert.ToDecimal(reader["total"]) : 0,
                State = reader["state"]?.ToString() ?? string.Empty,
                StartTime = reader["start_time"] != DBNull.Value ? Convert.ToDateTime(reader["start_time"]) : DateTime.MinValue,
                EndTime = reader["end_time"] != DBNull.Value ? Convert.ToDateTime(reader["end_time"]) : DateTime.MinValue,
                OrderUuid = reader["order_uuid"]?.ToString() ?? string.Empty
            });
        }

        return transactions;
    }
    private LatestTransactionRequest ParseRequest(object data)
    {
        var json = data.ToString();
        var arr = JsonSerializer.Deserialize<JsonElement>(json);

        var dict = new Dictionary<string, string>();

        foreach (var item in arr.EnumerateArray())
        {
            foreach (var prop in item.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.GetString() ?? prop.Value.ToString();
            }
        }

        return new LatestTransactionRequest
        {
            pump_id = dict.ContainsKey("pump_id") ? int.Parse(dict["pump_id"]) : (int?)null,
            nozzle_id = dict.ContainsKey("nozzle_id") ? int.Parse(dict["nozzle_id"]) : (int?)null,
            emp = dict.ContainsKey("emp") ? dict["emp"] : null,
            mode = dict.ContainsKey("mode") ? dict["mode"] : null
        };
    }
    public async Task<IEnumerable<PumpTransactions>> GetAllTransactionsAsync()
    {
        var transactions = new List<PumpTransactions>();

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_GetUnsyncedTransactions", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@Type", "All"); // required
        cmd.Parameters.AddWithValue("@FpId", DBNull.Value);
        cmd.Parameters.AddWithValue("@NozzleId", DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedDate", DBNull.Value);
        cmd.Parameters.AddWithValue("@ReferenceId", DBNull.Value);


        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transactions.Add(new PumpTransactions
            {
                TransactionId = reader["transaction_id"]?.ToString() ?? string.Empty,
                PumpId = reader["pump_id"] != DBNull.Value ? Convert.ToInt32(reader["pump_id"]) : 0,
                NozzleId = reader["nozzle_id"] != DBNull.Value ? Convert.ToInt32(reader["nozzle_id"]) : 0,
                Attendant = reader["attendant"]?.ToString() ?? string.Empty,
                ProductId = reader["product_id"]?.ToString() ?? string.Empty,
                Quantity = reader["qty"] != DBNull.Value ? Convert.ToDecimal(reader["qty"]) : 0,
                UnitPrice = reader["unit_price"] != DBNull.Value ? Convert.ToDecimal(reader["unit_price"]) : 0,
                Total = reader["total"] != DBNull.Value ? Convert.ToDecimal(reader["total"]) : 0,
                State = reader["state"]?.ToString() ?? string.Empty,
                StartTime = reader["start_time"] != DBNull.Value ? Convert.ToDateTime(reader["start_time"]) : DateTime.MinValue,
                EndTime = reader["end_time"] != DBNull.Value ? Convert.ToDateTime(reader["end_time"]) : DateTime.MinValue,
                OrderUuid = reader["order_uuid"]?.ToString() ?? string.Empty,
                PaymentId = reader["payment_id"]?.ToString(),
                AddToCart = reader["add_to_cart"] != DBNull.Value && Convert.ToBoolean(reader["add_to_cart"])
            });
        }

        return transactions;
    }

    public async Task<bool> UpdateAddToCartAsync(string transactionId, bool addToCart, string paymentId)
    {
        if (string.IsNullOrEmpty(transactionId))
            return false;

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("dbo.sp_UpdateAddToCart", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@TransactionId", transactionId);
        cmd.Parameters.AddWithValue("@AddToCart", addToCart ? 1 : 0);
        cmd.Parameters.AddWithValue("@PaymentId", paymentId);


        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var rows = Convert.ToInt32(reader["RowsAffected"]);
            return rows > 0;
        }

        return false;
    }


    public async Task<IEnumerable<PumpTransactions>> GetAllTransactionsAsyncAttendant()
    {
        var transactions = new List<PumpTransactions>();

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_GetUnsyncedTransactionsAttendant", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@Type", "All"); // required
        cmd.Parameters.AddWithValue("@FpId", DBNull.Value);
        cmd.Parameters.AddWithValue("@NozzleId", DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedDate", DBNull.Value);
        cmd.Parameters.AddWithValue("@ReferenceId", DBNull.Value);


        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            transactions.Add(new PumpTransactions
            {
                TransactionId = reader["transaction_id"]?.ToString() ?? string.Empty,
                PumpId = reader["pump_id"] != DBNull.Value ? Convert.ToInt32(reader["pump_id"]) : 0,
                NozzleId = reader["nozzle_id"] != DBNull.Value ? Convert.ToInt32(reader["nozzle_id"]) : 0,
                Attendant = reader["attendant"]?.ToString() ?? string.Empty,
                ProductId = reader["product_id"]?.ToString() ?? string.Empty,
                Quantity = reader["qty"] != DBNull.Value ? Convert.ToDecimal(reader["qty"]) : 0,
                UnitPrice = reader["unit_price"] != DBNull.Value ? Convert.ToDecimal(reader["unit_price"]) : 0,
                Total = reader["total"] != DBNull.Value ? Convert.ToDecimal(reader["total"]) : 0,
                State = reader["state"]?.ToString() ?? string.Empty,
                StartTime = reader["start_time"] != DBNull.Value ? Convert.ToDateTime(reader["start_time"]) : DateTime.MinValue,
                EndTime = reader["end_time"] != DBNull.Value ? Convert.ToDateTime(reader["end_time"]) : DateTime.MinValue,
                OrderUuid = reader["order_uuid"]?.ToString() ?? string.Empty
            });
        }

        return transactions;
    }

    //public async Task<bool> UpdateTransactionAsync(string transactionId,Dictionary<string, object> updateFields)
    //{
    //    if (updateFields == null || updateFields.Count == 0)
    //        return false;

    //    var json = JsonSerializer.Serialize(updateFields);

    //    using var conn = _context.CreateConnection();
    //    await conn.OpenAsync();

    //    using var cmd = new SqlCommand("dbo.sp_UpdatePumpTransactions", conn);
    //    cmd.CommandType = CommandType.StoredProcedure;

    //    cmd.Parameters.Add("@TransactionId", SqlDbType.NVarChar, 50)
    //        .Value = transactionId;

    //    cmd.Parameters.Add("@UpdateFields", SqlDbType.NVarChar)
    //        .Value = json;

    //    var result = await cmd.ExecuteScalarAsync();
    //    return Convert.ToInt32(result) == 1;
    //}

    public async Task<bool> UpdateTransactionAsync(string transactionId, Dictionary<string, object> updateFields)
    {
        if (updateFields == null || updateFields.Count == 0) return false;

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var tx = conn.BeginTransaction();
        try
        {
            var setClauses = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            foreach (var kvp in updateFields)
            {
                var column = kvp.Key;
                var paramName = "@" + column;
                setClauses.Add($"{column} = {paramName}");

                var p = cmd.CreateParameter();
                p.ParameterName = paramName;
                p.Value = kvp.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            cmd.CommandText = $"UPDATE PumpTransactions SET {string.Join(", ", setClauses)} WHERE TransactionId = @TransactionId";

            var txParam = cmd.CreateParameter();
            txParam.ParameterName = "@TransactionId";
            txParam.Value = transactionId;
            cmd.Parameters.Add(txParam);

            int rows = await cmd.ExecuteNonQueryAsync();

            if (rows > 0)
            {
                string attendant = null;
                using (var getCmd = conn.CreateCommand())
                {
                    getCmd.Transaction = tx;
                    getCmd.CommandText = "SELECT Attendant FROM PumpTransactions WHERE TransactionId = @tid";
                    getCmd.Parameters.AddWithValue("@tid", transactionId);

                    var result = await getCmd.ExecuteScalarAsync();
                    attendant = result?.ToString();
                }

                if (!string.IsNullOrEmpty(attendant))
                {
                    using var updateLimitCmd = conn.CreateCommand();
                    updateLimitCmd.Transaction = tx;
                    //;WITH Tx AS
                    //(
                    //    SELECT TransactionId, FpId
                    //    FROM PumpTransactions
                    //    WHERE TransactionId = @TransactionId
                    //      AND IsUnreconciled = 1
                    //)
                    //UPDATE AM
                    //SET ExtraLimits =
                    //    CASE
                    //        WHEN AM.ExtraLimits > 0 THEN AM.ExtraLimits - 1
                    //        ELSE 0
                    //    END
                    //FROM AttendantMaster AM
                    //INNER JOIN Tx ON Tx.FpId = AM.FpId;

                    //UPDATE PumpTransactions
                    //SET IsUnreconciled = 0
                    //WHERE TransactionId = @TransactionId
                    //  AND IsUnreconciled = 1;

                    //   UPDATE AttendantMaster
                    //SET LimiLeftCount = LimiLeftCount - 1
                    //WHERE MPITagId = @mpitag
                    //UPDATE AttendantMaster
                    updateLimitCmd.CommandText = @"
                        UPDATE AttendantMaster                
                    SET LimiLeftCount =
                        CASE
                            WHEN LimiLeftCount - 1 < 0 THEN 0
                            ELSE LimiLeftCount - 1
                        END
                    WHERE FpId IN (
                        SELECT PumpId
                        FROM PumpTransactions
                        WHERE TransactionId = @transactionId and OdooOrderId is not null

                    );


                                    ";

                    updateLimitCmd.Parameters.AddWithValue("@mpitag", attendant);
                    updateLimitCmd.Parameters.AddWithValue("@TransactionId", transactionId);

                    await updateLimitCmd.ExecuteNonQueryAsync();
                }

                tx.Commit();
                return true;
            }

            tx.Rollback();
            return false;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            return false;
        }
    }


    public async Task<bool> UpsertAttendantPumpCountAsync(AttendantPumpCountUpdate dto)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_UpsertAttendantPumpCount", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@FpId", dto.PumpNumber);
        cmd.Parameters.AddWithValue("@MPITagId", dto.EmpTagNo);
        cmd.Parameters.AddWithValue("@MaxLimitCount", dto.NewMaxTransaction);

        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task AddTransactionAsync(object? transactionData)
    {
        if (transactionData == null) return;

        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        var txn = transactionData as PumpTransactions;
        if (txn == null) throw new ArgumentException("Invalid transaction data");

        var query = @"
        INSERT INTO PumpTransactions (TransactionId, PumpId, NozzleId, Attendant, ProductId, Quantity, UnitPrice, Total, State, StartTime, EndTime, OrderUuid)
        VALUES (@TransactionId, @PumpId, @NozzleId, @Attendant, @ProductId, @Quantity, @UnitPrice, @Total, @State, @StartTime, @EndTime, @OrderUuid)";

        using var cmd = new SqlCommand(query, conn);
        cmd.Parameters.AddWithValue("@TransactionId", txn.TransactionId);
        cmd.Parameters.AddWithValue("@PumpId", txn.PumpId);
        cmd.Parameters.AddWithValue("@NozzleId", txn.NozzleId);
        cmd.Parameters.AddWithValue("@Attendant", txn.Attendant ?? string.Empty);
        cmd.Parameters.AddWithValue("@ProductId", txn.ProductId ?? string.Empty);
        cmd.Parameters.AddWithValue("@Quantity", txn.Quantity);
        cmd.Parameters.AddWithValue("@UnitPrice", txn.UnitPrice);
        cmd.Parameters.AddWithValue("@Total", txn.Total);
        cmd.Parameters.AddWithValue("@State", txn.State ?? string.Empty);
        cmd.Parameters.AddWithValue("@StartTime", txn.StartTime);
        cmd.Parameters.AddWithValue("@EndTime", txn.EndTime);
        cmd.Parameters.AddWithValue("@OrderUuid", txn.OrderUuid ?? string.Empty);

        await cmd.ExecuteNonQueryAsync();
    }

    //public async Task AddTransactionAsync(object? txn)
    //{
    //    if (txn is not PumpTransactions transaction)
    //        throw new ArgumentException("Invalid transaction data", nameof(txn));

    //    using var conn = _context.CreateConnection();
    //    await conn.OpenAsync();

    //    using var cmd = new SqlCommand("dbo.sp_AddPumpTransaction", conn);
    //    cmd.CommandType = CommandType.StoredProcedure;

    //    cmd.Parameters.Add("@TransactionId", SqlDbType.NVarChar, 50)
    //        .Value = transaction.TransactionId;

    //    cmd.Parameters.Add("@PumpId", SqlDbType.Int)
    //        .Value = transaction.PumpId;

    //    cmd.Parameters.Add("@NozzleId", SqlDbType.Int)
    //        .Value = transaction.NozzleId;

    //    cmd.Parameters.Add("@Attendant", SqlDbType.NVarChar, 100)
    //        .Value = transaction.Attendant ?? string.Empty;

    //    cmd.Parameters.Add("@ProductId", SqlDbType.NVarChar, 50)
    //        .Value = transaction.ProductId ?? string.Empty;

    //    cmd.Parameters.Add("@Quantity", SqlDbType.Decimal)
    //        .Value = transaction.Quantity;

    //    cmd.Parameters.Add("@UnitPrice", SqlDbType.Decimal)
    //        .Value = transaction.UnitPrice;

    //    cmd.Parameters.Add("@Total", SqlDbType.Decimal)
    //        .Value = transaction.Total;

    //    cmd.Parameters.Add("@State", SqlDbType.NVarChar, 50)
    //        .Value = transaction.State ?? string.Empty;

    //    cmd.Parameters.Add("@StartTime", SqlDbType.DateTime)
    //        .Value = transaction.StartTime;

    //    cmd.Parameters.Add("@EndTime", SqlDbType.DateTime)
    //        .Value = transaction.EndTime;

    //    cmd.Parameters.Add("@OrderUuid", SqlDbType.NVarChar, 100)
    //        .Value = transaction.OrderUuid ?? string.Empty;

    //    await cmd.ExecuteNonQueryAsync();
    //}


    public async Task<List<FpLimitDto?>> GetTransactionLimitCountByFpId(int fpId)
    {
        try
        {
            var limits = new List<FpLimitDto>();

            using var conn = _context.CreateConnection();
            conn.Open();

            using var cmd = new SqlCommand("Sp_GettransactionLimitCountByFpId", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.AddWithValue("@FpId", fpId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                limits.Add(new FpLimitDto
                {
                    FpId = reader["FpId"] != DBNull.Value ? Convert.ToInt32(reader["FpId"]) : 0,
                    MaxLimit = reader["MaxLimit"] != DBNull.Value ? Convert.ToInt32(reader["MaxLimit"]) : 0,
                    CurrentCount = reader["CurrentCount"] != DBNull.Value ? Convert.ToInt32(reader["CurrentCount"]) : 0,
                    Status = reader["Status"]?.ToString() ?? string.Empty,
                    IsAllowed = reader["IsAllowed"] != DBNull.Value && Convert.ToBoolean(reader["IsAllowed"])

                });
            }

            return limits;
        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching all transaction limit counts", ex);
        }

    }



    public async Task<bool> UpdatePaymentIdAsync(string transactionId, string paymentId)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("sp_UpdatePaymentId", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@TransactionId", transactionId);
        cmd.Parameters.AddWithValue("@PaymentId", paymentId ?? (object)DBNull.Value);

        int rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }
    public async Task UpdateIsAllowedAsync(int fpId, bool isAllowed)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.sp_UpdateIsAllowedByFpId";

        cmd.Parameters.AddWithValue("@FpId", fpId);
        cmd.Parameters.AddWithValue("@IsAllowed", isAllowed);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<FpLimitDto?>> GetTransactionLimitCountByFpId_Block(int fpId)
    {
        try
        {
            var limits = new List<FpLimitDto>();

            using var conn = _context.CreateConnection();
            conn.Open();

            using var cmd = new SqlCommand("Sp_GettransactionLimitCountByFpId_Block", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.AddWithValue("@FpId", fpId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                limits.Add(new FpLimitDto
                {
                    FpId = reader["FpId"] != DBNull.Value ? Convert.ToInt32(reader["FpId"]) : 0,
                    MaxLimit = reader["MaxLimit"] != DBNull.Value ? Convert.ToInt32(reader["MaxLimit"]) : 0,
                    CurrentCount = reader["CurrentCount"] != DBNull.Value ? Convert.ToInt32(reader["CurrentCount"]) : 0,
                    Status = reader["Status"]?.ToString() ?? string.Empty,
                    IsAllowed = reader["IsAllowed"] != DBNull.Value && Convert.ToBoolean(reader["IsAllowed"])
                });
            }

            return limits;
        }
        catch (Exception ex)
        {
            throw new Exception("Error fetching all transaction limit counts", ex);
        }

    }

    public async Task InsertBlockUnbloclHistory(int fpId, string actionType, string source, string note)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("Sp_InsertPumpActionHistory", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        cmd.Parameters.AddWithValue("@FpId", fpId);
        cmd.Parameters.AddWithValue("@ActionType", actionType);
        cmd.Parameters.AddWithValue("@Source", source);
        cmd.Parameters.AddWithValue("@Note", (object)note ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }
    public async Task FpLimitReset(int fpId, int NewLimit)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("Sp_UpdateFNewLimit", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        cmd.Parameters.AddWithValue("@FpId", fpId);
        cmd.Parameters.AddWithValue("@NewLimit", NewLimit);


        await cmd.ExecuteNonQueryAsync();
    }
    public async Task FpLimitReset(int fpId)
    {
        using var conn = _context.CreateConnection();
        await conn.OpenAsync();

        using var cmd = new SqlCommand("Sp_UpdateFpLimitReset", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        cmd.Parameters.AddWithValue("@FpId", fpId);


        await cmd.ExecuteNonQueryAsync();
    }

}
public class LatestTransactionRequest
{
    public int? pump_id { get; set; }
    public int? nozzle_id { get; set; }
    public string emp { get; set; }
    public string mode { get; set; }
}

