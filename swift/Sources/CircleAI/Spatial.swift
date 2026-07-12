// Spatial.swift
//
// Port of src/CircleAI.Spatial/:
//   • Contracts.cs                 — LatLon, GeoTile, RadarReading,
//                                     RadarReturn, SkyObject, Scene3D;
//                                     IGeoTileSource, IRadarReadout,
//                                     ISkyTracker, I3DSceneRenderer
//   • InMemorySpatial.cs           — InMemoryGeoTileSource (1x1 PNG + registered
//                                     place search), SyntheticRadarReadout
//                                     (deterministic seeded pattern),
//                                     SyntheticSkyTracker (rotation filter),
//                                     JsonScene3DRenderer (minimal GLTF 2.0)
//   • NullImplementations.cs       — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable`. `ReadOnlyMemory<byte>` → `[UInt8]`.
//     GeoTile / Scene3D hold bytes → Sendable + Equatable only.
//   • `System.Random(seed)` → `DotNetRandom(seed:)` (bit-identical .NET RNG,
//     already in the tree), with `next(_:_:)` for `rng.Next(0,5)`.
//   • The radar seed math replicates the C# Int64 arithmetic + XOR-fold to Int32
//     exactly, so the deterministic pattern matches.
//   • `Guid.NewGuid():n` → UUID hex. GLTF script embed uses
//     `JSONSerialization` to JSON-encode the script string (matches
//     `System.Text.Json.JsonSerializer.Serialize(string)`).
//   • Guards → `SpatialError`.

import Foundation

// MARK: - Records

/// A latitude/longitude pair (WGS-84 degrees).
public struct LatLon: Sendable, Equatable, Codable {
    public let latitude: Double
    public let longitude: Double
    public init(latitude: Double, longitude: Double) {
        self.latitude = latitude
        self.longitude = longitude
    }
}

/// A rendered map tile (z/x/y) plus its image bytes and MIME type.
public struct GeoTile: Sendable, Equatable {
    public let z: Int
    public let x: Int
    public let y: Int
    public let imageBytes: [UInt8]
    public let mimeType: String
    public init(z: Int, x: Int, y: Int, imageBytes: [UInt8], mimeType: String) {
        self.z = z
        self.x = x
        self.y = y
        self.imageBytes = imageBytes
        self.mimeType = mimeType
    }
}

/// A single radar return (position + doppler + intensity).
public struct RadarReturn: Sendable, Equatable, Codable {
    public let position: LatLon
    public let dopplerKmh: Double
    public let intensityDbz: Double
    public init(position: LatLon, dopplerKmh: Double, intensityDbz: Double) {
        self.position = position
        self.dopplerKmh = dopplerKmh
        self.intensityDbz = intensityDbz
    }
}

/// A radar sweep centred at a point with a range and a set of returns.
public struct RadarReading: Sendable, Equatable, Codable {
    public let centre: LatLon
    public let rangeKm: Double
    public let returns: [RadarReturn]
    public init(centre: LatLon, rangeKm: Double, returns: [RadarReturn]) {
        self.centre = centre
        self.rangeKm = rangeKm
        self.returns = returns
    }
}

/// A visible sky object (star / planet) with az/alt and apparent magnitude.
public struct SkyObject: Sendable, Equatable, Codable {
    public let name: String
    public let azimuthDeg: Double
    public let altitudeDeg: Double
    public let magnitudeApparent: Double
    public init(name: String, azimuthDeg: Double, altitudeDeg: Double, magnitudeApparent: Double) {
        self.name = name
        self.azimuthDeg = azimuthDeg
        self.altitudeDeg = altitudeDeg
        self.magnitudeApparent = magnitudeApparent
    }
}

/// A rendered 3D scene (encoded bytes + format tag).
public struct Scene3D: Sendable, Equatable {
    public let sceneId: String
    public let encoded: [UInt8]
    public let format: String
    public init(sceneId: String, encoded: [UInt8], format: String) {
        self.sceneId = sceneId
        self.encoded = encoded
        self.format = format
    }
}

// MARK: - Errors

public enum SpatialError: Error, Equatable, CustomStringConvertible {
    case nameRequired
    case coordinateOutOfRange
    case topKOutOfRange
    case rangeKmOutOfRange

    public var description: String {
        switch self {
        case .nameRequired: return "name required"
        case .coordinateOutOfRange: return "z"
        case .topKOutOfRange: return "topK"
        case .rangeKmOutOfRange: return "rangeKm"
        }
    }
}

