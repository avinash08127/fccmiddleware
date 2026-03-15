namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Extended pump status data from FpStatus_3 supplemental parameters.
/// Ported from legacy FpStatusResponse.SupplementalStatus (16 parameter IDs).
/// </summary>
public sealed record PumpStatusSupplemental
{
    /// <summary>Param 04: Available storage module IDs.</summary>
    public IReadOnlyList<int>? AvailableStorageModules { get; init; }

    /// <summary>Param 05: Available fuel grade IDs.</summary>
    public IReadOnlyList<int>? AvailableGrades { get; init; }

    /// <summary>Param 06: Grade option number.</summary>
    public int? GradeOptionNo { get; init; }

    /// <summary>Param 07: Extended fuelling volume.</summary>
    public long? FuellingVolumeExtended { get; init; }

    /// <summary>Param 08: Extended fuelling money.</summary>
    public long? FuellingMoneyExtended { get; init; }

    /// <summary>Param 09: Attendant account ID.</summary>
    public string? AttendantAccountId { get; init; }

    /// <summary>Param 10: Fuel point blocking status.</summary>
    public string? BlockingStatus { get; init; }

    /// <summary>Param 11: Full nozzle details.</summary>
    public NozzleDetail? NozzleDetail { get; init; }

    /// <summary>Param 12: Operation mode number.</summary>
    public int? OperationModeNo { get; init; }

    /// <summary>Param 13: Price group ID.</summary>
    public int? PriceGroupId { get; init; }

    /// <summary>Param 14: Nozzle tag reader ID.</summary>
    public string? NozzleTagReaderId { get; init; }

    /// <summary>Param 15: FP alarm status.</summary>
    public string? AlarmStatus { get; init; }

    /// <summary>Param 16: Minimum preset values.</summary>
    public IReadOnlyList<long>? MinPresetValues { get; init; }
}

/// <summary>Full nozzle identification details (Param 11).</summary>
public sealed record NozzleDetail(int Id, string? AsciiCode, string? AsciiChar);
