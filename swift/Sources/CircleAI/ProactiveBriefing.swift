// ProactiveBriefing.swift
//
// Port of CircleAI.Companion.ProactiveBriefingService (ProactiveBriefingService.cs)
// plus the slice of CircleAI.Integration.Contracts it consumes.
//
// Scheduled service that assembles a "what's happening" briefing from
// registered calendar / email / news / weather connectors, runs the result
// through an LLM summariser for a friendly summary, and pushes the outcome
// through any registered notifier (push / WhatsApp / Telegram / SMS …).
//
// Schedule is the simplest possible cron — a list of times-of-day (UTC) at
// which the briefing fires. Default: 06:30 and 18:00 UTC.
//
// External dependencies (connectors, notifier, AI summariser) are injected
// behind protocols so the whole thing runs + tests in-memory. The C# original
// is an IHostedService; here start/stop drive a Swift-concurrency loop.

import Foundation

// =====================================================================
// Integration DTOs + connector protocols (the subset the briefing uses)
// =====================================================================

/// A calendar event. Faithful port of `CircleAI.Integration.CalendarEvent`.
public struct CalendarEvent: Sendable, Equatable {
    public let eventId: String
    public let calendarId: String
    public let title: String
    public let description: String?
    public let location: String?
    public let startUtc: Date
    public let endUtc: Date
    public let isAllDay: Bool
    public let attendees: [String]

    public init(eventId: String, calendarId: String, title: String, description: String? = nil,
                location: String? = nil, startUtc: Date, endUtc: Date, isAllDay: Bool = false,
                attendees: [String] = []) {
        self.eventId = eventId
        self.calendarId = calendarId
        self.title = title
        self.description = description
        self.location = location
        self.startUtc = startUtc
        self.endUtc = endUtc
        self.isAllDay = isAllDay
        self.attendees = attendees
    }
}

/// Calendar provider. Port of the briefing-relevant surface of
/// `ICalendarConnector`.
public protocol ICalendarConnector: AnyObject, Sendable {
    var providerId: String { get }
    var isConfigured: Bool { get }
    func listEvents(fromUtc: Date, toUtc: Date) async throws -> [CalendarEvent]
}

/// An email message. Faithful port of `CircleAI.Integration.EmailMessage`.
public struct EmailMessage: Sendable, Equatable {
    public let messageId: String
    public let from: String
    public let to: [String]
    public let subject: String
    public let bodyText: String
    public let receivedUtc: Date
    public let unread: Bool
    public let labels: [String]

    public init(messageId: String, from: String, to: [String] = [], subject: String,
                bodyText: String = "", receivedUtc: Date, unread: Bool = true, labels: [String] = []) {
        self.messageId = messageId
        self.from = from
        self.to = to
        self.subject = subject
        self.bodyText = bodyText
        self.receivedUtc = receivedUtc
        self.unread = unread
        self.labels = labels
    }
}

/// Email provider. Port of the briefing-relevant surface of `IEmailConnector`.
public protocol IEmailConnector: AnyObject, Sendable {
    var providerId: String { get }
    var isConfigured: Bool { get }
    func listUnread(max: Int) async throws -> [EmailMessage]
}

/// A news / social-feed item. Faithful port of `CircleAI.Integration.NewsItem`.
public struct NewsItem: Sendable, Equatable {
    public let itemId: String
    public let sourceId: String
    public let title: String
    public let summary: String
    public let url: String
    public let publishedUtc: Date
    public let tags: [String]

    public init(itemId: String, sourceId: String, title: String, summary: String = "",
                url: String, publishedUtc: Date, tags: [String] = []) {
        self.itemId = itemId
        self.sourceId = sourceId
        self.title = title
        self.summary = summary
        self.url = url
        self.publishedUtc = publishedUtc
        self.tags = tags
    }
}

/// News source. Port of the briefing-relevant surface of `INewsSource`.
public protocol INewsSource: AnyObject, Sendable {
    var sourceId: String { get }
    var isConfigured: Bool { get }
    func fetchLatest(max: Int) async throws -> [NewsItem]
}

/// A weather observation. Faithful port of `CircleAI.Integration.WeatherSample`.
public struct WeatherSample: Sendable, Equatable {
    public let atUtc: Date
    public let tempC: Double
    public let feelsLikeC: Double
    public let precipMm: Double
    public let windKph: Double
    public let cloudPct: Int
    public let condition: String

