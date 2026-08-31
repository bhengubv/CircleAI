// SecurityDefense.swift
//
// Always-on network defence: watch what the device talks to, match it against
// known-bad indicators, notice scan and flood and beacon patterns, and hand
// what matters to a watchdog or an SOS escalation.
//
// Ported from src/CircleAI.Security.Defense.

import Foundation

// MARK: - Addresses
//
// Foundation has no IPAddress, and this module only ever needs three things
// from one: exact IPv4 equality, CIDR containment, and exact IPv6 equality.
// That is what this carries - no resolution, no sockets, no I/O.

/// An IPv4 or IPv6 literal, parsed once.
public struct IPAddressValue: Sendable, Equatable, Hashable, CustomStringConvertible {
    public enum Family: Sendable, Equatable { case iPv4, iPv6 }

    public let family: Family
    /// Host-order 32-bit value. Meaningful for IPv4 only.
    public let v4: UInt32
    /// Canonical lowercase text. This is what IPv6 is matched on.
    public let text: String

    private init(family: Family, v4: UInt32, text: String) {
        self.family = family
        self.v4 = v4
        self.text = text
    }

    public var description: String { text }

    /// Parses a dotted-quad or an IPv6 literal. Returns nil for anything else -
    /// a hostname is not an address and must not silently become one.
    public init?(_ s: String) {
        let t = s.trimmingCharacters(in: .whitespaces)
        if t.isEmpty { return nil }

        if IPAddressParsing.isValidIPv4(t) {
            let octets = t.split(separator: ".").compactMap { UInt32($0) }
            guard octets.count == 4 else { return nil }
            let value = (octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3]
            self = IPAddressValue(family: .iPv4, v4: value, text: t)
            return
        }
        if IPAddressParsing.isValidIPv6(t) {
            self = IPAddressValue(family: .iPv6, v4: 0, text: t.lowercased())
            return
        }
        return nil
    }

    /// Builds an IPv4 address from its host-order value.
    public static func iPv4(_ value: UInt32) -> IPAddressValue {
        let text = "\((value >> 24) & 0xFF).\((value >> 16) & 0xFF).\((value >> 8) & 0xFF).\(value & 0xFF)"
        return IPAddressValue(family: .iPv4, v4: value, text: text)
    }
}

// MARK: - What was observed

/// Which way the traffic was going.
public enum ThreatDirection: Int, Sendable, Equatable {
    case unknown = 0
    case outbound = 1
    case inbound = 2
    case lookup = 3
}

/// One connection or lookup, as the host reported it.
public struct NetworkObservation: Sendable, Equatable {
    public let host: String?
    public let remoteAddress: IPAddressValue?
    public let remotePort: Int
    public let direction: ThreatDirection
    public let proto: String
    public let appHint: String?
    public let observedAt: Date

    public init(host: String?, remoteAddress: IPAddressValue?, remotePort: Int,
                direction: ThreatDirection, proto: String, appHint: String?, observedAt: Date) {
        self.host = host
        self.remoteAddress = remoteAddress
        self.remotePort = remotePort
        self.direction = direction
        self.proto = proto
        self.appHint = appHint
        self.observedAt = observedAt
    }

    public static func outbound(address: IPAddressValue, port: Int, proto: String = "tcp",
                                host: String? = nil, appHint: String? = nil,
                                at: Date = Date()) -> NetworkObservation {
        NetworkObservation(host: host, remoteAddress: address, remotePort: port,
                           direction: .outbound, proto: proto, appHint: appHint, observedAt: at)
    }

    public static func dns(host: String, appHint: String? = nil, at: Date = Date()) -> NetworkObservation {
        NetworkObservation(host: host, remoteAddress: nil, remotePort: 0,
                           direction: .lookup, proto: "dns", appHint: appHint, observedAt: at)
    }
}

/// Where observations come from - a VpnService on Android, AetherNet connection
/// events, or a test double.
public protocol INetworkObservationFeed: Sendable {
    var sourceId: String { get }
    func observe() -> AsyncStream<NetworkObservation>
}

