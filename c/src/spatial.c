/*
 * spatial.c — CircleAI.Spatial (C11 port).
 *
 * GeoTile source keeps registered place names; radar + sky are pure functions of
 * their inputs (the radar uses a small deterministic LCG seeded off the
 * coordinates so results are stable per input, matching the "synthetic" intent);
 * the 3D renderer emits a minimal GLTF 2.0 document with the script embedded as
 * a JSON string. Deterministic. Pure C11 + libc (+ libm). No pthreads.
 */

#include "circle_ai/spatial.h"
#include "board_common.h"
#include <math.h>

/* 1x1 transparent PNG (byte-identical to the C# fixture). */
static const uint8_t PNG_1x1[] = {
    0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, 0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
    0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01, 0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
    0x89,0x00,0x00,0x00,0x0D,0x49,0x44,0x41, 0x54,0x78,0x9C,0x63,0x00,0x01,0x00,0x00,
    0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00, 0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
    0x42,0x60,0x82
};

/* ── record frees ───────────────────────────────────────────────────────── */

void ca_geo_tile_free(ca_geo_tile_t *t) {
    if (!t) return;
    free(t->image_bytes);
    free(t->mime_type);
    t->image_bytes = NULL; t->mime_type = NULL; t->image_len = 0;
}
void ca_radar_reading_free(ca_radar_reading_t *r) {
    if (!r) return;
    free(r->returns);
    r->returns = NULL; r->return_count = 0;
}
void ca_sky_object_free(ca_sky_object_t *o) {
    if (!o) return;
    free(o->name);
    o->name = NULL;
}
void ca_sky_object_free_array(ca_sky_object_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_sky_object_free(&arr[i]);
    free(arr);
}
void ca_scene3d_free(ca_scene3d_t *s) {
    if (!s) return;
    free(s->scene_id);
    free(s->encoded);
    free(s->format);
    s->scene_id = NULL; s->encoded = NULL; s->format = NULL; s->encoded_len = 0;
}

/* ── InMemoryGeoTileSource ──────────────────────────────────────────────── */

typedef struct {
    char        *name; /* owned */
    ca_lat_lon_t at;
} geo_place_t;

struct ca_geo_tile_source {
    geo_place_t *places;
    size_t       count, cap;
};

static int geo_register_internal(ca_geo_tile_source_t *s, const char *name,
                                 ca_lat_lon_t at) {
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ci_eq(s->places[i].name, name)) {
            s->places[i].at = at;
            return 0;
        }
    }
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 8;
        void *n = realloc(s->places, nc * sizeof(*s->places));
        if (!n) return -1;
        s->places = (geo_place_t *)n;
        s->cap = nc;
    }
    char *dup = cab_strdup_empty(name);
    if (!dup) return -1;
    s->places[s->count].name = dup;
    s->places[s->count].at = at;
    s->count++;
    return 0;
}

ca_geo_tile_source_t *ca_geo_tile_source_create(void) {
    ca_geo_tile_source_t *s =
        (ca_geo_tile_source_t *)calloc(1, sizeof(ca_geo_tile_source_t));
    if (!s) return NULL;
    struct { const char *n; double lat, lon; } seed[] = {
        {"Johannesburg", -26.2041,  28.0473},
        {"Cape Town",    -33.9249,  18.4241},
        {"Pretoria",     -25.7479,  28.2293},
        {"Durban",       -29.8587,  31.0218},
        {"Lagos",          6.5244,   3.3792},
        {"Nairobi",       -1.2921,  36.8219},
        {"London",        51.5074,  -0.1278},
        {"New York",      40.7128, -74.0060},
    };
    for (size_t i = 0; i < sizeof(seed) / sizeof(seed[0]); ++i) {
        ca_lat_lon_t at = { seed[i].lat, seed[i].lon };
        if (geo_register_internal(s, seed[i].n, at) != 0) {
            ca_geo_tile_source_destroy(s);
            return NULL;
        }
    }
    return s;
}
void ca_geo_tile_source_destroy(ca_geo_tile_source_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) free(s->places[i].name);
    free(s->places);
    free(s);
}
const char *ca_geo_tile_source_backend_id(const ca_geo_tile_source_t *s) {
    (void)s; return "in-memory";
}

int ca_geo_tile_source_register(ca_geo_tile_source_t *s, const char *name,
                                ca_lat_lon_t at) {
    if (!s || cab_is_ws(name)) return -1;
    return geo_register_internal(s, name, at);
}

