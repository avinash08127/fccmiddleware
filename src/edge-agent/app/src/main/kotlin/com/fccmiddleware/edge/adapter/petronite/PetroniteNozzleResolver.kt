package com.fccmiddleware.edge.adapter.petronite

import android.util.Log
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import io.ktor.client.HttpClient
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.statement.bodyAsText
import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.Json

/**
 * Resolves canonical pump/nozzle numbers to Petronite nozzle IDs and vice versa.
 *
 * Fetches GET /nozzles/assigned and builds a bidirectional lookup.
 * Thread-safe: the snapshot is an immutable map swapped atomically.
 * Periodic refresh every 30 minutes; callers may force refresh via [refresh].
 */
class PetroniteNozzleResolver(
    private val config: AgentFccConfig,
    private val oauthClient: PetroniteOAuthClient,
    private val httpClient: HttpClient,
    private val json: Json = Json { ignoreUnknownKeys = true },
) {

    private val mutex = Mutex()

    /** NozzleId -> (PumpNumber, NozzleNumber) */
    @Volatile
    private var nozzleIdToCanonical: Map<String, NozzleCanonical> = emptyMap()

    /** (PumpNumber, NozzleNumber) encoded as "pump-nozzle" -> NozzleId */
    @Volatile
    private var canonicalToNozzleId: Map<String, String> = emptyMap()

    /** Full assignment list for pump status synthesis. */
    @Volatile
    private var lastAssignments: List<PetroniteNozzleAssignment> = emptyList()

    @Volatile
    private var lastRefreshedAtMillis: Long = 0L

    /**
     * Resolves canonical pump/nozzle numbers to a Petronite nozzle ID.
     * Triggers a lazy refresh if the snapshot is stale.
     */
    suspend fun resolveNozzleId(pumpNumber: Int, nozzleNumber: Int): String {
        ensureFresh()

        val key = canonicalKey(pumpNumber, nozzleNumber)
        return canonicalToNozzleId[key]
            ?: throw IllegalStateException(
                "No Petronite nozzle mapping found for pump $pumpNumber nozzle $nozzleNumber",
            )
    }

    /**
     * Resolves a Petronite nozzle ID to canonical pump/nozzle numbers.
     * Triggers a lazy refresh if the snapshot is stale.
     */
    suspend fun resolveCanonical(nozzleId: String): NozzleCanonical {
        ensureFresh()

        return nozzleIdToCanonical[nozzleId]
            ?: throw IllegalStateException(
                "No canonical mapping found for Petronite nozzle ID '$nozzleId'",
            )
    }

    /**
     * Returns the current nozzleId-to-canonical snapshot for normalization use.
     * Does not trigger a refresh; returns whatever is cached.
     */
    fun getCurrentSnapshot(): Map<String, NozzleCanonical> = nozzleIdToCanonical

    /**
     * Returns the last fetched nozzle assignments for pump status synthesis.
     */
    fun getLastAssignments(): List<PetroniteNozzleAssignment> = lastAssignments

    /**
     * Forces an immediate refresh of the nozzle assignment snapshot.
     */
    suspend fun refresh() {
        loadAssignments()
    }

    // -- Private helpers ------------------------------------------------------

    private suspend fun ensureFresh() {
        if (System.currentTimeMillis() - lastRefreshedAtMillis < REFRESH_INTERVAL_MS) {
            return
        }
        loadAssignments()
    }

    private suspend fun loadAssignments() {
        mutex.withLock {
            // Double-check after acquiring the lock.
            if (System.currentTimeMillis() - lastRefreshedAtMillis < DEBOUNCE_MS) {
                return
            }

            try {
                val token = oauthClient.getAccessToken()
                val baseUrl = config.hostAddress.trimEnd('/')
                val url = "$baseUrl/nozzles/assigned"

                val response = httpClient.get(url) {
                    header("Authorization", "Bearer $token")
                }

                if (response.status != HttpStatusCode.OK) {
                    Log.w(TAG, "GET /nozzles/assigned returned HTTP ${response.status.value}")
                    return
                }

                val body = response.bodyAsText()
                val assignments = json.decodeFromString<List<PetroniteNozzleAssignment>>(body)

                val idToCanonical = mutableMapOf<String, NozzleCanonical>()
                val canonToId = mutableMapOf<String, String>()

                for (a in assignments) {
                    idToCanonical[a.nozzleId] = NozzleCanonical(a.pumpNumber, a.nozzleNumber)
                    canonToId[canonicalKey(a.pumpNumber, a.nozzleNumber)] = a.nozzleId
                }

                nozzleIdToCanonical = idToCanonical
                canonicalToNozzleId = canonToId
                lastAssignments = assignments
                lastRefreshedAtMillis = System.currentTimeMillis()

                Log.i(TAG, "Nozzle resolver refreshed: ${assignments.size} nozzle(s) mapped")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.w(TAG, "Nozzle resolver refresh failed: ${e::class.simpleName}: ${e.message}")
                // Keep the existing snapshot on failure.
            }
        }
    }

    companion object {
        private const val TAG = "PetroniteNozzle"

        /** Refresh interval: 30 minutes. */
        private const val REFRESH_INTERVAL_MS = 30L * 60 * 1000

        /** Debounce: do not refresh more often than every 5 seconds. */
        private const val DEBOUNCE_MS = 5_000L

        private fun canonicalKey(pumpNumber: Int, nozzleNumber: Int): String =
            "$pumpNumber-$nozzleNumber"
    }
}

/**
 * Canonical pump/nozzle number pair resolved from a Petronite nozzle ID.
 */
data class NozzleCanonical(
    val pumpNumber: Int,
    val nozzleNumber: Int,
)
