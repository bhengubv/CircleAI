package com.bhengubv.circleai

import com.fasterxml.jackson.databind.ObjectMapper
import com.fasterxml.jackson.databind.JsonNode
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

class AffectStateTest {
    private val EPSILON = 1e-5f
    private val mapper = ObjectMapper()

    @Test
    fun testAllVectors() {
        val fixturesPath = "C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\affect_state.json"
        val root: JsonNode = mapper.readTree(File(fixturesPath))
        val vectors = root["vectors"]

        for (vector in vectors) {
            val id = vector["id"].asText()
            val input = vector["input"]
            val operation = vector["operation"].asText()
            val operationParam = vector["operationParam"]
            val expected = vector["expected"]

            val state = AffectState(
                curiosity   = input["curiosity"].floatValue(),
                engagement  = input["engagement"].floatValue(),
                uncertainty = input["uncertainty"].floatValue(),
                rapport     = input["rapport"].floatValue(),
                energy      = input["energy"].floatValue()
            )

            when (operation) {
                "positive_signal" -> {
                    val count = if (operationParam.has("count")) operationParam["count"].intValue() else 1
                    repeat(count) { state.applyPositiveSignal() }
                }
                "negative_signal" -> {
                    val count = if (operationParam.has("count")) operationParam["count"].intValue() else 1
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
                    state.applyIdleDecay(operationParam["hours"].floatValue())
                }
                // legacy names kept for backwards compatibility
                "apply_positive_signal" -> repeat(operationParam.intValue()) { state.applyPositiveSignal() }
                "apply_negative_signal" -> repeat(operationParam.intValue()) { state.applyNegativeSignal() }
                "apply_idle_decay"      -> state.applyIdleDecay(operationParam.floatValue())
            }

            assertEquals("$id curiosity",   expected["curiosity"].floatValue(),   state.curiosity,   EPSILON)
            assertEquals("$id engagement",  expected["engagement"].floatValue(),  state.engagement,  EPSILON)
            assertEquals("$id uncertainty", expected["uncertainty"].floatValue(), state.uncertainty, EPSILON)
            assertEquals("$id rapport",     expected["rapport"].floatValue(),     state.rapport,     EPSILON)
            assertEquals("$id energy",      expected["energy"].floatValue(),      state.energy,      EPSILON)
        }
    }
}
