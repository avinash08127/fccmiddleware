package com.fccmiddleware.edge.runtime

import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.IFccAdapter

/**
 * Shared runtime holder for the currently resolved FCC adapter and config.
 *
 * Connectivity probing and service wiring both read from this singleton so the
 * agent has one source of truth for whether FCC runtime is actually available.
 */
class FccRuntimeState {
    @Volatile
    var adapter: IFccAdapter? = null
        private set

    @Volatile
    var config: AgentFccConfig? = null
        private set

    fun wire(adapter: IFccAdapter, config: AgentFccConfig) {
        this.adapter = adapter
        this.config = config
    }

    fun clear() {
        adapter = null
        config = null
    }

    fun isWired(): Boolean = adapter != null && config != null
}
