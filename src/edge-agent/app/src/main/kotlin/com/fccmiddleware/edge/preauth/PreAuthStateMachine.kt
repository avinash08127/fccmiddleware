package com.fccmiddleware.edge.preauth

import com.fccmiddleware.edge.adapter.common.PreAuthStatus

object PreAuthStateMachine {
    val activeStatuses: Set<PreAuthStatus> = setOf(
        PreAuthStatus.PENDING,
        PreAuthStatus.AUTHORIZED,
        PreAuthStatus.DISPENSING,
    )

    val terminalStatuses: Set<PreAuthStatus> = setOf(
        PreAuthStatus.COMPLETED,
        PreAuthStatus.CANCELLED,
        PreAuthStatus.EXPIRED,
        PreAuthStatus.FAILED,
    )

    private val allowedTransitions: Map<PreAuthStatus, Set<PreAuthStatus>> = mapOf(
        PreAuthStatus.PENDING to setOf(
            PreAuthStatus.AUTHORIZED,
            PreAuthStatus.CANCELLED,
            PreAuthStatus.EXPIRED,
            PreAuthStatus.FAILED,
        ),
        PreAuthStatus.AUTHORIZED to setOf(
            PreAuthStatus.DISPENSING,
            PreAuthStatus.COMPLETED,
            PreAuthStatus.CANCELLED,
            PreAuthStatus.EXPIRED,
            PreAuthStatus.FAILED,
        ),
        PreAuthStatus.DISPENSING to setOf(
            PreAuthStatus.COMPLETED,
            PreAuthStatus.CANCELLED,
            PreAuthStatus.EXPIRED,
            PreAuthStatus.FAILED,
        ),
        PreAuthStatus.COMPLETED to emptySet(),
        PreAuthStatus.CANCELLED to emptySet(),
        PreAuthStatus.EXPIRED to emptySet(),
        PreAuthStatus.FAILED to emptySet(),
    )

    fun isActive(status: PreAuthStatus): Boolean = status in activeStatuses

    fun isTerminal(status: PreAuthStatus): Boolean = status in terminalStatuses

    fun canTransition(from: PreAuthStatus, to: PreAuthStatus): Boolean =
        allowedTransitions[from]?.contains(to) == true

    fun allowedTransitionsFrom(from: PreAuthStatus): Set<PreAuthStatus> =
        allowedTransitions[from].orEmpty()
}
