#ifndef CIRCLE_AI_VISION_H
#define CIRCLE_AI_VISION_H

/*
 * vision.h — CircleAI.Vision (C11 port).
 *
 * Ports the CircleAI.Vision contract surface 1:1. The real CV backends (compv,
 * facex, ArcFace/ONNX, ultimateALPR, Bluehound) are native C++ SDK dependencies
 * injected behind vtable seams — exactly as the C# ships Null defaults out of the
 * box and swaps in the ONNX-backed impls at 2.2.1. This C port provides:
 *
 *   Primitives : BoundingBox, LandmarkPoint, DetectedFace, FaceEmbedding,
 *                LivenessResult, DocumentField, DocumentVerificationResult,
 *                PlateRecognitionResult, BluetoothAnomaly.
 *   Capture    : VideoPixelFormat enum + VideoFrame; IVideoCapture — NullVideoCapture
 *                (yields nothing) + a scripted in-memory capture (preloaded frame
 *                list drained frame-by-frame), mirroring CircleAI.Voice.IAudioCapture.
 *   CV seams   : IComputerVisionRuntime, IFaceDetector, IFaceEmbedder,
 *                IFaceLivenessDetector, IDocumentVerifier, IPlateRecognizer,
 *                IBluetoothAnomalyDetector — each an injectable vtable PLUS the
 *                fail-closed / empty Null implementation the C# defaults to.
 *
 * The ONNX detector/embedder/plate impls in the C# tree (OnnxFaceDetector etc.)
 * are Microsoft.ML.OnnxRuntime + SixLabors.ImageSharp dependencies — the native
 * CV backend that this port injects through the IFaceDetector / IFaceEmbedder /
 * IPlateRecognizer vtables rather than reimplementing an ONNX runtime in C.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with a
 * matching *_free (NULL-safe), deep-copy getters, errors via NULL / count SIZE_MAX.
 * Linear arrays, no hashtable, no pthreads. Handler snapshot-then-fire (no lock held
 * across a callback). Timestamps Unix ms UTC, passed in.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Primitives — BoundingBox / LandmarkPoint (value structs, no owned fields)
 * =========================================================================== */

/* readonly record struct BoundingBox(int X, int Y, int Width, int Height). */
typedef struct {
    int x;
    int y;
    int width;
    int height;
} ca_bounding_box_t;

/* readonly record struct LandmarkPoint(int X, int Y). */
typedef struct {
    int x;
    int y;
} ca_landmark_point_t;

/* ===========================================================================
 * DetectedFace(Region, Confidence, Landmarks?)
 *
 * Landmarks is an optional owned array (NULL when absent — matches the C#
 * nullable IReadOnlyList<LandmarkPoint>?).
 * =========================================================================== */

typedef struct {
    ca_bounding_box_t    region;
    float                confidence;
    ca_landmark_point_t *landmarks;   /* owned, or NULL */
    size_t               landmark_count;
} ca_detected_face_t;

void ca_detected_face_free(ca_detected_face_t *f);
void ca_detected_face_free_array(ca_detected_face_t *arr, size_t count);
/* Deep-copy src into *dst (dst freshly owned). 0 / -1. */
int  ca_detected_face_copy(ca_detected_face_t *dst, const ca_detected_face_t *src);

/* ===========================================================================
 * FaceEmbedding(Vector, Dimension)
 * =========================================================================== */

typedef struct {
    float *vector;      /* owned (may be NULL when dimension 0) */
    int    dimension;
} ca_face_embedding_t;

void ca_face_embedding_free(ca_face_embedding_t *e);

/* ===========================================================================
 * LivenessResult(IsLive, Confidence, FailureReason?)
 * =========================================================================== */

typedef struct {
    bool  is_live;
    float confidence;
    char *failure_reason;   /* owned, or NULL */
} ca_liveness_result_t;

void ca_liveness_result_free(ca_liveness_result_t *r);

/* ===========================================================================
 * DocumentField(Key, Value, Confidence)
 * =========================================================================== */

typedef struct {
    char *key;          /* owned, non-null */
    char *value;        /* owned, non-null */
    float confidence;
} ca_document_field_t;

/* ===========================================================================
 * DocumentVerificationResult(IsValid, DocumentType, IssuingCountry, Fields,
 *                            OverallConfidence, Warnings?)
 * =========================================================================== */

