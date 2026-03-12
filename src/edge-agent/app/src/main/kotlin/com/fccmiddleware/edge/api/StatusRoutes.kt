package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.ktor.http.HttpStatusCode
import io.ktor.server.response.respond
import io.ktor.server.routing.Routing
import io.ktor.server.routing.get
import java.time.Instant

/**
 * Status endpoint — Edge Agent health and connectivity snapshot.
 * p95 response time target: <= 100 ms.
 *
 * GET /api/v1/status — returns current operational status (connectivity, buffer, uptime).
 */
fun Routing.statusRoutes(
    connectivityManager: ConnectivityManager,
    transactionDao: TransactionBufferDao,
    syncStateDao: SyncStateDao,
    configManager: ConfigManager,
    agentVersion: String,
    deviceId: String,
    siteCode: String,
    serviceStartMs: Long,
) {
    get("/api/v1/status") {
        val state = connectivityManager.state.value
        val fccReachable = state == ConnectivityState.FULLY_ONLINE ||
            state == ConnectivityState.INTERNET_DOWN
        val fccHeartbeatAge = connectivityManager.fccHeartbeatAgeSeconds()

        val bufferDepth = try {
            transactionDao.countForLocalApi()
        } catch (_: Exception) {
            0
        }

        val syncState = try {
            syncStateDao.get()
        } catch (_: Exception) {
            null
        }

        val lastSyncUtc = syncState?.lastUploadAt
        val syncLagSeconds = if (lastSyncUtc != null) {
            try {
                val lastSync = Instant.parse(lastSyncUtc)
                ((System.currentTimeMillis() - lastSync.toEpochMilli()) / 1_000L).toInt()
            } catch (_: Exception) {
                null
            }
        } else null

        val uptimeSeconds = ((System.currentTimeMillis() - serviceStartMs) / 1_000L).toInt()

        call.respond(
            HttpStatusCode.OK,
            AgentStatusResponse(
                deviceId = deviceId,
                siteCode = siteCode,
                connectivityState = state.name,
                fccReachable = fccReachable,
                fccHeartbeatAgeSeconds = fccHeartbeatAge,
                bufferDepth = bufferDepth,
                syncLagSeconds = syncLagSeconds,
                lastSuccessfulSyncUtc = lastSyncUtc,
                configVersion = configManager.currentConfigVersion,
                agentVersion = agentVersion,
                uptimeSeconds = uptimeSeconds,
                reportedAtUtc = Instant.now().toString(),
            )
        )
    }
}