bool ca_geo_tile_source_get_tile(const ca_geo_tile_source_t *s, int z, int x,
                                 int y, ca_geo_tile_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || !out || z < 0 || x < 0 || y < 0) return false;
    out->z = z; out->x = x; out->y = y;
    out->mime_type = cab_strdup_empty("image/png");
    if (!out->mime_type) return false;
    out->image_len = sizeof(PNG_1x1);
    out->image_bytes = (uint8_t *)malloc(out->image_len);
    if (!out->image_bytes) { ca_geo_tile_free(out); return false; }
    memcpy(out->image_bytes, PNG_1x1, out->image_len);
    return true;
}

ca_lat_lon_t *ca_geo_tile_source_search_places(const ca_geo_tile_source_t *s,
                                               const char *query, int top_k,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(s->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ci_contains(s->places[i].name, query)) idx[n++] = i;
    /* order by name asc (ordinal) */
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(s->places[idx[j - 1]].name, s->places[key].name) > 0) {
            idx[j] = idx[j - 1]; j--;
        }
        idx[j] = key;
    }
    if ((size_t)top_k < n) n = (size_t)top_k;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    ca_lat_lon_t *out = (ca_lat_lon_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) out[i] = s->places[idx[i]].at;
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_spatial_null_geo_tile_source_backend_id(void) { return "null"; }

/* ── SyntheticRadarReadout ──────────────────────────────────────────────── */

/* Small deterministic LCG so radar output is stable per input. */
static uint32_t lcg_next(uint32_t *state) {
    *state = (*state * 1103515245u) + 12345u;
    return (*state >> 16) & 0x7FFF;
}
static double lcg_unit(uint32_t *state) {
    return (double)lcg_next(state) / 32768.0;
}

bool ca_radar_readout_current(ca_lat_lon_t at, double range_km,
                              ca_radar_reading_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || range_km <= 0) return false;

    out->centre = at;
    out->range_km = range_km;

    int64_t seed = (int64_t)(at.latitude * 1000) + (int64_t)(at.longitude * 1000)
                 + (int64_t)(range_km * 10);
    uint32_t state = (uint32_t)(seed ^ (seed >> 32));
    int count = 3 + (int)(lcg_next(&state) % 5);
    ca_radar_return_t *rets = (ca_radar_return_t *)calloc((size_t)count, sizeof(*rets));
    if (!rets) return false;
    for (int i = 0; i < count; ++i) {
        double d   = lcg_unit(&state) * range_km * 0.9;
        double ang = lcg_unit(&state) * M_PI * 2.0;
        double lat = at.latitude  + (cos(ang) * d) / 111.0;
        double lon = at.longitude + (sin(ang) * d) / 111.0;
        rets[i].position.latitude  = lat;
        rets[i].position.longitude = lon;
        rets[i].doppler_kmh   = lcg_unit(&state) * 60.0 - 30.0;
        rets[i].intensity_dbz = lcg_unit(&state) * 60.0;
    }
    out->returns = rets;
    out->return_count = (size_t)count;
    return true;
}
const char *ca_radar_readout_backend_id(void) { return "synthetic"; }

bool ca_spatial_null_radar_current(ca_lat_lon_t at, double range_km,
                                   ca_radar_reading_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    out->centre = at;
    out->range_km = range_km;
    out->returns = NULL;
    out->return_count = 0;
    return true;
}
const char *ca_spatial_null_radar_readout_backend_id(void) { return "null"; }

/* ── SyntheticSkyTracker ────────────────────────────────────────────────── */

typedef struct { const char *name; double az, alt, mag; } sky_base_t;

static const sky_base_t SKY_BASE[] = {
    {"Sirius",  102.7, 35.0, -1.46},
    {"Polaris",   0.0, 51.5,  1.97},
    {"Vega",     88.0, 70.0,  0.03},
    {"Mars",    135.4, 22.0,  0.5},
    {"Jupiter", 180.5, 40.0, -2.0},
    {"Saturn",  210.0, 30.0,  0.4},
};

