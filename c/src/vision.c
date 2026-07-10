/*
 * vision.c — CircleAI.Vision (C11 port).
 *
 * Primitives + VideoFrame capture source + the CV-runtime / detector / embedder /
 * liveness / document / plate / bluetooth-anomaly seams with their fail-closed
 * Null implementations. The real native CV SDKs are injected behind the vtables.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/vision.h"

#include <stdlib.h>
#include <string.h>

static char *vz_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ===========================================================================
 * DetectedFace
 * =========================================================================== */

void ca_detected_face_free(ca_detected_face_t *f) {
    if (!f) return;
    free(f->landmarks);
    f->landmarks = NULL;
    f->landmark_count = 0;
}
void ca_detected_face_free_array(ca_detected_face_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_detected_face_free(&arr[i]);
    free(arr);
}
int ca_detected_face_copy(ca_detected_face_t *dst, const ca_detected_face_t *src) {
    if (!dst || !src) return -1;
    memset(dst, 0, sizeof(*dst));
    dst->region = src->region;
    dst->confidence = src->confidence;
    if (src->landmarks && src->landmark_count) {
        dst->landmarks = (ca_landmark_point_t *)malloc(
            src->landmark_count * sizeof(*dst->landmarks));
        if (!dst->landmarks) return -1;
        memcpy(dst->landmarks, src->landmarks,
               src->landmark_count * sizeof(*dst->landmarks));
        dst->landmark_count = src->landmark_count;
    }
    return 0;
}

/* ===========================================================================
 * FaceEmbedding
 * =========================================================================== */

void ca_face_embedding_free(ca_face_embedding_t *e) {
    if (!e) return;
    free(e->vector);
    e->vector = NULL;
    e->dimension = 0;
}

/* ===========================================================================
 * LivenessResult
 * =========================================================================== */

void ca_liveness_result_free(ca_liveness_result_t *r) {
    if (!r) return;
    free(r->failure_reason);
    r->failure_reason = NULL;
}

/* ===========================================================================
 * DocumentVerificationResult
 * =========================================================================== */

void ca_document_verification_result_free(ca_document_verification_result_t *r) {
    if (!r) return;
    free(r->document_type);
    free(r->issuing_country);
    for (size_t i = 0; i < r->field_count; ++i) {
        free(r->fields[i].key);
        free(r->fields[i].value);
    }
    free(r->fields);
    for (size_t i = 0; i < r->warning_count; ++i) free(r->warnings[i]);
    free(r->warnings);
    r->document_type = r->issuing_country = NULL;
    r->fields = NULL;
    r->field_count = 0;
    r->warnings = NULL;
    r->warning_count = 0;
}

/* ===========================================================================
 * PlateRecognitionResult
 * =========================================================================== */

void ca_plate_recognition_result_free(ca_plate_recognition_result_t *r) {
    if (!r) return;
    free(r->plate_text);
    free(r->country_hint);
    r->plate_text = NULL;
    r->country_hint = NULL;
}
void ca_plate_recognition_result_free_array(ca_plate_recognition_result_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_plate_recognition_result_free(&arr[i]);
    free(arr);
}

/* ===========================================================================
 * BluetoothAnomaly
 * =========================================================================== */

void ca_bluetooth_anomaly_free(ca_bluetooth_anomaly_t *a) {
    if (!a) return;
    free(a->source);
    free(a->kind);
    free(a->description);
    a->source = a->kind = a->description = NULL;
}

/* ===========================================================================
 * VideoFrame + IVideoCapture
 * =========================================================================== */

void ca_video_frame_free(ca_video_frame_t *f) {
    if (!f) return;
    free(f->bytes);
    f->bytes = NULL;
    f->byte_count = 0;
}

typedef struct {
    uint8_t                *bytes;
    size_t                  len;
    int                     width;
    int                     height;
    ca_video_pixel_format_t pixel_format;
    int64_t                 captured_at_utc_ms;
    bool                    has_rotation;
    int                     rotation_degrees;
} vframe_t;

struct ca_video_capture {
    bool      is_null;
    vframe_t *frames;
    size_t    count, cap;
    size_t    cursor;
};