// MARK: - What it means

/// How bad. Comparable, because every floor in this module is a comparison.
public enum ThreatSeverity: Int, Sendable, Equatable, Comparable {
    case info = 0
    case low = 1
    case medium = 2
    case high = 3
    case critical = 4

    public static func < (a: ThreatSeverity, b: ThreatSeverity) -> Bool { a.rawValue < b.rawValue }
}

/// What kind of bad.
public enum ThreatCategory: Int, Sendable, Equatable {
    case unclassified = 0
    case maliciousEndpoint
    case knownMalwareHost
    case commandAndControl
    case phishing
    case dataExfiltration
    case portScan
    case connectionFlood
    case dnsAnomaly
}

/// One finding, with the observation that produced it kept alongside.
public struct ThreatSignal: Sendable, Equatable, Identifiable {
    public let id: UUID
    public let category: ThreatCategory
    public let severity: ThreatSeverity
    public let confidence: Double
    public let indicator: String
    public let threatDescription: String
    public let direction: ThreatDirection
    public let tags: [String]
    public let observation: NetworkObservation?
    public let detectedAt: Date

    public init(id: UUID, category: ThreatCategory, severity: ThreatSeverity, confidence: Double,
                indicator: String, threatDescription: String, direction: ThreatDirection,
                tags: [String], observation: NetworkObservation?, detectedAt: Date) {
        self.id = id
        self.category = category
        self.severity = severity
        self.confidence = confidence
        self.indicator = indicator
        self.threatDescription = threatDescription
        self.direction = direction
        self.tags = tags
        self.observation = observation
        self.detectedAt = detectedAt
    }

    /// Confidence is clamped here so no caller can publish a 1.4 or a -0.2.
    public static func create(
        category: ThreatCategory,
        severity: ThreatSeverity,
        confidence: Double,
        indicator: String,
        description: String,
        direction: ThreatDirection,
        tags: [String]? = nil,
        observation: NetworkObservation? = nil,
        at: Date = Date()
    ) -> ThreatSignal {
        ThreatSignal(
            id: UUID(),
            category: category,
            severity: severity,
            confidence: max(0.0, min(1.0, confidence)),
            indicator: indicator,
            threatDescription: description,
            direction: direction,
            tags: tags ?? [],
            observation: observation,
            detectedAt: at)
    }
}

// MARK: - Indicators

/// What sort of thing an indicator names.
public enum IndicatorKind: Int, Sendable, Equatable {
    case ipv4 = 0
    case ipv4Cidr
    case ipv6
    case domain

    /// Lowercase name, used as a signal tag.
    public var tagName: String {
        switch self {
        case .ipv4: return "ipv4"
        case .ipv4Cidr: return "ipv4cidr"
        case .ipv6: return "ipv6"
        case .domain: return "domain"
        }
    }
}

/// One entry read out of a blocklist.
public struct ParsedIndicator: Sendable, Equatable {
    public let kind: IndicatorKind
    public let value: String
    public init(kind: IndicatorKind, value: String) {
        self.kind = kind
        self.value = value
    }
}

/// Why an observation was flagged, and by which indicator.
public struct IndicatorMatch: Sendable, Equatable {
    public let indicator: String
    public let kind: IndicatorKind
    public let reason: String
    public init(indicator: String, kind: IndicatorKind, reason: String) {
        self.indicator = indicator
        self.kind = kind
        self.reason = reason
    }
}

/// An IPv4 network, stored as a masked value so containment is two operations.
public struct Ipv4Cidr: Sendable, Equatable, Hashable, CustomStringConvertible {
    public let network: UInt32
    public let mask: UInt32
    public let prefixLength: Int

    private init(network: UInt32, mask: UInt32, prefixLength: Int) {
        self.network = network
        self.mask = mask
        self.prefixLength = prefixLength
    }

