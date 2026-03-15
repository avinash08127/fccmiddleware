package com.fccmiddleware.edge.adapter.common

/**
 * Optional interface for adapters that receive peripheral device events.
 * Ported from legacy DPP port 5006 peripheral message handling.
 */
interface IFccPeripheralMonitor {
    suspend fun getPeripheralInventory(): PeripheralInventory
}

data class PeripheralInventory(
    val dispensers: List<DispenserInfo>,
    val eptTerminals: List<EptTerminalInfo>,
)

data class DispenserInfo(val dispenserId: String, val model: String)
data class EptTerminalInfo(val terminalId: String, val version: String)
data class BnaReport(val terminalId: String, val notesAccepted: Int, val reportedAtUtc: String)