ca_video_capture_t *ca_null_video_capture_create(void) {
    ca_video_capture_t *c = (ca_video_capture_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->is_null = true;
    return c;
}
ca_video_capture_t *ca_scripted_video_capture_create(void) {
    ca_video_capture_t *c = (ca_video_capture_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->is_null = false;
    return c;
}
void ca_video_capture_destroy(ca_video_capture_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) free(c->frames[i].bytes);
    free(c->frames);
    free(c);
}
int ca_scripted_video_capture_push(ca_video_capture_t *c,
                                   const uint8_t *data, size_t len,
                                   int width, int height,
                                   ca_video_pixel_format_t pixel_format,
                                   int64_t captured_at_utc_ms,
                                   bool has_rotation, int rotation_degrees) {
    if (!c || c->is_null) return -1;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 4;
        void *n = realloc(c->frames, nc * sizeof(*c->frames));
        if (!n) return -1;
        c->frames = (vframe_t *)n;
        c->cap = nc;
    }
    uint8_t *cpy = NULL;
    if (len) {
        cpy = (uint8_t *)malloc(len);
        if (!cpy) return -1;
        if (data) memcpy(cpy, data, len);
        else memset(cpy, 0, len);
    }
    vframe_t *f = &c->frames[c->count];
    f->bytes = cpy;
    f->len = len;
    f->width = width;
    f->height = height;
    f->pixel_format = pixel_format;
    f->captured_at_utc_ms = captured_at_utc_ms;
    f->has_rotation = has_rotation;
    f->rotation_degrees = rotation_degrees;
    c->count++;
    return 0;
}
bool ca_video_capture_next(ca_video_capture_t *c, ca_video_frame_t *out) {
    if (!c || !out) return false;
    if (c->is_null || c->cursor >= c->count) {
        memset(out, 0, sizeof(*out));
        return false;
    }
    vframe_t *f = &c->frames[c->cursor++];
    memset(out, 0, sizeof(*out));
    if (f->len) {
        out->bytes = (uint8_t *)malloc(f->len);
        if (!out->bytes) { memset(out, 0, sizeof(*out)); return false; }
        memcpy(out->bytes, f->bytes, f->len);
    }
    out->byte_count = f->len;
    out->width = f->width;
    out->height = f->height;
    out->pixel_format = f->pixel_format;
    out->captured_at_utc_ms = f->captured_at_utc_ms;
    out->has_rotation = f->has_rotation;
    out->rotation_degrees = f->rotation_degrees;
    return true;
}
void ca_video_capture_reset(ca_video_capture_t *c) {
    if (c) c->cursor = 0;
}

/* ===========================================================================
 * IComputerVisionRuntime
 * =========================================================================== */

void *ca_cv_runtime_decode(const ca_cv_runtime_t *rt, const uint8_t *bytes, size_t len) {
    if (!rt || !rt->decode) return NULL;
    return rt->decode(rt->self, bytes, len);
}
void *ca_cv_runtime_resize(const ca_cv_runtime_t *rt, void *image, int width, int height) {
    if (!rt || !rt->resize) return NULL;
    return rt->resize(rt->self, image, width, height);
}
const char *ca_cv_runtime_backend_id(const ca_cv_runtime_t *rt) {
    if (!rt || !rt->backend_id) return "null";
    return rt->backend_id(rt->self);
}

static void *nullcv_decode(void *self, const uint8_t *b, size_t n) {
    (void)self; (void)b; (void)n; return NULL;
}
static void *nullcv_resize(void *self, void *img, int w, int h) {
    (void)self; (void)img; (void)w; (void)h; return NULL;
}
static const char *nullcv_backend(void *self) { (void)self; return "null"; }

ca_cv_runtime_t ca_null_cv_runtime(void) {
    ca_cv_runtime_t rt;
    rt.self = NULL;
    rt.decode = nullcv_decode;
    rt.resize = nullcv_resize;
    rt.backend_id = nullcv_backend;
    return rt;
}

/* ===========================================================================
 * IFaceDetector
 * =========================================================================== */

