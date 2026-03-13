package com.fccmiddleware.edge.adapter.doms

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.*
import com.fccmiddleware.edge.adapter.common.PumpStatusCapability
import com.fccmiddleware.edge.adapter.doms.jpl.JplHeartbeatManager
import com.fccmiddleware.edge.adapter.doms.jpl.JplTcpClient
import com.fccmiddleware.edge.adapter.doms.mapping.DomsCanonicalMapper
import com.fccmiddleware.edge.adapter.doms.protocol.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.withContext
import kotlinx.coroutines.yield
import kotlinx.serialization.json.Json
import java.io.Closeable

/**
 * DomsJplAdapter — Full DOMS TCP/JPL adapter implementation.
 *
 * Implements both IFccAdapter (business operations) and IFccConnectionLifecycle
 * (persistent TCP connection management).
 *
 * Protocol: binary STX/ETX-framed JSON over persistent TCP socket.
 * Auth: FcLogon handshake with access code.
 * Heartbeat: empty frame every N seconds (default 30).
 * Fetch: lock → read → clear supervised buffer.
 * Pre-auth: authorize_Fp_req / deauthorize_Fp_req JPL messages.
 */
class DomsJplAdapter(
    private val config: AgentFccConfig,
    private val siteCode: String = "",
    private val legalEntityId: String = "",
    /** Optional callback invoked on the raw TCP socket before connect().
     *  Used to bind FCC traffic to WiFi via Android Network.bindSocket(). */
    private val socketBinder: ((java.net.Socket) -> Unit)? = null,
) : IFccAdapter, IFccConnectionLifecycle, Closeable {

    override val pumpStatusCapability = PumpStatusCapability.LIVE

    companion object {
        private const val TAG = "DomsJplAdapter"
        val VENDOR = FccVendor.DOMS
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "TCP_JPL"
        const val IS_IMPLEMENTED = true
    }

    private val adapterScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val tcpClient = JplTcpClient(
        host = config.hostAddress,
        port = config.jplPort ?: config.port,
        scope = adapterScope,
        socketBinder = socketBinder,
    )

    private val heartbeatManager = JplHeartbeatManager(
        tcpClient = tcpClient,
        scope = adapterScope,
        intervalSeconds = config.heartbeatIntervalSeconds ?: 30,
    )

    private var eventListener: IFccEventListener? = null
    private var logonComplete = false

    // ── IFccConnectionLifecycle ──────────────────────────────────────────────

    override suspend fun connect() {
        tcpClient.onDisconnected = { reason ->
            logonComplete = false
            heartbeatManager.stop()
            eventListener?.onConnectionLost(reason)
        }

        tcpClient.onUnsolicitedMessage = { message ->
            handleUnsolicitedMessage(message)
        }

        heartbeatManager.onDeadConnection = {
            logonComplete = false
            heartbeatManager.stop()
            eventListener?.onConnectionLost("Dead connection (no heartbeat response)")
        }

        // Connect TCP
        tcpClient.connect()

        // FcLogon handshake
        val logonRequest = DomsLogonHandler.buildLogonRequest(
            fcAccessCode = config.fcAccessCode ?: "",
            posVersionId = config.posVersionId ?: "FccMiddleware/1.0",
            countryCode = config.domsCountryCode ?: "ZA",
        )

        val logonResponse = tcpClient.sendAndReceive(
            logonRequest,
            DomsLogonHandler.LOGON_RESPONSE,
        )

        DomsLogonHandler.validateLogonResponse(logonResponse)
        logonComplete = true

        AppLogger.i(TAG, "FcLogon successful")

        // Start heartbeat
        heartbeatManager.start()
    }

    override suspend fun disconnect() {
        heartbeatManager.stop()
        logonComplete = false
        tcpClient.disconnect()
        adapterScope.cancel()
    }

    override fun close() {
        adapterScope.cancel()
    }

    override val isConnected: Boolean
        get() = tcpClient.isConnected && logonComplete

    override fun setEventListener(listener: IFccEventListener?) {
        this.eventListener = listener
    }

    // ── IFccAdapter ──────────────────────────────────────────────────────────

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return try {
            val dto = Json.decodeFromString(
                com.fccmiddleware.edge.adapter.doms.model.DomsTransactionDto.serializer(),
                rawPayload.payload,
            )
            DomsCanonicalMapper.mapToCanonical(dto, config, rawPayload.siteCode, legalEntityId)
        } catch (e: Exception) {
            NormalizationResult.Failure(
                errorCode = "INVALID_PAYLOAD",
                message = "Failed to parse DOMS transaction: ${e.message}",
            )
        }
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        if (!isConnected) {
            return PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "DOMS TCP not connected",
            )
        }

        return try {
            val fpId = command.pumpNumber - config.pumpNumberOffset

            val authRequest = DomsPreAuthHandler.buildAuthRequest(
                fpId = fpId,
                nozzleId = command.nozzleNumber ?: 0,
                amountMinorUnits = command.amountMinorUnits,
                currencyCode = command.currencyCode,
            )

            val response = tcpClient.sendAndReceive(
                authRequest,
                DomsPreAuthHandler.AUTH_RESPONSE,
            )

            val result = DomsPreAuthHandler.parseAuthResponse(response)

            PreAuthResult(
                status = result.status,
                authorizationCode = result.authorizationCode,
                expiresAtUtc = result.expiresAtUtc,
                message = result.message,
                correlationId = result.correlationId,
            )
        } catch (e: Exception) {
            PreAuthResult(
                status = PreAuthResultStatus.ERROR,
                message = "DOMS pre-auth failed: ${e.message}",
            )
        }
    }

    override suspend fun cancelPreAuth(command: CancelPreAuthCommand): Boolean {
        if (!isConnected) {
            AppLogger.w(TAG, "Cannot cancel pre-auth: DOMS TCP not connected")
            return false
        }

        return try {
            val fpId = command.pumpNumber - config.pumpNumberOffset
            val deauthRequest = DomsPreAuthHandler.buildDeauthRequest(fpId)
            tcpClient.sendAndReceive(deauthRequest, "deauthorize_Fp_resp")
            AppLogger.i(TAG, "FCC deauth succeeded for pump=${command.pumpNumber} (fpId=$fpId)")
            true
        } catch (e: Exception) {
            AppLogger.w(TAG, "FCC deauth failed for pump=${command.pumpNumber}: ${e::class.simpleName}: ${e.message}")
            false
        }
    }

    override suspend fun getPumpStatus(): List<PumpStatus> {
        if (!isConnected) return emptyList()

        return try {
            val request = DomsPumpStatusParser.buildStatusRequest(fpId = 0)
            val response = tcpClient.sendAndReceive(
                request,
                DomsPumpStatusParser.STATUS_RESPONSE,
            )

            DomsPumpStatusParser.parseStatusResponse(
                response = response,
                siteCode = siteCode,
                currencyCode = config.currencyCode,
                pumpNumberOffset = config.pumpNumberOffset,
                observedAtUtc = java.time.Instant.now().toString(),
            )
        } catch (e: Exception) {
            AppLogger.w(TAG, "getPumpStatus failed: ${e.message}")
            emptyList()
        }
    }

    override suspend fun heartbeat(): Boolean {
        return tcpClient.isConnected && logonComplete
    }

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        if (!isConnected) {
            return TransactionBatch(transactions = emptyList(), hasMore = false)
        }

        return try {
            // Step 1: Lock the supervised buffer
            val lockRequest = DomsTransactionParser.buildLockRequest()
            val lockResponse = tcpClient.sendAndReceive(
                lockRequest,
                DomsTransactionParser.LOCK_RESPONSE,
            )

            if (!DomsTransactionParser.validateLockResponse(lockResponse)) {
                return TransactionBatch(transactions = emptyList(), hasMore = false)
            }

            // Step 2: Read transactions
            val readRequest = DomsTransactionParser.buildReadRequest()
            val readResponse = tcpClient.sendAndReceive(
                readRequest,
                DomsTransactionParser.READ_RESPONSE,
            )

            val domsTxns = DomsTransactionParser.parseReadResponse(readResponse)

            if (domsTxns.isEmpty()) {
                return TransactionBatch(transactions = emptyList(), hasMore = false)
            }

            // AP-014: Normalize on Dispatchers.Default with periodic yield() so that
            // large batches (50+ txns) don't starve other coroutines on the same dispatcher.
            val canonicalTxns = withContext(Dispatchers.Default) {
                val results = mutableListOf<CanonicalTransaction>()
                for ((index, dto) in domsTxns.withIndex()) {
                    when (val result = DomsCanonicalMapper.mapToCanonical(dto, config, siteCode, legalEntityId)) {
                        is NormalizationResult.Success -> results.add(result.transaction)
                        is NormalizationResult.Failure -> {
                            AppLogger.w(TAG, "Normalization failed for txn ${dto.transactionId}: ${result.message}")
                        }
                    }
                    // Yield every 20 items to allow other coroutines to run
                    if (index % 20 == 19) yield()
                }
                results
            }

            // AF-016: Only clear the number of successfully normalized transactions so that
            // failed normalizations remain in the FCC buffer for re-fetch, not silently lost.
            val clearRequest = DomsTransactionParser.buildClearRequest(count = canonicalTxns.size)
            val clearResponse = tcpClient.sendAndReceive(
                clearRequest,
                DomsTransactionParser.CLEAR_RESPONSE,
            )

            if (!DomsTransactionParser.validateClearResponse(clearResponse)) {
                AppLogger.w(TAG, "Buffer clear failed — transactions may be re-fetched")
            }

            TransactionBatch(
                transactions = canonicalTxns,
                hasMore = false, // DOMS buffer is drained in one pass
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "fetchTransactions failed: ${e.message}")
            TransactionBatch(transactions = emptyList(), hasMore = false)
        }
    }

    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean {
        // DOMS acknowledgment is implicit in the lock-read-clear sequence.
        // Clear was already sent during fetchTransactions.
        return true
    }

    // ── Unsolicited message handling ─────────────────────────────────────────

    private fun handleUnsolicitedMessage(message: com.fccmiddleware.edge.adapter.doms.jpl.JplMessage) {
        AppLogger.d(TAG, "Unsolicited message: ${message.name}")

        when (message.name) {
            "FpStatusChanged" -> {
                val fpId = message.data["FpId"]?.toIntOrNull() ?: return
                val stateCode = message.data["FpMainState"]?.toIntOrNull() ?: return
                val domsFpState = com.fccmiddleware.edge.adapter.doms.model.DomsFpMainState.fromCode(stateCode)
                val canonicalState = domsFpState?.toCanonicalPumpState() ?: PumpState.UNKNOWN
                val pumpNumber = fpId + config.pumpNumberOffset

                eventListener?.onPumpStatusChanged(pumpNumber, canonicalState, stateCode.toString())
            }
            "TransactionAvailable" -> {
                val fpId = message.data["FpId"]?.toIntOrNull() ?: return
                val bufferIndex = message.data["BufferIndex"]?.toIntOrNull()

                eventListener?.onTransactionAvailable(
                    TransactionNotification(
                        fpId = fpId + config.pumpNumberOffset,
                        transactionBufferIndex = bufferIndex,
                        timestamp = java.time.Instant.now().toString(),
                    )
                )
            }
            "FuellingUpdate" -> {
                val fpId = message.data["FpId"]?.toIntOrNull() ?: return
                val volumeCl = message.data["Volume"]?.toLongOrNull() ?: return
                val amountX10 = message.data["Amount"]?.toLongOrNull() ?: return
                val pumpNumber = fpId + config.pumpNumberOffset

                eventListener?.onFuellingUpdate(
                    pumpNumber = pumpNumber,
                    volumeMicrolitres = DomsCanonicalMapper.centilitresToMicrolitres(volumeCl),
                    amountMinorUnits = DomsCanonicalMapper.domsAmountToMinorUnits(amountX10),
                )
            }
        }
    }
}
