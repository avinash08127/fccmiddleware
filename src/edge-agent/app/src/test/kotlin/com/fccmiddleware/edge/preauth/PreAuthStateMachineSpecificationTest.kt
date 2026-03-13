package com.fccmiddleware.edge.preauth

import com.fccmiddleware.edge.adapter.common.PreAuthStatus
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File
import java.nio.file.Paths

class PreAuthStateMachineSpecificationTest {
    @Test
    fun `shared specification matches android state machine`() {
        val spec = loadSpecification()

        assertEquals(spec.activeStates.toSet(), PreAuthStateMachine.activeStatuses.map { it.name }.toSet())
        assertEquals(spec.terminalStates.toSet(), PreAuthStateMachine.terminalStatuses.map { it.name }.toSet())

        for (from in PreAuthStatus.entries) {
            val expectedTargets = spec.transitions.getValue(from.name).toSet()
            val actualTargets = PreAuthStateMachine.allowedTransitionsFrom(from).map { it.name }.toSet()
            assertEquals(expectedTargets, actualTargets)
        }
    }

    private fun loadSpecification(): PreAuthStateMachineSpec {
        var current: File? = Paths.get("").toAbsolutePath().toFile()
        while (current != null) {
            val candidate = current.resolve("schemas/state-machines/pre-auth-state-machine.json")
            if (candidate.exists()) {
                return Json.decodeFromString(candidate.readText())
            }
            current = current.parentFile
        }

        error("Unable to locate schemas/state-machines/pre-auth-state-machine.json")
    }

    @Serializable
    private data class PreAuthStateMachineSpec(
        val states: List<String>,
        @SerialName("activeStates") val activeStates: List<String>,
        @SerialName("terminalStates") val terminalStates: List<String>,
        val transitions: Map<String, List<String>>,
    )
}
