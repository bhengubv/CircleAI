// BiosignalsTest.kt — verifies the CircleAI.Wearable.Biosignals port against the C# reference.

package com.bhengubv.circleai.wearable.biosignals

import com.bhengubv.circleai.memory.AffectState
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class BiosignalsTest {

    private fun sample(kind: BiosignalKind, value: Float, conf: Float = 1.0f, at: Instant = Instant.now()) =
        BiosignalSample(UUID.randomUUID(), kind, value, "u", conf, false, at)

    @Test
    fun `kind ordinals are stable`() {
        assertEquals(0, BiosignalKind.HeartRate.ordinal)
        assertEquals(8, BiosignalKind.Unknown.ordinal)
        assertEquals(5, BiosignalKind.SleepStage.ordinal)
    }

    @Test
    fun `create clamps confidence`() {
        assertEquals(1.0f, BiosignalSample.create(BiosignalKind.HeartRate, 70f, "bpm", confidence = 5f).confidence)
        assertEquals(0.0f, BiosignalSample.create(BiosignalKind.HeartRate, 70f, "bpm", confidence = -1f).confidence)
    }

    @Test
    fun `null source supports and emits nothing`() = runTest {
        val s = NullBiosignalSource()
        assertTrue(s.supportedKinds.isEmpty())
        assertFalse(s.isSupportedAsync(BiosignalKind.HeartRate))
        assertTrue(s.streamAsync().toList().isEmpty())
    }

    @Test
    fun `recorded source replays and reports supported kinds`() = runTest {
        val samples = listOf(
            sample(BiosignalKind.HeartRate, 60f),
            sample(BiosignalKind.OxygenSaturation, 97f),
            sample(BiosignalKind.HeartRate, 62f),
        )
        val src = RecordedBiosignalSource(samples)
        assertTrue(src.isSupportedAsync(BiosignalKind.HeartRate))
        assertTrue(src.isSupportedAsync(BiosignalKind.OxygenSaturation))
        assertFalse(src.isSupportedAsync(BiosignalKind.Steps))
        assertEquals(setOf(BiosignalKind.HeartRate, BiosignalKind.OxygenSaturation), src.supportedKinds.toSet())
        assertEquals(3, src.streamAsync().toList().size)
    }

    @Test
    fun `aggregator computes windowed stats`() = runTest {
        val now = Instant.now()
        val src = RecordedBiosignalSource(
            listOf(
                sample(BiosignalKind.HeartRate, 60f, at = now),
                sample(BiosignalKind.HeartRate, 80f, at = now),
                sample(BiosignalKind.HeartRate, 100f, at = now.minus(Duration.ofDays(1))), // outside window
            ),
        )
        val snap = BiosignalAggregator(src).snapshotAsync(Duration.ofMinutes(5))
        val hr = snap.stats[BiosignalKind.HeartRate]!!
        assertEquals(2, hr.sampleCount) // old sample dropped
        assertEquals(60f, hr.min)
        assertEquals(80f, hr.max)
        assertEquals(70f, hr.mean)
        assertFailsWith<IllegalArgumentException> { BiosignalAggregator(src).snapshotAsync(Duration.ZERO) }
    }

    @Test
    fun `affect mapper applies heart-rate rules and confidence gate`() {
        val a = AffectState()
        a.energy = 0.5f
        a.uncertainty = 0.2f
        // Low confidence -> no mutation.
        BiosignalAffectMapper.apply(sample(BiosignalKind.HeartRate, 200f, conf = 0.4f), a)
        assertEquals(0.5f, a.energy)

        // High HR (>130) -> energy +0.10, uncertainty +0.05.
        BiosignalAffectMapper.apply(sample(BiosignalKind.HeartRate, 140f), a)
        approx(0.60f, a.energy)
        approx(0.25f, a.uncertainty)
    }

    @Test
    fun `affect mapper applies hrv and spo2 rules`() {
        val a = AffectState()
        a.uncertainty = 0.2f
        a.rapport = 0.5f
        a.engagement = 0.5f

        BiosignalAffectMapper.apply(sample(BiosignalKind.HeartRateVariability, 10f), a) // <20
        approx(0.25f, a.uncertainty)
        approx(0.48f, a.rapport)

        BiosignalAffectMapper.apply(sample(BiosignalKind.HeartRateVariability, 70f), a) // >60
        approx(0.52f, a.engagement)

        val before = a.uncertainty
        BiosignalAffectMapper.apply(sample(BiosignalKind.OxygenSaturation, 85f), a) // <90
        approx((before + 0.10f).coerceIn(0f, 1f), a.uncertainty)

        // Sleep stage / other kinds -> no affect change (but lastUpdated is touched).
        val u = a.uncertainty
        BiosignalAffectMapper.apply(sample(BiosignalKind.SleepStage, 2f), a)
        approx(u, a.uncertainty)
    }
}

private fun approx(expected: Float, actual: Float, tol: Float = 1e-6f) =
    kotlin.test.assertTrue(kotlin.math.abs(expected - actual) <= tol, "expected $expected got $actual")
