package com.fccmiddleware.edge.preauth

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.CancelPreAuthCommand
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
import com.fccmiddleware.edge.security.KeystoreBackedStringCipher
import com.fccmiddleware.edge.security.KeystoreManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.net.ConnectException
import com.fccmiddleware.edge.adapter.common.AdapterTimeouts
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

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
    private val keystoreManager: KeystoreManager? = null,
    fccAdapter: IFccAdapter? = null,
    val config: PreAuthHandlerConfig = PreAuthHandlerConfig(),
) {
    /** Late-bound: wired when FCC config becomes available after startup. */
    @Volatile
    internal var fccAdapter: IFccAdapter? = fccAdapter

    internal fun wireFccAdapter(adapter: IFccAdapter?) {
        fccAdapter = adapter
    }

    data class PreAuthHandlerConfig(
        /** M-22: FCC call timeout — uses centralised default (2× adapter timeout to cover multi-step flows). */
        val fccTimeoutMs: Long = AdapterTimeouts.PREAUTH_TIMEOUT_MS * 2,
        /**
         * Default pre-auth TTL in seconds — how far in the future expiresAt is set when
         * the FCC does not return an explicit expiry. Overridden by site config once
         * ConfigManager is wired (EA-2.x).
         */
        val defaultPreAuthTtlSeconds: Long = 300L,
        /**
         * PA-P02 / AP-037: Maximum records loaded per expiry check cycle.
         * Reduced from 50 to 5 to cap worst-case tick duration at 5 × timeout.
         * Remaining records are picked up on subsequent ticks.
         */
        val expiryBatchSize: Int = 5,
    )

    companion object {
        private const val TAG = "PreAuthHandler"

        /**
         * AF-005: Maximum deauth retry attempts during expiry checks.
         * After exhausting retries, the record is force-expired with a diagnostic message.
         * The FCC's own TTL will expire the pre-auth naturally on its side.
         */
        private const val MAX_DEAUTH_RETRIES = 5
    }

    /**
     * AF-005: In-memory deauth attempt counter per pre-auth record ID.
     * Tracks how many times expiry-check deauth has failed for each record.
     * Resets on process restart (acceptable — FCC adapter also gets a fresh session).
     */
    internal val deauthAttemptCounts = ConcurrentHashMap<String, Int>()
    private val customerTaxIdCipher = KeystoreBackedStringCipher(
        keystoreManager = keystoreManager,
        alias = KeystoreManager.ALIAS_PREAUTH_PII,
    )

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
        if (command.unitPrice <= 0L) {
            return error("INVALID_UNIT_PRICE")
        }

        // ------------------------------------------------------------------
        // 1. Local dedup — prevent duplicate FCC calls for the same Odoo order
        // ------------------------------------------------------------------
        val existing = preAuthDao.getByOdooOrderId(odooOrderId, siteCode)
        if (existing != null && PreAuthStateMachine.isActive(existing.status)) {
            AppLogger.d(TAG, "Dedup hit: orderId=$odooOrderId status=${existing.status} — returning existing record")
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
            AppLogger.w(TAG, "Nozzle mapping not found: siteCode=$siteCode pump=${command.pumpNumber} nozzle=$odooNozzleNumber")
            // resolveForPreAuth already filters is_active=1, so null means either no
            // mapping or inactive nozzle — both treated as NOZZLE_MAPPING_NOT_FOUND
            return error("NOZZLE_MAPPING_NOT_FOUND")
        }

        // ------------------------------------------------------------------
        // 3. Connectivity guard — pre-auth requires FCC reachability (LAN)
        // ------------------------------------------------------------------
        val connState = connectivityManager.state.value
        if (connState == ConnectivityState.FCC_UNREACHABLE || connState == ConnectivityState.FULLY_OFFLINE) {
            AppLogger.w(TAG, "Pre-auth rejected: FCC not reachable (connState=$connState)")
            return error("FCC_UNREACHABLE")
        }

        // ------------------------------------------------------------------
        // 4. Adapter availability check
        // ------------------------------------------------------------------
        val adapter = fccAdapter ?: run {
            AppLogger.e(TAG, "No FCC adapter configured — cannot send pre-auth (EA-2.x pending)")
            return error("FCC adapter not configured")
        }

        // ------------------------------------------------------------------
        // 5. Persist PreAuthRecord with PENDING status before FCC call
        //    (ensures the record exists even if the process crashes mid-flight)
        // ------------------------------------------------------------------
        val now = Instant.now().toString()
        val expiresAt = Instant.now().plusSeconds(config.defaultPreAuthTtlSeconds).toString()
        val recordId = UUID.randomUUID().toString()
        val encryptedCustomerTaxId = try {
            customerTaxIdCipher.encryptForStorage(command.customerTaxId)
        } catch (e: IllegalStateException) {
            AppLogger.e(TAG, "Failed to encrypt customerTaxId before persistence: ${e.message}", e)
            return error("LOCAL_PII_ENCRYPTION_FAILED")
        }

        val record = PreAuthRecord(
            id = recordId,
            siteCode = siteCode,
            odooOrderId = odooOrderId,
            pumpNumber = nozzle.fccPumpNumber,
            nozzleNumber = nozzle.fccNozzleNumber,
            productCode = nozzle.productCode,
            currencyCode = command.currencyCode,
            requestedAmountMinorUnits = command.amountMinorUnits,
            unitPrice = command.unitPrice,
            authorizedAmountMinorUnits = null,
            status = PreAuthStatus.PENDING,
            fccCorrelationId = null,
            fccAuthorizationCode = null,
            failureReason = null,
            customerName = command.customerName,
            customerTaxId = encryptedCustomerTaxId, // PII — stored as Keystore-encrypted Base64
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
            vehicleNumber = command.vehicleNumber,
            customerBusinessName = command.customerBusinessName,
        )

        val insertedRowId = preAuthDao.insert(record)
        // -1 means the unique index (odoo_order_id, site_code) prevented insertion —
        // a concurrent request raced us and already created the record.
        // M-12: Return dedup result immediately to prevent both racers calling sendPreAuth().
        if (insertedRowId == -1L) {
            val raceWinner = preAuthDao.getByOdooOrderId(odooOrderId, siteCode) ?: record
            AppLogger.d(TAG, "Concurrent insert detected for orderId=$odooOrderId status=${raceWinner.status} — returning dedup result")
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
            AppLogger.w(TAG, "FCC sendPreAuth timed out for orderId=$odooOrderId")
            PreAuthResult(
                status = PreAuthResultStatus.TIMEOUT,
                message = "FCC_TIMEOUT",
            )
        } catch (e: ConnectException) {
            AppLogger.w(TAG, "FCC connection refused for orderId=$odooOrderId: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "FCC_CONNECTION_REFUSED",
            )
        } catch (e: java.io.IOException) {
            AppLogger.w(TAG, "FCC network I/O error for orderId=$odooOrderId: ${e.javaClass.simpleName}: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "FCC_NETWORK_ERROR",
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "Unexpected error in sendPreAuth for orderId=$odooOrderId: ${e.javaClass.simpleName}: ${e.message}")
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "FCC_INTERNAL_ERROR",
            )
        }

        // ------------------------------------------------------------------
        // 7. Update DB record based on FCC response; mark isCloudSynced=0
        // ------------------------------------------------------------------
        val newStatus: PreAuthStatus
        val authorizedAt: String?

        when (fccResult.status) {
            PreAuthResultStatus.AUTHORIZED -> {
                newStatus = PreAuthStatus.AUTHORIZED
                authorizedAt = Instant.now().toString()
            }
            else -> {
                newStatus = PreAuthStatus.FAILED
                authorizedAt = null
            }
        }

        // Validate FCC correlationId: warn if missing on AUTHORIZED (needed for reconciliation)
        val fccCorrelationId = fccResult.correlationId
        if (fccResult.status == PreAuthResultStatus.AUTHORIZED && fccCorrelationId == null) {
            AppLogger.w(TAG, "FCC returned AUTHORIZED without correlationId for orderId=$odooOrderId — reconciliation may fail")
        }

        preAuthDao.updateStatus(
            id = activeRecord.id,
            status = newStatus,
            fccCorrelationId = fccCorrelationId,
            fccAuthorizationCode = fccResult.authorizationCode,
            failureReason = if (fccResult.status != PreAuthResultStatus.AUTHORIZED) fccResult.message else null,
            authorizedAt = authorizedAt,
            completedAt = null,
        )

        // ------------------------------------------------------------------
        // Async audit log + cloud forward signal — never on the hot path
        // ------------------------------------------------------------------
        scope.launch {
            try {
                auditLogDao.insert(
                    AuditLog(
                        eventType = "PRE_AUTH_HANDLED",
                        message = "id=${activeRecord.id} orderId=$odooOrderId pump=${nozzle.fccPumpNumber} status=$newStatus",
                        correlationId = activeRecord.id,
                        createdAt = Instant.now().toString(),
                    )
                )
            } catch (e: Exception) {
                // AT-005: Fallback to file logger so audit intent is not silently lost
                AppLogger.e(TAG, "Audit log insert failed for PRE_AUTH_HANDLED id=${activeRecord.id}: ${e.message}", e)
            }
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
     * - PENDING / AUTHORIZED → attempt FCC deauthorization (best-effort), then update DB to CANCELLED.
     * - DISPENSING → cannot cancel; pump is actively dispensing. Returns failure.
     * - Terminal state (COMPLETED, CANCELLED, EXPIRED, FAILED) → idempotent success.
     * - No matching record → idempotent success (treated as already cancelled).
     *
     * FCC deauthorization is best-effort: if the adapter call fails, the DB record is still
     * updated to CANCELLED (the FCC pre-auth will expire naturally via its own TTL).
     */
    suspend fun cancel(odooOrderId: String, siteCode: String): CancelPreAuthResult {
        val record = preAuthDao.getByOdooOrderId(odooOrderId, siteCode)
            ?: return CancelPreAuthResult(success = true, message = "No record found — treated as already cancelled")

        return when (record.status) {
            PreAuthStatus.DISPENSING -> {
                AppLogger.w(TAG, "Cannot cancel: pre-auth in DISPENSING state for orderId=$odooOrderId")
                CancelPreAuthResult(success = false, message = "Cannot cancel: pump is actively dispensing")
            }

            PreAuthStatus.PENDING, PreAuthStatus.AUTHORIZED -> {
                AppLogger.i(TAG, "Cancelling pre-auth id=${record.id} orderId=$odooOrderId (status=${record.status})")

                // Best-effort FCC deauthorization — log and continue on failure.
                // The FCC pre-auth will expire naturally via its TTL if deauth fails.
                val adapter = fccAdapter
                if (adapter != null && record.status == PreAuthStatus.AUTHORIZED) {
                    try {
                        val cancelCommand = CancelPreAuthCommand(
                            siteCode = record.siteCode,
                            pumpNumber = record.pumpNumber,
                            nozzleNumber = record.nozzleNumber,
                            fccCorrelationId = record.fccCorrelationId,
                        )
                        val deauthOk = withTimeout(config.fccTimeoutMs) {
                            adapter.cancelPreAuth(cancelCommand)
                        }
                        if (deauthOk) {
                            AppLogger.i(TAG, "FCC deauth succeeded for orderId=$odooOrderId")
                        } else {
                            AppLogger.w(TAG, "FCC deauth returned false for orderId=$odooOrderId — proceeding with DB cancel")
                        }
                    } catch (e: Exception) {
                        AppLogger.w(TAG, "FCC deauth failed for orderId=$odooOrderId: ${e::class.simpleName}: ${e.message} — proceeding with DB cancel")
                    }
                }

                // GAP-3: Use atomic cancel+unsync so the cancellation is forwarded to cloud
                // even if the record was already synced as AUTHORIZED (is_cloud_synced = 1).
                preAuthDao.markCancelledAndUnsync(
                    id = record.id,
                    status = PreAuthStatus.CANCELLED,
                    cancelledAt = Instant.now().toString(),
                    failureReason = null,
                )

                scope.launch {
                    try {
                        auditLogDao.insert(
                            AuditLog(
                                eventType = "PRE_AUTH_CANCELLED",
                                message = "id=${record.id} orderId=$odooOrderId cancelled from ${record.status}",
                                correlationId = record.id,
                                createdAt = Instant.now().toString(),
                            )
                        )
                    } catch (e: Exception) {
                        AppLogger.e(TAG, "Audit log insert failed for PRE_AUTH_CANCELLED id=${record.id}: ${e.message}", e)
                    }
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
     * is available. Failures are logged and the record is left as AUTHORIZED so the next
     * cadence tick retries — this prevents "zombie" FCC authorizations.
     *
     * Called on every cadence tick. Fast under normal conditions (index hit, empty result set).
     */
    suspend fun runExpiryCheck() {
        val now = Instant.now().toString()
        val expiring = preAuthDao.getExpiring(now, config.expiryBatchSize)
        if (expiring.isEmpty()) return

        AppLogger.i(TAG, "Expiry check: ${expiring.size} pre-auth record(s) to expire")

        for (r in expiring) {
            // AF-005: Tracks whether this record's deauth was force-exhausted (for failure reason).
            var deauthExhausted = false

            // For AUTHORIZED records, attempt FCC deauthorization before marking EXPIRED.
            // If deauth fails, skip this record so it is retried on the next cadence tick —
            // this prevents "zombie" FCC authorizations where the pump stays authorized
            // but our DB says EXPIRED.
            if (r.status == PreAuthStatus.AUTHORIZED) {
                val adapter = fccAdapter
                if (adapter != null) {
                    // AF-005: Check if deauth retries have been exhausted for this record.
                    val attempts = deauthAttemptCounts.getOrDefault(r.id, 0)
                    if (attempts >= MAX_DEAUTH_RETRIES) {
                        // Force-expire: deauth retries exhausted. The FCC's own TTL
                        // will expire the pre-auth naturally on its side.
                        AppLogger.w(
                            TAG,
                            "AF-005: Deauth retries exhausted ($attempts/$MAX_DEAUTH_RETRIES) for id=${r.id} " +
                                "orderId=${r.odooOrderId} — force-expiring. FCC TTL will handle cleanup.",
                        )
                        deauthExhausted = true
                        deauthAttemptCounts.remove(r.id)
                        scope.launch {
                            try {
                                auditLogDao.insert(
                                    AuditLog(
                                        eventType = "PRE_AUTH_DEAUTH_EXHAUSTED",
                                        message = "id=${r.id} orderId=${r.odooOrderId} deauth failed after $attempts attempts, force-expired",
                                        correlationId = r.id,
                                        createdAt = Instant.now().toString(),
                                    )
                                )
                            } catch (e: Exception) {
                                AppLogger.e(TAG, "Audit log insert failed for PRE_AUTH_DEAUTH_EXHAUSTED id=${r.id}: ${e.message}", e)
                            }
                        }
                        // Fall through to mark EXPIRED below
                    } else {
                        var fccUnreachable = false
                        val deauthSucceeded = try {
                            val cancelCommand = CancelPreAuthCommand(
                                siteCode = r.siteCode,
                                pumpNumber = r.pumpNumber,
                                nozzleNumber = r.nozzleNumber,
                                fccCorrelationId = r.fccCorrelationId,
                            )
                            withTimeout(config.fccTimeoutMs) {
                                adapter.cancelPreAuth(cancelCommand)
                            }
                        } catch (e: Exception) {
                            // AP-037: Detect FCC unreachable conditions for early exit
                            if (e is TimeoutCancellationException || e is java.io.IOException) {
                                fccUnreachable = true
                            }
                            AppLogger.w(TAG, "FCC deauth on expiry failed for id=${r.id} (attempt ${attempts + 1}/$MAX_DEAUTH_RETRIES): ${e.javaClass.simpleName}")
                            false
                        }

                        if (!deauthSucceeded) {
                            // AF-005: Increment deauth attempt counter
                            deauthAttemptCounts[r.id] = attempts + 1
                            scope.launch {
                                try {
                                    auditLogDao.insert(
                                        AuditLog(
                                            eventType = "PRE_AUTH_DEAUTH_RETRY_PENDING",
                                            message = "id=${r.id} orderId=${r.odooOrderId} FCC deauth failed (attempt ${attempts + 1}/$MAX_DEAUTH_RETRIES), will retry",
                                            correlationId = r.id,
                                            createdAt = Instant.now().toString(),
                                        )
                                    )
                                } catch (e: Exception) {
                                    AppLogger.e(TAG, "Audit log insert failed for PRE_AUTH_DEAUTH_RETRY_PENDING id=${r.id}: ${e.message}", e)
                                }
                            }
                            // AP-037: If FCC is unreachable, stop processing remaining records —
                            // subsequent deauth calls will also fail. Defer to next tick.
                            if (fccUnreachable) {
                                AppLogger.w(TAG, "AP-037: FCC unreachable during expiry check — deferring remaining expired record(s) to next tick")
                                return
                            }
                            continue
                        }
                        // Deauth succeeded — clean up counter and fall through to mark EXPIRED
                        deauthAttemptCounts.remove(r.id)
                    }
                }
                // adapter == null: no FCC to deauthorize, safe to mark EXPIRED
            }

            // GAP-3: Use atomic expire+unsync so the expiry is forwarded to cloud
            // even if the record was already synced as AUTHORIZED (is_cloud_synced = 1).
            val failureReason = if (deauthExhausted) {
                "Pre-auth expired at ${r.expiresAt}; FCC deauth not confirmed after $MAX_DEAUTH_RETRIES attempts (FCC TTL will handle cleanup)"
            } else {
                "Pre-auth expired at ${r.expiresAt}"
            }
            preAuthDao.markExpiredAndUnsync(
                id = r.id,
                status = PreAuthStatus.EXPIRED,
                expiredAt = Instant.now().toString(),
                failureReason = failureReason,
            )

            scope.launch {
                try {
                    auditLogDao.insert(
                        AuditLog(
                            eventType = "PRE_AUTH_EXPIRED",
                            message = "id=${r.id} orderId=${r.odooOrderId} expired at ${r.expiresAt}",
                            correlationId = r.id,
                            createdAt = Instant.now().toString(),
                        )
                    )
                } catch (e: Exception) {
                    AppLogger.e(TAG, "Audit log insert failed for PRE_AUTH_EXPIRED id=${r.id}: ${e.message}", e)
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun dedupResult(existing: PreAuthRecord): PreAuthResult {
        return when (existing.status) {
            PreAuthStatus.AUTHORIZED, PreAuthStatus.DISPENSING -> PreAuthResult(
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
