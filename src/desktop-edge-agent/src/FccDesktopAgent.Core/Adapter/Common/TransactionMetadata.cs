namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Additional metadata from the DOMS supervised transaction buffer.
/// Ported from legacy FpSupTransBufStatusResponse TransInfoMask 8-bit flags.
/// </summary>
public sealed record TransactionInfoMask
{
    /// <summary>Bit 0: Transaction is a stored (completed) transaction.</summary>
    public bool IsStoredTransaction { get; init; }

    /// <summary>Bit 1: Transaction completed with error.</summary>
    public bool IsErrorTransaction { get; init; }

    /// <summary>Bit 2: Transaction amount exceeds minimum limit.</summary>
    public bool ExceedsMinLimit { get; init; }

    /// <summary>Bit 3: Prepay mode was used for this transaction.</summary>
    public bool PrepayModeUsed { get; init; }

    /// <summary>Bit 4: Volume (and optionally grade ID) included.</summary>
    public bool VolumeIncluded { get; init; }

    /// <summary>Bit 5: Transaction cannot be finalized.</summary>
    public bool FinalizeNotAllowed { get; init; }

    /// <summary>Bit 6: MoneyDue value is negative.</summary>
    public bool MoneyDueIsNegative { get; init; }

    /// <summary>Bit 7: MoneyDue field is included in the response.</summary>
    public bool MoneyDueIncluded { get; init; }

    /// <summary>MoneyDue amount if included (in DOMS x10 format).</summary>
    public long? MoneyDue { get; init; }

    /// <summary>Transaction sequence number in the supervised buffer.</summary>
    public int? TransSequenceNo { get; init; }

    /// <summary>Transaction lock ID in the supervised buffer.</summary>
    public int? TransLockId { get; init; }

    /// <summary>
    /// Decode an 8-bit info mask integer into a TransactionInfoMask.
    /// </summary>
    public static TransactionInfoMask FromBits(int mask) => new()
    {
        IsStoredTransaction = (mask & 0x01) != 0,
        IsErrorTransaction = (mask & 0x02) != 0,
        ExceedsMinLimit = (mask & 0x04) != 0,
        PrepayModeUsed = (mask & 0x08) != 0,
        VolumeIncluded = (mask & 0x10) != 0,
        FinalizeNotAllowed = (mask & 0x20) != 0,
        MoneyDueIsNegative = (mask & 0x40) != 0,
        MoneyDueIncluded = (mask & 0x80) != 0,
    };
}