typedef struct {
    bool                 is_valid;
    char                *document_type;    /* owned, non-null */
    char                *issuing_country;  /* owned, non-null */
    ca_document_field_t *fields;           /* owned (may be NULL/empty) */
    size_t               field_count;
    float                overall_confidence;
    char               **warnings;         /* owned array, or NULL */
    size_t               warning_count;
} ca_document_verification_result_t;

void ca_document_verification_result_free(ca_document_verification_result_t *r);

/* ===========================================================================
 * PlateRecognitionResult(PlateText, CountryHint?, Region, Confidence)
 * =========================================================================== */

typedef struct {
    char             *plate_text;    /* owned, non-null */
    char             *country_hint;  /* owned, or NULL */
    ca_bounding_box_t region;
    float             confidence;
} ca_plate_recognition_result_t;

void ca_plate_recognition_result_free(ca_plate_recognition_result_t *r);
void ca_plate_recognition_result_free_array(ca_plate_recognition_result_t *arr, size_t count);

/* ===========================================================================
 * BluetoothAnomaly(Source, Kind, Severity, Description, ObservedAtUtc)
 * =========================================================================== */

typedef struct {
    char   *source;         /* owned, non-null */
    char   *kind;           /* owned, non-null */
    float   severity;       /* 0..1 */
    char   *description;    /* owned, non-null */
    int64_t observed_at_utc_ms;
} ca_bluetooth_anomaly_t;

void ca_bluetooth_anomaly_free(ca_bluetooth_anomaly_t *a);

/* ===========================================================================
 * VideoPixelFormat + VideoFrame
 * =========================================================================== */

typedef enum {
    CA_VIDEO_PIXEL_YUV420 = 0,
    CA_VIDEO_PIXEL_NV21   = 1,
    CA_VIDEO_PIXEL_RGBA32 = 2,
    CA_VIDEO_PIXEL_BGR24  = 3,
    CA_VIDEO_PIXEL_JPEG   = 4
} ca_video_pixel_format_t;

/* VideoFrame(Bytes, Width, Height, PixelFormat, CapturedAtUtc, RotationDegrees?).
 * has_rotation models the nullable int? RotationDegrees. */
typedef struct {
    uint8_t                *bytes;      /* owned (may be NULL when len 0) */
    size_t                  byte_count;
    int                     width;
    int                     height;
    ca_video_pixel_format_t pixel_format;
    int64_t                 captured_at_utc_ms;
    bool                    has_rotation;
    int                     rotation_degrees;
} ca_video_frame_t;

void ca_video_frame_free(ca_video_frame_t *f);

/* ---------------------------------------------------------------------------
 * IVideoCapture — CaptureAsync yields VideoFrame's. Modelled (like
 * CircleAI.Voice.IAudioCapture) as an opaque source drained frame-by-frame.
 * The null source yields nothing; the scripted source is preloaded up-front.
 * --------------------------------------------------------------------------- */

typedef struct ca_video_capture ca_video_capture_t;

/* NullVideoCapture — yields no frames. */
ca_video_capture_t *ca_null_video_capture_create(void);
/* Scripted capture. Push frames with _push before draining. */
ca_video_capture_t *ca_scripted_video_capture_create(void);
void ca_video_capture_destroy(ca_video_capture_t *c);

/* Append a frame (bytes deep-copied) to a scripted source. 0 / -1. The frame's
 * `bytes` pointer is copied from (data,len); all other fields taken from *frame
 * (the frame's own bytes/byte_count are ignored — data/len are authoritative). */
int ca_scripted_video_capture_push(ca_video_capture_t *c,
                                   const uint8_t *data, size_t len,
                                   int width, int height,
                                   ca_video_pixel_format_t pixel_format,
                                   int64_t captured_at_utc_ms,
                                   bool has_rotation, int rotation_degrees);

/* Drain the next frame into *out (freshly owned; caller frees with
 * ca_video_frame_free). Returns true if a frame was produced, false at end. */
bool ca_video_capture_next(ca_video_capture_t *c, ca_video_frame_t *out);
/* Rewind the read cursor to the start. */
void ca_video_capture_reset(ca_video_capture_t *c);