    /// A bare address parses as a /32. A prefix outside 0...32 is rejected.
    public init?(_ text: String) {
        let t = text.trimmingCharacters(in: .whitespaces)
        if t.isEmpty { return nil }

        var prefix = 32
        var ipPart = t
        if let slash = t.firstIndex(of: "/") {
            ipPart = String(t[t.startIndex..<slash])
            let prefixPart = t[t.index(after: slash)...].trimmingCharacters(in: .whitespaces)
            guard let p = Int(prefixPart), p >= 0, p <= 32 else { return nil }
            prefix = p
        }

        guard let ip = IPAddressValue(ipPart.trimmingCharacters(in: .whitespaces)),
              ip.family == .iPv4 else { return nil }

        // A /0 shifted by 32 is undefined behaviour in C too; special-cased.
        let mask: UInt32 = prefix == 0 ? 0 : (0xFFFF_FFFF as UInt32) << (32 - prefix)
        self = Ipv4Cidr(network: ip.v4 & mask, mask: mask, prefixLength: prefix)
    }

    public func contains(_ ip: IPAddressValue) -> Bool {
        guard ip.family == .iPv4 else { return false }
        return (ip.v4 & mask) == network
    }

    public var description: String {
        "\((network >> 24) & 0xFF).\((network >> 16) & 0xFF).\((network >> 8) & 0xFF).\(network & 0xFF)/\(prefixLength)"
    }
}

/// Reads hosts-file and plain-list blocklists. Both formats are in the wild and
/// both are handled: a sinkhole prefix is dropped, comments are stripped.
public enum BlocklistParser {
    private static let sinkTokens: Set<String> = ["0.0.0.0", "127.0.0.1", "::", "::1"]

    public static func parse(_ text: String) -> [ParsedIndicator] {
        text.split(separator: "\n", omittingEmptySubsequences: false)
            .compactMap { parseLine(String($0)) }
    }

    public static func parseLine(_ rawLine: String) -> ParsedIndicator? {
        var line = rawLine.trimmingCharacters(in: .whitespacesAndNewlines)
        if line.isEmpty { return nil }

        if let hash = line.firstIndex(of: "#") {
            if hash == line.startIndex { return nil }
            line = String(line[line.startIndex..<hash]).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if line.isEmpty { return nil }

        let parts = line.split(whereSeparator: { $0.isWhitespace }).map(String.init)
        if parts.isEmpty { return nil }

        // "0.0.0.0 ads.example.com" - the sinkhole is not the indicator.
        let token = parts.count >= 2 && sinkTokens.contains(parts[0]) ? parts[1] : parts[0]
        return classify(token)
    }

    public static func classify(_ raw: String) -> ParsedIndicator? {
        var token = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        while token.hasSuffix(".") { token.removeLast() }
        token = token.lowercased()
        if token.isEmpty { return nil }

        if token.contains("/") {
            return Ipv4Cidr(token) != nil ? ParsedIndicator(kind: .ipv4Cidr, value: token) : nil
        }
        if let ip = IPAddressValue(token) {
            return ip.family == .iPv6
                ? ParsedIndicator(kind: .ipv6, value: ip.text)
                : ParsedIndicator(kind: .ipv4, value: token)
        }
        return isPlausibleDomain(token) ? ParsedIndicator(kind: .domain, value: token) : nil
    }

    /// At least one dot, so a bare word in a malformed list never becomes a
    /// domain that matches half the internet.
    static func isPlausibleDomain(_ s: String) -> Bool {
        if s.isEmpty || s.count > 253 { return false }
        var hasDot = false
        for c in s {
            if c == "." { hasDot = true; continue }
            guard c.isASCII, c.isLetter || c.isNumber || c == "-" || c == "_" else { return false }
        }
        return hasDot
    }
}

/// The indicator index a monitor asks.
public protocol IIndicatorSource: Sendable {
    var indicatorCount: Int { get }
    var lastUpdated: Date { get }
    func match(address: IPAddressValue?, host: String?) -> IndicatorMatch?
    @discardableResult
    func refresh(from text: String, replace: Bool) throws -> Int
}

public extension IIndicatorSource {
    @discardableResult
    func refresh(from text: String) throws -> Int { try refresh(from: text, replace: true) }
}

/// An in-memory IOC index. `match` reads one immutable snapshot, so it takes no
/// lock and a refresh swaps atomically instead of mutating under a reader.
public final class BlocklistIndicatorSource: IIndicatorSource, @unchecked Sendable {

