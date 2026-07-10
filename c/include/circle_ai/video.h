#ifndef CIRCLE_AI_VIDEO_H
#define CIRCLE_AI_VIDEO_H

/*
 * video.h — CircleAI.Video (C11 port).
 *
 * Ports the CircleAI.Video contract surface 1:1 — the txtMe Video Mail stack.
 * Three interfaces (generator, script rewriter, style catalogue) with their Null
 * defaults; the real on-device generators (CogVideoX-2B, LTX-Video) are injected
 * behind the IVideoGenerator vtable.
 *
 *   Primitives : StyleId; VideoResolution (+P480/P720/P1080 presets);
 *                StyleReferenceFrame(ImageBytes, MimeType, Caption?);
 *                StyleAttribution(Source, License, Url?);
 *                StyleReference(Id, DisplayName, ShortDescription, Attribution,
 *                               VoicePersonaId?, Frames);
 *                AudioTrack(AudioPcm16Mono, SampleRateHz, Duration);
 *                VideoGenerationRequest(...); VideoGenerationResult(...);
 *                StyleScriptRequest(...); StyleScriptResult(...).
 *   Generator  : IVideoGenerator — NullVideoGenerator (empty video/mp4) + seam.
 *   Script     : IStyleScript — NullStyleScript (echoes SourceMessage) + seam.
 *   Catalogue  : IStyleReference — InMemoryStyleReference (register/get/list,
 *                OrdinalIgnoreCase, last-write-wins) + NullStyleReference.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with a
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Durations in whole milliseconds (TimeSpan).
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
 * StyleId(Value) — a string wrapper. In C it is just a borrowed/owned char*
 * depending on context; helpers below match the ToString / implicit-string ops.
 * =========================================================================== */

/* ===========================================================================
 * VideoResolution(Width, Height)
 * =========================================================================== */

typedef struct {
    int width;
    int height;
} ca_video_resolution_t;

ca_video_resolution_t ca_video_resolution_p480(void);   /* 720x480 */
ca_video_resolution_t ca_video_resolution_p720(void);   /* 1280x720 */
ca_video_resolution_t ca_video_resolution_p1080(void);  /* 1920x1080 */

/* ===========================================================================
 * StyleReferenceFrame(ImageBytes, MimeType, Caption?)
 * =========================================================================== */

typedef struct {
    uint8_t *image_bytes;   /* owned (may be NULL when len 0) */
    size_t   image_len;
    char    *mime_type;     /* owned, non-null */
    char    *caption;       /* owned, or NULL */
} ca_style_reference_frame_t;

/* ===========================================================================
 * StyleAttribution(Source, License, Url?)
 * =========================================================================== */

typedef struct {
    char *source;    /* owned, non-null */
    char *license;   /* owned, non-null */
    char *url;       /* owned, or NULL */
} ca_style_attribution_t;

/* ===========================================================================
 * StyleReference(Id, DisplayName, ShortDescription, Attribution,
 *                VoicePersonaId?, Frames)
 * =========================================================================== */

typedef struct {
    char                       *id;                 /* owned, non-null (StyleId.Value) */
    char                       *display_name;       /* owned, non-null */
    char                       *short_description;  /* owned, non-null */
    ca_style_attribution_t      attribution;
    char                       *voice_persona_id;   /* owned, or NULL */
    ca_style_reference_frame_t *frames;             /* owned (may be NULL/empty) */
    size_t                      frame_count;
} ca_style_reference_t;

void ca_style_reference_free(ca_style_reference_t *s);
void ca_style_reference_free_array(ca_style_reference_t *arr, size_t count);
/* Deep-copy src into *dst (freshly owned). 0 / -1. */
int  ca_style_reference_copy(ca_style_reference_t *dst, const ca_style_reference_t *src);

/* ===========================================================================
 * AudioTrack(AudioPcm16Mono, SampleRateHz, Duration)
 * =========================================================================== */

typedef struct {
    uint8_t *audio_pcm16_mono;   /* owned (may be NULL when len 0) */
    size_t   audio_len;
    int      sample_rate_hz;
    int64_t  duration_ms;
} ca_audio_track_t;

void ca_audio_track_free(ca_audio_track_t *t);

/* ===========================================================================
 * VideoGenerationRequest(Prompt, Duration, Resolution, FrameRate=24, StyleId?,
 *                        ReferenceImage?, AudioTrack?, Seed?)
 *
 * The optional record fields are modelled with has_* flags + a NULL pointer for
 * the reference frame / audio track. `bytes` for those two are borrowed for the
 * call (this is a request, not owned state).
 * =========================================================================== */

typedef struct {
    const char                     *prompt;          /* borrowed */
    int64_t                         duration_ms;
    ca_video_resolution_t           resolution;
    int                             frame_rate;      /* default 24 */
    bool                            has_style_id;
    const char                     *style_id;        /* borrowed, valid when has_style_id */
    const ca_style_reference_frame_t *reference_image; /* borrowed, or NULL */
    const ca_audio_track_t         *audio_track;     /* borrowed, or NULL */
    bool                            has_seed;
    int64_t                         seed;
} ca_video_generation_request_t;

/* Initialise with record defaults (FrameRate=24, no style/ref/audio/seed). */
void ca_video_generation_request_init(ca_video_generation_request_t *req,
                                      const char *prompt,
                                      int64_t duration_ms,
                                      ca_video_resolution_t resolution);

