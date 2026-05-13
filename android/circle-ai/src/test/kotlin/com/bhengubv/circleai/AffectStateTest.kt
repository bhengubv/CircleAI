package com.bhengubv.circleai

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File
import kotlin.math.abs

class AffectStateTest {
    private val EPSILON = 1e-5f
    private val json = Json { ignoreUnknownKeys = true }

    private fun locateFixture(name: String): File {
        val absolute = File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\$name")
        if (absolute.exists()) return absolute
        val relative = File("../../fixtures/$name")
        if (relative.exists()) return relative
        error("Cannot locate fixture $name — tried $absolute and $relative")
    }

    @Test
    fun testAllVectors() {
        val root = json.parseToJsonElement(locateFixture("affect_state.json").readText()).jsonObject
        val vectors = root["vectors"]!!.jsonArray

        for (vector in vectors) {
            val vObj = vector.jsonObject
            val id = vObj["id"]!!.jsonPrimitive.content
            val inp = vObj["input"]!!.jsonObject
            val operation = vObj["operation"]!!.jsonPrimitive.content
            val operationParam = vObj["operationParam"]!!.jsonObject
            val exp = vObj["expected"]!!.jsonObject

            val state = AffectState(
                curiosity   = inp["curiosity"]!!.jsonPrimitive.double.toFloat(),
                engagement  = inp["engagement"]!!.jsonPrimitive.double.toFloat(),
                uncertainty = inp["uncertainty"]!!.jsonPrimitive.double.toFloat(),
                rapport     = inp["rapport"]!!.jsonPrimitive.double.toFloat(),
                energy      = inp["energy"]!!.jsonPrimitive.double.toFloat()
            )

            when (operation) {
                "positive_signal" -> {
                    val count = operationParam["count"]?.jsonPrimitive?.int ?: 1
                    repeat(count) { state.applyPositiveSignal() }
                }
                "negative_signal" -> {
                    val count = operationParam["count"]?.jsonPrimitive?.int ?: 1
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
                    val hours = operationParam["hours"]!!.jsonPrimitive.double.toFloat()
                    state.applyIdleDecay(hours)
                }
                else -> error("Unknown operation: $operation")
            }

            assertClose(id, "curiosity",   state.curiosity,   exp["curiosity"]!!.jsonPrimitive.double.toFloat())
            assertClose(id, "engagement",  state.engagement,  exp["engagement"]!!.jsonPrimitive.double.toFloat())
            assertClose(id, "uncertainty", state.uncertainty, exp["uncertainty"]!!.jsonPrimitive.double.toFloat())
            assertClose(id, "rapport",     state.rapport,     exp["rapport"]!!.jsonPrimitive.double.toFloat())
            assertClose(id, "energy",      state.energy,      exp["energy"]!!.jsonPrimitive.double.toFloat())
        }
    }

    private fun assertClose(id: String, field: String, actual: Float, expected: Float) {
        val delta = abs(actual - expected)
        assert(delta <= EPSILON) {
            "[$id] $field: expected $expected but was $actual (delta=$delta, epsilon=$EPSILON)"
        }
    }
}
