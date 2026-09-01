// Defense.kt
//
// Kotlin port of CircleAI.Security.Defense — the C# reference is the EXACT spec.
//
// Always-on network defence: watch what the device talks to, match it against
// known-bad indicators, notice scan and flood and beacon patterns, and hand
// what matters to a watchdog or an SOS escalation.
//
// Fidelity notes:
//   * C# `record` -> `data class`; `IPAddress` -> `IpAddressValue`, since the
//     module only ever needs exact IPv4 equality, CIDR containment and exact
//     IPv6 text from one.
//   * `Channel<ThreatSignal>` -> a list of listeners; the signal stream is
//     in-process in the C# too.
//   * Every threshold and every message string is reproduced.

package com.bhengubv.circleai.security.defense

import java.time.Instant
import java.util.UUID
import kotlin.math.max
import kotlin.math.min

// ── Addresses ───────────────────────────────────────────────────────────────

/**
 * An IPv4 or IPv6 literal, parsed once. Not [java.net.InetAddress]: that one
 * resolves names, and a hostname must never silently become an address here.
 */
data class IpAddressValue private constructor(
    val isIpv4: Boolean,
    /** Host-order 32-bit value. Meaningful for IPv4 only. */
    val v4: Long,
    /** Canonical lowercase text. This is what IPv6 is matched on. */
    val text: String,
) {
    override fun toString() = text

    companion object {
        fun parse(s: String): IpAddressValue? {
            val t = s.trim()
            if (t.isEmpty()) return null

            if (isDottedQuad(t)) {
                val o = t.split(Char(46)).map { it.toLong() }
                return IpAddressValue(true, (o[0] shl 24) or (o[1] shl 16) or (o[2] shl 8) or o[3], t)
            }
            if (looksLikeIpv6(t)) return IpAddressValue(false, 0, t.lowercase())
            return null
        }

        fun ofIpv4(value: Long): IpAddressValue {
            val v = value and 0xFFFFFFFFL
            val text = "${(v shr 24) and 0xFF}.${(v shr 16) and 0xFF}.${(v shr 8) and 0xFF}.${v and 0xFF}"
            return IpAddressValue(true, v, text)
        }

        private fun isDottedQuad(s: String): Boolean {
            val parts = s.split(Char(46))
            if (parts.size != 4) return false
            return parts.all { p ->
                p.isNotEmpty() && p.length <= 3 && p.all { it.isDigit() } &&
                    (p.toIntOrNull() ?: 999) in 0..255
            }
        }

        private fun looksLikeIpv6(s: String): Boolean {
            if (!s.contains(Char(58))) return false
            if (Regex("::").findAll(s).count() > 1) return false
            val groups = s.split(Char(58))
            if (groups.size > 8) return false
            return groups.all { g -> g.isEmpty() || (g.length <= 4 && g.all { isHex(it) }) }
        }

        private fun isHex(c: Char): Boolean {
            val v = c.lowercaseChar().code
            return (v >= 48 && v <= 57) || (v >= 97 && v <= 102)
        }
    }
}

// ── What was observed ───────────────────────────────────────────────────────

enum class ThreatDirection { UNKNOWN, OUTBOUND, INBOUND, LOOKUP }

/** One connection or lookup, as the host reported it. */
data class NetworkObservation(
    val host: String? = null,
    val remoteAddress: IpAddressValue? = null,
    val remotePort: Int = 0,
    val direction: ThreatDirection = ThreatDirection.UNKNOWN,
    val proto: String = "tcp",
    val appHint: String? = null,
    val observedAt: Instant = Instant.now(),
) {
    companion object {
        fun outbound(
            address: IpAddressValue, port: Int, proto: String = "tcp",
            host: String? = null, appHint: String? = null, at: Instant = Instant.now(),
        ) = NetworkObservation(host, address, port, ThreatDirection.OUTBOUND, proto, appHint, at)

        fun dns(host: String, appHint: String? = null, at: Instant = Instant.now()) =
            NetworkObservation(host, null, 0, ThreatDirection.LOOKUP, "dns", appHint, at)
    }
}

/** Where observations come from - a VpnService, mesh events, or a test double. */
interface NetworkObservationFeed {
    val sourceId: String
    fun observations(): Sequence<NetworkObservation>
}

// ── What it means ───────────────────────────────────────────────────────────

