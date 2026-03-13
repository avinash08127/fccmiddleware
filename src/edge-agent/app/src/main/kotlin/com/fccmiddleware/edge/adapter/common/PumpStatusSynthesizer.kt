package com.fccmiddleware.edge.adapter.common

import java.time.Instant

/**
 * Utility for synthesizing pump status from pre-auth state when the FCC protocol
 * does not provide real-time pump status.
 *
 * Used by adapters with [PumpStatusCapability.NOT_SUPPORTED] or [PumpStatusCapability.NOT_APPLICABLE]
 * to return meaningful status based on active pre-authorizations rather than empty lists.
 *
 * Synthesis rules:
 *   - Pump with an active pre-auth → [PumpState.AUTHORIZED]
 *   - Pump without an active pre-auth → [PumpState.IDLE]
 *   - All entries have [PumpStatusSource.EDGE_SYNTHESIZED]
 */
object PumpStatusSynthesizer {

    /**
     * Synthesize pump status for a set of configured pumps using active pre-auth data.
     *
     * @param configuredPumps Set of canonical pump numbers that this site has configured.
     * @param activePreAuths Current active pre-auth snapshots from [IPreAuthMatcher.getActivePreAuths].
     * @param siteCode Site identifier for the status entries.
     * @param currencyCode ISO 4217 currency code.
     * @return One [PumpStatus] per configured pump, with state derived from pre-auth presence.
     */
    fun synthesize(
        configuredPumps: Set<Int>,
        activePreAuths: List<ActivePreAuthSnapshot>,
        siteCode: String,
        currencyCode: String,
    ): List<PumpStatus> {
        if (configuredPumps.isEmpty()) return emptyList()

        val authorizedPumps = activePreAuths.map { it.pumpNumber }.toHashSet()
        val now = Instant.now().toString()

        return configuredPumps.sorted().map { pumpNumber ->
            PumpStatus(
                siteCode = siteCode,
                pumpNumber = pumpNumber,
                nozzleNumber = 1,
                state = if (pumpNumber in authorizedPumps) PumpState.AUTHORIZED else PumpState.IDLE,
                currencyCode = currencyCode,
                statusSequence = 0,
                observedAtUtc = now,
                source = PumpStatusSource.EDGE_SYNTHESIZED,
            )
        }
    }
}
