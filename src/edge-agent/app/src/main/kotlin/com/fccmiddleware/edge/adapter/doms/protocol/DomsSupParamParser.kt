package com.fccmiddleware.edge.adapter.doms.protocol

import com.fccmiddleware.edge.adapter.doms.model.DomsTransactionDto

/**
 * Parses DOMS supervised transaction parameters from JPL message data maps.
 *
 * The FpSupTrans response contains transaction data in a flat key-value map.
 * This parser extracts and validates the fields, returning a typed DTO.
 */
object DomsSupParamParser {

    /**
     * Parse a JPL data map from a FpSupTrans read response into a DomsTransactionDto.
     *
     * @param data Key-value map from JplMessage.data
     * @param bufferIndex The supervised buffer index this transaction was read from.
     * @return Parsed transaction DTO.
     * @throws IllegalArgumentException if required fields are missing or invalid.
     */
    fun parse(data: Map<String, String>, bufferIndex: Int): DomsTransactionDto {
        return DomsTransactionDto(
            transactionId = requireField(data, "TransId"),
            fpId = requireField(data, "FpId").toIntOrFail("FpId"),
            nozzleId = requireField(data, "NozzleId").toIntOrFail("NozzleId"),
            productCode = requireField(data, "ProductCode"),
            volumeCl = requireField(data, "Volume").toLongOrFail("Volume"),
            amountX10 = requireField(data, "Amount").toLongOrFail("Amount"),
            unitPriceX10 = requireField(data, "UnitPrice").toLongOrFail("UnitPrice"),
            timestamp = requireField(data, "Timestamp"),
            attendantId = data["AttendantId"],
            bufferIndex = bufferIndex,
        )
    }

    private fun requireField(data: Map<String, String>, key: String): String {
        return data[key]
            ?: throw IllegalArgumentException("Missing required DOMS field: $key")
    }

    private fun String.toIntOrFail(fieldName: String): Int {
        return this.toIntOrNull()
            ?: throw IllegalArgumentException("Invalid integer value for $fieldName: '$this'")
    }

    private fun String.toLongOrFail(fieldName: String): Long {
        return this.toLongOrNull()
            ?: throw IllegalArgumentException("Invalid long value for $fieldName: '$this'")
    }
}
