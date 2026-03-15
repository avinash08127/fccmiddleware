package com.fccmiddleware.edge.adapter.common

import kotlinx.serialization.Serializable

/**
 * Extended pump status data from FpStatus_3 supplemental parameters.
 * Ported from legacy FpStatusResponse.SupplementalStatus (16 parameter IDs).
 */
@Serializable
data class PumpStatusSupplemental(
    /** Param 04: Available storage module IDs. */
    val availableStorageModules: List<Int>? = null,

    /** Param 05: Available fuel grade IDs. */
    val availableGrades: List<Int>? = null,

    /** Param 06: Grade option number. */
    val gradeOptionNo: Int? = null,

    /** Param 07: Extended fuelling volume. */
    val fuellingVolumeExtended: Long? = null,

    /** Param 08: Extended fuelling money. */
    val fuellingMoneyExtended: Long? = null,

    /** Param 09: Attendant account ID. */
    val attendantAccountId: String? = null,

    /** Param 10: Fuel point blocking status. */
    val blockingStatus: String? = null,

    /** Param 11: Full nozzle details. */
    val nozzleDetail: NozzleDetail? = null,

    /** Param 12: Operation mode number. */
    val operationModeNo: Int? = null,

    /** Param 13: Price group ID. */
    val priceGroupId: Int? = null,

    /** Param 14: Nozzle tag reader ID. */
    val nozzleTagReaderId: String? = null,

    /** Param 15: FP alarm status. */
    val alarmStatus: String? = null,

    /** Param 16: Minimum preset values. */
    val minPresetValues: List<Long>? = null,
)

/** Full nozzle identification details (Param 11). */
@Serializable
data class NozzleDetail(val id: Int, val asciiCode: String? = null, val asciiChar: String? = null)
