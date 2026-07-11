// IntegrationGeo.swift
//
// Port of the CircleAI.Integration.Geo vertical (collapsing the C# folder's two
// files into one):
//   • OpenMeteoWeatherProvider.cs → OpenMeteoWeatherProvider (IWeatherProvider)
//   • OsrmRoutingProvider.cs      → OsrmOptions + OsrmRoutingProvider (IRoutingProvider)
//
// Both talk HTTP → the injected `IIntegrationHttpTransport`; every URL, the
// unit conversions (m/s → km/h, m → km, s → TimeInterval), the WMO weather-code
// decode table, and the OSRM `code == "Ok"` guard are ported verbatim and
// asserted against `FakeIntegrationHttpTransport` (no real calls).

import Foundation

// MARK: - Open-Meteo weather

/// Open-Meteo free, no-API-key `IWeatherProvider`. Port of the C#
/// `OpenMeteoWeatherProvider`.
public final class OpenMeteoWeatherProvider: IWeatherProvider, @unchecked Sendable {
    private let http: IIntegrationHttpTransport

    public init(http: IIntegrationHttpTransport) {
        self.http = http
    }

    public var providerId: String { "open-meteo" }

    public func current(lat: Double, lon: Double) async throws -> WeatherSample {
        let url = "https://api.open-meteo.com/v1/forecast?latitude=\(Self.num(lat))&longitude=\(Self.num(lon))"
            + "&current=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: url))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)
        guard let cur = IntegrationJson.object(doc, "current") else {
            throw IntegrationError.invalidOperation("Open-Meteo response missing 'current'.")
        }
        let ts = IntegrationJson.string(cur, "time")
        return WeatherSample(
            atUtc: Self.parseTime(ts),
            tempC: IntegrationJson.double(cur, "temperature_2m") ?? 0,
            feelsLikeC: IntegrationJson.double(cur, "apparent_temperature") ?? 0,
            precipMm: IntegrationJson.double(cur, "precipitation") ?? 0,
            windKph: (IntegrationJson.double(cur, "wind_speed_10m") ?? 0) * 3.6, // m/s → km/h
            cloudPct: IntegrationJson.int(cur, "cloud_cover") ?? 0,
            condition: Self.wmoDecode(IntegrationJson.int(cur, "weather_code") ?? -1))
    }

    public func hourly(lat: Double, lon: Double, hours: Int) async throws -> [WeatherSample] {
        if hours <= 0 || hours > 168 { throw IntegrationError.argumentOutOfRange("hours") }
        let url = "https://api.open-meteo.com/v1/forecast?latitude=\(Self.num(lat))&longitude=\(Self.num(lon))"
            + "&hourly=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code"
            + "&forecast_hours=\(hours)"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: url))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)
        guard let h = IntegrationJson.object(doc, "hourly"),
              let time = IntegrationJson.array(h, "time"),
              let temp = IntegrationJson.array(h, "temperature_2m"),
              let feel = IntegrationJson.array(h, "apparent_temperature"),
              let prec = IntegrationJson.array(h, "precipitation"),
              let wind = IntegrationJson.array(h, "wind_speed_10m"),
              let cld = IntegrationJson.array(h, "cloud_cover"),
              let code = IntegrationJson.array(h, "weather_code") else {
            throw IntegrationError.invalidOperation("Open-Meteo response missing 'hourly' arrays.")
        }
        let n = Swift.min(time.count, hours)
        var result: [WeatherSample] = []
        result.reserveCapacity(n)
        for i in 0..<n {
            result.append(WeatherSample(
                atUtc: Self.parseTime(time[i] as? String),
                tempC: Self.dbl(temp[i]),
                feelsLikeC: Self.dbl(feel[i]),
                precipMm: Self.dbl(prec[i]),
                windKph: Self.dbl(wind[i]) * 3.6,
                cloudPct: Self.int(cld[i]),
                condition: Self.wmoDecode(Self.int(code[i]))))
        }
        return result
    }

    /// Decode a WMO weather code (Open-Meteo standard). Verbatim from the C#
    /// switch, including the "unknown" default.
    static func wmoDecode(_ code: Int) -> String {
        switch code {
        case 0: return "clear sky"
        case 1, 2, 3: return "partly cloudy"
        case 45, 48: return "fog"
        case 51, 53, 55: return "drizzle"
        case 56, 57: return "freezing drizzle"
        case 61, 63, 65: return "rain"
        case 66, 67: return "freezing rain"
        case 71, 73, 75: return "snow"
        case 77: return "snow grains"
        case 80, 81, 82: return "rain showers"
        case 85, 86: return "snow showers"
        case 95: return "thunderstorm"
        case 96, 99: return "thunderstorm with hail"
        default: return "unknown"
        }
    }

    /// The C# parses the `time` with `DateTimeOffset.Parse(... AssumeUniversal)`;
    /// on a missing value it substitutes `DateTime.UtcNow.ToString("O")` (i.e.
    /// "now"). Reproduce that: a present, parseable value → that instant;
    /// otherwise → now.
    static func parseTime(_ s: String?) -> Date {
        guard let s, !s.isBlank else { return Date() }
        let d = IntegrationDates.parseUtc(s)
        return d == IntegrationDates.minValue ? Date() : d
    }

    /// Invariant-culture number formatting for the query string (matches
    /// `double.ToString(CultureInfo.InvariantCulture)` — `.` decimal separator,
    /// no thousands separator, trailing zeros trimmed).
    static func num(_ v: Double) -> String {
        if v == v.rounded() && abs(v) < 1e15 {
            // 12.0 → "12" like .NET's default double formatting.
            return String(Int64(v))
        }
        return String(v)
    }

    private static func dbl(_ any: Any) -> Double {
        if let n = any as? NSNumber { return n.doubleValue }
        if let s = any as? String { return Double(s) ?? 0 }
        return 0
    }

    private static func int(_ any: Any) -> Int {
        if let n = any as? NSNumber { return n.intValue }
        if let s = any as? String { return Int(s) ?? -1 }
        return -1
    }
}