    struct IndexSnapshot: Sendable {
        var ipv4: Set<UInt32> = []
        var cidrs: [Ipv4Cidr] = []
        var ipv6: Set<String> = []
        var domains: Set<String> = []
        var updatedAt = Date(timeIntervalSince1970: 0)
        static let empty = IndexSnapshot()
    }

    private let lock = NSLock()
    private var index = IndexSnapshot.empty

    public init() {}

    private var snapshot: IndexSnapshot {
        lock.lock(); defer { lock.unlock() }
        return index
    }

    public var indicatorCount: Int {
        let s = snapshot
        return s.ipv4.count + s.cidrs.count + s.ipv6.count + s.domains.count
    }

    public var lastUpdated: Date { snapshot.updatedAt }

    @discardableResult
    public func refresh(from text: String, replace: Bool = true) throws -> Int {
        let current = snapshot
        var ipv4 = replace ? Set<UInt32>() : current.ipv4
        var cidrs = replace ? [Ipv4Cidr]() : current.cidrs
        var ipv6 = replace ? Set<String>() : current.ipv6
        var domains = replace ? Set<String>() : current.domains

        var added = 0
        for indicator in BlocklistParser.parse(text) {
            switch indicator.kind {
            case .ipv4:
                if let ip = IPAddressValue(indicator.value), ipv4.insert(ip.v4).inserted { added += 1 }
            case .ipv4Cidr:
                if let cidr = Ipv4Cidr(indicator.value) { cidrs.append(cidr); added += 1 }
            case .ipv6:
                if ipv6.insert(indicator.value).inserted { added += 1 }
            case .domain:
                if domains.insert(indicator.value).inserted { added += 1 }
            }
        }

        lock.lock()
        index = IndexSnapshot(ipv4: ipv4, cidrs: cidrs, ipv6: ipv6, domains: domains, updatedAt: Date())
        lock.unlock()
        return added
    }

    public func match(address: IPAddressValue?, host: String?) -> IndicatorMatch? {
        let index = snapshot

        if let address {
            switch address.family {
            case .iPv4:
                if index.ipv4.contains(address.v4) {
                    return IndicatorMatch(indicator: address.text, kind: .ipv4, reason: "known-bad-ip")
                }
                for cidr in index.cidrs where cidr.contains(address) {
                    return IndicatorMatch(indicator: cidr.description, kind: .ipv4Cidr, reason: "known-bad-range")
                }
            case .iPv6:
                if index.ipv6.contains(address.text) {
                    return IndicatorMatch(indicator: address.text, kind: .ipv6, reason: "known-bad-ip")
                }
            }
        }

        guard var h = host?.trimmingCharacters(in: .whitespacesAndNewlines), !h.isEmpty else { return nil }
        while h.hasSuffix(".") { h.removeLast() }
        h = h.lowercased()

        if index.domains.contains(h) {
            return IndicatorMatch(indicator: h, kind: .domain, reason: "known-bad-domain")
        }
        // Blocking "evil.com" has to block "cdn.evil.com" too, so every parent
        // suffix is checked - that is how blocklists are meant to be read.
        var rest = Substring(h)
        while let dot = rest.firstIndex(of: ".") {
            let parent = String(rest[rest.index(after: dot)...])
            if parent.isEmpty { break }
            if index.domains.contains(parent) {
                return IndicatorMatch(indicator: parent, kind: .domain, reason: "known-bad-parent-domain")
            }
            rest = rest[rest.index(after: dot)...]
        }
        return nil
    }
}

// MARK: - Options

/// Every threshold in one place. All of them are bounded on purpose: this runs
/// on a phone, so nothing here may grow without a ceiling.
public final class DefenseOptions: @unchecked Sendable {
    public var minReportSeverity: ThreatSeverity = .low
    public var watchdogSeverityFloor: ThreatSeverity = .high
    public var sosSeverityFloor: ThreatSeverity = .critical
    public var enableAnomalyDetection = true
    public var anomalyWindow: TimeInterval = 10
    public var distinctDestinationScanThreshold = 20
    public var connectionFloodThreshold = 100
    public var maxTrackedConnections = 512
    public var beaconRepeatThreshold = 3
    public var beaconWindow: TimeInterval = 300
    public var allowedHosts = Set<String>()
    public var allowedAddresses = Set<String>()
    public var refreshHint: TimeInterval = 12 * 3600

