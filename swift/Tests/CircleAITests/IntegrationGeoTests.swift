// IntegrationGeoTests.swift
//
// Exercises the OpenMeteo weather provider and the OSRM routing provider
// against FakeIntegrationHttpTransport, plus the pure WMO decode table and the
// unit conversions. Mirrors src/CircleAI.Integration.Geo/.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationGeoTests: XCTestCase {

    // ── Open-Meteo ───────────────────────────────────────────────────────────

    func testOpenMeteoProviderId() {
        XCTAssertEqual(OpenMeteoWeatherProvider(http: FakeIntegrationHttpTransport()).providerId, "open-meteo")
    }

    func testOpenMeteoCurrentParsesAndConvertsWind() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/v1/forecast", json: """
        {"current":{"time":"2024-10-02T12:00:00Z","temperature_2m":18.0,"apparent_temperature":17.0,
          "precipitation":0.5,"wind_speed_10m":10.0,"cloud_cover":40,"weather_code":61}}
        """)
        let s = try await OpenMeteoWeatherProvider(http: http).current(lat: -26.2, lon: 28.04)
        XCTAssertEqual(s.tempC, 18.0)
        XCTAssertEqual(s.feelsLikeC, 17.0)
        XCTAssertEqual(s.precipMm, 0.5)
        XCTAssertEqual(s.windKph, 36.0, accuracy: 0.0001) // 10 m/s * 3.6
        XCTAssertEqual(s.cloudPct, 40)
        XCTAssertEqual(s.condition, "rain") // code 61
        XCTAssertNotEqual(s.atUtc, Date.distantPast)
        // query carried invariant-culture coords + current fields
        XCTAssertTrue(http.lastRequest?.url.contains("latitude=") ?? false)
        XCTAssertTrue(http.lastRequest?.url.contains("current=temperature_2m") ?? false)
    }

    func testOpenMeteoHourlyLimitsAndConverts() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/v1/forecast", json: """
        {"hourly":{
          "time":["2024-10-02T00:00:00Z","2024-10-02T01:00:00Z","2024-10-02T02:00:00Z"],
          "temperature_2m":[10,11,12],"apparent_temperature":[9,10,11],
          "precipitation":[0,0,1],"wind_speed_10m":[1,2,3],
          "cloud_cover":[10,20,30],"weather_code":[0,3,95]}}
        """)
        // Ask for 2 hours; response has 3 samples → min(3,2)=2.
        let samples = try await OpenMeteoWeatherProvider(http: http).hourly(lat: 0, lon: 0, hours: 2)
        XCTAssertEqual(samples.count, 2)
        XCTAssertEqual(samples[0].condition, "clear sky") // 0
        XCTAssertEqual(samples[1].condition, "partly cloudy") // 3
        XCTAssertEqual(samples[1].windKph, 2.0 * 3.6, accuracy: 0.0001)
        XCTAssertTrue(http.lastRequest?.url.contains("forecast_hours=2") ?? false)
    }

    func testOpenMeteoHourlyValidatesHours() async {
        let c = OpenMeteoWeatherProvider(http: FakeIntegrationHttpTransport())
        for bad in [0, -1, 169] {
            do { _ = try await c.hourly(lat: 0, lon: 0, hours: bad); XCTFail("hours=\(bad)") }
            catch IntegrationError.argumentOutOfRange {} catch { XCTFail("wrong \(error)") }
        }
    }

    func testWmoDecodeTable() {
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(0), "clear sky")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(2), "partly cloudy")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(48), "fog")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(55), "drizzle")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(65), "rain")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(75), "snow")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(82), "rain showers")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(95), "thunderstorm")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(99), "thunderstorm with hail")
        XCTAssertEqual(OpenMeteoWeatherProvider.wmoDecode(7), "unknown")
    }

    // ── OSRM ─────────────────────────────────────────────────────────────────

    func testOsrmProviderId() {
        XCTAssertEqual(OsrmRoutingProvider(http: FakeIntegrationHttpTransport()).providerId, "osrm")
    }

    func testOsrmRouteParsesDistanceDurationAndPolyline() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/route/v1/driving/", json: """
        {"code":"Ok","routes":[{"distance":12000,"duration":900,
          "geometry":{"coordinates":[[28.0,-26.2],[28.1,-26.3]]}}]}
        """)
        let est = try await OsrmRoutingProvider(http: http).route(
            fromLat: -26.2, fromLon: 28.0, toLat: -26.3, toLon: 28.1)
        XCTAssertEqual(est.distanceKm, 12.0, accuracy: 0.0001)   // 12000 m / 1000
        XCTAssertEqual(est.duration, 900)                        // seconds
        XCTAssertEqual(est.polyline.count, 2)
        // GeoJSON is [lon, lat]; RoutePoint stores (lat, lon).
        XCTAssertEqual(est.polyline[0].lat, -26.2, accuracy: 0.0001)
        XCTAssertEqual(est.polyline[0].lon, 28.0, accuracy: 0.0001)
    }

    func testOsrmProfileMappingForModes() async throws {
        for (mode, profile) in [("bike", "bike"), ("bicycle", "bike"), ("foot", "foot"), ("walk", "foot"), ("car", "driving")] {
            let http = FakeIntegrationHttpTransport()
            http.on(.get, urlContains: "/route/v1/", json: #"{"code":"Ok","routes":[{"distance":0,"duration":0}]}"#)
            _ = try await OsrmRoutingProvider(http: http).route(fromLat: 0, fromLon: 0, toLat: 1, toLon: 1, mode: mode)
            XCTAssertTrue(http.lastRequest?.url.contains("/route/v1/\(profile)/") ?? false, "mode=\(mode)")
        }
    }

    func testOsrmThrowsWhenCodeNotOk() async {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/route/v1/", json: #"{"code":"NoRoute","routes":[]}"#)
        do {
            _ = try await OsrmRoutingProvider(http: http).route(fromLat: 0, fromLon: 0, toLat: 1, toLon: 1)
            XCTFail("expected throw")
        } catch IntegrationError.invalidOperation { /* ok */ } catch { XCTFail("wrong \(error)") }
    }

    func testOsrmHostTrailingSlashTrimmed() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/route/v1/", json: #"{"code":"Ok","routes":[{"distance":0,"duration":0}]}"#)
        _ = try await OsrmRoutingProvider(opts: OsrmOptions(host: "https://osrm.local/"), http: http)
            .route(fromLat: 0, fromLon: 0, toLat: 1, toLon: 1)
        // No double slash before /route.
        XCTAssertFalse(http.lastRequest?.url.contains("//route") ?? true)
        XCTAssertTrue(http.lastRequest?.url.hasPrefix("https://osrm.local/route/v1/") ?? false)
    }
}
