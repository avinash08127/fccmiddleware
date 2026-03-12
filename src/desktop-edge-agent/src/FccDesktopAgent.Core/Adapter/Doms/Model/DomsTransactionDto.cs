namespace FccDesktopAgent.Core.Adapter.Doms.Model;

/// <summary>
/// Raw DOMS transaction from the supervised buffer (FpSupTrans response).
/// Numeric values use integer types with explicit scale to avoid floating-point precision loss:
/// <list type="bullet">
///   <item><see cref="VolumeCl"/> — volume in centilitres</item>
///   <item><see cref="AmountX10"/> — x10 of minor currency units</item>
///   <item><see cref="UnitPriceX10"/> — x10 of minor currency units per litre</item>
/// </list>
/// </summary>
public sealed record DomsJplTransactionDto(
    string TransactionId,
    int FpId,
    int NozzleId,
    string ProductCode,
    long VolumeCl,
    long AmountX10,
    long UnitPriceX10,
    string Timestamp,
    string? AttendantId,
    int BufferIndex);
