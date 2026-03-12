package com.fccmiddleware.edge.preauth

import android.util.Log
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.PreAuthCommand
import com.fccmiddleware.edge.adapter.common.PreAuthResult
import com.fccmiddleware.edge.adapter.common.PreAuthResultStatus
import com.fccmiddleware.edge.adapter.common.PreAuthStatus
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.NozzleDao
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.net.ConnectException
import java.time.Instant
import java.util.UUID

/**
 * PreAuthHandler — processes pre-authorization requests from Odoo POS.
 *
 * TOP LATENCY PATH: always operates over LAN (never cloud). Cloud forwarding is
 * always async and never on the request path (p95 local overhead <= 150 ms).
 *
 * Flow per EA-2.5:
 *   1. Local dedup: return existing non-terminal record for same (odooOrderId, siteCode)
 *   2. Resolve FCC pump/nozzle numbers from Nozzle mapping table
 *   3. Connectivity guard: reject if FCC_UNREACHABLE or FULLY_OFFLINE
 *   4. Persist PreAuthRecord with PENDING status
 *   5. Call IFccAdapter.sendPreAuth() over LAN with FCC numbers
 *   6. Update record status based on FCC response (AUTHORIZED or FAILED)
 *   7. Queue record for async cloud forwarding (isCloudSynced=0)
 *
 * Pre-auth is ALWAYS via LAN regardless of internet state.
 * Cloud forwarding is triggered asynchronously and never blocks the response path.
 */