ca_detected_face_t *ca_face_detector_detect(const ca_face_detector_t *d,
                                            const uint8_t *bytes, size_t len,
                                            size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!d || !d->detect) return NULL;
    return d->detect(d->self, bytes, len, out_count);
}
static ca_detected_face_t *nullfd_detect(void *self, const uint8_t *b, size_t n,
                                         size_t *out_count) {
    (void)self; (void)b; (void)n;
    if (out_count) *out_count = 0;
    return NULL;   /* Array.Empty<DetectedFace>() */
}
ca_face_detector_t ca_null_face_detector(void) {
    ca_face_detector_t d;
    d.self = NULL;
    d.detect = nullfd_detect;
    return d;
}

/* ===========================================================================
 * IFaceEmbedder
 * =========================================================================== */

int ca_face_embedder_dimension(const ca_face_embedder_t *e) {
    if (!e || !e->dimension) return 0;
    return e->dimension(e->self);
}
int ca_face_embedder_embed(const ca_face_embedder_t *e, const uint8_t *bytes, size_t len,
                           const ca_detected_face_t *face, ca_face_embedding_t *out) {
    if (!e || !e->embed || !out) return -1;
    return e->embed(e->self, bytes, len, face, out);
}

struct ca_null_face_embedder { int dimension; };

ca_null_face_embedder_t *ca_null_face_embedder_create(int dimension) {
    ca_null_face_embedder_t *e = (ca_null_face_embedder_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->dimension = dimension > 0 ? dimension : 512;
    return e;
}
void ca_null_face_embedder_destroy(ca_null_face_embedder_t *e) { free(e); }

static int nullfe_dimension(void *self) {
    return ((ca_null_face_embedder_t *)self)->dimension;
}
static int nullfe_embed(void *self, const uint8_t *b, size_t n,
                        const ca_detected_face_t *face, ca_face_embedding_t *out) {
    (void)b; (void)n; (void)face;
    int dim = ((ca_null_face_embedder_t *)self)->dimension;
    memset(out, 0, sizeof(*out));
    if (dim > 0) {
        out->vector = (float *)calloc((size_t)dim, sizeof(float));   /* new float[Dimension] */
        if (!out->vector) return -1;
    }
    out->dimension = dim;
    return 0;
}
ca_face_embedder_t ca_null_face_embedder_as_iface(ca_null_face_embedder_t *e) {
    ca_face_embedder_t i;
    i.self = e;
    i.dimension = nullfe_dimension;
    i.embed = nullfe_embed;
    return i;
}

/* ===========================================================================
 * IFaceLivenessDetector
 * =========================================================================== */

int ca_face_liveness_check(const ca_face_liveness_detector_t *d,
                           const uint8_t *bytes, size_t len,
                           ca_liveness_result_t *out) {
    if (!d || !d->check || !out) return -1;
    return d->check(d->self, bytes, len, out);
}
static int nulllv_check(void *self, const uint8_t *b, size_t n,
                        ca_liveness_result_t *out) {
    (void)self; (void)b; (void)n;
    memset(out, 0, sizeof(*out));
    out->is_live = false;
    out->confidence = 0.0f;
    out->failure_reason = vz_strdup("no liveness backend registered");
    if (!out->failure_reason) return -1;
    return 0;
}
ca_face_liveness_detector_t ca_null_face_liveness_detector(void) {
    ca_face_liveness_detector_t d;
    d.self = NULL;
    d.check = nulllv_check;
    return d;
}

/* ===========================================================================
 * IDocumentVerifier
 * =========================================================================== */

int ca_document_verify(const ca_document_verifier_t *v,
                       const uint8_t *bytes, size_t len,
                       ca_document_verification_result_t *out) {
    if (!v || !v->verify || !out) return -1;
    return v->verify(v->self, bytes, len, out);
}
static int nulldv_verify(void *self, const uint8_t *b, size_t n,
                         ca_document_verification_result_t *out) {
    (void)self; (void)b; (void)n;
    memset(out, 0, sizeof(*out));
    out->is_valid = false;
    out->document_type = vz_strdup("unknown");
    out->issuing_country = vz_strdup("unknown");
    out->fields = NULL;
    out->field_count = 0;
    out->overall_confidence = 0.0f;
    out->warnings = (char **)malloc(sizeof(char *));
    if (!out->document_type || !out->issuing_country || !out->warnings) {
        ca_document_verification_result_free(out);
        return -1;
    }
    out->warnings[0] = vz_strdup("no document verifier backend registered");
    if (!out->warnings[0]) { ca_document_verification_result_free(out); return -1; }
    out->warning_count = 1;
    return 0;
}
ca_document_verifier_t ca_null_document_verifier(void) {
    ca_document_verifier_t v;
    v.self = NULL;
    v.verify = nulldv_verify;
    return v;
}

/* ===========================================================================
 * IPlateRecognizer
 * =========================================================================== */

ca_plate_recognition_result_t *ca_plate_recognizer_recognize(
    const ca_plate_recognizer_t *r, const uint8_t *bytes, size_t len,
    size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!r || !r->recognize) return NULL;
    return r->recognize(r->self, bytes, len, out_count);
}
static ca_plate_recognition_result_t *nullpr_recognize(void *self, const uint8_t *b,
                                                       size_t n, size_t *out_count) {
    (void)self; (void)b; (void)n;
    if (out_count) *out_count = 0;
    return NULL;   /* Array.Empty<PlateRecognitionResult>() */
}
ca_plate_recognizer_t ca_null_plate_recognizer(void) {
    ca_plate_recognizer_t r;
    r.self = NULL;
    r.recognize = nullpr_recognize;
    return r;
}