    public init() {}
}

// MARK: - Patterns nobody declared

/// A bounded sliding window over recent destinations: enough to see a scan or a
/// flood, small enough that a busy phone does not pay for it.
final class ConnectionRateAnomalyDetector: @unchecked Sendable {
    private struct Entry { let at: TimeInterval; let destination: String }

    private let options: DefenseOptions
    private let lock = NSLock()
    private var events: [Entry] = []
    private var distinctCounts: [String: Int] = [:]

    init(options: DefenseOptions) { self.options = options }

    func observe(_ observation: NetworkObservation, now: Date = Date()) -> ThreatSignal? {
        let destination = observation.remoteAddress?.text ?? observation.host ?? "unknown"
        let nowT = now.timeIntervalSince1970
        let window = options.anomalyWindow

        var total = 0
        var distinct = 0
        lock.lock()
        events.append(Entry(at: nowT, destination: destination))
        increment(destination)
        // Age out, then cap - the cap is what keeps this bounded when a flood
        // arrives faster than the window expires.
        var head = 0
        while head < events.count && nowT - events[head].at > window {
            decrement(events[head].destination)
            head += 1
        }
        if head > 0 { events.removeFirst(head) }
        while events.count > options.maxTrackedConnections {
            decrement(events[0].destination)
            events.removeFirst()
        }
        total = events.count
        distinct = distinctCounts.count
        lock.unlock()

        let seconds = Int(window.rounded())

        if distinct >= options.distinctDestinationScanThreshold {
            return ThreatSignal.create(
                category: .portScan, severity: .medium, confidence: 0.55,
                indicator: destination,
                description: "Outbound fan-out to \(distinct) distinct destinations within \(seconds)s - scan/sweep pattern.",
                direction: .outbound,
                tags: ["scan-pattern", "distinct-\(distinct)"],
                observation: observation, at: now)
        }

        if total >= options.connectionFloodThreshold {
            return ThreatSignal.create(
                category: .connectionFlood, severity: .medium, confidence: 0.50,
                indicator: destination,
                description: "\(total) outbound connections within \(seconds)s - flood / DoS-source pattern.",
                direction: .outbound,
                tags: ["flood-pattern", "count-\(total)"],
                observation: observation, at: now)
        }
        return nil
    }

    private func increment(_ d: String) { distinctCounts[d, default: 0] += 1 }

    private func decrement(_ d: String) {
        guard let count = distinctCounts[d] else { return }
        if count <= 1 { distinctCounts.removeValue(forKey: d) } else { distinctCounts[d] = count - 1 }
    }
}

/// Counts repeat contacts with the same indicator inside a window. One contact
/// with a known-bad host is a mistake; the same one every five minutes is a
/// program phoning home.
final class BeaconTracker: @unchecked Sendable {
    private let options: DefenseOptions
    private let lock = NSLock()
    private var hits: [String: [TimeInterval]] = [:]

    init(options: DefenseOptions) { self.options = options }

