package com.fccmiddleware.edge.sync

import android.content.Context
import android.content.Intent
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.ui.MainActivity
import java.time.Instant
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

enum class LocalCommandAckOutcome {
    SUCCEEDED,
    FAILED,
    IGNORED_ALREADY_APPLIED,
    IGNORED_EXPIRED,
}

enum class PostAckAction {
    NONE,
    FINALIZE_RESET_LOCAL_STATE,
    SHOW_DECOMMISSIONED_UI,
}

data class AgentCommandExecutionResult(
    val outcome: LocalCommandAckOutcome,
    val failureCode: String? = null,
    val failureMessage: String? = null,
    val result: JsonObject,
    val postAckAction: PostAckAction = PostAckAction.NONE,
)

interface AgentCommandExecutor {
    suspend fun execute(command: EdgeCommandDto, serverTimeUtc: Instant?): AgentCommandExecutionResult
    fun finalizeAckedResetIfNeeded(origin: String): Boolean
    fun completePostAckAction(action: PostAckAction, commandId: String)
}

class AndroidAgentCommandExecutor(
    private val context: Context,
    private val configManager: ConfigManager,
    private val configPollWorker: ConfigPollWorker,
    private val encryptedPrefs: EncryptedPrefsManager,
    private val tokenProvider: DeviceTokenProvider,
    private val bufferDatabase: BufferDatabase,
    private val localOverrideManager: LocalOverrideManager,
    private val keystoreManager: KeystoreManager,
) : AgentCommandExecutor {

    companion object {
        private const val TAG = "AgentCommandExecutor"
        private const val DEVICE_DECOMMISSIONED = "DEVICE_DECOMMISSIONED"
    }

    override suspend fun execute(
        command: EdgeCommandDto,
        serverTimeUtc: Instant?,
    ): AgentCommandExecutionResult {
        if (isExpired(command, serverTimeUtc)) {
            return ignored(
                LocalCommandAckOutcome.IGNORED_EXPIRED,
                command,
            )
        }

        return when (command.commandType) {
            AgentCommandType.FORCE_CONFIG_PULL -> executeForceConfigPull(command)
            AgentCommandType.RESET_LOCAL_STATE -> executeResetLocalState(command)
            AgentCommandType.DECOMMISSION -> executeDecommission(command)
        }
    }

    override fun finalizeAckedResetIfNeeded(origin: String): Boolean {
        if (!encryptedPrefs.pendingResetAcked) {
            return false
        }

        AppLogger.w(TAG, "Finalizing previously acked reset command (origin=$origin)")
        finalizeResetLocalState()
        return true
    }

    override fun completePostAckAction(action: PostAckAction, commandId: String) {
        when (action) {
            PostAckAction.NONE -> Unit
            PostAckAction.FINALIZE_RESET_LOCAL_STATE -> {
                AppLogger.w(TAG, "Completing reset-local-state command after ack: $commandId")
                finalizeResetLocalState()
            }
            PostAckAction.SHOW_DECOMMISSIONED_UI -> {
                AppLogger.w(TAG, "Surfacing decommissioned UI after command: $commandId")
                showDecommissionedUi()
            }
        }
    }

    private suspend fun executeForceConfigPull(command: EdgeCommandDto): AgentCommandExecutionResult {
        val requestedVersion = command.payload
            ?.get("configVersion")
            ?.jsonPrimitive
            ?.contentOrNull
            ?.toIntOrNull()

        val currentVersion = configManager.currentConfigVersion
        if (requestedVersion != null && currentVersion != null && currentVersion >= requestedVersion) {
            return ignored(
                LocalCommandAckOutcome.IGNORED_ALREADY_APPLIED,
                command,
                extra = mapOf(
                    "currentConfigVersion" to currentVersion.toString(),
                    "requestedConfigVersion" to requestedVersion.toString(),
                ),
            )
        }

        return when (val result = configPollWorker.pollConfig()) {
            is ConfigPollExecutionResult.Applied ->
                success(
                    command,
                    extra = mapOf("appliedConfigVersion" to result.configVersion.toString()),
                )

            is ConfigPollExecutionResult.Unchanged ->
                ignored(
                    LocalCommandAckOutcome.IGNORED_ALREADY_APPLIED,
                    command,
                    extra = mapOf(
                        "currentConfigVersion" to (result.currentConfigVersion?.toString() ?: "unknown"),
                    ),
                )

            is ConfigPollExecutionResult.Skipped ->
                ignored(
                    LocalCommandAckOutcome.IGNORED_ALREADY_APPLIED,
                    command,
                    extra = mapOf("currentConfigVersion" to result.configVersion.toString()),
                )

            is ConfigPollExecutionResult.Rejected ->
                failure(
                    command = command,
                    failureCode = result.reason,
                    failureMessage = "Config command rejected by local validation",
                    extra = mapOf("configVersion" to result.configVersion.toString()),
                )

            is ConfigPollExecutionResult.RateLimited ->
                failure(
                    command = command,
                    failureCode = "CONFIG_RATE_LIMITED",
                    failureMessage = "Config pull rate limited by cloud",
                    extra = mapOf(
                        "retryAfterSeconds" to (result.retryAfterSeconds?.toString() ?: "unknown"),
                    ),
                )

            is ConfigPollExecutionResult.Decommissioned ->
                failure(
                    command = command,
                    failureCode = DEVICE_DECOMMISSIONED,
                    failureMessage = "Device was decommissioned during config pull",
                )

            is ConfigPollExecutionResult.TransportFailure ->
                failure(
                    command = command,
                    failureCode = "CONFIG_PULL_FAILED",
                    failureMessage = result.message,
                )

            is ConfigPollExecutionResult.Unavailable ->
                failure(
                    command = command,
                    failureCode = "CONFIG_PULL_UNAVAILABLE",
                    failureMessage = result.reason,
                )
        }
    }

    private fun executeResetLocalState(command: EdgeCommandDto): AgentCommandExecutionResult {
        val pendingCommandId = encryptedPrefs.pendingResetCommandId
        if (encryptedPrefs.pendingResetAcked) {
            return ignored(
                LocalCommandAckOutcome.IGNORED_ALREADY_APPLIED,
                command,
                extra = mapOf("pendingResetState" to "acked"),
            )
        }

        if (pendingCommandId != null && pendingCommandId != command.commandId) {
            return ignored(
                LocalCommandAckOutcome.IGNORED_ALREADY_APPLIED,
                command,
                extra = mapOf("pendingResetCommandId" to pendingCommandId),
            )
        }

        if (pendingCommandId == null) {
            encryptedPrefs.markResetPending(command.commandId)
        }

        return success(
            command = command,
            extra = mapOf("pendingResetCommandId" to command.commandId),
            postAckAction = PostAckAction.FINALIZE_RESET_LOCAL_STATE,
        )
    }

    private fun executeDecommission(command: EdgeCommandDto): AgentCommandExecutionResult {
        if (tokenProvider.isDecommissioned() || encryptedPrefs.isDecommissioned) {
            return ignored(
                LocalCommandAckOutcome.IGNORED_ALREADY_APPLIED,
                command,
            )
        }

        tokenProvider.markDecommissioned()
        return success(
            command = command,
            postAckAction = PostAckAction.SHOW_DECOMMISSIONED_UI,
        )
    }

    private fun finalizeResetLocalState() {
        context.stopService(Intent(context, EdgeAgentForegroundService::class.java))
        bufferDatabase.clearAllData()
        localOverrideManager.clearAllOverrides()
        keystoreManager.clearAll()
        encryptedPrefs.clearAll()

        val intent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        context.startActivity(intent)
    }

    private fun showDecommissionedUi() {
        context.stopService(Intent(context, EdgeAgentForegroundService::class.java))

        val intent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        context.startActivity(intent)
    }

    private fun isExpired(command: EdgeCommandDto, serverTimeUtc: Instant?): Boolean {
        val now = serverTimeUtc ?: Instant.now()
        val expiresAt = runCatching { Instant.parse(command.expiresAt) }.getOrNull() ?: return false
        return !expiresAt.isAfter(now)
    }

    private fun success(
        command: EdgeCommandDto,
        extra: Map<String, String> = emptyMap(),
        postAckAction: PostAckAction = PostAckAction.NONE,
    ): AgentCommandExecutionResult = AgentCommandExecutionResult(
        outcome = LocalCommandAckOutcome.SUCCEEDED,
        result = buildOutcomeJson(command, LocalCommandAckOutcome.SUCCEEDED, extra),
        postAckAction = postAckAction,
    )

    private fun ignored(
        outcome: LocalCommandAckOutcome,
        command: EdgeCommandDto,
        extra: Map<String, String> = emptyMap(),
    ): AgentCommandExecutionResult = AgentCommandExecutionResult(
        outcome = outcome,
        result = buildOutcomeJson(command, outcome, extra),
    )

    private fun failure(
        command: EdgeCommandDto,
        failureCode: String,
        failureMessage: String,
        extra: Map<String, String> = emptyMap(),
    ): AgentCommandExecutionResult = AgentCommandExecutionResult(
        outcome = LocalCommandAckOutcome.FAILED,
        failureCode = failureCode,
        failureMessage = failureMessage,
        result = buildOutcomeJson(command, LocalCommandAckOutcome.FAILED, extra),
    )

    private fun buildOutcomeJson(
        command: EdgeCommandDto,
        outcome: LocalCommandAckOutcome,
        extra: Map<String, String>,
    ): JsonObject = buildJsonObject {
        put("commandType", command.commandType.name)
        put("outcome", outcome.name)
        extra.forEach { (key, value) -> put(key, value) }
    }
}