// MARK: - OSRM routing

/// OSRM connector config. Port of the C# `OsrmOptions` record.
public struct OsrmOptions: Sendable, Equatable {
    /// OSRM host. Default the public demo server.
    public let host: String
    public init(host: String = "https://router.project-osrm.org") {
        self.host = host
    }
}

/// Open Source Routing Machine (OSRM) `IRoutingProvider`. Port of the C#
/// `OsrmRoutingProvider`.
public final class OsrmRoutingProvider: IRoutingProvider, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: OsrmOptions

    public init(opts: OsrmOptions = OsrmOptions(), http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
    }

    public var providerId: String { "osrm" }

    public func route(
        fromLat: Double, fromLon: Double, toLat: Double, toLon: Double, mode: String
    ) async throws -> RouteEstimate {
        let profile: String
        switch mode {
        case "bike", "bicycle": profile = "bike"
        case "foot", "walk": profile = "foot"
        default: profile = "driving"
        }
        var host = opts.host
        while host.hasSuffix("/") { host.removeLast() } // TrimEnd('/')
        let url = "\(host)/route/v1/\(profile)/"
            + "\(OpenMeteoWeatherProvider.num(fromLon)),\(OpenMeteoWeatherProvider.num(fromLat));"
            + "\(OpenMeteoWeatherProvider.num(toLon)),\(OpenMeteoWeatherProvider.num(toLat))"
            + "?overview=full&geometries=geojson"

        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: url))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)

        let code = IntegrationJson.string(doc, "code")
        if code != "Ok" {
            throw IntegrationError.invalidOperation("OSRM returned code=\(code ?? "")")
        }
        guard let routes = IntegrationJson.array(doc, "routes"), let first = routes.first as? [String: Any] else {
            throw IntegrationError.invalidOperation("OSRM returned no routes.")
        }
        let dist = IntegrationJson.double(first, "distance") ?? 0 // metres
        let dur = IntegrationJson.double(first, "duration") ?? 0  // seconds
        var poly: [RoutePoint] = []
        if let geom = IntegrationJson.object(first, "geometry"), let coords = IntegrationJson.array(geom, "coordinates") {
            for case let pt as [Any] in coords where pt.count >= 2 {
                let lon = (pt[0] as? NSNumber)?.doubleValue ?? 0
                let lat = (pt[1] as? NSNumber)?.doubleValue ?? 0
                poly.append(RoutePoint(lat: lat, lon: lon))
            }
        }
        return RouteEstimate(distanceKm: dist / 1000.0, duration: dur, polyline: poly)
    }
}