    func record(_ indicator: String, now: Date = Date()) -> Int {
        let key = indicator.lowercased()
        let nowT = now.timeIntervalSince1970
        lock.lock(); defer { lock.unlock() }

        var timestamps = hits[key] ?? []
        timestamps.append(nowT)
        timestamps.removeAll { nowT - $0 > options.beaconWindow }
        hits[key] = timestamps

        if hits.count > options.maxTrackedConnections {
            for (k, v) in hits where v.isEmpty { hits.removeValue(forKey: k) }
        }
        return timestamps.count
    }
}

// MARK: - Where findings go

/// Somewhere to send a signal.
public protocol IThreatSink: Sendable {
    func handle(_ signal: ThreatSignal) async throws
}

/// Discards. The default when a host wires nothing up.
public struct NullThreatSink: IThreatSink {
    public static let instance = NullThreatSink()
    public init() {}
    public func handle(_ signal: ThreatSignal) async throws {}
}

/// A closure as a sink.
public struct DelegateThreatSink: IThreatSink {
    private let handler: @Sendable (ThreatSignal) async throws -> Void
    public init(_ handler: @escaping @Sendable (ThreatSignal) async throws -> Void) {
        self.handler = handler
    }
    public func handle(_ signal: ThreatSignal) async throws { try await handler(signal) }
}

/// Several sinks, in order. One that throws does not stop the rest - a logging
/// sink that fails must not be able to suppress an SOS.
public struct CompositeThreatSink: IThreatSink {
    private let sinks: [any IThreatSink]
    public init(_ sinks: [any IThreatSink]) { self.sinks = sinks }
    public init(_ sinks: any IThreatSink...) { self.sinks = sinks }

    public func handle(_ signal: ThreatSignal) async throws {
        for sink in sinks {
            do { try await sink.handle(signal) }
            catch is CancellationError { throw CancellationError() }
            catch { continue }
        }
    }
}

// MARK: - Escalation

/// The host's own emergency path: a loud alert, a trusted contact, evidence
/// capture. Kept as a protocol so this library never depends on any of that.
public protocol ISosEscalation: Sendable {
    func escalate(_ signal: ThreatSignal) async throws
}

public struct NullSosEscalation: ISosEscalation {
    public static let instance = NullSosEscalation()
    public init() {}
    public func escalate(_ signal: ThreatSignal) async throws {}
}

public struct DelegateSosEscalation: ISosEscalation {
    private let handler: @Sendable (ThreatSignal) async throws -> Void
    public init(_ handler: @escaping @Sendable (ThreatSignal) async throws -> Void) {
        self.handler = handler
    }
    public func escalate(_ signal: ThreatSignal) async throws { try await handler(signal) }
}

/// Escalates only what clears the SOS floor - critical, by default. Waking
/// somebody for a medium-confidence scan pattern teaches them to ignore it.
public struct SosThreatSink: IThreatSink {
    private let sos: any ISosEscalation
    private let options: DefenseOptions

    public init(sos: any ISosEscalation, options: DefenseOptions? = nil) {
        self.sos = sos
        self.options = options ?? DefenseOptions()
    }

    public func handle(_ signal: ThreatSignal) async throws {
        guard signal.severity >= options.sosSeverityFloor else { return }
        try await sos.escalate(signal)
    }
}

/// Forwards high-severity network findings to the security watchdog as an
/// anomaly, so network evidence lands in the same place as everything else.
public struct WatchdogThreatSink: IThreatSink {
    private let watchdog: any ISecurityWatchdog
    private let options: DefenseOptions

    public init(watchdog: any ISecurityWatchdog, options: DefenseOptions? = nil) {
        self.watchdog = watchdog
        self.options = options ?? DefenseOptions()
    }

