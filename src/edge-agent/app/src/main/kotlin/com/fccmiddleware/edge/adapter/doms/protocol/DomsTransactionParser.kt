package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.doms.jpl.JplMessage
import com.fccmiddleware.edge.adapter.doms.model.DomsTransactionDto

/**
 * Handles the DOMS supervised transaction buffer: lock → read → clear.
 *
 * DOMS maintains a "supervised buffer" of completed transactions per fuelling point.
 * To safely retrieve transactions:
 *   1. Lock the buffer (FpSupTrans_lock_req) — prevents new transactions from entering
 *   2. Read transactions (FpSupTrans_read_req) — returns buffered transactions
 *   3. Clear the buffer (FpSupTrans_clear_req) — removes read transactions
 *
 * If clear is not sent, transactions remain in the buffer and will be returned again.
 */
object DomsTransactionParser {

    // ── JPL message names ────────────────────────────────────────────────────

    const val LOCK_REQUEST = "FpSupTrans_lock_req"
    const val LOCK_RESPONSE = "FpSupTrans_lock_resp"
    const val READ_REQUEST = "FpSupTrans_read_req"
    const val READ_RESPONSE = "FpSupTrans_read_resp"
    const val CLEAR_REQUEST = "FpSupTrans_clear_req"
    const val CLEAR_RESPONSE = "FpSupTrans_clear_resp"

    /** Result code indicating success. */
    const val RESULT_OK = "0"

    /** Result code indicating no transactions available. */
    const val RESULT_EMPTY = "1"

    // ── Lock ─────────────────────────────────────────────────────────────────

    /**
     * Build a lock request for the supervised transaction buffer.
     *
     * @param fpId Fuelling point ID (0 = all pumps).
     */
    fun buildLockRequest(fpId: Int = 0): JplMessage {
        return JplMessage(
            name = LOCK_REQUEST,
            data = mapOf("FpId" to fpId.toString()),
        )
    }

    /**
     * Validate a lock response.
     *
     * @return true if lock was acquired successfully.
     */
    fun validateLockResponse(response: JplMessage): Boolean {
        if (response.name != LOCK_RESPONSE) return false
        val resultCode = response.data["ResultCode"] ?: return false
        return resultCode == RESULT_OK
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    /**
     * Build a read request for the supervised transaction buffer.
     *
     * @param fpId Fuelling point ID (0 = all pumps).
     * @param bufferIndex Starting buffer index to read from.
     */
    fun buildReadRequest(fpId: Int = 0, bufferIndex: Int = 0): JplMessage {
        return JplMessage(
            name = READ_REQUEST,
            data = mapOf(
                "FpId" to fpId.toString(),
                "BufferIndex" to bufferIndex.toString(),
            ),
        )
    }

    /**
     * Parse a read response into transaction DTOs.
     *
     * @param response JPL response message.
     * @return List of parsed transactions, or empty if buffer was empty.
     */
    fun parseReadResponse(response: JplMessage): List<DomsTransactionDto> {
        if (response.name != READ_RESPONSE) return emptyList()

        val resultCode = response.data["ResultCode"]
        if (resultCode == RESULT_EMPTY) return emptyList()
        if (resultCode != RESULT_OK) return emptyList()

        val count = response.data["TransCount"]?.toIntOrNull() ?: return emptyList()
        if (count == 0) return emptyList()

        // For a single transaction, parse directly from the data map
        // For multiple transactions, data contains indexed fields (Trans_0_*, Trans_1_*, etc.)
        return if (count == 1) {
            listOf(DomsSupParamParser.parse(response.data, bufferIndex = 0))
        } else {
            (0 until count).mapNotNull { index ->
                try {
                    val indexedData = extractIndexedTransaction(response.data, index)
                    DomsSupParamParser.parse(indexedData, bufferIndex = index)
                } catch (_: Exception) {
                    null
                }
            }
        }
    }

    // ── Clear ────────────────────────────────────────────────────────────────

    /**
     * Build a clear request to remove read transactions from the buffer.
     *
     * @param fpId Fuelling point ID.
     * @param count Number of transactions to clear.
     */
    fun buildClearRequest(fpId: Int = 0, count: Int): JplMessage {
        return JplMessage(
            name = CLEAR_REQUEST,
            data = mapOf(
                "FpId" to fpId.toString(),
                "TransCount" to count.toString(),
            ),
        )
    }

    /**
     * Validate a clear response.
     *
     * @return true if clear was successful.
     */
    fun validateClearResponse(response: JplMessage): Boolean {
        if (response.name != CLEAR_RESPONSE) return false
        val resultCode = response.data["ResultCode"] ?: return false
        return resultCode == RESULT_OK
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /**
     * Extract indexed transaction fields from a multi-transaction response.
     * Fields are prefixed with "Trans_{index}_" (e.g., "Trans_0_TransId", "Trans_0_FpId").
     */
    private fun extractIndexedTransaction(data: Map<String, String>, index: Int): Map<String, String> {
        val prefix = "Trans_${index}_"
        return data.entries
            .filter { it.key.startsWith(prefix) }
            .associate { it.key.removePrefix(prefix) to it.value }
    }
}
