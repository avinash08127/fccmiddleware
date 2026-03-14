package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.security.EncryptedPrefsManager
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import io.mockk.verify
import java.time.Instant
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class CommandPollWorkerTest {

    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()
    private val commandExecutor: AgentCommandExecutor = mockk(relaxed = true)
    private val encryptedPrefs: EncryptedPrefsManager = mockk(relaxed = true)

    private lateinit var worker: CommandPollWorker

    companion object {
        private const val INITIAL_TOKEN = "initial-device-token"
        private const val REFRESHED_TOKEN = "refreshed-device-token"
    }

    @Before
    fun setUp() {
        worker = CommandPollWorker(
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            commandExecutor = commandExecutor,
            encryptedPrefs = encryptedPrefs,
        )

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.isReprovisioningRequired() } returns false
        every { tokenProvider.getAccessToken() } returns INITIAL_TOKEN
    }

    @Test
    fun `polls commands and posts ACKED result`() = runTest {
        val command = sampleCommand()
        val ackRequest = slot<CommandAckRequest>()

        coEvery { cloudApiClient.pollCommands(INITIAL_TOKEN) } returns CloudCommandPollResult.Success(
            EdgeCommandPollResponse(
                serverTimeUtc = Instant.now().toString(),
                commands = listOf(command),
            ),
        )
        coEvery { commandExecutor.execute(command, any()) } returns AgentCommandExecutionResult(
            outcome = LocalCommandAckOutcome.SUCCEEDED,
            result = buildJsonObject { put("outcome", "SUCCEEDED") },
        )
        coEvery {
            cloudApiClient.ackCommand(command.commandId, capture(ackRequest), INITIAL_TOKEN)
        } returns CloudCommandAckResult.Success(
            CommandAckResponse(
                commandId = command.commandId,
                status = AgentCommandStatus.ACKED,
                acknowledgedAt = Instant.now().toString(),
                duplicate = false,
            ),
        )

        val result = worker.pollCommands()

        assertEquals(CommandPollExecutionResult.Processed(1, 1), result)
        assertEquals(AgentCommandCompletionStatus.ACKED, ackRequest.captured.completionStatus)
        assertEquals("SUCCEEDED", ackRequest.captured.result?.get("outcome")?.toString()?.trim('"'))
    }

    @Test
    fun `reset command finalizes only after ack succeeds`() = runTest {
        val command = sampleCommand(type = AgentCommandType.RESET_LOCAL_STATE)

        coEvery { cloudApiClient.pollCommands(INITIAL_TOKEN) } returns CloudCommandPollResult.Success(
            EdgeCommandPollResponse(
                serverTimeUtc = Instant.now().toString(),
                commands = listOf(command),
            ),
        )
        coEvery { commandExecutor.execute(command, any()) } returns AgentCommandExecutionResult(
            outcome = LocalCommandAckOutcome.SUCCEEDED,
            result = buildJsonObject { put("outcome", "SUCCEEDED") },
            postAckAction = PostAckAction.FINALIZE_RESET_LOCAL_STATE,
        )
        coEvery {
            cloudApiClient.ackCommand(command.commandId, any(), INITIAL_TOKEN)
        } returns CloudCommandAckResult.Success(
            CommandAckResponse(
                commandId = command.commandId,
                status = AgentCommandStatus.ACKED,
                acknowledgedAt = Instant.now().toString(),
                duplicate = false,
            ),
        )

        val result = worker.pollCommands()

        assertEquals(CommandPollExecutionResult.Processed(1, 1), result)
        verify { encryptedPrefs.markResetAcked(command.commandId) }
        verify { commandExecutor.completePostAckAction(PostAckAction.FINALIZE_RESET_LOCAL_STATE, command.commandId) }
    }

    @Test
    fun `401 on command poll refreshes token and retries`() = runTest {
        coEvery { cloudApiClient.pollCommands(INITIAL_TOKEN) } returns CloudCommandPollResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf(INITIAL_TOKEN, REFRESHED_TOKEN)
        coEvery { cloudApiClient.pollCommands(REFRESHED_TOKEN) } returns CloudCommandPollResult.Success(
            EdgeCommandPollResponse(
                serverTimeUtc = Instant.now().toString(),
                commands = emptyList(),
            ),
        )

        val result = worker.pollCommands()

        assertEquals(CommandPollExecutionResult.Empty, result)
        coVerify { tokenProvider.refreshAccessToken() }
        coVerify { cloudApiClient.pollCommands(REFRESHED_TOKEN) }
    }

    @Test
    fun `reset ack conflict clears pending reset state`() = runTest {
        val command = sampleCommand(type = AgentCommandType.RESET_LOCAL_STATE)

        coEvery { cloudApiClient.pollCommands(INITIAL_TOKEN) } returns CloudCommandPollResult.Success(
            EdgeCommandPollResponse(
                serverTimeUtc = Instant.now().toString(),
                commands = listOf(command),
            ),
        )
        coEvery { commandExecutor.execute(command, any()) } returns AgentCommandExecutionResult(
            outcome = LocalCommandAckOutcome.SUCCEEDED,
            result = buildJsonObject { put("outcome", "SUCCEEDED") },
            postAckAction = PostAckAction.FINALIZE_RESET_LOCAL_STATE,
        )
        every { encryptedPrefs.pendingResetCommandId } returns command.commandId
        coEvery {
            cloudApiClient.ackCommand(command.commandId, any(), INITIAL_TOKEN)
        } returns CloudCommandAckResult.Conflict(
            errorCode = "COMMAND_NOT_ACTIONABLE",
            message = "Command is already expired",
        )

        val result = worker.pollCommands()

        assertEquals(CommandPollExecutionResult.Processed(1, 0), result)
        verify { encryptedPrefs.clearPendingReset() }
        verify(exactly = 0) {
            commandExecutor.completePostAckAction(PostAckAction.FINALIZE_RESET_LOCAL_STATE, command.commandId)
        }
    }

    private fun sampleCommand(type: AgentCommandType = AgentCommandType.FORCE_CONFIG_PULL): EdgeCommandDto =
        EdgeCommandDto(
            commandId = "cmd-123",
            commandType = type,
            status = AgentCommandStatus.PENDING,
            reason = "test",
            createdAt = Instant.now().minusSeconds(30).toString(),
            expiresAt = Instant.now().plusSeconds(300).toString(),
        )
}