    public func handle(_ signal: ThreatSignal) async throws {
        guard signal.severity >= options.watchdogSeverityFloor else { return }

        var evidence: [String: String] = [
            "indicator": signal.indicator,
            "category": String(describing: signal.category),
            "severity": String(describing: signal.severity),
            "direction": String(describing: signal.direction),
        ]
        if let host = signal.observation?.host, !host.isEmpty { evidence["host"] = host }
        if let remote = signal.observation?.remoteAddress { evidence["remote"] = remote.text }
        if let app = signal.observation?.appHint, !app.isEmpty { evidence["app"] = app }

        let anomaly = AnomalySignal.create(
            vector: Self.mapVector(signal.category),
            confidence: Float(signal.confidence),
            affectedModule: "CircleAI.Security.Defense",
            description: signal.threatDescription,
            evidence: evidence)

        _ = try await watchdog.onAnomalyDetected(anomaly, checkpoint: nil)
    }

    static func mapVector(_ category: ThreatCategory) -> ThreatVector {
        switch category {
        case .commandAndControl, .dataExfiltration, .maliciousEndpoint, .knownMalwareHost, .phishing:
            return .networkPivot
        default:
            return .unknown
        }
    }
}

// MARK: - The monitor

/// Turns an observation into a finding, or into nothing.
public protocol IThreatMonitor: Sendable {
    func evaluate(_ observation: NetworkObservation) -> ThreatSignal?
    func streamSignals() -> AsyncStream<ThreatSignal>
}

/// Indicator lookup first, then the pattern detectors. `evaluate` is synchronous
/// and allocation-light because it sits on the path of every connection a
/// low-end phone makes.
public final class BlocklistThreatMonitor: IThreatMonitor, @unchecked Sendable {
    private let indicators: any IIndicatorSource
    private let options: DefenseOptions
    private let anomaly: ConnectionRateAnomalyDetector
    private let beacons: BeaconTracker

    private let lock = NSLock()
    private var continuations: [UUID: AsyncStream<ThreatSignal>.Continuation] = [:]

    public init(indicators: any IIndicatorSource, options: DefenseOptions? = nil) {
        self.indicators = indicators
        let opts = options ?? DefenseOptions()
        self.options = opts
        self.anomaly = ConnectionRateAnomalyDetector(options: opts)
        self.beacons = BeaconTracker(options: opts)
    }

    public func evaluate(_ observation: NetworkObservation) -> ThreatSignal? {
        if isAllowed(observation) { return nil }
        guard let signal = classify(observation), signal.severity >= options.minReportSeverity else {
            return nil
        }
        publish(signal)
        return signal
    }

    public func streamSignals() -> AsyncStream<ThreatSignal> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            continuations[id] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                self.continuations.removeValue(forKey: id)
                self.lock.unlock()
            }
        }
    }

    private func publish(_ signal: ThreatSignal) {
        lock.lock()
        let live = Array(continuations.values)
        lock.unlock()
        for c in live { c.yield(signal) }
    }

    private func classify(_ observation: NetworkObservation) -> ThreatSignal? {
        if let hit = indicators.match(address: observation.remoteAddress, host: observation.host) {
            let repeats = beacons.record(hit.indicator, now: observation.observedAt)
            let beaconing = repeats >= options.beaconRepeatThreshold

            let category: ThreatCategory = beaconing
                ? .commandAndControl
                : (hit.kind == .domain ? .knownMalwareHost : .maliciousEndpoint)

            var tags = [hit.reason, hit.kind.tagName]
            if beaconing { tags.append("beacon-x\(repeats)") }

            let description = beaconing
                ? "Repeated contact (\(repeats)x) with known-bad indicator '\(hit.indicator)' - possible C2 beaconing."
                : "Contact with known-bad indicator '\(hit.indicator)' (\(hit.reason))."

            return ThreatSignal.create(
                category: category,
                severity: beaconing ? .critical : .high,
                confidence: beaconing ? 0.98 : 0.90,
                indicator: hit.indicator,
                description: description,
                direction: observation.direction,
                tags: tags,
                observation: observation,
                at: observation.observedAt)
        }

        if options.enableAnomalyDetection && observation.direction == .outbound {
            return anomaly.observe(observation, now: observation.observedAt)
        }
        return nil
    }

    private func isAllowed(_ observation: NetworkObservation) -> Bool {
        if var host = observation.host, !host.isEmpty {
            while host.hasSuffix(".") { host.removeLast() }
            if options.allowedHosts.contains(where: { $0.lowercased() == host.lowercased() }) { return true }
        }
        if let remote = observation.remoteAddress, options.allowedAddresses.contains(remote.text) {
            return true
        }
        return false
    }
}

