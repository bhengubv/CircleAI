// ProactiveBriefingTests.swift
//
// Verifies ProactiveBriefingService (ProactiveBriefing.swift): context assembly
// from calendar/email/news/weather connectors, LLM summarisation with raw-context
// fallback, notifier delivery, no-signal skip, and the timeUntilNextFire schedule
// math (always > 30 s to avoid double-fires).

import XCTest
@testable import CircleAI

final class ProactiveBriefingTests: XCTestCase {

    private var utc: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()
    private func date(_ y: Int, _ mo: Int, _ d: Int, _ h: Int, _ mi: Int) -> Date {
        utc.date(from: DateComponents(year: y, month: mo, day: d, hour: h, minute: mi, second: 0))!
    }

    // ── Fakes ─────────────────────────────────────────────────────────────
    final class FakeCalendar: ICalendarConnector, @unchecked Sendable {
        let events: [CalendarEvent]
        init(_ events: [CalendarEvent]) { self.events = events }
        var providerId: String { "fake-cal" }
        var isConfigured: Bool { true }
        func listEvents(fromUtc: Date, toUtc: Date) async throws -> [CalendarEvent] { events }
    }
    final class FakeEmail: IEmailConnector, @unchecked Sendable {
        let msgs: [EmailMessage]
        init(_ msgs: [EmailMessage]) { self.msgs = msgs }
        var providerId: String { "fake-mail" }
        var isConfigured: Bool { true }
        func listUnread(max: Int) async throws -> [EmailMessage] { Array(msgs.prefix(max)) }
    }
    final class FakeWeather: IWeatherProvider, @unchecked Sendable {
        var providerId: String { "fake-wx" }
        func current(lat: Double, lon: Double) async throws -> WeatherSample {
            WeatherSample(atUtc: Date(), tempC: 21.4, feelsLikeC: 20.0, windKph: 12.6, condition: "Clear")
        }
    }
    final class EchoSummarizer: IBriefingSummarizer, @unchecked Sendable {
        private(set) var lastPrompt: String?
        let reply: String
        init(_ reply: String) { self.reply = reply }
        func summarize(prompt: String) async throws -> String {
            lastPrompt = prompt
            return reply
        }
    }
    final class ThrowingSummarizer: IBriefingSummarizer, @unchecked Sendable {
        struct Boom: Error {}
        func summarize(prompt: String) async throws -> String { throw Boom() }
    }
    final class CapturingNotifier: IBriefingNotifier, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var deliveries: [(headline: String, body: String, address: String?)] = []
        func deliver(headline: String, body: String, address: String?) async throws {
            lock.lock(); deliveries.append((headline, body, address)); lock.unlock()
        }
    }

    // ── Assembly + summarise + deliver ────────────────────────────────────
    func testFireOnceDeliversSummary() async throws {
        let cal = FakeCalendar([
            CalendarEvent(eventId: "1", calendarId: "c", title: "Standup",
                          startUtc: date(2026, 7, 8, 9, 0), endUtc: date(2026, 7, 8, 9, 15)),
        ])
        let email = FakeEmail([
            EmailMessage(messageId: "m1", from: "boss@co", subject: "Q3 numbers", receivedUtc: Date()),
        ])
        let ai = EchoSummarizer("Good morning! You have standup at 9.")
        let notifier = CapturingNotifier()
        let svc = ProactiveBriefingService(
            opts: ProactiveBriefingOptions(headline: "Brief", deliveryAddress: "+27123"),
            calendars: [cal], emails: [email], notifiers: [notifier], ai: ai)

        try await svc.fireOnce()

        XCTAssertEqual(notifier.deliveries.count, 1)
        XCTAssertEqual(notifier.deliveries[0].headline, "Brief")
        XCTAssertEqual(notifier.deliveries[0].body, "Good morning! You have standup at 9.")
        XCTAssertEqual(notifier.deliveries[0].address, "+27123")
        // The prompt should include the assembled context.
        XCTAssertTrue(ai.lastPrompt!.contains("Standup"))
        XCTAssertTrue(ai.lastPrompt!.contains("Q3 numbers"))
    }

    func testFireOnceFallsBackToRawContextOnAiFailure() async throws {
        let cal = FakeCalendar([
            CalendarEvent(eventId: "1", calendarId: "c", title: "Dentist",
                          startUtc: date(2026, 7, 8, 14, 0), endUtc: date(2026, 7, 8, 14, 30)),
        ])
        let notifier = CapturingNotifier()
        let svc = ProactiveBriefingService(
            opts: ProactiveBriefingOptions(), calendars: [cal],
            notifiers: [notifier], ai: ThrowingSummarizer())
        try await svc.fireOnce()
        XCTAssertEqual(notifier.deliveries.count, 1)
        // Raw context delivered (contains the calendar header).
        XCTAssertTrue(notifier.deliveries[0].body.contains("Dentist"))
    }

    func testFireOnceIncludesWeather() async throws {
        let notifier = CapturingNotifier()
        let svc = ProactiveBriefingService(
            opts: ProactiveBriefingOptions(latitude: -29.85, longitude: 31.02),
            weather: FakeWeather(), notifiers: [notifier])
        try await svc.fireOnce()
        XCTAssertEqual(notifier.deliveries.count, 1)
        // F0 rounding: 21.4 → "21", wind 12.6 → "13".
        XCTAssertTrue(notifier.deliveries[0].body.contains("21°C Clear"))
        XCTAssertTrue(notifier.deliveries[0].body.contains("wind 13 km/h"))
    }

    func testFireOnceSkipsWhenNoSignals() async throws {
        let notifier = CapturingNotifier()
        let svc = ProactiveBriefingService(opts: ProactiveBriefingOptions(), notifiers: [notifier])
        try await svc.fireOnce()
        XCTAssertTrue(notifier.deliveries.isEmpty, "no signals → no delivery")
    }

    func testFireOnceWithoutAiSendsRawContext() async throws {
        let cal = FakeCalendar([
            CalendarEvent(eventId: "1", calendarId: "c", title: "Lunch",
                          startUtc: date(2026, 7, 8, 12, 0), endUtc: date(2026, 7, 8, 13, 0)),
        ])
        let notifier = CapturingNotifier()
        let svc = ProactiveBriefingService(opts: ProactiveBriefingOptions(), calendars: [cal], notifiers: [notifier])
        try await svc.fireOnce()
        XCTAssertTrue(notifier.deliveries[0].body.contains("Lunch"))
    }

    // ── Schedule math ─────────────────────────────────────────────────────
    func testTimeUntilNextFirePicksNearest() {
        // Fire times 06:30 and 18:00 UTC. At 05:00, next is 06:30 → 1.5h.
        let svc = ProactiveBriefingService(opts: ProactiveBriefingOptions())
        let now = date(2026, 7, 8, 5, 0)
        let gap = svc.timeUntilNextFire(now: now)
        XCTAssertEqual(gap, 1.5 * 3600, accuracy: 1.0)
    }

    func testTimeUntilNextFireRollsToTomorrow() {
        // At 19:00, both today's fire times passed → next is tomorrow 06:30.
        let svc = ProactiveBriefingService(opts: ProactiveBriefingOptions())
        let now = date(2026, 7, 8, 19, 0)
        let gap = svc.timeUntilNextFire(now: now)
        // From 19:00 to next-day 06:30 = 11.5h.
        XCTAssertEqual(gap, 11.5 * 3600, accuracy: 1.0)
    }

    func testTimeUntilNextFireAvoidsDoubleFire() {
        // Exactly at a fire time, the 30 s guard pushes it to the NEXT fire.
        let svc = ProactiveBriefingService(opts: ProactiveBriefingOptions())
        let now = date(2026, 7, 8, 6, 30)
        let gap = svc.timeUntilNextFire(now: now)
        XCTAssertGreaterThan(gap, 30, "must not schedule a fire within 30 s of now")
    }

    func testTimeUntilNextFireEmptyDefaultsToHour() {
        let svc = ProactiveBriefingService(opts: ProactiveBriefingOptions(fireTimesUtc: []))
        XCTAssertEqual(svc.timeUntilNextFire(now: Date()), 3600)
    }
}
