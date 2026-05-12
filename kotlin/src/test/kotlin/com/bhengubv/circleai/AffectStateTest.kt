// AffectStateTest.kt
//
// Parametrized test suite for AffectState — verifies all 12 vectors from
// fixtures/affect_state.json with epsilon 1e-5f.

package com.bhengubv.circleai

import com.bhengubv.circleai.memory.AffectState
import com.fasterxml.jackson.databind.JsonNode
import com.fasterxml.jackson.databind.ObjectMapper
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.MethodSource
import java.io.File
import java.time.Duration
import java.util.stream.Stream
import kotlin.math.abs
import kotlin.test.assertTrue

// ---------------------------------------------------------------------------
// Data holder for one test vector
// ---------------------------------------------------------------------------

data class AffectVector(
    val id: String,
    val description: String,
    val inputCuriosity: Float,
    val inputEngagement: Float,
    val inputUncertainty: Float,
    val inputRapport: Float,
    val inputEnergy: Float,
    val operation: String,
    val operationParam: JsonNode,
    val expectedCuriosity: Float,
    val expectedEngagement: Float,
    val expectedUncertainty: Float,
    val expectedRapport: Float,
    val expectedEnergy: Float
)

class AffectStateTest {

    companion object {
        private const val EPSILON = 1e-5f

        private fun locateFixture(name: String): File {
            // Absolute path — works on this machine.
            val absolute = File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\$name")
            if (absolute.exists()) return absolute
            // Relative from Gradle working directory (kotlin/ project root).
            val relative = File("../../fixtures/$name")
            if (relative.exists()) return relative
            error("Cannot locate fixture $name — tried $absolute and $relative")
        }

        /**
         * Load all vectors from affect_state.json and return them as a JUnit 5 Stream.
         */
        @JvmStatic
        fun vectors(): Stream<AffectVector> {
            val mapper = ObjectMapper()
            val root = mapper.readTree(locateFixture("affect_state.json"))
            val list = mutableListOf<AffectVector>()
            for (v in root["vectors"]) {
                val inp = v["input"]
                val exp = v["expected"]
                list.add(
                    AffectVector(
                        id                  = v["id"].asText(),
                        description         = v["description"].asText(),
                        inputCuriosity      = inp["curiosity"].floatValue(),
                        inputEngagement     = inp["engagement"].floatValue(),
                        inputUncertainty    = inp["uncertainty"].floatValue(),
                        inputRapport        = inp["rapport"].floatValue(),
                        inputEnergy         = inp["energy"].floatValue(),
                        operation           = v["operation"].asText(),
                        operationParam      = v["operationParam"],
                        expectedCuriosity   = exp["curiosity"].floatValue(),
                        expectedEngagement  = exp["engagement"].floatValue(),
                        expectedUncertainty = exp["uncertainty"].floatValue(),
                        expectedRapport     = exp["rapport"].floatValue(),
                        expectedEnergy      = exp["energy"].floatValue()
                    )
                )
            }
            return list.stream()
        }
    }

    @ParameterizedTest(name = "[{index}] {0}")
    @MethodSource("vectors")
    fun `affect state vector passes`(vector: AffectVector) {
        val state = AffectState("test-user").apply {
            curiosity   = vector.inputCuriosity
            engagement  = vector.inputEngagement
            uncertainty = vector.inputUncertainty
            rapport     = vector.inputRapport
            energy      = vector.inputEnergy
        }

        when (vector.operation) {
            "positive_signal" -> {
                val count = if (vector.operationParam.has("count")) vector.operationParam["count"].intValue() else 1
                repeat(count) { state.applyPositiveSignal() }
            }
            "negative_signal" -> {
                val count = if (vector.operationParam.has("count")) vector.operationParam["count"].intValue() else 1
                repeat(count) { state.applyNegativeSignal() }
            }
            "positive_then_negative" -> {
                state.applyPositiveSignal()
                state.applyNegativeSignal()
            }
            "negative_then_positive" -> {
                state.applyNegativeSignal()
                state.applyPositiveSignal()
            }
            "idle_decay" -> {
                val hours = vector.operationParam["hours"].doubleValue()
                val seconds = (hours * 3600.0).toLong()
                state.applyIdleDecay(Duration.ofSeconds(seconds))
            }
            else -> error("Unknown operation: ${vector.operation}")
        }

        assertClose(vector.id, "curiosity",   state.curiosity,   vector.expectedCuriosity)
        assertClose(vector.id, "engagement",  state.engagement,  vector.expectedEngagement)
        assertClose(vector.id, "uncertainty", state.uncertainty, vector.expectedUncertainty)
        assertClose(vector.id, "rapport",     state.rapport,     vector.expectedRapport)
        assertClose(vector.id, "energy",      state.energy,      vector.expectedEnergy)
    }

    private fun assertClose(id: String, field: String, actual: Float, expected: Float) {
        assertTrue(
            abs(actual - expected) < EPSILON,
            "[$id] $field: expected $expected but was $actual (delta=${abs(actual - expected)}, epsilon=$EPSILON)"
        )
    }
}