    public init(atUtc: Date, tempC: Double, feelsLikeC: Double, precipMm: Double = 0,
                windKph: Double, cloudPct: Int = 0, condition: String) {
        self.atUtc = atUtc
        self.tempC = tempC
        self.feelsLikeC = feelsLikeC
        self.precipMm = precipMm
        self.windKph = windKph
        self.cloudPct = cloudPct
        self.condition = condition
    }
}

/// Weather provider. Port of the briefing-relevant surface of `IWeatherProvider`.
public protocol IWeatherProvider: AnyObject, Sendable {
    var providerId: String { get }
    func current(lat: Double, lon: Double) async throws -> WeatherSample
}

// =====================================================================
// Briefing summariser (injected AI dependency)
// =====================================================================

/// The AI summarisation dependency for the briefing service — an idiomatic
/// subset of `CircleAI.Hosting.IAIService.ChatAsync`. Injected so the service
/// can run without an in-process model; hosts wire a real model behind it.
public protocol IBriefingSummarizer: AnyObject, Sendable {
    func summarize(prompt: String) async throws -> String
}

// =====================================================================
// Notifier
// =====================================================================

/// Pluggable notifier — hosts wire WhatsApp, Telegram, SMS, push, etc. Ported
/// from `IBriefingNotifier`.
public protocol IBriefingNotifier: AnyObject, Sendable {
    func deliver(headline: String, body: String, address: String?) async throws
}

// =====================================================================
// Options
// =====================================================================

/// Configuration knobs for `ProactiveBriefingService`. Ported from
/// `ProactiveBriefingOptions`. `fireTimesUtc` are time-of-day offsets (seconds
/// since UTC midnight); default 06:30 and 18:00.
public struct ProactiveBriefingOptions: Sendable {
    /// UTC times-of-day at which to fire, as seconds-since-midnight. Default: 06:30 and 18:00.
    public var fireTimesUtc: [TimeInterval]
    /// Latitude for weather lookup. Nil = skip weather.
    public var latitude: Double?
    /// Longitude for weather lookup. Nil = skip weather.
    public var longitude: Double?
    /// Headline used by the notifier. Default "Your briefing".
    public var headline: String
    /// Where to deliver — phone E.164 for SMS/WhatsApp; channel id for Telegram; etc.
    public var deliveryAddress: String?

    public init(
        fireTimesUtc: [TimeInterval] = [6 * 3600 + 30 * 60, 18 * 3600],
        latitude: Double? = nil,
        longitude: Double? = nil,
        headline: String = "Your briefing",
        deliveryAddress: String? = nil
    ) {
        self.fireTimesUtc = fireTimesUtc
        self.latitude = latitude
        self.longitude = longitude
        self.headline = headline
        self.deliveryAddress = deliveryAddress
    }
}

// =====================================================================
// Service
// =====================================================================

/// Scheduled service that assembles + summarises + delivers a briefing. Ported
/// from `ProactiveBriefingService`. Start it with `start()` (spawns the loop);
/// call `stop()` to cancel. `fireOnce()` runs one assemble→summarise→deliver
/// pass directly (also used by tests). `timeUntilNextFire(now:)` computes the
/// gap to the next configured fire moment (always > 30 s to avoid double-fires).
public final class ProactiveBriefingService: @unchecked Sendable {
    private let calendars: [ICalendarConnector]
    private let emails: [IEmailConnector]
    private let news: [INewsSource]
    private let weather: IWeatherProvider?
    private let notifiers: [IBriefingNotifier]
    private let ai: IBriefingSummarizer?
    private let opts: ProactiveBriefingOptions

    private let lock = NSLock()
    private var loopTask: Task<Void, Never>?

    public init(
        opts: ProactiveBriefingOptions,
        calendars: [ICalendarConnector] = [],
        emails: [IEmailConnector] = [],
        news: [INewsSource] = [],
        weather: IWeatherProvider? = nil,
        notifiers: [IBriefingNotifier] = [],
        ai: IBriefingSummarizer? = nil
    ) {
        self.opts = opts
        self.calendars = calendars
        self.emails = emails
        self.news = news
        self.weather = weather
        self.notifiers = notifiers
        self.ai = ai
    }

    public var isRunning: Bool {
        lock.lock(); defer { lock.unlock() }
        return loopTask != nil
    }

    public func start() {
        lock.lock(); defer { lock.unlock() }
        if loopTask != nil { return }
        loopTask = Task { [weak self] in
            await self?.loop()
        }
    }

    public func stop() {
        lock.lock()
        let t = loopTask
        loopTask = nil
        lock.unlock()
        t?.cancel()
    }

