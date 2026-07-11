#ifndef CIRCLE_AI_SPATIAL_H
#define CIRCLE_AI_SPATIAL_H

/*
 * spatial.h — CircleAI.Spatial (C11 port of Contracts.cs + InMemorySpatial.cs +
 * NullImplementations.cs).
 *
 *   Records : LatLon(Latitude, Longitude);
 *             GeoTile(Z, X, Y, ImageBytes, MimeType);
 *             RadarReading(Centre, RangeKm, RadarReturn[]);
 *             RadarReturn(Position, DopplerKmh, IntensityDbz);
 *             SkyObject(Name, AzimuthDeg, AltitudeDeg, MagnitudeApparent);
 *             Scene3D(SceneId, Encoded, Format).
 *   Tiles   : IGeoTileSource -> InMemoryGeoTileSource. Seeded with 8 cities;
 *               GetTile(z,x,y) -> 1x1 transparent PNG "image/png" (z,x,y >= 0);
 *               SearchPlaces(query, topK=5) -> LatLon of registered names whose
 *               key Contains query (OrdinalIgnoreCase) ordered by name asc, topK
 *               (query non-null, topK > 0). BackendId "in-memory".
 *   Radar   : IRadarReadout -> SyntheticRadarReadout. GetCurrentReading(at,
 *               rangeKm=50) -> deterministic returns keyed off the coordinates
 *               (rangeKm > 0). BackendId "synthetic".
 *   Sky     : ISkyTracker -> SyntheticSkyTracker. Visible(at, utc) -> the fixed
 *               object table filtered by (altitude - |lat| > 0) with a daily
 *               azimuth rotation. BackendId "synthetic".
 *   Scene   : I3DSceneRenderer -> JsonScene3DRenderer. Render(script,
 *               format="gltf") -> minimal GLTF 2.0 JSON embedding the script.
 *               BackendId "json".
 *   Null variants return empty tiles/readings/scenes.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. utc as
 * int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc (+ libm).
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* LatLon(Latitude, Longitude). */
typedef struct {
    double latitude;
    double longitude;
} ca_lat_lon_t;

/* GeoTile(Z, X, Y, ImageBytes, MimeType). */
typedef struct {
    int      z, x, y;
    uint8_t *image_bytes; /* owned, or NULL when image_len == 0 */
    size_t   image_len;
    char    *mime_type;   /* owned, non-null */
} ca_geo_tile_t;

void ca_geo_tile_free(ca_geo_tile_t *t);

/* RadarReturn(Position, DopplerKmh, IntensityDbz). */
typedef struct {
    ca_lat_lon_t position;
    double       doppler_kmh;
    double       intensity_dbz;
} ca_radar_return_t;

/* RadarReading(Centre, RangeKm, Returns[]). */
typedef struct {
    ca_lat_lon_t       centre;
    double             range_km;
    ca_radar_return_t *returns; /* owned; NULL when return_count == 0 */
    size_t             return_count;
} ca_radar_reading_t;

void ca_radar_reading_free(ca_radar_reading_t *r);

/* SkyObject(Name, AzimuthDeg, AltitudeDeg, MagnitudeApparent). */
typedef struct {
    char  *name;              /* owned, non-null */
    double azimuth_deg;
    double altitude_deg;
    double magnitude_apparent;
} ca_sky_object_t;

void ca_sky_object_free(ca_sky_object_t *o);
void ca_sky_object_free_array(ca_sky_object_t *arr, size_t count);

/* Scene3D(SceneId, Encoded, Format). */
typedef struct {
    char    *scene_id; /* owned, non-null */
    uint8_t *encoded;  /* owned, or NULL when encoded_len == 0 */
    size_t   encoded_len;
    char    *format;   /* owned, non-null */
} ca_scene3d_t;

void ca_scene3d_free(ca_scene3d_t *s);

/* ── IGeoTileSource -> InMemoryGeoTileSource ────────────────────────────── */

typedef struct ca_geo_tile_source ca_geo_tile_source_t;

ca_geo_tile_source_t *ca_geo_tile_source_create(void); /* seeds cities; NULL OOM */
void ca_geo_tile_source_destroy(ca_geo_tile_source_t *s);
const char *ca_geo_tile_source_backend_id(const ca_geo_tile_source_t *s);

/* Register(name, at) — keyed by name (OrdinalIgnoreCase). 0 / -1 on bad args. */
int ca_geo_tile_source_register(ca_geo_tile_source_t *s, const char *name,
                                ca_lat_lon_t at);
/* GetTile(z,x,y) -> fresh tile into *out, true; false on bad args (any < 0). */
bool ca_geo_tile_source_get_tile(const ca_geo_tile_source_t *s, int z, int x,
                                 int y, ca_geo_tile_t *out);
/* SearchPlaces(query, topK) -> fresh LatLon array ordered by name asc, topK.
 * NULL + 0 empty; NULL + SIZE_MAX on error (query required, top_k > 0). */
ca_lat_lon_t *ca_geo_tile_source_search_places(const ca_geo_tile_source_t *s,
                                               const char *query, int top_k,
                                               size_t *out_count);

const char *ca_spatial_null_geo_tile_source_backend_id(void); /* "null" */

/* ── IRadarReadout -> SyntheticRadarReadout ─────────────────────────────── */

/* GetCurrentReading(at, rangeKm) -> fresh reading into *out, true; false on bad
 * args (rangeKm <= 0). BackendId "synthetic". */
bool ca_radar_readout_current(ca_lat_lon_t at, double range_km,
                              ca_radar_reading_t *out);
const char *ca_radar_readout_backend_id(void); /* "synthetic" */

/* Null: reading with the given centre/range and no returns. */
bool ca_spatial_null_radar_current(ca_lat_lon_t at, double range_km,
                                   ca_radar_reading_t *out);
const char *ca_spatial_null_radar_readout_backend_id(void); /* "null" */

/* ── ISkyTracker -> SyntheticSkyTracker ─────────────────────────────────── */

/* Visible(at, utc) -> fresh SkyObject array (filtered/rotated). NULL + 0 empty;
 * NULL + SIZE_MAX on OOM. BackendId "synthetic". */
ca_sky_object_t *ca_sky_tracker_visible(ca_lat_lon_t at, int64_t utc_ms,
                                        size_t *out_count);
const char *ca_sky_tracker_backend_id(void); /* "synthetic" */

const char *ca_spatial_null_sky_tracker_backend_id(void); /* "null" */

/* ── I3DSceneRenderer -> JsonScene3DRenderer ────────────────────────────── */

/* Render(script, format) -> fresh Scene3D into *out, true; false on bad args
 * (script required). format defaults to "gltf" when blank. BackendId "json". */
bool ca_scene3d_render(const char *scene_script, const char *format,
                       ca_scene3d_t *out);
const char *ca_scene3d_renderer_backend_id(void); /* "json" */

const char *ca_spatial_null_scene3d_renderer_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SPATIAL_H */