// MARK: - Contracts

public protocol IGeoTileSource: Sendable {
    var backendId: String { get }
    func getTile(z: Int, x: Int, y: Int) async throws -> GeoTile
    func searchPlaces(query: String, topK: Int) async throws -> [LatLon]
}

public protocol IRadarReadout: Sendable {
    var backendId: String { get }
    func getCurrentReading(at: LatLon, rangeKm: Double) async throws -> RadarReading
}

public protocol ISkyTracker: Sendable {
    var backendId: String { get }
    func visible(at: LatLon, utc: Date) async throws -> [SkyObject]
}

public protocol I3DSceneRenderer: Sendable {
    var backendId: String { get }
    func render(sceneScript: String, format: String) async throws -> Scene3D
}

// MARK: - In-memory geo-tile source

/// Deterministic geo-tile source: returns a 1x1 transparent PNG for every tile
/// and searches a registered gazetteer of place names (case-insensitive
/// substring, ordered ascending by name).
public final class InMemoryGeoTileSource: IGeoTileSource, @unchecked Sendable {
    private let lock = NSLock()
    private var places: [String: LatLon] = [:]

    public init() {
        register("Johannesburg", LatLon(latitude: -26.2041, longitude: 28.0473))
        register("Cape Town", LatLon(latitude: -33.9249, longitude: 18.4241))
        register("Pretoria", LatLon(latitude: -25.7479, longitude: 28.2293))
        register("Durban", LatLon(latitude: -29.8587, longitude: 31.0218))
        register("Lagos", LatLon(latitude: 6.5244, longitude: 3.3792))
        register("Nairobi", LatLon(latitude: -1.2921, longitude: 36.8219))
        register("London", LatLon(latitude: 51.5074, longitude: -0.1278))
        register("New York", LatLon(latitude: 40.7128, longitude: -74.0060))
    }

    public var backendId: String { "in-memory" }

    public func register(_ name: String, _ at: LatLon) {
        lock.lock(); defer { lock.unlock() }
        places[name] = at
    }

    public func getTile(z: Int, x: Int, y: Int) async throws -> GeoTile {
        if z < 0 || x < 0 || y < 0 { throw SpatialError.coordinateOutOfRange }
        // 1x1 transparent PNG.
        let png: [UInt8] = [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ]
        return GeoTile(z: z, x: x, y: y, imageBytes: png, mimeType: "image/png")
    }

    public func searchPlaces(query: String, topK: Int = 5) async throws -> [LatLon] {
        if topK <= 0 { throw SpatialError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let hits = places
            .filter { $0.key.range(of: query, options: .caseInsensitive) != nil }
            .sorted { $0.key < $1.key }
            .prefix(topK)
            .map { $0.value }
        return Array(hits)
    }
}

// MARK: - Synthetic radar

/// Deterministic radar readout — the return pattern is fully determined by the
/// coordinates and range, seeded via the byte-identical .NET RNG so results are
/// reproducible.
public struct SyntheticRadarReadout: IRadarReadout {
    public init() {}
    public var backendId: String { "synthetic" }

    public func getCurrentReading(at: LatLon, rangeKm: Double = 50) async throws -> RadarReading {
        if rangeKm <= 0 { throw SpatialError.rangeKmOutOfRange }

        // Deterministic seed — replicates the C# Int64 arithmetic + XOR-fold.
        let seed: Int64 = Int64(at.latitude * 1000) + Int64(at.longitude * 1000) + Int64(rangeKm * 10)
        let seed32 = Int32(truncatingIfNeeded: seed ^ (seed >> 32))
        var rng = DotNetRandom(seed: seed32)

        let count = 3 + rng.next(0, 5)
        var returns: [RadarReturn] = []
        returns.reserveCapacity(count)
        for _ in 0..<count {
            let d = rng.nextDouble() * rangeKm * 0.9
            let ang = rng.nextDouble() * Double.pi * 2
            let lat = at.latitude + (cos(ang) * d) / 111.0
            let lon = at.longitude + (sin(ang) * d) / 111.0
            returns.append(RadarReturn(
                position: LatLon(latitude: lat, longitude: lon),
                dopplerKmh: rng.nextDouble() * 60 - 30,
                intensityDbz: rng.nextDouble() * 60))
        }
        return RadarReading(centre: at, rangeKm: rangeKm, returns: returns)
    }
}

// MARK: - Synthetic sky tracker

/// Deterministic sky tracker — a fixed catalogue of bright objects rotated by
/// time-of-day and filtered by a crude altitude/latitude visibility rule.
public struct SyntheticSkyTracker: ISkyTracker {
    private static let baseObjects: [(name: String, azimuth: Double, altitude: Double, mag: Double)] = [
        ("Sirius", 102.7, 35.0, -1.46),
        ("Polaris", 0.0, 51.5, 1.97),
        ("Vega", 88.0, 70.0, 0.03),
        ("Mars", 135.4, 22.0, 0.5),
        ("Jupiter", 180.5, 40.0, -2.0),
        ("Saturn", 210.0, 30.0, 0.4),
    ]

