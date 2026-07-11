// Spatial.kt
//
// Kotlin port of CircleAI.Spatial (Contracts.cs + InMemorySpatial.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Geo tiles +
// place search, synthetic radar, synthetic sky tracking, and a JSON (GLTF)
// 3D-scene renderer.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ReadOnlyMemory<byte>` -> `ByteArray`.
//   * C# `DateTimeOffset` -> `java.time.Instant`; C# `Random(int)` -> `java.util.Random(long)`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * The synthetic radar seeds a Random from the coordinates so tests are
//     deterministic; the sky tracker filters by a daily-rotation altitude check.
//   * SearchPlacesAsync filters by name substring (OrdinalIgnoreCase), orders by
//     name, takes topK.

package com.bhengubv.circleai.spatial

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive
import java.time.Instant
import java.time.ZoneOffset
import java.util.Random
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.sin

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A latitude/longitude pair. Mirrors C# `LatLon`. */
data class LatLon(val latitude: Double, val longitude: Double)

/** A single map tile. Mirrors C# `GeoTile`. */
data class GeoTile(val z: Int, val x: Int, val y: Int, val imageBytes: ByteArray, val mimeType: String)

/** A single radar return. Mirrors C# `RadarReturn`. */
data class RadarReturn(val position: LatLon, val dopplerKmh: Double, val intensityDbz: Double)

/** A radar reading over a centre + range. Mirrors C# `RadarReading`. */
data class RadarReading(val centre: LatLon, val rangeKm: Double, val returns: List<RadarReturn>)

/** A visible sky object. Mirrors C# `SkyObject`. */
data class SkyObject(val name: String, val azimuthDeg: Double, val altitudeDeg: Double, val magnitudeApparent: Double)

/** An encoded 3D scene. Mirrors C# `Scene3D`. */
data class Scene3D(val sceneId: String, val encoded: ByteArray, val format: String)

/** Map-tile source (deck.gl / cesium pattern). Mirrors C# `IGeoTileSource`. */
interface IGeoTileSource {
    val backendId: String
    suspend fun getTileAsync(z: Int, x: Int, y: Int): GeoTile
    suspend fun searchPlacesAsync(query: String, topK: Int = 5): List<LatLon>
}

/** Weather / surveillance radar (RADAR pattern). Mirrors C# `IRadarReadout`. */
interface IRadarReadout {
    val backendId: String
    suspend fun getCurrentReadingAsync(at: LatLon, rangeKm: Double = 50.0): RadarReading
}

/** Visible-sky tracking (skylight pattern). Mirrors C# `ISkyTracker`. */
interface ISkyTracker {
    val backendId: String
    suspend fun visibleAsync(at: LatLon, utc: Instant): List<SkyObject>
}

/** 3D-scene rendering hook (flame / anime pattern). Mirrors C# `I3DSceneRenderer`. */
interface I3DSceneRenderer {
    val backendId: String
    suspend fun renderAsync(sceneScript: String, format: String = "gltf"): Scene3D
}

// =====================================================================
// In-memory implementations (InMemorySpatial.cs)
// =====================================================================

/** In-memory geo tile source with registered places. Mirrors C# `InMemoryGeoTileSource`. */
class InMemoryGeoTileSource : IGeoTileSource {
    private val places = ConcurrentHashMap<String, LatLon>()

    init {
        register("Johannesburg", LatLon(-26.2041, 28.0473))
        register("Cape Town", LatLon(-33.9249, 18.4241))
        register("Pretoria", LatLon(-25.7479, 28.2293))
        register("Durban", LatLon(-29.8587, 31.0218))
        register("Lagos", LatLon(6.5244, 3.3792))
        register("Nairobi", LatLon(-1.2921, 36.8219))
        register("London", LatLon(51.5074, -0.1278))
        register("New York", LatLon(40.7128, -74.0060))
    }

    override val backendId: String get() = "in-memory"

    fun register(name: String, at: LatLon) {
        require(name.isNotBlank()) { "name required" }
        places[name] = at
    }

    override suspend fun getTileAsync(z: Int, x: Int, y: Int): GeoTile {
        if (z < 0 || x < 0 || y < 0) throw IndexOutOfBoundsException("z")
        // 1x1 transparent PNG.
        val pngBytes = byteArrayOf(
            0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4.toByte(),
            0x89.toByte(), 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C.toByte(), 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4.toByte(), 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE.toByte(),
            0x42, 0x60, 0x82.toByte(),
        )
        return GeoTile(z, x, y, pngBytes, "image/png")
    }

    override suspend fun searchPlacesAsync(query: String, topK: Int): List<LatLon> {
        if (topK <= 0) throw IndexOutOfBoundsException("topK")
        return places.entries
            .filter { it.key.contains(query, ignoreCase = true) }
            .sortedBy { it.key }
            .take(topK)
            .map { it.value }
    }
}

/** Deterministic synthetic radar. Mirrors C# `SyntheticRadarReadout`. */
class SyntheticRadarReadout : IRadarReadout {
    override val backendId: String get() = "synthetic"

