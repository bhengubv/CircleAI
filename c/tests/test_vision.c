/*
 * test_vision.c — CircleAI.Vision (C11 port).
 *
 * Verifies the primitives (deep copy/free), the scripted + null VideoCapture,
 * the null CV runtime, the null + injected face detector / embedder, the null
 * liveness / document / plate impls (fail-closed), and the null + in-memory
 * Bluetooth anomaly detector — including the Start()-gated delivery and the
 * snapshot-then-fire safety of unsubscribing from inside a handler.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── VideoCapture ───────────────────────────────────────────────────────── */

static void test_video_capture(void) {
    /* Null yields nothing. */
    ca_video_capture_t *nc = ca_null_video_capture_create();
    ca_video_frame_t f;
    assert(!ca_video_capture_next(nc, &f));
    ca_video_capture_destroy(nc);

    /* Scripted: push two frames, drain both, then end. */
    ca_video_capture_t *sc = ca_scripted_video_capture_create();
    uint8_t b0[3] = { 1, 2, 3 };
    uint8_t b1[2] = { 9, 8 };
    assert(ca_scripted_video_capture_push(sc, b0, 3, 640, 480,
                                          CA_VIDEO_PIXEL_RGBA32, 111, false, 0) == 0);
    assert(ca_scripted_video_capture_push(sc, b1, 2, 320, 240,
                                          CA_VIDEO_PIXEL_JPEG, 222, true, 90) == 0);

    assert(ca_video_capture_next(sc, &f));
    assert(f.byte_count == 3 && f.bytes && f.bytes[0] == 1 && f.bytes[2] == 3);
    assert(f.width == 640 && f.height == 480);
    assert(f.pixel_format == CA_VIDEO_PIXEL_RGBA32);
    assert(f.captured_at_utc_ms == 111);
    assert(!f.has_rotation);
    ca_video_frame_free(&f);

    assert(ca_video_capture_next(sc, &f));
    assert(f.byte_count == 2 && f.pixel_format == CA_VIDEO_PIXEL_JPEG);
    assert(f.has_rotation && f.rotation_degrees == 90);
    ca_video_frame_free(&f);

    assert(!ca_video_capture_next(sc, &f));   /* end */

    /* reset re-drains from the start. */
    ca_video_capture_reset(sc);
    assert(ca_video_capture_next(sc, &f));
    assert(f.captured_at_utc_ms == 111);
    ca_video_frame_free(&f);

    ca_video_capture_destroy(sc);
    printf("  video_capture: ok\n");
}

/* ── primitives deep-copy ───────────────────────────────────────────────── */

static void test_primitives(void) {
    ca_landmark_point_t lm[2] = { { 10, 20 }, { 30, 40 } };
    ca_detected_face_t src;
    memset(&src, 0, sizeof(src));
    src.region = (ca_bounding_box_t){ 5, 6, 100, 120 };
    src.confidence = 0.9f;
    src.landmarks = lm;
    src.landmark_count = 2;

    ca_detected_face_t dst;
    assert(ca_detected_face_copy(&dst, &src) == 0);
    assert(dst.region.x == 5 && dst.region.width == 100);
    assert(dst.landmark_count == 2 && dst.landmarks != lm);   /* deep copy */
    assert(dst.landmarks[1].x == 30 && dst.landmarks[1].y == 40);
    ca_detected_face_free(&dst);

    /* face with no landmarks. */
    ca_detected_face_t nf;
    memset(&nf, 0, sizeof(nf));
    nf.region = (ca_bounding_box_t){ 0, 0, 1, 1 };
    ca_detected_face_t nfc;
    assert(ca_detected_face_copy(&nfc, &nf) == 0);
    assert(nfc.landmarks == NULL && nfc.landmark_count == 0);
    ca_detected_face_free(&nfc);
    printf("  primitives: ok\n");
}

/* ── CV runtime ─────────────────────────────────────────────────────────── */

static void test_cv_runtime(void) {
    ca_cv_runtime_t rt = ca_null_cv_runtime();
    assert(strcmp(ca_cv_runtime_backend_id(&rt), "null") == 0);
    assert(ca_cv_runtime_decode(&rt, (const uint8_t *)"x", 1) == NULL);
    assert(ca_cv_runtime_resize(&rt, NULL, 10, 10) == NULL);
    printf("  cv_runtime: ok\n");
}

/* ── face detector: null + injected ─────────────────────────────────────── */