/* ===========================================================================
 * IBluetoothAnomalyDetector
 * =========================================================================== */

void *ca_bt_anomaly_subscribe(const ca_bluetooth_anomaly_detector_t *d,
                              ca_bt_anomaly_handler_fn handler, void *user) {
    if (!d || !d->subscribe) return NULL;
    return d->subscribe(d->self, handler, user);
}
void ca_bt_anomaly_unsubscribe(const ca_bluetooth_anomaly_detector_t *d, void *token) {
    if (d && d->unsubscribe) d->unsubscribe(d->self, token);
}
int ca_bt_anomaly_start(const ca_bluetooth_anomaly_detector_t *d) {
    if (!d || !d->start) return -1;
    return d->start(d->self);
}
int ca_bt_anomaly_stop(const ca_bluetooth_anomaly_detector_t *d) {
    if (!d || !d->stop) return -1;
    return d->stop(d->self);
}
const char *ca_bt_anomaly_backend_id(const ca_bluetooth_anomaly_detector_t *d) {
    if (!d || !d->backend_id) return "null";
    return d->backend_id(d->self);
}

/* ── NullBluetoothAnomalyDetector ───────────────────────────────────────── */

static void *nullbt_subscribe(void *self, ca_bt_anomaly_handler_fn h, void *user) {
    (void)self; (void)h; (void)user;
    /* EmptyDisposable.Instance — a non-NULL sentinel so unsubscribe is a no-op. */
    static int sentinel;
    return &sentinel;
}
static void nullbt_unsubscribe(void *self, void *token) { (void)self; (void)token; }
static int  nullbt_start(void *self) { (void)self; return 0; }
static int  nullbt_stop(void *self)  { (void)self; return 0; }
static const char *nullbt_backend(void *self) { (void)self; return "null"; }

ca_bluetooth_anomaly_detector_t ca_null_bluetooth_anomaly_detector(void) {
    ca_bluetooth_anomaly_detector_t d;
    d.self = NULL;
    d.subscribe = nullbt_subscribe;
    d.unsubscribe = nullbt_unsubscribe;
    d.start = nullbt_start;
    d.stop = nullbt_stop;
    d.backend_id = nullbt_backend;
    return d;
}

/* ── In-memory detector ─────────────────────────────────────────────────── */

typedef struct {
    ca_bt_anomaly_handler_fn handler;
    void                    *user;
} bt_handler_node;

struct ca_inmem_bluetooth_anomaly_detector {
    bt_handler_node *handlers;
    size_t           count, cap;
    bool             started;
};