    override suspend fun getCurrentReadingAsync(at: LatLon, rangeKm: Double): RadarReading {
        if (rangeKm <= 0) throw IndexOutOfBoundsException("rangeKm")

        val seed = (at.latitude * 1000).toLong() + (at.longitude * 1000).toLong() + (rangeKm * 10).toLong()
        val rng = Random((seed xor (seed shr 32)))
        val count = 3 + rng.nextInt(5)
        val rets = ArrayList<RadarReturn>(count)
        for (i in 0 until count) {
            val d = rng.nextDouble() * rangeKm * 0.9
            val ang = rng.nextDouble() * Math.PI * 2
            val lat = at.latitude + (cos(ang) * d) / 111.0
            val lon = at.longitude + (sin(ang) * d) / 111.0
            rets.add(RadarReturn(LatLon(lat, lon), rng.nextDouble() * 60 - 30, rng.nextDouble() * 60))
        }
        return RadarReading(at, rangeKm, rets)
    }
}

/** Deterministic synthetic sky tracker. Mirrors C# `SyntheticSkyTracker`. */
class SyntheticSkyTracker : ISkyTracker {
    override val backendId: String get() = "synthetic"

    override suspend fun visibleAsync(at: LatLon, utc: Instant): List<SkyObject> {
        val tod = utc.atOffset(ZoneOffset.UTC).toLocalTime()
        val hours = tod.toNanoOfDay() / 3_600_000_000_000.0
        val rot = hours * 15.0 // earth rotation degrees-per-hour
        val hits = ArrayList<SkyObject>(BASE_OBJECTS.size)
        for ((n, az, alt, mag) in BASE_OBJECTS) {
            val az2 = (az - rot + 360) % 360
            if (alt - abs(at.latitude) > 0) {
                hits.add(SkyObject(n, az2, alt, mag))
            }
        }
        return hits
    }

    private companion object {
        val BASE_OBJECTS = arrayOf(
            SkyBase("Sirius", 102.7, 35.0, -1.46),
            SkyBase("Polaris", 0.0, 51.5, 1.97),
            SkyBase("Vega", 88.0, 70.0, 0.03),
            SkyBase("Mars", 135.4, 22.0, 0.5),
            SkyBase("Jupiter", 180.5, 40.0, -2.0),
            SkyBase("Saturn", 210.0, 30.0, 0.4),
        )
    }

    private data class SkyBase(val name: String, val azimuth: Double, val altitude: Double, val mag: Double)
}

/** Minimal-GLTF JSON 3D-scene renderer. Mirrors C# `JsonScene3DRenderer`. */
class JsonScene3DRenderer : I3DSceneRenderer {
    override val backendId: String get() = "json"

    override suspend fun renderAsync(sceneScript: String, format: String): Scene3D {
        val fmt = format.ifBlank { "gltf" }
        val sceneId = UUID.randomUUID().toString().replace("-", "")
        val escapedScript = Json.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(sceneScript))
        val json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"CircleAI.Spatial.JsonScene3DRenderer\"}," +
            "\"scenes\":[{\"nodes\":[]}],\"scene\":0,\"extras\":{\"script\":$escapedScript}}"
        return Scene3D(sceneId, json.toByteArray(Charsets.UTF_8), fmt)
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

private const val SPATIAL_EMPTY_GUID = "00000000-0000-0000-0000-000000000000"

/** No-op [IGeoTileSource]. Mirrors C# `NullGeoTileSource`. */
class NullGeoTileSource private constructor() : IGeoTileSource {
    override val backendId: String get() = "null"
    override suspend fun getTileAsync(z: Int, x: Int, y: Int): GeoTile =
        GeoTile(z, x, y, ByteArray(0), "image/png")
    override suspend fun searchPlacesAsync(query: String, topK: Int): List<LatLon> = emptyList()

    companion object {
        val Instance = NullGeoTileSource()
    }
}

/** No-op [IRadarReadout]. Mirrors C# `NullRadarReadout`. */
class NullRadarReadout private constructor() : IRadarReadout {
    override val backendId: String get() = "null"
    override suspend fun getCurrentReadingAsync(at: LatLon, rangeKm: Double): RadarReading =
        RadarReading(at, rangeKm, emptyList())

    companion object {
        val Instance = NullRadarReadout()
    }
}

/** No-op [ISkyTracker]. Mirrors C# `NullSkyTracker`. */
class NullSkyTracker private constructor() : ISkyTracker {
    override val backendId: String get() = "null"
    override suspend fun visibleAsync(at: LatLon, utc: Instant): List<SkyObject> = emptyList()

    companion object {
        val Instance = NullSkyTracker()
    }
}

/** No-op [I3DSceneRenderer]. Mirrors C# `Null3DSceneRenderer`. */
class Null3DSceneRenderer private constructor() : I3DSceneRenderer {
    override val backendId: String get() = "null"
    override suspend fun renderAsync(sceneScript: String, format: String): Scene3D =
        Scene3D(SPATIAL_EMPTY_GUID, ByteArray(0), format)

    companion object {
        val Instance = Null3DSceneRenderer()
    }
}