// MARK: - The always-on loop

/// Something that runs by itself once started.
public protocol IAutonomicDefense: Sendable {
    var isActive: Bool { get }
    func start() async
    func stop() async
}

/// Reads the feed forever, evaluates each observation, hands findings to the
/// sink. A monitor that throws, or a sink that throws, is logged past - the
/// loop is the one thing that must not stop because one observation was odd.
public final class AlwaysOnDefenseSentinel: IAutonomicDefense, @unchecked Sendable {
    private let monitor: any IThreatMonitor
    private let feed: any INetworkObservationFeed
    private let sink: any IThreatSink

    private let lock = NSLock()
    private var task: Task<Void, Never>?
    private var active = false

    public init(monitor: any IThreatMonitor,
                feed: any INetworkObservationFeed,
                sink: (any IThreatSink)? = nil,
                options: DefenseOptions? = nil) {
        self.monitor = monitor
        self.feed = feed
        self.sink = sink ?? NullThreatSink.instance
        _ = options // reserved for loop tuning; kept for a stable init shape
    }

    public var isActive: Bool {
        lock.lock(); defer { lock.unlock() }
        return active
    }

    // The lock never spans a suspension point: each of these is a plain
    // synchronous critical section, and the awaiting happens outside them.
    private func beginActive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if active { return false }
        active = true
        return true
    }

    private func setTask(_ t: Task<Void, Never>) {
        lock.lock(); defer { lock.unlock() }
        task = t
    }

    private func endActive() -> Task<Void, Never>? {
        lock.lock(); defer { lock.unlock() }
        if !active { return nil }
        active = false
        let running = task
        task = nil
        return running
    }

    public func start() async {
        guard beginActive() else { return }
        setTask(Task { [monitor, feed, sink] in
            for await observation in feed.observe() {
                if Task.isCancelled { break }
                guard let signal = monitor.evaluate(observation) else { continue }
                do { try await sink.handle(signal) }
                catch { continue }
            }
        })
    }

    public func stop() async {
        guard let running = endActive() else { return }
        running.cancel()
        await running.value
    }
}

/// The whole thing, wired: an index, a monitor over it, and a sentinel reading
/// the host's feed.
public struct DefenseModule: Sendable {
    public let indicators: any IIndicatorSource
    public let monitor: any IThreatMonitor
    public let sentinel: any IAutonomicDefense
    public let options: DefenseOptions

    public init(indicators: any IIndicatorSource, monitor: any IThreatMonitor,
                sentinel: any IAutonomicDefense, options: DefenseOptions) {
        self.indicators = indicators
        self.monitor = monitor
        self.sentinel = sentinel
        self.options = options
    }

    /// Builds a module over a blocklist supplied as text. There is no bundled
    /// list here on purpose: a blocklist that ships inside the binary is stale
    /// the day it ships, and the host knows where its own copy lives.
    public static func create(
        feed: any INetworkObservationFeed,
        blocklist: String? = nil,
        sink: (any IThreatSink)? = nil,
        options: DefenseOptions? = nil
    ) throws -> DefenseModule {
        let options = options ?? DefenseOptions()
        let indicators = BlocklistIndicatorSource()
        if let blocklist { try indicators.refresh(from: blocklist, replace: true) }

        let monitor = BlocklistThreatMonitor(indicators: indicators, options: options)
        let sentinel = AlwaysOnDefenseSentinel(monitor: monitor, feed: feed, sink: sink, options: options)
        return DefenseModule(indicators: indicators, monitor: monitor, sentinel: sentinel, options: options)
    }
}