/* ===========================================================================
 * IComputerVisionRuntime — Decode / Resize / BackendId
 *
 * The C# surface returns a backend-private opaque image (object?). In C that is
 * a void* the backend owns; the seam mirrors it directly. The Null runtime
 * reports BackendId "null" and returns NULL from Decode/Resize.
 * =========================================================================== */

typedef struct {
    void *self;
    /* DecodeAsync(imageBytes) -> opaque image (or NULL). */
    void       *(*decode)(void *self, const uint8_t *bytes, size_t len);
    /* ResizeAsync(image, w, h) -> new opaque image (or NULL). */
    void       *(*resize)(void *self, void *image, int width, int height);
    const char *(*backend_id)(void *self);   /* non-null */
} ca_cv_runtime_t;

void       *ca_cv_runtime_decode(const ca_cv_runtime_t *rt, const uint8_t *bytes, size_t len);
void       *ca_cv_runtime_resize(const ca_cv_runtime_t *rt, void *image, int width, int height);
const char *ca_cv_runtime_backend_id(const ca_cv_runtime_t *rt);

/* NullComputerVisionRuntime — BackendId "null"; decode/resize return NULL. */
ca_cv_runtime_t ca_null_cv_runtime(void);

/* ===========================================================================
 * IFaceDetector — DetectAsync(imageBytes) -> DetectedFace[]
 * =========================================================================== */

typedef struct {
    void *self;
    /* Fill a fresh owned array (*out_count). NULL + *out_count SIZE_MAX on error;
     * NULL + 0 when no faces. */
    ca_detected_face_t *(*detect)(void *self, const uint8_t *bytes, size_t len,
                                  size_t *out_count);
} ca_face_detector_t;

/* Dispatcher. */
ca_detected_face_t *ca_face_detector_detect(const ca_face_detector_t *d,
                                            const uint8_t *bytes, size_t len,
                                            size_t *out_count);

/* NullFaceDetector — returns no faces (NULL + 0). */
ca_face_detector_t ca_null_face_detector(void);

/* ===========================================================================
 * IFaceEmbedder — Dimension + EmbedAsync(imageBytes, face) -> FaceEmbedding
 * =========================================================================== */

typedef struct {
    void *self;
    int  (*dimension)(void *self);
    /* Fill *out (owned). 0 / -1. */
    int  (*embed)(void *self, const uint8_t *bytes, size_t len,
                  const ca_detected_face_t *face, ca_face_embedding_t *out);
} ca_face_embedder_t;

int ca_face_embedder_dimension(const ca_face_embedder_t *e);
int ca_face_embedder_embed(const ca_face_embedder_t *e, const uint8_t *bytes, size_t len,
                           const ca_detected_face_t *face, ca_face_embedding_t *out);

/* NullFaceEmbedder(dimension=512) — returns a zero vector at that dimension. */
typedef struct ca_null_face_embedder ca_null_face_embedder_t;
ca_null_face_embedder_t *ca_null_face_embedder_create(int dimension);
void ca_null_face_embedder_destroy(ca_null_face_embedder_t *e);
ca_face_embedder_t ca_null_face_embedder_as_iface(ca_null_face_embedder_t *e);

/* ===========================================================================
 * IFaceLivenessDetector — CheckAsync(imageBytes) -> LivenessResult
 * =========================================================================== */

typedef struct {
    void *self;
    int  (*check)(void *self, const uint8_t *bytes, size_t len,
                  ca_liveness_result_t *out);   /* 0 / -1 */
} ca_face_liveness_detector_t;

int ca_face_liveness_check(const ca_face_liveness_detector_t *d,
                           const uint8_t *bytes, size_t len,
                           ca_liveness_result_t *out);

/* NullFaceLivenessDetector — fail-closed: IsLive=false, Confidence=0,
 * FailureReason="no liveness backend registered". */
ca_face_liveness_detector_t ca_null_face_liveness_detector(void);

/* ===========================================================================
 * IDocumentVerifier — VerifyAsync(imageBytes) -> DocumentVerificationResult
 * =========================================================================== */

typedef struct {
    void *self;
    int  (*verify)(void *self, const uint8_t *bytes, size_t len,
                   ca_document_verification_result_t *out);   /* 0 / -1 */
} ca_document_verifier_t;