static ca_detected_face_t *fake_detect(void *self, const uint8_t *b, size_t n,
                                        size_t *out_count) {
    (void)self; (void)b; (void)n;
    ca_detected_face_t *arr = (ca_detected_face_t *)calloc(1, sizeof(*arr));
    arr[0].region = (ca_bounding_box_t){ 1, 2, 3, 4 };
    arr[0].confidence = 0.75f;
    *out_count = 1;
    return arr;
}

static void test_face_detector(void) {
    ca_face_detector_t nd = ca_null_face_detector();
    size_t n = 123;
    ca_detected_face_t *faces = ca_face_detector_detect(&nd, (const uint8_t *)"x", 1, &n);
    assert(faces == NULL && n == 0);   /* Array.Empty */

    ca_face_detector_t fd = { NULL, fake_detect };
    faces = ca_face_detector_detect(&fd, (const uint8_t *)"x", 1, &n);
    assert(n == 1 && faces && faces[0].confidence == 0.75f);
    ca_detected_face_free_array(faces, n);
    printf("  face_detector: ok\n");
}

/* ── face embedder: null zero-vector ────────────────────────────────────── */

static void test_face_embedder(void) {
    ca_null_face_embedder_t *ne = ca_null_face_embedder_create(0);   /* default 512 */
    ca_face_embedder_t e = ca_null_face_embedder_as_iface(ne);
    assert(ca_face_embedder_dimension(&e) == 512);

    ca_detected_face_t face;
    memset(&face, 0, sizeof(face));
    face.region = (ca_bounding_box_t){ 0, 0, 10, 10 };

    ca_face_embedding_t emb;
    assert(ca_face_embedder_embed(&e, (const uint8_t *)"x", 1, &face, &emb) == 0);
    assert(emb.dimension == 512 && emb.vector);
    for (int i = 0; i < 512; ++i) assert(emb.vector[i] == 0.0f);   /* zero vector */
    ca_face_embedding_free(&emb);
    ca_null_face_embedder_destroy(ne);

    /* explicit dimension. */
    ca_null_face_embedder_t *ne2 = ca_null_face_embedder_create(128);
    ca_face_embedder_t e2 = ca_null_face_embedder_as_iface(ne2);
    assert(ca_face_embedder_dimension(&e2) == 128);
    ca_null_face_embedder_destroy(ne2);
    printf("  face_embedder: ok\n");
}

/* ── liveness / document / plate: fail-closed nulls ─────────────────────── */

static void test_fail_closed(void) {
    ca_face_liveness_detector_t lv = ca_null_face_liveness_detector();
    ca_liveness_result_t lr;
    assert(ca_face_liveness_check(&lv, (const uint8_t *)"x", 1, &lr) == 0);
    assert(!lr.is_live && lr.confidence == 0.0f);
    assert(lr.failure_reason && strcmp(lr.failure_reason, "no liveness backend registered") == 0);
    ca_liveness_result_free(&lr);

    ca_document_verifier_t dv = ca_null_document_verifier();
    ca_document_verification_result_t dr;
    assert(ca_document_verify(&dv, (const uint8_t *)"x", 1, &dr) == 0);
    assert(!dr.is_valid);
    assert(strcmp(dr.document_type, "unknown") == 0);
    assert(strcmp(dr.issuing_country, "unknown") == 0);
    assert(dr.field_count == 0);
    assert(dr.overall_confidence == 0.0f);
    assert(dr.warning_count == 1);
    assert(strcmp(dr.warnings[0], "no document verifier backend registered") == 0);
    ca_document_verification_result_free(&dr);

    ca_plate_recognizer_t pr = ca_null_plate_recognizer();
    size_t n = 55;
    ca_plate_recognition_result_t *plates =
        ca_plate_recognizer_recognize(&pr, (const uint8_t *)"x", 1, &n);
    assert(plates == NULL && n == 0);
    printf("  fail_closed: ok\n");
}

/* ── Bluetooth anomaly detector ─────────────────────────────────────────── */

typedef struct { int count; float last_sev; } bt_sink_t;
static void bt_handler(void *user, const ca_bluetooth_anomaly_t *a) {
    bt_sink_t *s = (bt_sink_t *)user;
    s->count++;
    s->last_sev = a->severity;
}

/* handler that unsubscribes itself on first fire — must not corrupt iteration. */
typedef struct {
    ca_bluetooth_anomaly_detector_t *iface;
    void                            *token;
    int                              count;
} bt_self_unsub_t;
static void bt_self_unsub_handler(void *user, const ca_bluetooth_anomaly_t *a) {
    (void)a;
    bt_self_unsub_t *s = (bt_self_unsub_t *)user;
    s->count++;
    ca_bt_anomaly_unsubscribe(s->iface, s->token);
}

