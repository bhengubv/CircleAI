// spatial/index.ts
//
// Full-parity port of CircleAI.Spatial (C#). C# is the exact spec.
//
// Spatial / geo contract surface: IGeoTileSource / IRadarReadout / ISkyTracker /
// I3DSceneRenderer, the LatLon / GeoTile / RadarReading / RadarReturn /
// SkyObject / Scene3D records, deterministic synthetic implementations, and the
// Null* defaults.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   double                                → number
//   ReadOnlyMemory<byte> ImageBytes       → Uint8Array
//   IReadOnlyList<T>                       → readonly T[]
//   ValueTask<T>                          → Promise<T>
//
// Determinism note: the C# radar uses System.Random (not reproducible across
// runtimes). The port uses a deterministic LCG seeded identically-in-spirit so
// output is stable within this runtime — count in [3,8), returns within range.

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A latitude/longitude pair. Mirrors C# `LatLon`. */
export interface LatLon {
  readonly latitude: number;
  readonly longitude: number;
}

/** Constructs a {@link LatLon}. */
export function latLon(latitude: number, longitude: number): LatLon {
  return { latitude, longitude };
}

/** A map tile. Mirrors C# `GeoTile`. */
export interface GeoTile {
  readonly z: number;
  readonly x: number;
  readonly y: number;
  readonly imageBytes: Uint8Array;
  readonly mimeType: string;
}

/** Constructs a {@link GeoTile}. */
export function geoTile(z: number, x: number, y: number, imageBytes: Uint8Array, mimeType: string): GeoTile {
  return { z, x, y, imageBytes, mimeType };
}

/** A radar return. Mirrors C# `RadarReturn`. */
export interface RadarReturn {
  readonly position: LatLon;
  readonly dopplerKmh: number;
  readonly intensityDbz: number;
}

/** Constructs a {@link RadarReturn}. */
export function radarReturn(position: LatLon, dopplerKmh: number, intensityDbz: number): RadarReturn {
  return { position, dopplerKmh, intensityDbz };
}

/** A radar reading. Mirrors C# `RadarReading`. */
export interface RadarReading {
  readonly centre: LatLon;
  readonly rangeKm: number;
  readonly returns: readonly RadarReturn[];
}

/** Constructs a {@link RadarReading}. */
export function radarReading(centre: LatLon, rangeKm: number, returns: readonly RadarReturn[]): RadarReading {
  return { centre, rangeKm, returns };
}

/** A visible-sky object. Mirrors C# `SkyObject`. */
export interface SkyObject {
  readonly name: string;
  readonly azimuthDeg: number;
  readonly altitudeDeg: number;
  readonly magnitudeApparent: number;
}

/** Constructs a {@link SkyObject}. */
export function skyObject(name: string, azimuthDeg: number, altitudeDeg: number, magnitudeApparent: number): SkyObject {
  return { name, azimuthDeg, altitudeDeg, magnitudeApparent };
}

/** A rendered 3D scene. Mirrors C# `Scene3D`. */
export interface Scene3D {
  readonly sceneId: string;
  readonly encoded: Uint8Array;
  readonly format: string;
}