/** How bad. Ordered, because every floor in this module is a comparison. */
enum class ThreatSeverity { INFO, LOW, MEDIUM, HIGH, CRITICAL }

/** What kind of bad. */
enum class ThreatCategory {
    UNCLASSIFIED, MALICIOUS_ENDPOINT, KNOWN_MALWARE_HOST, COMMAND_AND_CONTROL,
    PHISHING, DATA_EXFILTRATION, PORT_SCAN, CONNECTION_FLOOD, DNS_ANOMALY,
}

/** One finding, with the observation that produced it kept alongside. */
data class ThreatSignal(
    val id: UUID,
    val category: ThreatCategory,
    val severity: ThreatSeverity,
    val confidence: Double,
    val indicator: String,
    val description: String,
    val direction: ThreatDirection,
    val tags: List<String>,
    val observation: NetworkObservation?,
    val detectedAt: Instant,
) {
    companion object {
        /** Confidence is clamped here so no caller can publish a 1.4 or a -0.2. */
        fun create(
            category: ThreatCategory,
            severity: ThreatSeverity,
            confidence: Double,
            indicator: String,
            description: String,
            direction: ThreatDirection,
            tags: List<String> = emptyList(),
            observation: NetworkObservation? = null,
            at: Instant = Instant.now(),
        ) = ThreatSignal(
            UUID.randomUUID(), category, severity,
            max(0.0, min(1.0, confidence)),
            indicator, description, direction, tags, observation, at,
        )
    }
}

// ── Indicators ──────────────────────────────────────────────────────────────

enum class IndicatorKind {
    IPV4, IPV4_CIDR, IPV6, DOMAIN;

    /** Lowercase name, used as a signal tag. */
    val tagName: String get() = when (this) {
        IPV4 -> "ipv4"; IPV4_CIDR -> "ipv4cidr"; IPV6 -> "ipv6"; DOMAIN -> "domain"
    }
}

data class ParsedIndicator(val kind: IndicatorKind, val value: String)

/** Why an observation was flagged, and by which indicator. */
data class IndicatorMatch(val indicator: String, val kind: IndicatorKind, val reason: String)

/** An IPv4 network, stored masked so containment is two operations. */
data class Ipv4Cidr private constructor(
    val network: Long,
    val mask: Long,
    val prefixLength: Int,
) {
    fun contains(ip: IpAddressValue): Boolean = ip.isIpv4 && (ip.v4 and mask) == network

    override fun toString() =
        "${(network shr 24) and 0xFF}.${(network shr 16) and 0xFF}." +
            "${(network shr 8) and 0xFF}.${network and 0xFF}/$prefixLength"

    companion object {
        /** A bare address parses as a /32. A prefix outside 0..32 is rejected. */
        fun parse(text: String): Ipv4Cidr? {
            val t = text.trim()
            if (t.isEmpty()) return null

            var prefix = 32
            var ipPart = t
            val slash = t.indexOf(Char(47))
            if (slash >= 0) {
                ipPart = t.substring(0, slash)
                prefix = t.substring(slash + 1).trim().toIntOrNull() ?: return null
                if (prefix !in 0..32) return null
            }

            val ip = IpAddressValue.parse(ipPart.trim()) ?: return null
            if (!ip.isIpv4) return null

            // A /0 shifted by 32 is undefined in C too; special-cased.
            val mask = if (prefix == 0) 0L else (0xFFFFFFFFL shl (32 - prefix)) and 0xFFFFFFFFL
            return Ipv4Cidr(ip.v4 and mask, mask, prefix)
        }
    }
}

/**
 * Reads hosts-file and plain-list blocklists. Both formats are in the wild and
 * both are handled: a sinkhole prefix is dropped, comments are stripped.
 */
object BlocklistParser {
    private val sinkTokens = setOf("0.0.0.0", "127.0.0.1", "::", "::1")

    fun parse(text: String): List<ParsedIndicator> =
        text.split(Char(10)).mapNotNull { parseLine(it) }