class PreAuthHandler(
    private val preAuthDao: PreAuthDao,
    private val nozzleDao: NozzleDao,
    private val connectivityManager: ConnectivityManager,
    private val auditLogDao: AuditLogDao,
    private val scope: CoroutineScope,
    /** Nullable until adapter factory is wired (EA-2.x). Absent adapter → ERROR result. */
    private val fccAdapter: IFccAdapter? = null,
    val config: PreAuthHandlerConfig = PreAuthHandlerConfig(),
) {

    data class PreAuthHandlerConfig(
        /** FCC call timeout in milliseconds. Default 30 s per spec. */
        val fccTimeoutMs: Long = 30_000L,
        /**
         * Default pre-auth TTL in seconds — how far in the future expiresAt is set when
         * the FCC does not return an explicit expiry. Overridden by site config once
         * ConfigManager is wired (EA-2.x).
         */
        val defaultPreAuthTtlSeconds: Long = 300L,
    )

    companion object {
        private const val TAG = "PreAuthHandler"

        /** Pre-auth statuses that indicate an active, in-flight or in-progress request. */
        private val NON_TERMINAL_STATUSES = setOf(
            PreAuthStatus.PENDING.name,
            PreAuthStatus.AUTHORIZED.name,
            PreAuthStatus.DISPENSING.name,
        )
    }

    // -------------------------------------------------------------------------
    // handle — submit pre-auth to FCC over LAN
    // -------------------------------------------------------------------------

    /**
     * Submit a pre-authorization command to the FCC over LAN.
     *
     * Returns the FCC result immediately. Cloud forwarding runs asynchronously
     * and never adds to this call's latency.
     *
     * p95 local API overhead target: <= 150 ms before FCC call time.
     * p95 end-to-end on healthy FCC LAN: <= 1.5 s; p99 <= 3 s.
     */
    suspend fun handle(command: PreAuthCommand): PreAuthResult {
        val siteCode = command.siteCode
        val odooOrderId = command.odooOrderId
            ?: return error("odooOrderId is required")

        // ------------------------------------------------------------------
        // 1. Local dedup — prevent duplicate FCC calls for the same Odoo order
        // ------------------------------------------------------------------
        val existing = preAuthDao.getByOdooOrderId(odooOrderId, siteCode)
        if (existing != null && existing.status in NON_TERMINAL_STATUSES) {
            Log.d(TAG, "Dedup hit: orderId=$odooOrderId status=${existing.status} — returning existing record")
            return dedupResult(existing)
        }

        // ------------------------------------------------------------------
        // 2. Resolve FCC pump/nozzle numbers from Nozzle mapping table
        // ------------------------------------------------------------------
        val odooNozzleNumber = command.nozzleNumber ?: command.pumpNumber
        val nozzle = nozzleDao.resolveForPreAuth(
            siteCode = siteCode,
            odooPumpNumber = command.pumpNumber,
            odooNozzleNumber = odooNozzleNumber,
        ) ?: run {
            Log.w(TAG, "Nozzle mapping not found: siteCode=$siteCode pump=${command.pumpNumber} nozzle=$odooNozzleNumber")
            // resolveForPreAuth already filters is_active=1, so null means either no
            // mapping or inactive nozzle — both treated as NOZZLE_MAPPING_NOT_FOUND
            return error("NOZZLE_MAPPING_NOT_FOUND")
        }

        // ------------------------------------------------------------------
        // 3. Connectivity guard — pre-auth requires FCC reachability (LAN)
        // ------------------------------------------------------------------
        val connState = connectivityManager.state.value
        if (connState == ConnectivityState.FCC_UNREACHABLE || connState == ConnectivityState.FULLY_OFFLINE) {
            Log.w(TAG, "Pre-auth rejected: FCC not reachable (connState=$connState)")
            return error("FCC_UNREACHABLE")
        }

        // ------------------------------------------------------------------
        // 4. Adapter availability check
        // ------------------------------------------------------------------
        val adapter = fccAdapter ?: run {
            Log.e(TAG, "No FCC adapter configured — cannot send pre-auth (EA-2.x pending)")
            return error("FCC adapter not configured")
        }

        // ------------------------------------------------------------------
        // 5. Persist PreAuthRecord with PENDING status before FCC call
        //    (ensures the record exists even if the process crashes mid-flight)
        // ------------------------------------------------------------------
        val now = Instant.now().toString()
        val expiresAt = Instant.now().plusSeconds(config.defaultPreAuthTtlSeconds).toString()
        val recordId = UUID.randomUUID().toString()

        val record = PreAuthRecord(
            id = recordId,
            siteCode = siteCode,
            odooOrderId = odooOrderId,
            pumpNumber = nozzle.fccPumpNumber,
            nozzleNumber = nozzle.fccNozzleNumber,
            productCode = nozzle.productCode,
            currencyCode = command.currencyCode,
            requestedAmountMinorUnits = command.amountMinorUnits,
            authorizedAmountMinorUnits = null,
            status = PreAuthStatus.PENDING.name,
            fccCorrelationId = null,
            fccAuthorizationCode = null,
            failureReason = null,
            customerName = null,
            customerTaxId = command.customerTaxId, // PII — NEVER log
            rawFccResponse = null,
            requestedAt = now,
            authorizedAt = null,
            completedAt = null,
            expiresAt = expiresAt,
            isCloudSynced = 0,
            cloudSyncAttempts = 0,
            lastCloudSyncAttemptAt = null,
            schemaVersion = 1,
            createdAt = now,
        )

        val insertedRowId = preAuthDao.insert(record)
        // -1 means the unique index (odoo_order_id, site_code) prevented insertion —
        // a concurrent request raced us and already created the record.
        // M-12: Return dedup result immediately to prevent both racers calling sendPreAuth().
        if (insertedRowId == -1L) {
            val raceWinner = preAuthDao.getByOdooOrderId(odooOrderId, siteCode) ?: record
            Log.d(TAG, "Concurrent insert detected for orderId=$odooOrderId status=${raceWinner.status} — returning dedup result")
            return dedupResult(raceWinner)
        }
        val activeRecord = record

        // ------------------------------------------------------------------
        // 6. Send pre-auth to FCC over LAN using FCC numbers from nozzle mapping
        // ------------------------------------------------------------------
        val fccCommand = command.copy(
            pumpNumber = nozzle.fccPumpNumber,
            nozzleNumber = nozzle.fccNozzleNumber,
        )

        val fccResult = try {
            withTimeout(config.fccTimeoutMs) {
                adapter.sendPreAuth(fccCommand)
            }
        } catch (e: TimeoutCancellationException) {
            Log.w(TAG, "FCC sendPreAuth timed out for orderId=$odooOrderId")
            PreAuthResult(
                status = PreAuthResultStatus.TIMEOUT,
                message = "FCC did not respond within ${config.fccTimeoutMs}ms timeout",
            )
        } catch (e: ConnectException) {
            Log.w(TAG, "FCC connection refused for orderId=$odooOrderId: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "FCC_CONNECTION_REFUSED",
            )
        } catch (e: java.io.IOException) {
            Log.w(TAG, "FCC network I/O error for orderId=$odooOrderId: ${e.javaClass.simpleName}: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "FCC_NETWORK_ERROR: ${e.javaClass.simpleName}",
            )
        } catch (e: Exception) {
            Log.e(TAG, "Unexpected error in sendPreAuth for orderId=$odooOrderId: ${e.javaClass.simpleName}: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "FCC_INTERNAL_ERROR: ${e.javaClass.simpleName}",
            )
        }

        // ------------------------------------------------------------------
        // 7. Update DB record based on FCC response; mark isCloudSynced=0
        // ------------------------------------------------------------------
        val newStatus: String
        val authorizedAt: String?

        when (fccResult.status) {
            PreAuthResultStatus.AUTHORIZED -> {
                newStatus = PreAuthStatus.AUTHORIZED.name
                authorizedAt = Instant.now().toString()
            }
            else -> {
                newStatus = PreAuthStatus.FAILED.name
                authorizedAt = null
            }
        }

        preAuthDao.updateStatus(
            id = activeRecord.id,
            status = newStatus,
            fccCorrelationId = null,
            fccAuthorizationCode = fccResult.authorizationCode,
            failureReason = if (fccResult.status != PreAuthResultStatus.AUTHORIZED) fccResult.message else null,
            authorizedAt = authorizedAt,
            completedAt = null,
        )

        // ------------------------------------------------------------------
        // Async audit log + cloud forward signal — never on the hot path
        // ------------------------------------------------------------------
        scope.launch {
            auditLogDao.insert(
                AuditLog(
                    eventType = "PRE_AUTH_HANDLED",
                    message = "id=${activeRecord.id} orderId=$odooOrderId pump=${nozzle.fccPumpNumber} status=$newStatus",
                    correlationId = activeRecord.id,
                    createdAt = Instant.now().toString(),
                )
            )
            // Record is already isCloudSynced=0; CloudUploadWorker (EA-2.x) will pick it
            // up on its next cycle without any explicit signal needed here.
        }

        return fccResult
    }

    // -------------------------------------------------------------------------
    // cancel — deauthorize an active pre-auth
    // -------------------------------------------------------------------------

    /**
     * Cancel an active pre-authorization by Odoo order ID.
     *
     * - PENDING / AUTHORIZED → update DB to CANCELLED; attempt FCC deauthorization (best-effort).
     * - DISPENSING → cannot cancel; pump is actively dispensing. Returns failure.
     * - Terminal state (COMPLETED, CANCELLED, EXPIRED, FAILED) → idempotent success.
     * - No matching record → idempotent success (treated as already cancelled).
     *
     * TODO (EA-3.x): IFccAdapter should expose a dedicated cancelPreAuth() method.
     * Until then, FCC deauthorization is omitted and only the DB record is updated.
     */
    suspend fun cancel(odooOrderId: String, siteCode: String): CancelPreAuthResult {
        val record = preAuthDao.getByOdooOrderId(odooOrderId, siteCode)
            ?: return CancelPreAuthResult(success = true, message = "No record found — treated as already cancelled")

        return when (record.status) {
            PreAuthStatus.DISPENSING.name -> {
                Log.w(TAG, "Cannot cancel: pre-auth in DISPENSING state for orderId=$odooOrderId")
                CancelPreAuthResult(success = false, message = "Cannot cancel: pump is actively dispensing")
            }

            PreAuthStatus.PENDING.name, PreAuthStatus.AUTHORIZED.name -> {
                // TODO (EA-3.x): call adapter.cancelPreAuth() when the interface adds that method.
                // For now we update the DB record only; the FCC pre-auth will expire naturally.
                Log.i(TAG, "Cancelling pre-auth id=${record.id} orderId=$odooOrderId (status=${record.status})")

                preAuthDao.updateStatus(
                    id = record.id,
                    status = PreAuthStatus.CANCELLED.name,
                    fccCorrelationId = record.fccCorrelationId,
                    fccAuthorizationCode = record.fccAuthorizationCode,
                    failureReason = null,
                    authorizedAt = record.authorizedAt,
                    completedAt = Instant.now().toString(),
                )

                scope.launch {
                    auditLogDao.insert(
                        AuditLog(
                            eventType = "PRE_AUTH_CANCELLED",
                            message = "id=${record.id} orderId=$odooOrderId cancelled from ${record.status}",
                            correlationId = record.id,
                            createdAt = Instant.now().toString(),
                        )
                    )
                }

                CancelPreAuthResult(success = true)
            }

            else -> {
                // Already in a terminal state — idempotent
                CancelPreAuthResult(success = true, message = "Pre-auth already in terminal state: ${record.status}")
            }
        }
    }

    // -------------------------------------------------------------------------
    // runExpiryCheck — periodic expiry worker (called by CadenceController)
    // -------------------------------------------------------------------------

    /**
     * Find pre-auth records past their [expiresAt] that are still PENDING/AUTHORIZED/DISPENSING
     * and transition them to EXPIRED.
     *
     * FCC deauthorization is attempted (best-effort) for AUTHORIZED records when an adapter
     * is available. Failures are logged and ignored.
     *
     * Called on every cadence tick. Fast under normal conditions (index hit, empty result set).
     *
     * TODO (EA-3.x): replace sendPreAuth(amount=0) with a dedicated adapter.cancelPreAuth().
     */
    suspend fun runExpiryCheck() {
        val now = Instant.now().toString()
        val expiring = preAuthDao.getExpiring(now)
        if (expiring.isEmpty()) return

        Log.i(TAG, "Expiry check: ${expiring.size} pre-auth record(s) to expire")

        for (r in expiring) {
            // For AUTHORIZED records, attempt FCC deauthorization before marking EXPIRED.
            // If deauth fails, skip this record so it is retried on the next cadence tick —
            // this prevents "zombie" FCC authorizations where the pump stays authorized
            // but our DB says EXPIRED.
            if (r.status == PreAuthStatus.AUTHORIZED.name) {
                val adapter = fccAdapter
                if (adapter != null) {
                    val deauthSucceeded = try {
                        val deauthCommand = PreAuthCommand(
                            siteCode = r.siteCode,
                            pumpNumber = r.pumpNumber,
                            amountMinorUnits = 0L,
                            currencyCode = r.currencyCode,
                            nozzleNumber = r.nozzleNumber,
                            odooOrderId = r.odooOrderId,
                            customerTaxId = null,
                        )
                        withTimeout(config.fccTimeoutMs) {
                            adapter.sendPreAuth(deauthCommand)
                        }
                        true
                    } catch (e: Exception) {
                        Log.w(TAG, "FCC deauth on expiry failed for id=${r.id}, will retry next cycle: ${e.javaClass.simpleName}")
                        false
                    }

                    if (!deauthSucceeded) {
                        // Leave record as AUTHORIZED so next expiry check retries deauth
                        scope.launch {
                            auditLogDao.insert(
                                AuditLog(
                                    eventType = "PRE_AUTH_DEAUTH_RETRY_PENDING",
                                    message = "id=${r.id} orderId=${r.odooOrderId} FCC deauth failed, will retry",
                                    correlationId = r.id,
                                    createdAt = Instant.now().toString(),
                                )
                            )
                        }
                        continue
                    }
                }
                // adapter == null: no FCC to deauthorize, safe to mark EXPIRED
            }

            preAuthDao.updateStatus(
                id = r.id,
                status = PreAuthStatus.EXPIRED.name,
                fccCorrelationId = r.fccCorrelationId,
                fccAuthorizationCode = r.fccAuthorizationCode,
                failureReason = "Pre-auth expired at ${r.expiresAt}",
                authorizedAt = r.authorizedAt,
                completedAt = Instant.now().toString(),
            )

            scope.launch {
                auditLogDao.insert(
                    AuditLog(
                        eventType = "PRE_AUTH_EXPIRED",
                        message = "id=${r.id} orderId=${r.odooOrderId} expired at ${r.expiresAt}",
                        correlationId = r.id,
                        createdAt = Instant.now().toString(),
                    )
                )
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun dedupResult(existing: PreAuthRecord): PreAuthResult {
        return when (existing.status) {
            PreAuthStatus.AUTHORIZED.name, PreAuthStatus.DISPENSING.name -> PreAuthResult(
                status = PreAuthResultStatus.AUTHORIZED,
                authorizationCode = existing.fccAuthorizationCode,
                expiresAtUtc = existing.expiresAt,
                message = "Existing active pre-auth returned (idempotent)",
            )
            else -> PreAuthResult(
                // PENDING: FCC call is in-flight — signal Odoo to wait and retry
                // (not ERROR, which Odoo would interpret as a permanent failure)
                status = PreAuthResultStatus.IN_PROGRESS,
                message = "Pre-auth in progress for orderId=${existing.odooOrderId}",
            )
        }
    }

    private fun error(message: String) = PreAuthResult(
        status = PreAuthResultStatus.ERROR,
        message = message,
    )
}

data class CancelPreAuthResult(
    val success: Boolean,
    val message: String? = null,
)
