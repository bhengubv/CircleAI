// IntegrationInMemoryConnectors.swift
//
// Port of src/CircleAI.Integration/InMemoryIntegrationConnectors.cs — the
// deterministic, dependency-free in-memory reference implementations of the six
// integration connector contracts. These are the canonical offline/test doubles
// for ICalendarConnector / IEmailConnector / INewsSource / IWeatherProvider /
// IRoutingProvider / IHomeAutomationConnector — usable without any external
// provider, mirroring the InMemory* pattern every other package ships. The real
// provider bindings live in IntegrationCalendar/Email/News/Geo/HomeAssistant.swift.
//
// The connector protocols + DTOs they implement are defined elsewhere in the
// tree and are NOT redeclared here:
//   • ICalendarConnector / CalendarEvent, IEmailConnector / EmailMessage,
//     INewsSource / NewsItem, IWeatherProvider / WeatherSample
//       → ProactiveBriefing.swift (full-surface protocols)
//   • IRoutingProvider / RouteEstimate / RoutePoint,
//     IHomeAutomationConnector / HaEntity / IntegrationServiceArg
//       → IntegrationContracts.swift
//
// Numeric parity notes (must match the C# byte-for-byte where numeric):
//   • C#'s `Math.Round(x, digits)` uses round-half-to-EVEN (banker's rounding),
//     so this port rounds with `.toNearestOrEven`, NOT `String(format:"%.2f")`
//     (which rounds half away from zero). See `roundEven`.
//   • Weather: tempC = round(15 + 10·cos((lat+hour)·π/12), 2); feelsLike =
//     round(tempC − 1.5, 2); precip 0, wind 12, cloud 40, "Clear"; atUtc =
//     UnixEpoch + hourOffset hours.
//   • Routing: great-circle (haversine, r = 6371 km) + a mode speed
//     (walk 5 / bike 18 / transit 30 / else 60 kph); duration seconds =
//     (kph <= 0 ? 0 : km/kph)·3600; distanceKm = round(km, 3); 2-point polyline.
//
// Concurrency: each connector guards its seeded/mutable map with a single
// NSLock, confined to synchronous critical sections (never held across an
// await), matching the tree-wide rule.

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// Rounding helper (shared by the weather + routing connectors)
// ──────────────────────────────────────────────────────────────────────────