/** Constructs a {@link Scene3D}. */
export function scene3D(sceneId: string, encoded: Uint8Array, format: string): Scene3D {
  return { sceneId, encoded, format };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Map-tile source. Mirrors C# `IGeoTileSource`. */
export interface IGeoTileSource {
  readonly backendId: string;
  getTileAsync(z: number, x: number, y: number): Promise<GeoTile>;
  searchPlacesAsync(query: string, topK?: number): Promise<readonly LatLon[]>;
}

/** Weather / surveillance radar. Mirrors C# `IRadarReadout`. */
export interface IRadarReadout {
  readonly backendId: string;
  getCurrentReadingAsync(at: LatLon, rangeKm?: number): Promise<RadarReading>;
}

/** Visible-sky tracking. Mirrors C# `ISkyTracker`. */
export interface ISkyTracker {
  readonly backendId: string;
  visibleAsync(at: LatLon, utc: Date): Promise<readonly SkyObject[]>;
}

/** 3D-scene rendering hook. Mirrors C# `I3DSceneRenderer`. */
export interface I3DSceneRenderer {
  readonly backendId: string;
  renderAsync(sceneScript: string, format?: string): Promise<Scene3D>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory / synthetic implementations
// ─────────────────────────────────────────────────────────────────────────────

const TRANSPARENT_PNG_1X1 = new Uint8Array([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00,
  0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x0d, 0x49,
  0x44, 0x41, 0x54, 0x78, 0x9c, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00, 0x00,
  0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
]);

/** In-memory tile source + place index. Mirrors C# `InMemoryGeoTileSource`. */
export class InMemoryGeoTileSource implements IGeoTileSource {
  private readonly places = new Map<string, { name: string; at: LatLon }>();

  constructor() {
    this.register("Johannesburg", latLon(-26.2041, 28.0473));
    this.register("Cape Town", latLon(-33.9249, 18.4241));
    this.register("Pretoria", latLon(-25.7479, 28.2293));
    this.register("Durban", latLon(-29.8587, 31.0218));
    this.register("Lagos", latLon(6.5244, 3.3792));
    this.register("Nairobi", latLon(-1.2921, 36.8219));
    this.register("London", latLon(51.5074, -0.1278));
    this.register("New York", latLon(40.7128, -74.006));
  }

  get backendId(): string {
    return "in-memory";
  }

  register(name: string, at: LatLon): void {
    if (name == null || name.trim().length === 0) throw new Error("name required");
    this.places.set(name.toLowerCase(), { name, at });
  }

  async getTileAsync(z: number, x: number, y: number): Promise<GeoTile> {
    if (z < 0 || x < 0 || y < 0) throw new Error("z out of range");
    return geoTile(z, x, y, TRANSPARENT_PNG_1X1, "image/png");
  }

  async searchPlacesAsync(query: string, topK = 5): Promise<readonly LatLon[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK out of range");
    const q = query.toLowerCase();
    return [...this.places.values()]
      .filter((p) => p.name.toLowerCase().includes(q))
      .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
      .slice(0, topK)
      .map((p) => p.at);
  }
}

/** Deterministic synthetic radar. Mirrors C# `SyntheticRadarReadout`. */
export class SyntheticRadarReadout implements IRadarReadout {
  get backendId(): string {
    return "synthetic";
  }

  async getCurrentReadingAsync(at: LatLon, rangeKm = 50): Promise<RadarReading> {
    if (at == null) throw new Error("at required");
    if (rangeKm <= 0) throw new Error("rangeKm out of range");
    // Deterministic pattern based on coordinates so tests can assert against it.
    const seed = Math.trunc(at.latitude * 1000) + Math.trunc(at.longitude * 1000) + Math.trunc(rangeKm * 10);
    const rng = new Lcg(seed);
    const count = 3 + rng.nextInt(5);
    const rets: RadarReturn[] = [];
    for (let i = 0; i < count; i++) {
      const d = rng.nextDouble() * rangeKm * 0.9;
      const ang = rng.nextDouble() * Math.PI * 2;
      const lat = at.latitude + (Math.cos(ang) * d) / 111;
      const lon = at.longitude + (Math.sin(ang) * d) / 111;
      rets.push(radarReturn(latLon(lat, lon), rng.nextDouble() * 60 - 30, rng.nextDouble() * 60));
    }
    return radarReading(at, rangeKm, rets);
  }
}

const SKY_BASE_OBJECTS: ReadonlyArray<{ name: string; azimuth: number; altitude: number; mag: number }> = [
  { name: "Sirius", azimuth: 102.7, altitude: 35.0, mag: -1.46 },
  { name: "Polaris", azimuth: 0.0, altitude: 51.5, mag: 1.97 },
  { name: "Vega", azimuth: 88.0, altitude: 70.0, mag: 0.03 },
  { name: "Mars", azimuth: 135.4, altitude: 22.0, mag: 0.5 },
  { name: "Jupiter", azimuth: 180.5, altitude: 40.0, mag: -2.0 },
  { name: "Saturn", azimuth: 210.0, altitude: 30.0, mag: 0.4 },
];

/** Deterministic synthetic sky tracker. Mirrors C# `SyntheticSkyTracker`. */
export class SyntheticSkyTracker implements ISkyTracker {
  get backendId(): string {
    return "synthetic";
  }

  async visibleAsync(at: LatLon, utc: Date): Promise<readonly SkyObject[]> {
    if (at == null) throw new Error("at required");
    // Visibility filter (matches C#): altitude - |lat| > 0 after daily rotation.
    const hours =
      utc.getUTCHours() + utc.getUTCMinutes() / 60 + utc.getUTCSeconds() / 3600 + utc.getUTCMilliseconds() / 3_600_000;
    const rot = hours * 15.0; // earth rotation degrees-per-hour
    const hits: SkyObject[] = [];
    for (const o of SKY_BASE_OBJECTS) {
      const az2 = ((o.azimuth - rot) % 360 + 360) % 360;
      if (o.altitude - Math.abs(at.latitude) > 0) {
        hits.push(skyObject(o.name, az2, o.altitude, o.mag));
      }
    }
    return hits;
  }
}

/** Minimal-valid-GLTF 3D scene renderer. Mirrors C# `JsonScene3DRenderer`. */
export class JsonScene3DRenderer implements I3DSceneRenderer {
  get backendId(): string {
    return "json";
  }

  async renderAsync(sceneScript: string, format = "gltf"): Promise<Scene3D> {
    if (sceneScript == null) throw new Error("sceneScript required");
    if (format == null || format.trim().length === 0) format = "gltf";
    const sceneId = newGuidN();
    const json =
      `{"asset":{"version":"2.0","generator":"CircleAI.Spatial.JsonScene3DRenderer"},` +
      `"scenes":[{"nodes":[]}],"scene":0,"extras":{"script":${JSON.stringify(sceneScript)}}}`;
    return scene3D(sceneId, utf8(json), format);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link IGeoTileSource}. */
export class NullGeoTileSource implements IGeoTileSource {
  static readonly instance = new NullGeoTileSource();
  get backendId(): string {
    return "null";
  }
  async getTileAsync(z: number, x: number, y: number): Promise<GeoTile> {
    return geoTile(z, x, y, new Uint8Array(0), "image/png");
  }
  async searchPlacesAsync(): Promise<readonly LatLon[]> {
    return [];
  }
}

/** Fail-safe {@link IRadarReadout}. */
export class NullRadarReadout implements IRadarReadout {
  static readonly instance = new NullRadarReadout();
  get backendId(): string {
    return "null";
  }
  async getCurrentReadingAsync(at: LatLon, rangeKm = 50): Promise<RadarReading> {
    return radarReading(at, rangeKm, []);
  }
}

/** Fail-safe {@link ISkyTracker}. */
export class NullSkyTracker implements ISkyTracker {
  static readonly instance = new NullSkyTracker();
  get backendId(): string {
    return "null";
  }
  async visibleAsync(): Promise<readonly SkyObject[]> {
    return [];
  }
}

/** Fail-safe {@link I3DSceneRenderer}. */
export class Null3DSceneRenderer implements I3DSceneRenderer {
  static readonly instance = new Null3DSceneRenderer();
  get backendId(): string {
    return "null";
  }
  async renderAsync(_sceneScript: string, format = "gltf"): Promise<Scene3D> {
    return scene3D(EMPTY_GUID, new Uint8Array(0), format);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

/** 32-char lowercase hex id (mirrors C# `Guid.NewGuid().ToString("n")`). */
function newGuidN(): string {
  let s = "";
  for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
  return s;
}

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

/**
 * Deterministic 31-bit LCG (numerical-recipes constants) used to make the
 * synthetic radar reproducible. C# `System.Random` is not portable, so this
 * yields stable-within-runtime output with the same value ranges.
 */
class Lcg {
  private state: number;
  constructor(seed: number) {
    // Fold seed into a non-zero 32-bit state.
    this.state = (Math.trunc(seed) ^ 0x9e3779b9) >>> 0;
    if (this.state === 0) this.state = 0x1;
  }
  private next(): number {
    // 32-bit LCG (Numerical Recipes).
    this.state = (Math.imul(1664525, this.state) + 1013904223) >>> 0;
    return this.state;
  }
  /** Uniform double in [0, 1). */
  nextDouble(): number {
    return this.next() / 0x1_0000_0000;
  }
  /** Uniform int in [0, maxExclusive). */
  nextInt(maxExclusive: number): number {
    return Math.floor(this.nextDouble() * maxExclusive);
  }
}
