package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import java.time.Instant
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

sealed class CommandPollExecutionResult {
    data class Processed(val commandCount: Int, val ackedCount: Int) : CommandPollExecutionResult()
    data object Empty : CommandPollExecutionResult()
    data class RateLimited(val retryAfterSeconds: Long?) : CommandPollExecutionResult()
    data object Decommissioned : CommandPollExecutionResult()
    data class TransportFailure(val message: String) : CommandPollExecutionResult()
    data class Unavailable(val reason: String) : CommandPollExecutionResult()
}

private sealed class CommandPollAttemptResult {
    data class Success(val response: EdgeCommandPollResponse) : CommandPollAttemptResult()
    data class RateLimited(val retryAfterSeconds: Long?) : CommandPollAttemptResult()
    data object Decommissioned : CommandPollAttemptResult()
    data class TransportFailure(val message: String) : CommandPollAttemptResult()
}

class CommandPollWorker(
    private val cloudApiClient: CloudApiClient? = null,
    private val tokenProvider: DeviceTokenProvider? = null,
    private val commandExecutor: AgentCommandExecutor? = null,
    private val encryptedPrefs: EncryptedPrefsManager? = null,
) {

    companion object {
        private const val TAG = "CommandPollWorker"
        private const val DECOMMISSIONED_ERROR_CODE = "DEVICE_DECOMMISSIONED"
        private const val BASE_BACKOFF_MS = 1_000L
        private const val MAX_BACKOFF_MS = 60_000L
    }

    internal val circuitBreaker = CircuitBreaker(
        name = "CommandPoll",
        baseBackoffMs = BASE_BACKOFF_MS,
        maxBackoffMs = MAX_BACKOFF_MS,
    )

    suspend fun pollCommands(): CommandPollExecutionResult {
        val client = cloudApiClient ?: run {
            AppLogger.d(TAG, "pollCommands() skipped — cloudApiClient not wired")
            return CommandPollExecutionResult.Unavailable("cloudApiClient not wired")
        }
        val provider = tokenProvider ?: run {
            AppLogger.d(TAG, "pollCommands() skipped — tokenProvider not wired")
            return CommandPollExecutionResult.Unavailable("tokenProvider not wired")
        }
        val executor = commandExecutor ?: run {
            AppLogger.d(TAG, "pollCommands() skipped — commandExecutor not wired")
            return CommandPollExecutionResult.Unavailable("commandExecutor not wired")
        }

        if (provider.isDecommissioned()) {
            AppLogger.w(TAG, "pollCommands() skipped — device decommissioned")
            return CommandPollExecutionResult.Decommissioned
        }

        if (provider.isReprovisioningRequired()) {
            AppLogger.w(TAG, "pollCommands() skipped — re-provisioning required")
            return CommandPollExecutionResult.Unavailable("re-provisioning required")
        }

        if (!circuitBreaker.allowRequest()) {
            val waitMs = circuitBreaker.remainingBackoffMs()
            AppLogger.d(TAG, "pollCommands() skipped — circuit breaker (${waitMs}ms remaining)")
            return CommandPollExecutionResult.Unavailable("circuit breaker open for ${waitMs}ms")
        }

        val token = provider.getAccessToken() ?: run {
            AppLogger.w(TAG, "pollCommands() skipped — no access token available")
            return CommandPollExecutionResult.Unavailable("no access token available")
        }

        val result = doPoll(client, provider, token)
        return handlePollResult(result, token, provider, executor)
    }

    private suspend fun doPoll(
        client: CloudApiClient,
        provider: DeviceTokenProvider,
        bearerToken: String,
    ): CommandPollAttemptResult {
        return when (val result = client.pollCommands(bearerToken)) {
            is CloudCommandPollResult.Success -> CommandPollAttemptResult.Success(result.response)
            is CloudCommandPollResult.RateLimited -> CommandPollAttemptResult.RateLimited(result.retryAfterSeconds)
            is CloudCommandPollResult.Forbidden -> {
                if (result.errorCode == DECOMMISSIONED_ERROR_CODE) {
                    CommandPollAttemptResult.Decommissioned
                } else {
                    CommandPollAttemptResult.TransportFailure("403 Forbidden: ${result.errorCode}")
                }
            }
            is CloudCommandPollResult.TransportError ->
                CommandPollAttemptResult.TransportFailure(result.message)
            is CloudCommandPollResult.Unauthorized -> {
                AppLogger.i(TAG, "Command poll returned 401 — attempting token refresh")
                val refreshed = provider.refreshAccessToken()
                if (!refreshed) {
                    CommandPollAttemptResult.TransportFailure(
                        "401 Unauthorized — token refresh failed",
                    )
                } else {
                    val refreshedToken = provider.getAccessToken()
                    if (refreshedToken == null) {
                        CommandPollAttemptResult.TransportFailure(
                            "Refreshed token missing after successful refresh",
                        )
                    } else {
                        when (val retry = client.pollCommands(refreshedToken)) {
                            is CloudCommandPollResult.Success -> CommandPollAttemptResult.Success(retry.response)
                            is CloudCommandPollResult.RateLimited ->
                                CommandPollAttemptResult.RateLimited(retry.retryAfterSeconds)
                            is CloudCommandPollResult.Forbidden -> {
                                if (retry.errorCode == DECOMMISSIONED_ERROR_CODE) {
                                    CommandPollAttemptResult.Decommissioned
                                } else {
                                    CommandPollAttemptResult.TransportFailure(
                                        "403 Forbidden after refresh: ${retry.errorCode}",
                                    )
                                }
                            }
                            is CloudCommandPollResult.TransportError ->
                                CommandPollAttemptResult.TransportFailure(retry.message)
                            is CloudCommandPollResult.Unauthorized ->
                                CommandPollAttemptResult.TransportFailure(
                                    "401 Unauthorized after token refresh retry",
                                )
                        }
                    }
                }
            }
        }
    }

    private suspend fun handlePollResult(
        result: CommandPollAttemptResult,
        pollToken: String,
        provider: DeviceTokenProvider,
        executor: AgentCommandExecutor,
    ): CommandPollExecutionResult {
        return when (result) {
            is CommandPollAttemptResult.Success -> {
                circuitBreaker.recordSuccess()
                if (result.response.commands.isEmpty()) {
                    CommandPollExecutionResult.Empty
                } else {
                    handleCommands(result.response, pollToken, provider, executor)
                }
            }

            is CommandPollAttemptResult.RateLimited -> {
                val retryAfter = result.retryAfterSeconds
                if (retryAfter != null) {
                    circuitBreaker.setBackoffSeconds(retryAfter)
                    AppLogger.w(TAG, "Command poll rate limited (429); backing off for ${retryAfter}s")
                } else {
                    recordFailure("429 Too Many Requests (no Retry-After header)")
                }
                CommandPollExecutionResult.RateLimited(retryAfter)
            }

            is CommandPollAttemptResult.Decommissioned -> {
                provider.markDecommissioned()
                CommandPollExecutionResult.Decommissioned
            }

            is CommandPollAttemptResult.TransportFailure -> {
                recordFailure(result.message)
                CommandPollExecutionResult.TransportFailure(result.message)
            }
        }
    }

    private suspend fun handleCommands(
        response: EdgeCommandPollResponse,
        pollToken: String,
        provider: DeviceTokenProvider,
        executor: AgentCommandExecutor,
    ): CommandPollExecutionResult {
        val serverTime = runCatching { Instant.parse(response.serverTimeUtc) }.getOrNull()
        val commands = response.commands
            .sortedBy { it.createdAt }

        var ackedCount = 0
        for (command in commands) {
            val execution = executor.execute(command, serverTime)
            val ackApplied = acknowledgeCommand(
                command = command,
                execution = execution,
                initialToken = pollToken,
                provider = provider,
            )

            if (execution.postAckAction == PostAckAction.FINALIZE_RESET_LOCAL_STATE) {
                when (ackApplied) {
                    true -> {
                        encryptedPrefs?.markResetAcked(command.commandId)
                        executor.completePostAckAction(execution.postAckAction, command.commandId)
                        ackedCount++
                        return CommandPollExecutionResult.Processed(commands.size, ackedCount)
                    }
                    false -> Unit
                }
            } else {
                if (ackApplied) {
                    ackedCount++
                }
                if (execution.postAckAction == PostAckAction.SHOW_DECOMMISSIONED_UI) {
                    executor.completePostAckAction(execution.postAckAction, command.commandId)
                    return CommandPollExecutionResult.Processed(commands.size, ackedCount)
                }
            }
        }

        return CommandPollExecutionResult.Processed(commands.size, ackedCount)
    }

    private suspend fun acknowledgeCommand(
        command: EdgeCommandDto,
        execution: AgentCommandExecutionResult,
        initialToken: String,
        provider: DeviceTokenProvider,
    ): Boolean {
        val request = buildAckRequest(execution)
        val initialResult = cloudApiClient?.ackCommand(command.commandId, request, initialToken)
            ?: return false

        return when (initialResult) {
            is CloudCommandAckResult.Success -> {
                AppLogger.i(
                    TAG,
                    "Command ${command.commandId} acked with ${execution.outcome} duplicate=${initialResult.response.duplicate}",
                )
                true
            }

            is CloudCommandAckResult.Conflict -> {
                AppLogger.w(
                    TAG,
                    "Command ack conflict for ${command.commandId}: ${initialResult.errorCode ?: initialResult.message}",
                )
                if (
                    encryptedPrefs?.pendingResetCommandId == command.commandId &&
                    execution.postAckAction == PostAckAction.FINALIZE_RESET_LOCAL_STATE
                ) {
                    encryptedPrefs.clearPendingReset()
                }
                false
            }

            is CloudCommandAckResult.Forbidden -> {
                if (initialResult.errorCode == DECOMMISSIONED_ERROR_CODE) {
                    provider.markDecommissioned()
                }
                AppLogger.w(
                    TAG,
                    "Command ack forbidden for ${command.commandId}: ${initialResult.errorCode}",
                )
                if (
                    encryptedPrefs?.pendingResetCommandId == command.commandId &&
                    execution.postAckAction == PostAckAction.FINALIZE_RESET_LOCAL_STATE
                ) {
                    encryptedPrefs.clearPendingReset()
                }
                false
            }

            is CloudCommandAckResult.Unauthorized -> {
                AppLogger.i(TAG, "Command ack returned 401 for ${command.commandId} — attempting refresh")
                if (!provider.refreshAccessToken()) {
                    AppLogger.w(TAG, "Command ack refresh failed for ${command.commandId}")
                    return false
                }

                val refreshedToken = provider.getAccessToken()
                if (refreshedToken == null) {
                    AppLogger.w(TAG, "Refreshed token missing during command ack for ${command.commandId}")
                    return false
                }

                when (val retry = cloudApiClient?.ackCommand(command.commandId, request, refreshedToken)) {
                    is CloudCommandAckResult.Success -> true
                    is CloudCommandAckResult.Forbidden -> {
                        if (retry.errorCode == DECOMMISSIONED_ERROR_CODE) {
                            provider.markDecommissioned()
                        }
                        false
                    }
                    is CloudCommandAckResult.Conflict -> {
                        if (
                            encryptedPrefs?.pendingResetCommandId == command.commandId &&
                            execution.postAckAction == PostAckAction.FINALIZE_RESET_LOCAL_STATE
                        ) {
                            encryptedPrefs.clearPendingReset()
                        }
                        false
                    }
                    else -> false
                }
            }

            is CloudCommandAckResult.TransportError -> {
                AppLogger.w(TAG, "Command ack transport failure for ${command.commandId}: ${initialResult.message}")
                false
            }
        }
    }

    private fun buildAckRequest(execution: AgentCommandExecutionResult): CommandAckRequest {
        val resultJson = buildJsonObject {
            execution.result.forEach { (key, value) -> put(key, value) }
        }

        return if (execution.outcome == LocalCommandAckOutcome.FAILED) {
            CommandAckRequest(
                completionStatus = AgentCommandCompletionStatus.FAILED,
                handledAtUtc = Instant.now().toString(),
                failureCode = execution.failureCode,
                failureMessage = execution.failureMessage,
                result = resultJson,
            )
        } else {
            CommandAckRequest(
                completionStatus = AgentCommandCompletionStatus.ACKED,
                handledAtUtc = Instant.now().toString(),
                result = resultJson,
            )
        }
    }

    private suspend fun recordFailure(message: String) {
        val backoffMs = circuitBreaker.recordFailure()
        AppLogger.w(
            TAG,
            "Command poll failed (failure #${circuitBreaker.consecutiveFailureCount}, " +
                "state=${circuitBreaker.state}); next retry after ${backoffMs}ms. Error: $message",
        )
    }
}