ca_inmem_bluetooth_anomaly_detector_t *ca_inmem_bluetooth_anomaly_detector_create(void) {
    return (ca_inmem_bluetooth_anomaly_detector_t *)calloc(1, sizeof(ca_inmem_bluetooth_anomaly_detector_t));
}
void ca_inmem_bluetooth_anomaly_detector_destroy(ca_inmem_bluetooth_anomaly_detector_t *d) {
    if (!d) return;
    free(d->handlers);
    free(d);
}

static void *inmembt_subscribe(void *self, ca_bt_anomaly_handler_fn h, void *user) {
    ca_inmem_bluetooth_anomaly_detector_t *d = (ca_inmem_bluetooth_anomaly_detector_t *)self;
    if (!d || !h) return NULL;
    if (d->count == d->cap) {
        size_t nc = d->cap ? d->cap * 2 : 4;
        void *n = realloc(d->handlers, nc * sizeof(*d->handlers));
        if (!n) return NULL;
        d->handlers = (bt_handler_node *)n;
        d->cap = nc;
    }
    d->handlers[d->count].handler = h;
    d->handlers[d->count].user = user;
    d->count++;
    /* token identifies (handler,user) — return the slot pointer (stable enough:
     * unsubscribe matches on (handler,user), not on the pointer). */
    return &d->handlers[d->count - 1];
}
static void inmembt_unsubscribe(void *self, void *token) {
    ca_inmem_bluetooth_anomaly_detector_t *d = (ca_inmem_bluetooth_anomaly_detector_t *)self;
    if (!d || !token) return;
    bt_handler_node *tok = (bt_handler_node *)token;
    for (size_t i = 0; i < d->count; ++i) {
        if (d->handlers[i].handler == tok->handler &&
            d->handlers[i].user == tok->user) {
            memmove(&d->handlers[i], &d->handlers[i + 1],
                    (d->count - i - 1) * sizeof(*d->handlers));
            d->count--;
            return;
        }
    }
}
static int inmembt_start(void *self) {
    ca_inmem_bluetooth_anomaly_detector_t *d = (ca_inmem_bluetooth_anomaly_detector_t *)self;
    if (!d) return -1;
    d->started = true;   /* idempotent */
    return 0;
}
static int inmembt_stop(void *self) {
    ca_inmem_bluetooth_anomaly_detector_t *d = (ca_inmem_bluetooth_anomaly_detector_t *)self;
    if (!d) return -1;
    d->started = false;  /* idempotent */
    return 0;
}
static const char *inmembt_backend(void *self) { (void)self; return "in-memory"; }

ca_bluetooth_anomaly_detector_t ca_inmem_bluetooth_anomaly_detector_as_iface(
    ca_inmem_bluetooth_anomaly_detector_t *d) {
    ca_bluetooth_anomaly_detector_t i;
    i.self = d;
    i.subscribe = inmembt_subscribe;
    i.unsubscribe = inmembt_unsubscribe;
    i.start = inmembt_start;
    i.stop = inmembt_stop;
    i.backend_id = inmembt_backend;
    return i;
}
size_t ca_inmem_bluetooth_anomaly_publish(ca_inmem_bluetooth_anomaly_detector_t *d,
                                          const ca_bluetooth_anomaly_t *a) {
    if (!d || !a || !d->started) return 0;
    /* Snapshot the handler list, RELEASE any conceptual lock, THEN fire — so a
     * handler that unsubscribes (or re-subscribes) mid-callback cannot corrupt
     * the iteration or self-deadlock. */
    size_t n = d->count;
    if (n == 0) return 0;
    bt_handler_node *snapshot = (bt_handler_node *)malloc(n * sizeof(*snapshot));
    if (!snapshot) return 0;
    memcpy(snapshot, d->handlers, n * sizeof(*snapshot));
    size_t fired = 0;
    for (size_t i = 0; i < n; ++i) {
        if (snapshot[i].handler) { snapshot[i].handler(snapshot[i].user, a); fired++; }
    }
    free(snapshot);
    return fired;
}
