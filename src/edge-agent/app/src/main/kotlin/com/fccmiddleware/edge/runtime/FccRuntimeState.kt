package com.fccmiddleware.edge.runtime

import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import java.io.Closeable

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
        closeCurrentAdapter()
        this.adapter = adapter
        this.config = config
    }

    fun clear() {
        closeCurrentAdapter()
        config = null
    }

    fun isWired(): Boolean = adapter != null && config != null

    private fun closeCurrentAdapter() {
        (adapter as? Closeable)?.close()
        adapter = null
    }
}