/* ===========================================================================
 * VideoGenerationResult(VideoBytes, MimeType, Duration, FrameCount, Resolution,
 *                       BackendId)
 * =========================================================================== */

typedef struct {
    uint8_t              *video_bytes;   /* owned (may be NULL when len 0) */
    size_t                video_len;
    char                 *mime_type;     /* owned, non-null */
    int64_t               duration_ms;
    int                   frame_count;
    ca_video_resolution_t resolution;
    char                 *backend_id;    /* owned, non-null */
} ca_video_generation_result_t;

void ca_video_generation_result_free(ca_video_generation_result_t *r);

/* ===========================================================================
 * StyleScriptRequest(SourceMessage, Style, SpeakerHint?, LanguageHint?)
 * =========================================================================== */

typedef struct {
    const char *source_message;   /* borrowed */
    const char *style;            /* borrowed (StyleId.Value) */
    const char *speaker_hint;     /* borrowed, or NULL */
    const char *language_hint;    /* borrowed, or NULL */
} ca_style_script_request_t;

/* ===========================================================================
 * StyleScriptResult(RewrittenText, Style, VoicePersonaId?, EstimatedSpokenDuration)
 * =========================================================================== */

typedef struct {
    char    *rewritten_text;    /* owned, non-null */
    char    *style;             /* owned, non-null (StyleId.Value) */
    char    *voice_persona_id;  /* owned, or NULL */
    int64_t  estimated_spoken_duration_ms;
} ca_style_script_result_t;

void ca_style_script_result_free(ca_style_script_result_t *r);

/* ===========================================================================
 * IVideoGenerator — BackendId + GenerateAsync
 *
 * generate: fill *out (owned). 0 / -1. The C# GenerateAsync "throws if the
 * device cannot satisfy the request" — a seam impl signals that with -1.
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);   /* non-null */
    int         (*generate)(void *self, const ca_video_generation_request_t *req,
                            ca_video_generation_result_t *out);
} ca_video_generator_t;

const char *ca_video_generator_backend_id(const ca_video_generator_t *g);
int         ca_video_generator_generate(const ca_video_generator_t *g,
                                        const ca_video_generation_request_t *req,
                                        ca_video_generation_result_t *out);

/* NullVideoGenerator — BackendId "null"; returns an empty video (0 bytes, mime
 * "video/mp4", zero duration, 0 frames, the request's Resolution, BackendId
 * "null"). */
ca_video_generator_t ca_null_video_generator(void);

/* ===========================================================================
 * IStyleScript — BackendId + RewriteAsync
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);   /* non-null */
    int         (*rewrite)(void *self, const ca_style_script_request_t *req,
                           ca_style_script_result_t *out);   /* 0 / -1 */
} ca_style_script_t;

const char *ca_style_script_backend_id(const ca_style_script_t *s);
int         ca_style_script_rewrite(const ca_style_script_t *s,
                                    const ca_style_script_request_t *req,
                                    ca_style_script_result_t *out);

/* NullStyleScript — BackendId "null"; echoes SourceMessage unchanged, Style
 * passed through, VoicePersonaId NULL, EstimatedSpokenDuration zero. */
ca_style_script_t ca_null_style_script(void);

/* ===========================================================================
 * IStyleReference — BackendId + Register / Get / List
 * =========================================================================== */

/* InMemoryStyleReference — BackendId "in-memory". OrdinalIgnoreCase keying,
 * last-write-wins on Register. */
typedef struct ca_style_reference_store ca_style_reference_store_t;

ca_style_reference_store_t *ca_inmemory_style_reference_create(void);
void ca_inmemory_style_reference_destroy(ca_style_reference_store_t *store);
const char *ca_inmemory_style_reference_backend_id(const ca_style_reference_store_t *store);

/* RegisterAsync — deep-copies `style`, upserting by Id (case-insensitive). 0/-1.*/
int ca_inmemory_style_reference_register(ca_style_reference_store_t *store,
                                         const ca_style_reference_t *style);

/* GetAsync — deep-copies the match into *out (caller frees with
 * ca_style_reference_free). Returns true if found, false when absent. */
bool ca_inmemory_style_reference_get(const ca_style_reference_store_t *store,
                                     const char *style_id,
                                     ca_style_reference_t *out);

/* ListAsync — fresh deep-copied array (caller frees with
 * ca_style_reference_free_array); *out_count set (0 -> NULL). Insertion order. */
ca_style_reference_t *ca_inmemory_style_reference_list(
    const ca_style_reference_store_t *store, size_t *out_count);

size_t ca_inmemory_style_reference_count(const ca_style_reference_store_t *store);

/* NullStyleReference — BackendId "null"; Register is a no-op, Get always misses,
 * List is always empty. */
typedef struct ca_null_style_reference ca_null_style_reference_t;
ca_null_style_reference_t *ca_null_style_reference_create(void);
void ca_null_style_reference_destroy(ca_null_style_reference_t *s);
const char *ca_null_style_reference_backend_id(const ca_null_style_reference_t *s);
int  ca_null_style_reference_register(ca_null_style_reference_t *s,
                                      const ca_style_reference_t *style);
bool ca_null_style_reference_get(const ca_null_style_reference_t *s,
                                 const char *style_id, ca_style_reference_t *out);
ca_style_reference_t *ca_null_style_reference_list(const ca_null_style_reference_t *s,
                                                   size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VIDEO_H */
