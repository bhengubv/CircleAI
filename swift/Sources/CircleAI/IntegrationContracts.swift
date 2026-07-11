// IntegrationContracts.swift
//
// Port of the remaining external-integration abstractions from
// src/CircleAI.Integration/Contracts.cs that are NOT already defined by
// ProactiveBriefing.swift.
//
// ProactiveBriefing.swift already ports the "briefing-relevant" slice of
// Contracts.cs — the DTOs `CalendarEvent`, `EmailMessage`, `NewsItem`,
// `WeatherSample` and the protocols `ICalendarConnector`, `IEmailConnector`,
// `INewsSource`, `IWeatherProvider` (in their read-only briefing shape). To
// avoid duplicate declarations, those live there and are NOT redeclared here.
// This file adds the parts of Contracts.cs that had no prior Swift home:
//   DTOs       — RoutePoint (the C# `(double Lat, double Lon)` tuple element),
//                RouteEstimate, HaEntity, IntegrationServiceArg.
//   Interfaces — IRoutingProvider, IHomeAutomationConnector.
//
// The three connector protocols that already exist are widened to the full C#
// surface (createEvent/deleteEvent, search/markRead, hourly) at their original
// declaration site in ProactiveBriefing.swift — see the "full-surface" MARK
// there — so this port stays additive and does not fork the type.
//
// Porting notes:
//   • `TimeSpan Duration` → `TimeInterval` (seconds), matching the tree-wide
//     convention.
//   • The C# `(double Lat, double Lon)` polyline tuple is a named `RoutePoint`
//     struct so `RouteEstimate` stays Codable/Equatable.
//   • `IReadOnlyDictionary<string,string> Attributes` → `[String: String]`.
//   • `CallServiceAsync`'s `IReadOnlyDictionary<string, object?>? data` → an
//     ordered `[IntegrationServiceArg]` (a Swift `Dictionary` is unordered and
//     `object?` is not `Sendable`; the connector serialises these to JSON).
//   • The C# `CancellationToken ct = default` parameter has no Swift analogue
//     and is dropped, matching the Telephony port.

import Foundation

// MARK: - Routing / traffic

/// One point on a route polyline — the Swift form of the C# `(double Lat,
/// double Lon)` tuple element.
public struct RoutePoint: Sendable, Equatable, Codable {
    /// Latitude.
    public let lat: Double
    /// Longitude.
    public let lon: Double
    public init(lat: Double, lon: Double) {
        self.lat = lat
        self.lon = lon
    }
}

/// A routing estimate. Port of the C# `RouteEstimate` record.
public struct RouteEstimate: Sendable, Equatable, Codable {
    /// Route distance (km).
    public let distanceKm: Double
    /// Estimated travel duration, seconds (C# `TimeSpan`).
    public let duration: TimeInterval
    /// Route geometry as ordered lat/lon points.
    public let polyline: [RoutePoint]

    public init(distanceKm: Double, duration: TimeInterval, polyline: [RoutePoint]) {
        self.distanceKm = distanceKm
        self.duration = duration
        self.polyline = polyline
    }
}

/// A routing / traffic provider. Port of the C# `IRoutingProvider`.
public protocol IRoutingProvider: AnyObject, Sendable {
    /// Stable provider identifier (e.g. "osrm").
    var providerId: String { get }
    /// Route between two coordinates. `mode` is "car" | "bike"/"bicycle" |
    /// "foot"/"walk"; defaults to "car".
    func route(
        fromLat: Double, fromLon: Double, toLat: Double, toLon: Double, mode: String
    ) async throws -> RouteEstimate
}

public extension IRoutingProvider {
    /// Overload matching the C# default `mode = "car"`.
    func route(fromLat: Double, fromLon: Double, toLat: Double, toLon: Double) async throws -> RouteEstimate {
        try await route(fromLat: fromLat, fromLon: fromLon, toLat: toLat, toLon: toLon, mode: "car")
    }
}

// MARK: - Home automation

/// A home-automation entity. Port of the C# `HaEntity` record.
public struct HaEntity: Sendable, Equatable, Codable {
    /// Entity identifier (e.g. "light.kitchen").
    public let entityId: String
    /// Friendly display name.
    public let friendlyName: String
    /// Entity domain (e.g. "light").
    public let domain: String
    /// Current state (e.g. "on").
    public let state: String
    /// Extra attributes as string values.
    public let attributes: [String: String]

    public init(
        entityId: String,
        friendlyName: String,
        domain: String,
        state: String,
        attributes: [String: String]
    ) {
        self.entityId = entityId
        self.friendlyName = friendlyName
        self.domain = domain
        self.state = state
        self.attributes = attributes
    }
}

/// One argument passed to a home-automation service call — the ordered Swift
/// form of a `IReadOnlyDictionary<string, object?>` entry. `value` is JSON
/// (string / number / bool / array) so the connector can serialise it.
public struct IntegrationServiceArg: Sendable, Equatable {
    /// Argument name.
    public let name: String
    /// Argument value.
    public let value: IntegrationJsonValue
    public init(_ name: String, _ value: IntegrationJsonValue) {
        self.name = name
        self.value = value
    }
}

/// A home-automation connector. Port of the C# `IHomeAutomationConnector`.
public protocol IHomeAutomationConnector: AnyObject, Sendable {
    /// Stable provider identifier (e.g. "home-assistant").
    var providerId: String { get }
    /// Whether the connector has the config it needs to operate.
    var isConfigured: Bool { get }
    /// List all known entities.
    func listEntities() async throws -> [HaEntity]
    /// Call a service on a domain with the given (ordered) arguments.
    func callService(domain: String, service: String, data: [IntegrationServiceArg]?) async throws
}
