package com.fccmiddleware.edge.adapter.doms.model

import com.fccmiddleware.edge.adapter.common.PumpState

/**
 * DOMS FP_MAIN_STATE values from the DOMS protocol specification.
 * Maps raw integer state codes to the canonical [PumpState] enum.
 */
enum class DomsFpMainState(val code: Int) {
    FP_INOPERATIVE(0),
    FP_CLOSED(1),
    FP_IDLE(2),
    FP_CALLING(3),
    FP_AUTHORIZED(4),
    FP_STARTED(5),
    FP_FUELLING(6),
    FP_SUSPENDED(7),
    FP_COMPLETED(8),
    FP_LOCKED(9),
    FP_ERROR(10),
    FP_EMERGENCY_STOP(11),
    FP_DISCONNECTED(12),
    FP_OFFLINE(13);

    /** Convert this DOMS-specific pump state to the canonical [PumpState]. */
    fun toCanonicalPumpState(): PumpState = when (this) {
        FP_INOPERATIVE -> PumpState.OFFLINE
        FP_CLOSED -> PumpState.OFFLINE
        FP_IDLE -> PumpState.IDLE
        FP_CALLING -> PumpState.CALLING
        FP_AUTHORIZED -> PumpState.AUTHORIZED
        FP_STARTED -> PumpState.AUTHORIZED   // G-13 fix: started = authorized, not yet dispensing
        FP_FUELLING -> PumpState.DISPENSING
        FP_SUSPENDED -> PumpState.PAUSED
        FP_COMPLETED -> PumpState.COMPLETED
        FP_LOCKED -> PumpState.OFFLINE        // G-13 fix: locked = offline, must not accept transactions
        FP_ERROR -> PumpState.ERROR
        FP_EMERGENCY_STOP -> PumpState.ERROR
        FP_DISCONNECTED -> PumpState.OFFLINE
        FP_OFFLINE -> PumpState.OFFLINE
    }

    companion object {
        private val byCode = entries.associateBy { it.code }

        /** Look up a [DomsFpMainState] by its integer code. Returns null if unknown. */
        fun fromCode(code: Int): DomsFpMainState? = byCode[code]
    }
}