int ca_document_verify(const ca_document_verifier_t *v,
                       const uint8_t *bytes, size_t len,
                       ca_document_verification_result_t *out);

/* NullDocumentVerifier — fail-closed: IsValid=false, DocumentType="unknown",
 * IssuingCountry="unknown", no fields, confidence 0, one warning
 * "no document verifier backend registered". */
ca_document_verifier_t ca_null_document_verifier(void);

/* ===========================================================================
 * IPlateRecognizer — RecognizeAsync(imageBytes) -> PlateRecognitionResult[]
 * =========================================================================== */

typedef struct {
    void *self;
    ca_plate_recognition_result_t *(*recognize)(void *self,
                                                const uint8_t *bytes, size_t len,
                                                size_t *out_count);
} ca_plate_recognizer_t;

ca_plate_recognition_result_t *ca_plate_recognizer_recognize(
    const ca_plate_recognizer_t *r, const uint8_t *bytes, size_t len,
    size_t *out_count);

/* NullPlateRecognizer — returns no plates (NULL + 0). */
ca_plate_recognizer_t ca_null_plate_recognizer(void);

/* ===========================================================================
 * IBluetoothAnomalyDetector — Subscribe / Start / Stop / BackendId + Dispose
 *
 * Long-running lifecycle. Subscribe returns an opaque unsubscribe token. The
 * Null detector never fires. An in-memory detector is provided that lets a host
 * (or test) publish anomalies synchronously; delivery snapshots the handler list
 * and fires WITHOUT a lock held (no re-entrancy self-deadlock), and buffers no
 * events (fan-out is immediate to currently-attached, started subscribers —
 * mirroring the C# event model where Start gates delivery). Subscribe before
 * Publish to receive; unbuffered by design.
 * =========================================================================== */

/* Anomaly handler — `a` borrowed for the call. */
typedef void (*ca_bt_anomaly_handler_fn)(void *user, const ca_bluetooth_anomaly_t *a);

typedef struct {
    void *self;
    /* Subscribe(handler,user) -> opaque token (or NULL on OOM). */
    void       *(*subscribe)(void *self, ca_bt_anomaly_handler_fn handler, void *user);
    void        (*unsubscribe)(void *self, void *token);
    int         (*start)(void *self);   /* idempotent; 0 / -1 */
    int         (*stop)(void *self);    /* idempotent; 0 / -1 */
    const char *(*backend_id)(void *self);
} ca_bluetooth_anomaly_detector_t;

void       *ca_bt_anomaly_subscribe(const ca_bluetooth_anomaly_detector_t *d,
                                    ca_bt_anomaly_handler_fn handler, void *user);
void        ca_bt_anomaly_unsubscribe(const ca_bluetooth_anomaly_detector_t *d, void *token);
int         ca_bt_anomaly_start(const ca_bluetooth_anomaly_detector_t *d);
int         ca_bt_anomaly_stop(const ca_bluetooth_anomaly_detector_t *d);
const char *ca_bt_anomaly_backend_id(const ca_bluetooth_anomaly_detector_t *d);

/* NullBluetoothAnomalyDetector — BackendId "null"; Subscribe is a no-op handle;
 * Start/Stop succeed; never fires. */
ca_bluetooth_anomaly_detector_t ca_null_bluetooth_anomaly_detector(void);

/* In-memory detector — BackendId "in-memory". Publish delivers to subscribers
 * only while started (Start()/Stop() gate it, idempotent). */
typedef struct ca_inmem_bluetooth_anomaly_detector ca_inmem_bluetooth_anomaly_detector_t;
ca_inmem_bluetooth_anomaly_detector_t *ca_inmem_bluetooth_anomaly_detector_create(void);
void ca_inmem_bluetooth_anomaly_detector_destroy(ca_inmem_bluetooth_anomaly_detector_t *d);
ca_bluetooth_anomaly_detector_t ca_inmem_bluetooth_anomaly_detector_as_iface(
    ca_inmem_bluetooth_anomaly_detector_t *d);
/* Publish one anomaly to all currently-attached handlers (only when started).
 * `a` borrowed. Returns the number of handlers notified (0 when stopped). */
size_t ca_inmem_bluetooth_anomaly_publish(ca_inmem_bluetooth_anomaly_detector_t *d,
                                          const ca_bluetooth_anomaly_t *a);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VISION_H */
