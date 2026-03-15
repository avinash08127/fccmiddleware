package com.fccmiddleware.edge.adapter.common

import kotlinx.serialization.Serializable

/**
 * Additional metadata from the DOMS supervised transaction buffer.
 * Ported from legacy FpSupTransBufStatusResponse TransInfoMask 8-bit flags.
 */
@Serializable
data class TransactionInfoMask(
    val isStoredTransaction: Boolean = false,
    val isErrorTransaction: Boolean = false,
    val exceedsMinLimit: Boolean = false,
    val prepayModeUsed: Boolean = false,
    val volumeIncluded: Boolean = false,
    val finalizeNotAllowed: Boolean = false,
    val moneyDueIsNegative: Boolean = false,
    val moneyDueIncluded: Boolean = false,
    val moneyDue: Long? = null,
    val transSequenceNo: Int? = null,
    val transLockId: Int? = null,
) {
    companion object {
        fun fromBits(mask: Int): TransactionInfoMask = TransactionInfoMask(
            isStoredTransaction = (mask and 0x01) != 0,
            isErrorTransaction = (mask and 0x02) != 0,
            exceedsMinLimit = (mask and 0x04) != 0,
            prepayModeUsed = (mask and 0x08) != 0,
            volumeIncluded = (mask and 0x10) != 0,
            finalizeNotAllowed = (mask and 0x20) != 0,
            moneyDueIsNegative = (mask and 0x40) != 0,
            moneyDueIncluded = (mask and 0x80) != 0,
        )
    }
}