    fun parseLine(rawLine: String): ParsedIndicator? {
        var line = rawLine.trim()
        if (line.isEmpty()) return null

        val hash = line.indexOf(Char(35))
        if (hash == 0) return null
        if (hash > 0) line = line.substring(0, hash).trim()
        if (line.isEmpty()) return null

        val parts = line.split(Char(32), Char(9)).map { it.trim() }.filter { it.isNotEmpty() }
        // was: Regex("\s+")).filter { it.isNotEmpty() }
        if (parts.isEmpty()) return null

        // "0.0.0.0 ads.example.com" - the sinkhole is not the indicator.
        val token = if (parts.size >= 2 && parts[0] in sinkTokens) parts[1] else parts[0]
        return classify(token)
    }

    fun classify(raw: String): ParsedIndicator? {
        var token = raw.trim().trimEnd(Char(46)).lowercase()
        if (token.isEmpty()) return null

        if (token.contains(Char(47))) {
            return if (Ipv4Cidr.parse(token) != null) ParsedIndicator(IndicatorKind.IPV4_CIDR, token) else null
        }
        val ip = IpAddressValue.parse(token)
        if (ip != null) {
            return if (!ip.isIpv4) ParsedIndicator(IndicatorKind.IPV6, ip.text)
            else ParsedIndicator(IndicatorKind.IPV4, token)
        }
        return if (isPlausibleDomain(token)) ParsedIndicator(IndicatorKind.DOMAIN, token) else null
    }

    /**
     * At least one dot, so a bare word in a malformed list never becomes a
     * domain that matches half the internet.
     */
    internal fun isPlausibleDomain(s: String): Boolean {
        if (s.isEmpty() || s.length > 253) return false
        var hasDot = false
        for (c in s) {
            if (c == Char(46)) { hasDot = true; continue }
            val code = c.code
            val ok = (code >= 97 && code <= 122) || (code >= 65 && code <= 90) || c.isDigit() || c == Char(45) || c == Char(95)
            // was: (c in a..z) || (c in A..Z) || c.isDigit() || c == Char(45) || c == Char(95)
            if (!ok) return false
        }
        return hasDot
    }
}

// ── The indicator index ─────────────────────────────────────────────────────

interface IndicatorSource {
    val indicatorCount: Int
    val lastUpdated: Instant
    fun match(address: IpAddressValue?, host: String?): IndicatorMatch?
    fun refresh(text: String, replace: Boolean = true): Int
}

/**
 * An in-memory IOC index. `match` reads ONE immutable snapshot, so it takes no
 * lock and a refresh swaps atomically instead of mutating under a reader.
 */
class BlocklistIndicatorSource : IndicatorSource {

    private data class Snapshot(
        val ipv4: Set<Long> = emptySet(),
        val cidrs: List<Ipv4Cidr> = emptyList(),
        val ipv6: Set<String> = emptySet(),
        val domains: Set<String> = emptySet(),
        val updatedAt: Instant = Instant.EPOCH,
    )

    @Volatile private var index = Snapshot()

    override val indicatorCount: Int
        get() = index.let { it.ipv4.size + it.cidrs.size + it.ipv6.size + it.domains.size }

    override val lastUpdated: Instant get() = index.updatedAt

    override fun refresh(text: String, replace: Boolean): Int {
        val current = index
        val ipv4 = if (replace) mutableSetOf<Long>() else current.ipv4.toMutableSet()
        val cidrs = if (replace) mutableListOf<Ipv4Cidr>() else current.cidrs.toMutableList()
        val ipv6 = if (replace) mutableSetOf<String>() else current.ipv6.toMutableSet()
        val domains = if (replace) mutableSetOf<String>() else current.domains.toMutableSet()

        var added = 0
        for (ind in BlocklistParser.parse(text)) {
            when (ind.kind) {
                IndicatorKind.IPV4 -> IpAddressValue.parse(ind.value)?.let { if (ipv4.add(it.v4)) added++ }
                IndicatorKind.IPV4_CIDR -> Ipv4Cidr.parse(ind.value)?.let { cidrs.add(it); added++ }
                IndicatorKind.IPV6 -> if (ipv6.add(ind.value)) added++
                IndicatorKind.DOMAIN -> if (domains.add(ind.value)) added++
            }
        }

        index = Snapshot(ipv4, cidrs, ipv6, domains, Instant.now())
        return added
    }

