package com.fccmiddleware.edge.adapter.common

/**
 * Optional interface for adapters that support direct pump control commands.
 * Ported from legacy ForecourtClient: EmergencyBlock(), UnblockPump(), SoftLock(), Unlock().
 */
interface IFccPumpControl {
    suspend fun emergencyStop(fpId: Int): PumpControlResult
    suspend fun cancelEmergencyStop(fpId: Int): PumpControlResult
    suspend fun closePump(fpId: Int): PumpControlResult
    suspend fun openPump(fpId: Int): PumpControlResult
}

data class PumpControlResult(val success: Boolean, val errorMessage: String? = null)