ca_sky_object_t *ca_sky_tracker_visible(ca_lat_lon_t at, int64_t utc_ms,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    /* hours since UTC midnight of the given day (C# utc.UtcDateTime.TimeOfDay). */
    int64_t day_ms = cab_day_start_ms(utc_ms);
    double hours = (double)(utc_ms - day_ms) / 3600000.0;
    double rot = hours * 15.0;

    size_t base_n = sizeof(SKY_BASE) / sizeof(SKY_BASE[0]);
    ca_sky_object_t *out = (ca_sky_object_t *)calloc(base_n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    double abslat = fabs(at.latitude);
    for (size_t i = 0; i < base_n; ++i) {
        if (SKY_BASE[i].alt - abslat > 0) {
            double az2 = fmod(SKY_BASE[i].az - rot + 360.0, 360.0);
            out[n].name = cab_strdup_empty(SKY_BASE[i].name);
            if (!out[n].name) { ca_sky_object_free_array(out, n); *out_count = (size_t)-1; return NULL; }
            out[n].azimuth_deg = az2;
            out[n].altitude_deg = SKY_BASE[i].alt;
            out[n].magnitude_apparent = SKY_BASE[i].mag;
            n++;
        }
    }
    if (n == 0) { free(out); *out_count = 0; return NULL; }
    *out_count = n;
    return out;
}
const char *ca_sky_tracker_backend_id(void) { return "synthetic"; }
const char *ca_spatial_null_sky_tracker_backend_id(void) { return "null"; }

/* ── JsonScene3DRenderer ────────────────────────────────────────────────── */

/* Append `raw` as a JSON string literal (with surrounding quotes) to a buffer. */
static bool append_json_string(char **buf, size_t *len, size_t *cap,
                               const char *raw) {
    /* worst case: each char -> \uXXXX (6 bytes) + 2 quotes. */
    size_t need = *len + strlen(raw) * 6 + 3;
    if (need > *cap) {
        size_t nc = *cap ? *cap : 64;
        while (nc < need) nc *= 2;
        char *nb = (char *)realloc(*buf, nc);
        if (!nb) return false;
        *buf = nb; *cap = nc;
    }
    char *p = *buf + *len;
    *p++ = '"';
    for (const unsigned char *c = (const unsigned char *)raw; *c; ++c) {
        switch (*c) {
            case '"':  *p++ = '\\'; *p++ = '"';  break;
            case '\\': *p++ = '\\'; *p++ = '\\'; break;
            case '\b': *p++ = '\\'; *p++ = 'b';  break;
            case '\f': *p++ = '\\'; *p++ = 'f';  break;
            case '\n': *p++ = '\\'; *p++ = 'n';  break;
            case '\r': *p++ = '\\'; *p++ = 'r';  break;
            case '\t': *p++ = '\\'; *p++ = 't';  break;
            default:
                if (*c < 0x20) {
                    static const char hex[] = "0123456789abcdef";
                    *p++ = '\\'; *p++ = 'u'; *p++ = '0'; *p++ = '0';
                    *p++ = hex[(*c >> 4) & 0xF];
                    *p++ = hex[*c & 0xF];
                } else {
                    *p++ = (char)*c;
                }
        }
    }
    *p++ = '"';
    *p = '\0';
    *len = (size_t)(p - *buf);
    return true;
}

bool ca_scene3d_render(const char *scene_script, const char *format,
                       ca_scene3d_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || !scene_script) return false;
    const char *fmt = (format && !cab_is_ws(format)) ? format : "gltf";

    const char *head =
        "{\"asset\":{\"version\":\"2.0\",\"generator\":"
        "\"CircleAI.Spatial.JsonScene3DRenderer\"},\"scenes\":[{\"nodes\":[]}],"
        "\"scene\":0,\"extras\":{\"script\":";
    const char *tail = "}}";

    size_t cap = 0, len = 0;
    char *buf = NULL;
    size_t hlen = strlen(head);
    cap = hlen + 64;
    buf = (char *)malloc(cap);
    if (!buf) return false;
    memcpy(buf, head, hlen);
    len = hlen;
    buf[len] = '\0';
    if (!append_json_string(&buf, &len, &cap, scene_script)) { free(buf); return false; }
    size_t tlen = strlen(tail);
    if (len + tlen + 1 > cap) {
        char *nb = (char *)realloc(buf, len + tlen + 1);
        if (!nb) { free(buf); return false; }
        buf = nb;
    }
    memcpy(buf + len, tail, tlen);
    len += tlen;
    buf[len] = '\0';

    out->scene_id = cab_strdup_empty("scene");
    out->format   = cab_strdup_empty(fmt);
    if (!out->scene_id || !out->format) { free(buf); ca_scene3d_free(out); return false; }
    out->encoded = (uint8_t *)buf;
    out->encoded_len = len;
    return true;
}
const char *ca_scene3d_renderer_backend_id(void) { return "json"; }
const char *ca_spatial_null_scene3d_renderer_backend_id(void) { return "null"; }