    override fun match(address: IpAddressValue?, host: String?): IndicatorMatch? {
        val snap = index          // single volatile read: a stable view for this call

        if (address != null) {
            if (address.isIpv4) {
                if (snap.ipv4.contains(address.v4)) {
                    return IndicatorMatch(address.text, IndicatorKind.IPV4, "known-bad-ip")
                }
                snap.cidrs.firstOrNull { it.contains(address) }?.let {
                    return IndicatorMatch(it.toString(), IndicatorKind.IPV4_CIDR, "known-bad-range")
                }
            } else if (snap.ipv6.contains(address.text)) {
                return IndicatorMatch(address.text, IndicatorKind.IPV6, "known-bad-ip")
            }
        }

        val h = host?.trim()?.trimEnd(Char(46))?.lowercase()
        if (h.isNullOrEmpty()) return null

        if (snap.domains.contains(h)) {
            return IndicatorMatch(h, IndicatorKind.DOMAIN, "known-bad-domain")
        }
        // Blocking evil.com has to block cdn.evil.com too, so every parent
        // suffix is checked - that is how blocklists are meant to be read.
        var rest: String = h
        while (true) {
            val dot = rest.indexOf(Char(46))
            if (dot < 0) break
            val parent = rest.substring(dot + 1)
            if (parent.isEmpty()) break
            if (snap.domains.contains(parent)) {
                return IndicatorMatch(parent, IndicatorKind.DOMAIN, "known-bad-parent-domain")
            }
            rest = parent
        }
        return null
    }
}

// ── Options ─────────────────────────────────────────────────────────────────

/**
 * Every threshold in one place. All of them are bounded on purpose: this runs
 * on a phone, so nothing here may grow without a ceiling.
 */
class DefenseOptions {
    var minReportSeverity: ThreatSeverity = ThreatSeverity.LOW
    var watchdogSeverityFloor: ThreatSeverity = ThreatSeverity.HIGH
    var sosSeverityFloor: ThreatSeverity = ThreatSeverity.CRITICAL
    var enableAnomalyDetection: Boolean = true
    var anomalyWindowSeconds: Double = 10.0
    var distinctDestinationScanThreshold: Int = 20
    var connectionFloodThreshold: Int = 100
    var maxTrackedConnections: Int = 512
    var beaconRepeatThreshold: Int = 3
    var beaconWindowSeconds: Double = 300.0
    val allowedHosts: MutableSet<String> = mutableSetOf()
    val allowedAddresses: MutableSet<String> = mutableSetOf()
    var refreshHintSeconds: Double = 12 * 3600.0
}

// ── Patterns nobody declared ────────────────────────────────────────────────

/**
 * A bounded sliding window over recent destinations: enough to see a scan or a
 * flood, small enough that a busy phone does not pay for it.
 */
internal class ConnectionRateAnomalyDetector(private val options: DefenseOptions) {

    private data class Entry(val at: Double, val destination: String)

    private val lock = Any()
    private val events = ArrayDeque<Entry>()
    private val distinctCounts = mutableMapOf<String, Int>()

    fun observe(observation: NetworkObservation, now: Instant = Instant.now()): ThreatSignal? {
        val destination = observation.remoteAddress?.text ?: observation.host ?: "unknown"
        val nowT = now.toEpochMilli() / 1000.0
        val window = options.anomalyWindowSeconds

        var total: Int
        var distinct: Int
        synchronized(lock) {
            events.addLast(Entry(nowT, destination))
            increment(destination)

            // Age out, then cap - the cap keeps this bounded when a flood
            // arrives faster than the window expires.
            while (events.isNotEmpty() && nowT - events.first().at > window) {
                decrement(events.removeFirst().destination)
            }
            while (events.size > options.maxTrackedConnections) {
                decrement(events.removeFirst().destination)
            }
            total = events.size
            distinct = distinctCounts.size
        }

        val seconds = Math.round(window).toInt()

        if (distinct >= options.distinctDestinationScanThreshold) {
            return ThreatSignal.create(
                ThreatCategory.PORT_SCAN, ThreatSeverity.MEDIUM, 0.55, destination,
                "Outbound fan-out to $distinct distinct destinations within ${seconds}s - scan/sweep pattern.",
                ThreatDirection.OUTBOUND,
                listOf("scan-pattern", "distinct-$distinct"), observation, now,
            )
        }

        if (total >= options.connectionFloodThreshold) {
            return ThreatSignal.create(
                ThreatCategory.CONNECTION_FLOOD, ThreatSeverity.MEDIUM, 0.50, destination,
                "$total outbound connections within ${seconds}s - flood / DoS-source pattern.",
                ThreatDirection.OUTBOUND,
                listOf("flood-pattern", "count-$total"), observation, now,
            )
        }
        return null
    }