    private func loop() async {
        while !Task.isCancelled {
            let sleep = timeUntilNextFire(now: Date())
            do {
                try await Task.sleep(nanoseconds: UInt64(max(0, sleep) * 1_000_000_000))
            } catch {
                return // cancelled
            }
            if Task.isCancelled { return }
            do {
                try await fireOnce()
            } catch {
                // fire failed — swallow, retry on next interval (matches reference).
            }
        }
    }

    /// Compute time until the next configured fire moment. Always > 30 s to
    /// avoid double-fires. Ported from `TimeUntilNextFire`.
    public func timeUntilNextFire(now: Date) -> TimeInterval {
        if opts.fireTimesUtc.isEmpty { return 3600 }
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let todayBase = cal.startOfDay(for: now)

        var best: TimeInterval? = nil
        for tod in opts.fireTimesUtc {
            var candidate = todayBase.addingTimeInterval(tod)
            if candidate <= now.addingTimeInterval(30) {
                candidate = candidate.addingTimeInterval(86400)
            }
            let gap = candidate.timeIntervalSince(now)
            if best == nil || gap < best! { best = gap }
        }
        return best ?? 3600
    }

    /// Assemble the briefing context, summarise via the LLM, deliver. Ported
    /// from `FireOnceAsync`. Individual connector failures are swallowed so one
    /// broken source never kills the briefing.
    public func fireOnce() async throws {
        var ctxParts: [String] = []

        // Calendar — next 24 hours.
        for cal in calendars where cal.isConfigured {
            do {
                let events = try await cal.listEvents(fromUtc: Date(), toUtc: Date().addingTimeInterval(24 * 3600))
                if !events.isEmpty {
                    ctxParts.append("### Calendar (\(cal.providerId))")
                    for e in events.sorted(by: { $0.startUtc < $1.startUtc }).prefix(8) {
                        let loc = (e.location?.isEmpty ?? true) ? "" : " @ " + e.location!
                        ctxParts.append("- \(Self.localHHmm(e.startUtc)) \(e.title)\(loc)")
                    }
                }
            } catch {
                // calendar skipped
            }
        }

        // Email — unread.
        for em in emails where em.isConfigured {
            do {
                let unread = try await em.listUnread(max: 5)
                if !unread.isEmpty {
                    ctxParts.append("### Unread email (\(em.providerId))")
                    for m in unread { ctxParts.append("- \(m.from): \(m.subject)") }
                }
            } catch {
                // email skipped
            }
        }

        // News — latest from each source.
        for src in news where src.isConfigured {
            do {
                let items = try await src.fetchLatest(max: 5)
                if !items.isEmpty {
                    ctxParts.append("### News (\(src.sourceId))")
                    for i in items { ctxParts.append("- \(i.title)") }
                }
            } catch {
                // news skipped
            }
        }

        // Weather — if location configured.
        if let weather, let lat = opts.latitude, let lon = opts.longitude {
            do {
                let w = try await weather.current(lat: lat, lon: lon)
                ctxParts.append("### Weather (\(weather.providerId))")
                ctxParts.append("- \(Self.f0(w.tempC))°C \(w.condition), feels \(Self.f0(w.feelsLikeC))°C, wind \(Self.f0(w.windKph)) km/h")
            } catch {
                // weather skipped
            }
        }

        if ctxParts.isEmpty {
            return // no signals; skip fire
        }

        let context = ctxParts.joined(separator: "\n")
        let prompt = "Summarise the user's morning briefing in 80 words or less. Warm but factual. End with the one thing they should do first today.\n\n" + context

        var summary: String
        if let ai {
            do {
                summary = try await ai.summarize(prompt: prompt)
            } catch {
                summary = context // AI failed; send raw context
            }
        } else {
            summary = context
        }

        for notifier in notifiers {
            do {
                try await notifier.deliver(headline: opts.headline, body: summary, address: opts.deliveryAddress)
            } catch {
                // notifier failed — swallow
            }
        }
    }

    // MARK: - formatting helpers

    /// Local-time "HH:mm" for a UTC instant (C# used e.StartUtc.ToLocalTime():HH:mm).
    static func localHHmm(_ date: Date) -> String {
        let cal = Calendar(identifier: .gregorian) // current (local) time zone
        let c = cal.dateComponents([.hour, .minute], from: date)
        return String(format: "%02d:%02d", c.hour ?? 0, c.minute ?? 0)
    }

    /// F0 — round to nearest integer, no decimals (matches C# "F0").
    static func f0(_ v: Double) -> String {
        String(Int(v.rounded()))
    }
}