/// Round `value` to `digits` decimal places using round-half-to-even (banker's
/// rounding), matching .NET's default `Math.Round(double, int)`.
private func roundEven(_ value: Double, _ digits: Int) -> Double {
    let factor = pow(10.0, Double(digits))
    return (value * factor).rounded(.toNearestOrEven) / factor
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryCalendarConnector (ICalendarConnector)
// ──────────────────────────────────────────────────────────────────────────

/// In-memory `ICalendarConnector`: events are held in a map; listing returns
/// those overlapping the window, ordered by start. Port of the C#
/// `InMemoryCalendarConnector`.
public final class InMemoryCalendarConnector: ICalendarConnector, @unchecked Sendable {
    private let lock = NSLock()
    private var events: [String: CalendarEvent] = [:]

    public init() {}

    public var providerId: String { "in-memory" }
    public var isConfigured: Bool { true }

    /// Events overlapping [fromUtc, toUtc): `StartUtc < toUtc && EndUtc > fromUtc`,
    /// ordered by start (matches C#'s `ListEventsAsync`).
    public func listEvents(fromUtc: Date, toUtc: Date) async throws -> [CalendarEvent] {
        lock.lock(); defer { lock.unlock() }
        return events.values
            .filter { $0.startUtc < toUtc && $0.endUtc > fromUtc }
            .sorted { $0.startUtc < $1.startUtc }
    }

    /// Store (or replace) the event keyed by its id; return it (C#'s
    /// `CreateEventAsync`).
    @discardableResult
    public func createEvent(_ ev: CalendarEvent) async throws -> CalendarEvent {
        lock.lock(); events[ev.eventId] = ev; lock.unlock()
        return ev
    }

    /// Remove the event by id (calendarId is ignored, matching C#'s
    /// `DeleteEventAsync`).
    public func deleteEvent(calendarId: String, eventId: String) async throws {
        lock.lock(); events[eventId] = nil; lock.unlock()
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryEmailConnector (IEmailConnector)
// ──────────────────────────────────────────────────────────────────────────

/// In-memory `IEmailConnector`: seeded with messages; unread + search read
/// newest-first, markRead flips the flag. Port of the C# `InMemoryEmailConnector`.
public final class InMemoryEmailConnector: IEmailConnector, @unchecked Sendable {
    private let lock = NSLock()
    private var messages: [String: EmailMessage] = [:]

    /// - Parameter seed: optional initial messages, keyed by their id.
    public init(seed: [EmailMessage]? = nil) {
        if let seed { for m in seed { messages[m.messageId] = m } }
    }

    public var providerId: String { "in-memory" }
    public var isConfigured: Bool { true }

    /// Unread messages, newest first, capped at `max(0, max)` (matches C#'s
    /// `ListUnreadAsync`).
    public func listUnread(max: Int) async throws -> [EmailMessage] {
        lock.lock(); defer { lock.unlock() }
        return Array(messages.values
            .filter { $0.unread }
            .sorted { $0.receivedUtc > $1.receivedUtc }
            .prefix(Swift.max(0, max)))
    }

    /// Messages whose subject or body contains `query` (case-insensitive),
    /// newest first, capped at `max(0, max)` (matches C#'s `SearchAsync`; the
    /// C# `query ??= ""` null-guard is unrepresentable for a Swift String).
    public func search(query: String, max: Int) async throws -> [EmailMessage] {
        lock.lock(); defer { lock.unlock() }
        return Array(messages.values
            .filter {
                $0.subject.range(of: query, options: .caseInsensitive) != nil
                    || $0.bodyText.range(of: query, options: .caseInsensitive) != nil
            }
            .sorted { $0.receivedUtc > $1.receivedUtc }
            .prefix(Swift.max(0, max)))
    }

    /// Mark a message read by id (no-op if unknown). Mirrors C#'s
    /// `MarkReadAsync` (`m with { Unread = false }`).
    public func markRead(messageId: String) async throws {
        lock.lock(); defer { lock.unlock() }
        guard let m = messages[messageId] else { return }
        messages[messageId] = EmailMessage(
            messageId: m.messageId, from: m.from, to: m.to, subject: m.subject,
            bodyText: m.bodyText, receivedUtc: m.receivedUtc, unread: false, labels: m.labels)
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryNewsSource (INewsSource)
// ──────────────────────────────────────────────────────────────────────────

/// In-memory `INewsSource`: seeded items, newest-first. Port of the C#
/// `InMemoryNewsSource`.
public final class InMemoryNewsSource: INewsSource, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: NewsItem] = [:]

    /// - Parameter seed: optional initial items, keyed by their id.
    public init(seed: [NewsItem]? = nil) {
        if let seed { for i in seed { items[i.itemId] = i } }
    }

    public var sourceId: String { "in-memory" }
    public var isConfigured: Bool { true }

    /// Latest items, newest first, capped at `max(0, max)` (matches C#'s
    /// `FetchLatestAsync`).
    public func fetchLatest(max: Int) async throws -> [NewsItem] {
        lock.lock(); defer { lock.unlock() }
        return Array(items.values
            .sorted { $0.publishedUtc > $1.publishedUtc }
            .prefix(Swift.max(0, max)))
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryWeatherProvider (IWeatherProvider)
// ──────────────────────────────────────────────────────────────────────────

/// In-memory `IWeatherProvider`: deterministic pseudo-weather derived from
/// coordinates + hour (no randomness, reproducible across platforms). Port of
/// the C# `InMemoryWeatherProvider`.
public final class InMemoryWeatherProvider: IWeatherProvider, @unchecked Sendable {
    public init() {}

    public var providerId: String { "in-memory" }

    /// Current weather (hour offset 0). C#'s `CurrentAsync`.
    public func current(lat: Double, lon: Double) async throws -> WeatherSample {
        Self.sample(lat: lat, lon: lon, hourOffset: 0)
    }

    /// `max(0, hours)` hourly samples from offset 0. C#'s `HourlyAsync`.
    public func hourly(lat: Double, lon: Double, hours: Int) async throws -> [WeatherSample] {
        (0..<Swift.max(0, hours)).map { Self.sample(lat: lat, lon: lon, hourOffset: $0) }
    }

    /// The deterministic sample formula (mirrors C#'s private `Sample`). `lon` is
    /// intentionally unused, exactly as in the C# reference.
    private static func sample(lat: Double, lon: Double, hourOffset: Int) -> WeatherSample {
        let tempC = roundEven(15.0 + 10.0 * cos((lat + Double(hourOffset)) * Double.pi / 12.0), 2)
        return WeatherSample(
            atUtc: Date(timeIntervalSince1970: 0).addingTimeInterval(Double(hourOffset) * 3600.0),
            tempC: tempC,
            feelsLikeC: roundEven(tempC - 1.5, 2),
            precipMm: 0.0,
            windKph: 12.0,
            cloudPct: 40,
            condition: "Clear")
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryRoutingProvider (IRoutingProvider)
// ──────────────────────────────────────────────────────────────────────────

/// In-memory `IRoutingProvider`: great-circle distance and a mode-based speed
/// give a deterministic estimate with a 2-point polyline. Port of the C#
/// `InMemoryRoutingProvider`.
public final class InMemoryRoutingProvider: IRoutingProvider, @unchecked Sendable {
    public init() {}

    public var providerId: String { "in-memory" }

    /// Haversine distance + mode speed → deterministic estimate. Mode speeds
    /// mirror the C# switch exactly: walk 5 / bike 18 / transit 30 / else 60 kph.
    /// Duration (seconds) = (kph <= 0 ? 0 : km/kph)·3600; distance rounded to 3dp.
    public func route(
        fromLat: Double, fromLon: Double, toLat: Double, toLon: Double, mode: String
    ) async throws -> RouteEstimate {
        let km = Self.haversine(fromLat, fromLon, toLat, toLon)
        let kph: Double
        switch mode {
        case "walk": kph = 5.0
        case "bike": kph = 18.0
        case "transit": kph = 30.0
        default: kph = 60.0
        }
        let hours = kph <= 0 ? 0.0 : km / kph
        return RouteEstimate(
            distanceKm: roundEven(km, 3),
            duration: hours * 3600.0,
            polyline: [RoutePoint(lat: fromLat, lon: fromLon), RoutePoint(lat: toLat, lon: toLon)])
    }

    /// Great-circle distance in km (r = 6371). Mirrors the C# `Haversine`.
    private static func haversine(_ lat1: Double, _ lon1: Double, _ lat2: Double, _ lon2: Double) -> Double {
        let r = 6371.0
        let dLat = (lat2 - lat1) * Double.pi / 180.0
        let dLon = (lon2 - lon1) * Double.pi / 180.0
        let a = sin(dLat / 2) * sin(dLat / 2)
            + cos(lat1 * Double.pi / 180.0) * cos(lat2 * Double.pi / 180.0)
            * sin(dLon / 2) * sin(dLon / 2)
        return r * 2 * atan2(sqrt(a), sqrt(1 - a))
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryHomeAutomationConnector (IHomeAutomationConnector)
// ──────────────────────────────────────────────────────────────────────────

/// In-memory `IHomeAutomationConnector`: seeded entities; turn_on / turn_off /
/// toggle deterministically mutate matching-domain entity state. Port of the C#
/// `InMemoryHomeAutomationConnector`.
public final class InMemoryHomeAutomationConnector: IHomeAutomationConnector, @unchecked Sendable {
    private let lock = NSLock()
    private var entities: [String: HaEntity] = [:]

    /// - Parameter seed: optional initial entities, keyed by their id.
    public init(seed: [HaEntity]? = nil) {
        if let seed { for e in seed { entities[e.entityId] = e } }
    }

    public var providerId: String { "in-memory" }
    public var isConfigured: Bool { true }

    /// All entities, ordered by entity id (matches C#'s `ListEntitiesAsync` →
    /// `OrderBy(EntityId)`).
    public func listEntities() async throws -> [HaEntity] {
        lock.lock(); defer { lock.unlock() }
        return entities.values.sorted { $0.entityId < $1.entityId }
    }

    /// Apply a service to every entity in the matching domain (case-insensitive):
    /// turn_on → "on", turn_off → "off", toggle → flip, anything else → unchanged.
    /// `data` is accepted for contract parity but ignored (matches C#'s
    /// `CallServiceAsync`).
    public func callService(domain: String, service: String, data: [IntegrationServiceArg]?) async throws {
        lock.lock(); defer { lock.unlock() }
        let matches = entities.values.filter { $0.domain.caseInsensitiveCompare(domain) == .orderedSame }
        for e in matches {
            let newState: String
            switch service {
            case "turn_on": newState = "on"
            case "turn_off": newState = "off"
            case "toggle": newState = e.state == "on" ? "off" : "on"
            default: newState = e.state
            }
            entities[e.entityId] = HaEntity(
                entityId: e.entityId, friendlyName: e.friendlyName, domain: e.domain,
                state: newState, attributes: e.attributes)
        }
    }
}