    private fun increment(d: String) { distinctCounts[d] = (distinctCounts[d] ?: 0) + 1 }

    private fun decrement(d: String) {
        val c = distinctCounts[d] ?: return
        if (c <= 1) distinctCounts.remove(d) else distinctCounts[d] = c - 1
    }
}

/**
 * Counts repeat contacts with the same indicator inside a window. One contact
 * with a known-bad host is a mistake; the same one every five minutes is a
 * program phoning home.
 */
internal class BeaconTracker(private val options: DefenseOptions) {
    private val lock = Any()
    private val hits = mutableMapOf<String, MutableList<Double>>()

    fun record(indicator: String, now: Instant = Instant.now()): Int {
        val key = indicator.lowercase()
        val nowT = now.toEpochMilli() / 1000.0
        synchronized(lock) {
            val stamps = hits.getOrPut(key) { mutableListOf() }
            stamps.add(nowT)
            stamps.removeAll { nowT - it > options.beaconWindowSeconds }
            if (hits.size > options.maxTrackedConnections) {
                hits.entries.removeAll { it.value.isEmpty() }
            }
            return stamps.size
        }
    }
}

// ── Where findings go ───────────────────────────────────────────────────────

interface ThreatSink {
    suspend fun handle(signal: ThreatSignal)
}

/** Discards. The default when a host wires nothing up. */
object NullThreatSink : ThreatSink {
    override suspend fun handle(signal: ThreatSignal) {}
}

class DelegateThreatSink(private val handler: suspend (ThreatSignal) -> Unit) : ThreatSink {
    override suspend fun handle(signal: ThreatSignal) = handler(signal)
}

/**
 * Several sinks, in order. One that throws does not stop the rest - a logging
 * sink that fails must not be able to suppress an SOS.
 */
class CompositeThreatSink(private val sinks: List<ThreatSink>) : ThreatSink {
    constructor(vararg sinks: ThreatSink) : this(sinks.toList())

    override suspend fun handle(signal: ThreatSignal) {
        for (s in sinks) {
            try { s.handle(signal) } catch (_: Exception) { /* never let one sink silence another */ }
        }
    }
}

/**
 * The host own emergency path: a loud alert, a trusted contact, evidence
 * capture. Kept as an interface so this library never depends on any of that.
 */
interface SosEscalation {
    suspend fun escalate(signal: ThreatSignal)
}

object NullSosEscalation : SosEscalation {
    override suspend fun escalate(signal: ThreatSignal) {}
}

class DelegateSosEscalation(private val handler: suspend (ThreatSignal) -> Unit) : SosEscalation {
    override suspend fun escalate(signal: ThreatSignal) = handler(signal)
}

/**
 * Escalates only what clears the SOS floor - critical, by default. Waking
 * somebody for a medium-confidence scan pattern teaches them to ignore it.
 */
class SosThreatSink(
    private val sos: SosEscalation,
    private val options: DefenseOptions = DefenseOptions(),
) : ThreatSink {
    override suspend fun handle(signal: ThreatSignal) {
        if (signal.severity < options.sosSeverityFloor) return
        sos.escalate(signal)
    }
}

// ── The monitor ─────────────────────────────────────────────────────────────

interface ThreatMonitor {
    fun evaluate(observation: NetworkObservation): ThreatSignal?
    fun onSignal(listener: (ThreatSignal) -> Unit)
}

/**
 * Indicator lookup first, then the pattern detectors. `evaluate` is synchronous
 * and allocation-light because it sits on the path of every connection a
 * low-end phone makes.
 */
