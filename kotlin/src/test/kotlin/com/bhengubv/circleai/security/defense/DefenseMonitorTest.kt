package com.bhengubv.circleai.security.defense

import kotlinx.coroutines.test.runTest
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The monitor, the patterns and the sinks. */
class DefenseMonitorTest {

    private fun source(list: String) = BlocklistIndicatorSource().apply { refresh(list, replace = true) }
    private fun ip(s: String) = IpAddressValue.parse(s)!!

    private fun signal(sev: ThreatSeverity) = ThreatSignal.create(
        ThreatCategory.PORT_SCAN, sev, 0.5, "x", "d", ThreatDirection.OUTBOUND)

    @Test fun `a known bad host is high severity`() {
        val s = BlocklistThreatMonitor(source("evil.example.com"))
            .evaluate(NetworkObservation.dns("evil.example.com"))
        assertEquals(ThreatSeverity.HIGH, s!!.severity)
        assertEquals(ThreatCategory.KNOWN_MALWARE_HOST, s.category)
        assertEquals(0.90, s.confidence)
    }

    @Test fun `a known bad address is a malicious endpoint not a malware host`() {
        val s = BlocklistThreatMonitor(source("203.0.113.5"))
            .evaluate(NetworkObservation.outbound(ip("203.0.113.5"), 443))
        assertEquals(ThreatCategory.MALICIOUS_ENDPOINT, s!!.category)
    }

    // One contact is a mistake; the same one over and over is a program
    // phoning home, and that is a different severity entirely.
    @Test fun `repeated contact escalates to beaconing`() {
        val m = BlocklistThreatMonitor(source("evil.example.com"))
        m.evaluate(NetworkObservation.dns("evil.example.com"))
        m.evaluate(NetworkObservation.dns("evil.example.com"))
        val third = m.evaluate(NetworkObservation.dns("evil.example.com"))
        assertEquals(ThreatCategory.COMMAND_AND_CONTROL, third!!.category)
        assertEquals(ThreatSeverity.CRITICAL, third.severity)
        assertTrue(third.tags.contains("beacon-x3"))
    }

    @Test fun `an allowed host is never flagged even when blocklisted`() {
        val o = DefenseOptions().apply { allowedHosts.add("evil.example.com") }
        assertNull(BlocklistThreatMonitor(source("evil.example.com"), o)
            .evaluate(NetworkObservation.dns("evil.example.com")))
    }

    @Test fun `clean traffic produces nothing`() {
        assertNull(BlocklistThreatMonitor(source("evil.example.com"))
            .evaluate(NetworkObservation.dns("www.example.org")))
    }

    @Test fun `a severity floor suppresses quieter findings`() {
        val o = DefenseOptions().apply { minReportSeverity = ThreatSeverity.CRITICAL }
        assertNull(BlocklistThreatMonitor(source("evil.example.com"), o)
            .evaluate(NetworkObservation.dns("evil.example.com")))
    }

    @Test fun `fan-out to many destinations reads as a scan`() {
        val o = DefenseOptions().apply { distinctDestinationScanThreshold = 5 }
        val m = BlocklistThreatMonitor(BlocklistIndicatorSource(), o)
        var last: ThreatSignal? = null
        for (i in 1..5) last = m.evaluate(NetworkObservation.outbound(ip("203.0.113." + i), 80))
        assertEquals(ThreatCategory.PORT_SCAN, last!!.category)
        assertTrue(last!!.tags.contains("distinct-5"))
    }

    // Many connections to ONE destination is a flood, not a scan - the two
    // thresholds count different things and must not be confused.
    @Test fun `many connections to one destination reads as a flood`() {
        val o = DefenseOptions().apply {
            distinctDestinationScanThreshold = 100
            connectionFloodThreshold = 6
        }
        val m = BlocklistThreatMonitor(BlocklistIndicatorSource(), o)
        var last: ThreatSignal? = null
        repeat(6) { last = m.evaluate(NetworkObservation.outbound(ip("203.0.113.9"), 80)) }
        assertEquals(ThreatCategory.CONNECTION_FLOOD, last!!.category)
        assertTrue(last!!.tags.contains("count-6"))
    }

    @Test fun `inbound traffic is not scored for fan-out`() {
        val o = DefenseOptions().apply { distinctDestinationScanThreshold = 2 }
        val m = BlocklistThreatMonitor(BlocklistIndicatorSource(), o)
        fun inbound(s: String) = NetworkObservation(
            null, ip(s), 80, ThreatDirection.INBOUND, "tcp", null, Instant.now())
        m.evaluate(inbound("203.0.113.1"))
        assertNull(m.evaluate(inbound("203.0.113.2")))
    }

    @Test fun `confidence is clamped so no caller can publish nonsense`() {
        val over = ThreatSignal.create(
            ThreatCategory.PORT_SCAN, ThreatSeverity.LOW, 1.4, "x", "d", ThreatDirection.OUTBOUND)
        val under = ThreatSignal.create(
            ThreatCategory.PORT_SCAN, ThreatSeverity.LOW, -0.2, "x", "d", ThreatDirection.OUTBOUND)
        assertEquals(1.0, over.confidence)
        assertEquals(0.0, under.confidence)
    }

    // A logging sink that throws must not be able to suppress an SOS.
    @Test fun `a failing sink does not stop the ones after it`() = runTest {
        var hits = 0
        val composite = CompositeThreatSink(
            DelegateThreatSink { throw IllegalStateException("boom") },
            DelegateThreatSink { hits++ },
        )
        composite.handle(signal(ThreatSeverity.HIGH))
        assertEquals(1, hits)
    }

    @Test fun `the sos sink ignores anything below its floor`() = runTest {
        var hits = 0
        val sink = SosThreatSink(DelegateSosEscalation { hits++ })
        sink.handle(signal(ThreatSeverity.MEDIUM))
        assertEquals(0, hits)
        sink.handle(signal(ThreatSeverity.CRITICAL))
        assertEquals(1, hits)
    }

    @Test fun `the sentinel evaluates the feed and forwards findings`() = runTest {
        var hits = 0
        val feed = object : NetworkObservationFeed {
            override val sourceId = "scripted"
            override fun observations() = sequenceOf(
                NetworkObservation.dns("www.example.org"),
                NetworkObservation.dns("evil.example.com"),
            )
        }
        AlwaysOnDefenseSentinel(
            BlocklistThreatMonitor(source("evil.example.com")), feed,
            DelegateThreatSink { hits++ },
        ).start()
        assertEquals(1, hits)
    }

    @Test fun `the module wires itself from a list of indicators`() {
        val feed = object : NetworkObservationFeed {
            override val sourceId = "empty"
            override fun observations() = emptySequence<NetworkObservation>()
        }
        val module = DefenseModule.create(feed, blocklist = "evil.example.com\n203.0.113.0/24")
        assertEquals(2, module.indicators.indicatorCount)
        assertNotNull(module.monitor.evaluate(NetworkObservation.dns("evil.example.com")))
    }
}