    public init() {}
    public var backendId: String { "synthetic" }

    public func visible(at: LatLon, utc: Date) async throws -> [SkyObject] {
        // hours = fractional hours since UTC midnight of `utc`.
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let comps = cal.dateComponents([.hour, .minute, .second, .nanosecond], from: utc)
        let hH  = Double(comps.hour ?? 0)
        let hM  = Double(comps.minute ?? 0) / 60.0
        let hS  = Double(comps.second ?? 0) / 3600.0
        let hNs = Double(comps.nanosecond ?? 0) / 3_600_000_000_000.0
        let hours = hH + hM + hS + hNs
        let rot = hours * 15.0 // earth rotation degrees-per-hour

        var hits: [SkyObject] = []
        for (n, az, alt, mag) in SyntheticSkyTracker.baseObjects {
            let az2 = (az - rot + 360).truncatingRemainder(dividingBy: 360)
            if alt - abs(at.latitude) > 0 {
                hits.append(SkyObject(name: n, azimuthDeg: az2, altitudeDeg: alt, magnitudeApparent: mag))
            }
        }
        return hits
    }
}

// MARK: - JSON 3D-scene renderer

/// Wraps a scene script into a minimal valid GLTF 2.0 JSON document (the script
/// is stored as a JSON-escaped `extras.script` blob).
public struct JsonScene3DRenderer: I3DSceneRenderer {
    public init() {}
    public var backendId: String { "json" }

    public func render(sceneScript: String, format: String = "gltf") async throws -> Scene3D {
        let fmt = format.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "gltf" : format
        let sceneId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()

        // JSON-encode the script string exactly as System.Text.Json would.
        let scriptData = (try? JSONSerialization.data(withJSONObject: sceneScript, options: [.fragmentsAllowed])) ?? Data("\"\"".utf8)
        let scriptLiteral = String(decoding: scriptData, as: UTF8.self)

        let json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"CircleAI.Spatial.JsonScene3DRenderer\"},\"scenes\":[{\"nodes\":[]}],\"scene\":0,\"extras\":{\"script\":\(scriptLiteral)}}"
        return Scene3D(sceneId: sceneId, encoded: Array(json.utf8), format: fmt)
    }
}

// MARK: - Null backends

public struct NullGeoTileSource: IGeoTileSource {
    public static let instance = NullGeoTileSource()
    public init() {}
    public var backendId: String { "null" }
    public func getTile(z: Int, x: Int, y: Int) async throws -> GeoTile {
        GeoTile(z: z, x: x, y: y, imageBytes: [], mimeType: "image/png")
    }
    public func searchPlaces(query: String, topK: Int = 5) async throws -> [LatLon] { [] }
}

public struct NullRadarReadout: IRadarReadout {
    public static let instance = NullRadarReadout()
    public init() {}
    public var backendId: String { "null" }
    public func getCurrentReading(at: LatLon, rangeKm: Double = 50) async throws -> RadarReading {
        RadarReading(centre: at, rangeKm: rangeKm, returns: [])
    }
}

public struct NullSkyTracker: ISkyTracker {
    public static let instance = NullSkyTracker()
    public init() {}
    public var backendId: String { "null" }
    public func visible(at: LatLon, utc: Date) async throws -> [SkyObject] { [] }
}

public struct Null3DSceneRenderer: I3DSceneRenderer {
    public static let instance = Null3DSceneRenderer()
    public init() {}
    public var backendId: String { "null" }
    public func render(sceneScript: String, format: String = "gltf") async throws -> Scene3D {
        Scene3D(sceneId: "00000000-0000-0000-0000-000000000000", encoded: [], format: format)
    }
}
