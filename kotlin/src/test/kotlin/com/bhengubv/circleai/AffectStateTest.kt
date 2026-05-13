// AffectStateTest.kt
//
// Parametrized test suite for AffectState — verifies all 12 vectors from
// fixtures/affect_state.json with epsilon 1e-5f.

package com.bhengubv.circleai

import com.bhengubv.circleai.memory.AffectState
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
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
    val operationParam: JsonElement,
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
            val json = Json { ignoreUnknownKeys = true }
            val root = json.parseToJsonElement(locateFixture("affect_state.json").readText()).jsonObject
            val list = mutableListOf<AffectVector>()
            for (v in root["vectors"]!!.jsonArray) {
                val vObj = v.jsonObject
                val inp = vObj["input"]!!.jsonObject
                val exp = vObj["expected"]!!.jsonObject
                list.add(
                    AffectVector(
                        id                  = vObj["id"]!!.jsonPrimitive.content,
                        description         = vObj["description"]!!.jsonPrimitive.content,
                        inputCuriosity      = inp["curiosity"]!!.jsonPrimitive.double.toFloat(),
                        inputEngagement     = inp["engagement"]!!.jsonPrimitive.double.toFloat(),
                        inputUncertainty    = inp["uncertainty"]!!.jsonPrimitive.double.toFloat(),
                        inputRapport        = inp["rapport"]!!.jsonPrimitive.double.toFloat(),
                        inputEnergy         = inp["energy"]!!.jsonPrimitive.double.toFloat(),
                        operation           = vObj["operation"]!!.jsonPrimitive.content,
                        operationParam      = vObj["operationParam"]!!,
                        expectedCuriosity   = exp["curiosity"]!!.jsonPrimitive.double.toFloat(),
                        expectedEngagement  = exp["engagement"]!!.jsonPrimitive.double.toFloat(),
                        expectedUncertainty = exp["uncertainty"]!!.jsonPrimitive.double.toFloat(),
                        expectedRapport     = exp["rapport"]!!.jsonPrimitive.double.toFloat(),
                        expectedEnergy      = exp["energy"]!!.jsonPrimitive.double.toFloat()
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
                val paramObj = vector.operationParam.jsonObject
                val count = paramObj["count"]?.jsonPrimitive?.int ?: 1
                repeat(count) { state.applyPositiveSignal() }
            }
            "negative_signal" -> {
                val paramObj = vector.operationParam.jsonObject
                val count = paramObj["count"]?.jsonPrimitive?.int ?: 1
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
                val hours = vector.operationParam.jsonObject["hours"]!!.jsonPrimitive.double
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