static void test_bluetooth_anomaly(void) {
    /* Null: never fires; Start/Stop succeed; subscribe returns a no-op handle. */
    ca_bluetooth_anomaly_detector_t nd = ca_null_bluetooth_anomaly_detector();
    assert(strcmp(ca_bt_anomaly_backend_id(&nd), "null") == 0);
    assert(ca_bt_anomaly_start(&nd) == 0);
    assert(ca_bt_anomaly_start(&nd) == 0);   /* idempotent */
    void *tok = ca_bt_anomaly_subscribe(&nd, bt_handler, NULL);
    assert(tok != NULL);
    ca_bt_anomaly_unsubscribe(&nd, tok);
    assert(ca_bt_anomaly_stop(&nd) == 0);

    /* In-memory: publish only reaches subscribers while started. */
    ca_inmem_bluetooth_anomaly_detector_t *im = ca_inmem_bluetooth_anomaly_detector_create();
    ca_bluetooth_anomaly_detector_t iface = ca_inmem_bluetooth_anomaly_detector_as_iface(im);
    assert(strcmp(ca_bt_anomaly_backend_id(&iface), "in-memory") == 0);

    bt_sink_t sink = { 0, 0.0f };
    void *t = ca_bt_anomaly_subscribe(&iface, bt_handler, &sink);
    assert(t != NULL);

    ca_bluetooth_anomaly_t a;
    memset(&a, 0, sizeof(a));
    a.source = (char *)"radio";
    a.kind = (char *)"beacon-spoof";
    a.severity = 0.8f;
    a.description = (char *)"suspicious";
    a.observed_at_utc_ms = 1000;

    /* Not started yet -> no delivery. */
    assert(ca_inmem_bluetooth_anomaly_publish(im, &a) == 0);
    assert(sink.count == 0);

    /* Start -> delivery. */
    assert(ca_bt_anomaly_start(&iface) == 0);
    size_t fired = ca_inmem_bluetooth_anomaly_publish(im, &a);
    assert(fired == 1 && sink.count == 1 && sink.last_sev == 0.8f);

    /* Second subscriber; both fire. */
    bt_sink_t sink2 = { 0, 0.0f };
    ca_bt_anomaly_subscribe(&iface, bt_handler, &sink2);
    fired = ca_inmem_bluetooth_anomaly_publish(im, &a);
    assert(fired == 2 && sink.count == 2 && sink2.count == 1);

    /* Unsubscribe the first -> only the second fires. */
    ca_bt_anomaly_unsubscribe(&iface, t);
    fired = ca_inmem_bluetooth_anomaly_publish(im, &a);
    assert(fired == 1 && sink.count == 2 && sink2.count == 2);

    /* Stop -> no more delivery. */
    assert(ca_bt_anomaly_stop(&iface) == 0);
    assert(ca_inmem_bluetooth_anomaly_publish(im, &a) == 0);
    assert(sink2.count == 2);

    ca_inmem_bluetooth_anomaly_detector_destroy(im);

    /* Concurrency safety: a handler that unsubscribes itself mid-fire. */
    ca_inmem_bluetooth_anomaly_detector_t *im2 = ca_inmem_bluetooth_anomaly_detector_create();
    ca_bluetooth_anomaly_detector_t if2 = ca_inmem_bluetooth_anomaly_detector_as_iface(im2);
    assert(ca_bt_anomaly_start(&if2) == 0);
    bt_self_unsub_t su = { &if2, NULL, 0 };
    su.token = ca_bt_anomaly_subscribe(&if2, bt_self_unsub_handler, &su);
    fired = ca_inmem_bluetooth_anomaly_publish(im2, &a);
    assert(fired == 1 && su.count == 1);
    /* now unsubscribed — a second publish must not call it again. */
    fired = ca_inmem_bluetooth_anomaly_publish(im2, &a);
    assert(fired == 0 && su.count == 1);
    ca_inmem_bluetooth_anomaly_detector_destroy(im2);
    printf("  bluetooth_anomaly: ok\n");
}

int main(void) {
    test_video_capture();
    test_primitives();
    test_cv_runtime();
    test_face_detector();
    test_face_embedder();
    test_fail_closed();
    test_bluetooth_anomaly();
    printf("test_vision: all assertions passed\n");
    return 0;
}