class BlocklistThreatMonitor(
    private val indicators: IndicatorSource,
    private val options: DefenseOptions = DefenseOptions(),
) : ThreatMonitor {

    private val anomaly = ConnectionRateAnomalyDetector(options)
    private val beacons = BeaconTracker(options)
    private val lock = Any()
    private val listeners = mutableListOf<(ThreatSignal) -> Unit>()

    override fun onSignal(listener: (ThreatSignal) -> Unit) {
        synchronized(lock) { listeners.add(listener) }
    }

    override fun evaluate(observation: NetworkObservation): ThreatSignal? {
        if (isAllowed(observation)) return null
        val signal = classify(observation) ?: return null
        if (signal.severity < options.minReportSeverity) return null
        synchronized(lock) { listeners.toList() }.forEach { it(signal) }
        return signal
    }

    private fun classify(observation: NetworkObservation): ThreatSignal? {
        val hit = indicators.match(observation.remoteAddress, observation.host)
        if (hit != null) {
            val repeats = beacons.record(hit.indicator, observation.observedAt)
            val beaconing = repeats >= options.beaconRepeatThreshold

            val category = when {
                beaconing -> ThreatCategory.COMMAND_AND_CONTROL
                hit.kind == IndicatorKind.DOMAIN -> ThreatCategory.KNOWN_MALWARE_HOST
                else -> ThreatCategory.MALICIOUS_ENDPOINT
            }

            val tags = mutableListOf(hit.reason, hit.kind.tagName)
            if (beaconing) tags.add("beacon-x$repeats")

            val description = if (beaconing) {
                "Repeated contact (${repeats}x) with known-bad indicator " +
                    "${hit.indicator} - possible C2 beaconing."
            } else {
                "Contact with known-bad indicator ${hit.indicator} (${hit.reason})."
            }

            return ThreatSignal.create(
                category,
                if (beaconing) ThreatSeverity.CRITICAL else ThreatSeverity.HIGH,
                if (beaconing) 0.98 else 0.90,
                hit.indicator, description, observation.direction,
                tags, observation, observation.observedAt,
            )
        }

        if (options.enableAnomalyDetection && observation.direction == ThreatDirection.OUTBOUND) {
            return anomaly.observe(observation, observation.observedAt)
        }
        return null
    }

    private fun isAllowed(observation: NetworkObservation): Boolean {
        val host = observation.host?.trimEnd(Char(46))
        if (!host.isNullOrEmpty() && options.allowedHosts.any { it.equals(host, ignoreCase = true) }) {
            return true
        }
        val remote = observation.remoteAddress
        return remote != null && options.allowedAddresses.contains(remote.text)
    }
}

// ── The always-on loop ──────────────────────────────────────────────────────

interface AutonomicDefense {
    val isActive: Boolean
    suspend fun start()
    fun stop()
}

/**
 * Reads the feed, evaluates each observation, hands findings to the sink. A
 * monitor that throws, or a sink that throws, is stepped past - the loop is the
 * one thing that must not stop because one observation was odd.
 */
class AlwaysOnDefenseSentinel(
    private val monitor: ThreatMonitor,
    private val feed: NetworkObservationFeed,
    private val sink: ThreatSink = NullThreatSink,
) : AutonomicDefense {

    @Volatile private var active = false
    override val isActive: Boolean get() = active

    override suspend fun start() {
        if (active) return
        active = true
        for (observation in feed.observations()) {
            if (!active) break
            val signal = try { monitor.evaluate(observation) } catch (_: Exception) { null } ?: continue
            try { sink.handle(signal) } catch (_: Exception) { /* a sink must not end the loop */ }
        }
    }

    override fun stop() { active = false }
}

/** The whole thing, wired: an index, a monitor over it, and a sentinel. */
class DefenseModule private constructor(
    val indicators: IndicatorSource,
    val monitor: ThreatMonitor,
    val sentinel: AutonomicDefense,
    val options: DefenseOptions,
) {
    companion object {
        /**
         * Builds a module over a blocklist supplied as TEXT. There is no
         * bundled list here on purpose: a blocklist that ships inside the
         * binary is stale the day it ships, and the host knows where its own
         * copy lives.
         */
        fun create(
            feed: NetworkObservationFeed,
            blocklist: String? = null,
            sink: ThreatSink = NullThreatSink,
            options: DefenseOptions = DefenseOptions(),
        ): DefenseModule {
            val indicators = BlocklistIndicatorSource()
            if (blocklist != null) indicators.refresh(blocklist, replace = true)
            val monitor = BlocklistThreatMonitor(indicators, options)
            val sentinel = AlwaysOnDefenseSentinel(monitor, feed, sink)
            return DefenseModule(indicators, monitor, sentinel, options)
        }
    }
}
