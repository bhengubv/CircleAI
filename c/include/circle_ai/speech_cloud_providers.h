#ifndef CIRCLE_AI_SPEECH_CLOUD_PROVIDERS_H
#define CIRCLE_AI_SPEECH_CLOUD_PROVIDERS_H

/*
 * speech_cloud_providers.h — CircleAI.Speech.Cloud provider recognizers +
 * synthesizers (C11 port).
 *
 * Ports the 12 cloud voice backends from src/CircleAI.Speech.Cloud/:
 *   Recognizers (ISpeechRecognizer): OpenAI Whisper, Deepgram, Azure, Google,
 *                                    AssemblyAI, Cartesia.
 *   Synthesizers (ISpeechSynthesizer): OpenAI TTS, Deepgram Aura, Azure,
 *                                      Google, Cartesia Sonic, ElevenLabs,
 *                                      PlayHT.
 *
 * The recognizer / synthesizer vtables (ca_speech_recognizer_t /
 * ca_speech_synthesizer_t) and the result records (ca_transcription_result_t /
 * ca_synthesis_result_t / ca_transcribed_segment_t) are the ones already
 * defined in speech.h — these providers plug into that surface.
 *
 * The C# backends drive an HttpClient. That real HTTP call is the ONE external
 * dependency, so it is injected here as a ca_ fn-ptr seam (ca_speech_http_t),
 * mirroring the telephony carrier ca_tel_http_t pattern. Everything else — the
 * WAV-envelope construction, multipart form assembly, JSON request bodies, the
 * response-parsing, the base64 audio encode/decode, the PCM sample→duration
 * maths, and the fail-soft "not configured -> empty result" behaviour — is
 * ported as real logic in C.
 *
 * Each provider is created from an options struct + an injected HTTP transport.
 * The returned handle exposes as_recognizer() / as_synthesizer() to obtain the
 * speech.h vtable value. IsConfigured mirrors the C# guard (non-blank key, etc).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, deep-copy result getters
 * (filled via speech.h's *_free), errors via NULL / -1. Pure C11 + libc.
 */

#include "circle_ai/speech.h"   /* result types + recognizer/synth vtables */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Injected HTTP transport seam (the "real HttpClient is injected" boundary).
 *
 * The provider builds the exact method + path + headers + body the C# adapter
 * would, hands them here, and parses the returned status + response bytes.
 * =========================================================================== */

/* One request header (name/value). */
typedef struct { const char *name; const char *value; } ca_speech_http_header_t;

/* The multipart form for the OpenAI/Cartesia/AssemblyAI uploads is modelled as
 * either raw bytes (Content-Type set via a header) OR a set of form fields plus
 * one binary "file" part. To keep the seam simple the provider serializes the
 * request BODY into bytes itself (multipart bodies are built as raw bytes with
 * a boundary and the matching Content-Type header). So the transport only ever
 * sees: method + path + headers + a raw request body byte range. */

typedef struct {
    void *self;
    /* Perform one request. `method` is "GET"/"POST". `path` is relative to the
     * provider base URL, including any query string. `headers`/`header_count`
     * carry every header the binding computed (Authorization, Content-Type,
     * provider-specific keys). `body`/`body_len` is the request entity (may be
     * NULL/0 for GET). On success writes the HTTP status into *out_status and a
     * freshly malloc'd response body (bytes) into *out_body / *out_body_len
     * (set *out_body to NULL for an empty body) and returns 0. Return -1 to
     * model a transport exception thrown before any response. */
    int (*request)(void *self, const char *method, const char *path,
                   const ca_speech_http_header_t *headers, size_t header_count,
                   const uint8_t *body, size_t body_len,
                   int *out_status, uint8_t **out_body, size_t *out_body_len);
} ca_speech_http_t;

/* ===========================================================================
 * Options structs (defaults match Options.cs; pass NULL to a create() options
 * pointer to accept every default). base_address NULL -> the documented host.
 * =========================================================================== */

typedef struct {
    const char *base_address;         /* "https://api.openai.com" */
    const char *api_key;              /* NULL == unset */
    const char *transcription_model;  /* "whisper-1" */
    const char *speech_model;         /* "tts-1" */
    const char *default_voice;        /* "alloy" */
    int         pcm_sample_rate_hz;   /* 24000 */
} ca_speech_openai_options_t;

typedef struct {
    const char *base_address;         /* "https://api.deepgram.com" */
    const char *api_key;
    const char *model;                /* "nova-2-general" */
} ca_speech_deepgram_stt_options_t;

typedef struct {
    const char *base_address;         /* "https://api.deepgram.com" */
    const char *api_key;
    const char *voice;                /* "aura-asteria-en" */
    int         pcm_sample_rate_hz;   /* 24000 */
} ca_speech_deepgram_tts_options_t;

typedef struct {
    const char *base_address;         /* region endpoint; required for IsConfigured */
    const char *api_key;
    const char *language_code;        /* "en-US" */
} ca_speech_azure_stt_options_t;

typedef struct {
    const char *base_address;         /* region endpoint; required for IsConfigured */
    const char *api_key;
    const char *language_code;        /* "en-US" */
    const char *default_voice_name;   /* "en-US-AvaMultilingualNeural" */
    int         pcm_sample_rate_hz;   /* 24000 */
} ca_speech_azure_tts_options_t;

typedef struct {
    const char *base_address;         /* "https://speech.googleapis.com" */
    const char *api_key;
    const char *language_code;        /* "en-US" */
} ca_speech_google_stt_options_t;

typedef struct {
    const char *base_address;         /* "https://texttospeech.googleapis.com" */
    const char *api_key;
    const char *language_code;        /* "en-US" */
    const char *default_voice_name;   /* "en-US-Studio-O" */
    int         pcm_sample_rate_hz;   /* 24000 */
} ca_speech_google_tts_options_t;

typedef struct {
    const char *base_address;         /* "https://api.assemblyai.com" */
    const char *api_key;
    const char *speech_model;         /* "universal" */
} ca_speech_assemblyai_options_t;

typedef struct {
    const char *base_address;         /* "https://api.cartesia.ai" */
    const char *api_key;
    const char *model;                /* "ink-whisper" */
    const char *cartesia_version;     /* "2025-04-16" */
} ca_speech_cartesia_stt_options_t;

typedef struct {
    const char *base_address;         /* "https://api.cartesia.ai" */
    const char *api_key;
    const char *model;                /* "sonic-2" */
    const char *default_voice_id;     /* a sample */
    const char *output_container;     /* "raw" */
    const char *output_encoding;      /* "pcm_s16le" */
    int         pcm_sample_rate_hz;   /* 24000 */
    const char *cartesia_version;     /* "2025-04-16" */
} ca_speech_cartesia_tts_options_t;

typedef struct {
    const char *base_address;         /* "https://api.elevenlabs.io" */
    const char *api_key;
    const char *default_voice_id;     /* "21m00Tcm4TlvDq8ikWAM" */
    const char *model;                /* "eleven_flash_v2_5" */
    const char *output_format;        /* "pcm_24000" */
    int         pcm_sample_rate_hz;   /* 24000 */
} ca_speech_elevenlabs_options_t;

typedef struct {
    const char *base_address;         /* "https://api.play.ht" */
    const char *api_key;
    const char *user_id;              /* required for IsConfigured */
    const char *default_voice;        /* s3://... manifest */
    const char *model;                /* "PlayDialog" */
    int         pcm_sample_rate_hz;   /* 24000 */
} ca_speech_playht_options_t;

/* ===========================================================================
 * Recognizers — each borrows the injected http (must outlive the recognizer)
 * and copies its options. NULL on OOM / NULL http.
 * =========================================================================== */

typedef struct ca_speech_openai_recognizer     ca_speech_openai_recognizer_t;
typedef struct ca_speech_deepgram_recognizer   ca_speech_deepgram_recognizer_t;
typedef struct ca_speech_azure_recognizer      ca_speech_azure_recognizer_t;
typedef struct ca_speech_google_recognizer     ca_speech_google_recognizer_t;
typedef struct ca_speech_assemblyai_recognizer ca_speech_assemblyai_recognizer_t;
typedef struct ca_speech_cartesia_recognizer   ca_speech_cartesia_recognizer_t;

ca_speech_openai_recognizer_t *ca_speech_openai_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_openai_options_t *options);
void ca_speech_openai_recognizer_destroy(ca_speech_openai_recognizer_t *r);
bool ca_speech_openai_recognizer_is_configured(const ca_speech_openai_recognizer_t *r);
ca_speech_recognizer_t ca_speech_openai_recognizer_as_recognizer(ca_speech_openai_recognizer_t *r);

ca_speech_deepgram_recognizer_t *ca_speech_deepgram_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_deepgram_stt_options_t *options);
void ca_speech_deepgram_recognizer_destroy(ca_speech_deepgram_recognizer_t *r);
bool ca_speech_deepgram_recognizer_is_configured(const ca_speech_deepgram_recognizer_t *r);
ca_speech_recognizer_t ca_speech_deepgram_recognizer_as_recognizer(ca_speech_deepgram_recognizer_t *r);

ca_speech_azure_recognizer_t *ca_speech_azure_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_azure_stt_options_t *options);
void ca_speech_azure_recognizer_destroy(ca_speech_azure_recognizer_t *r);
bool ca_speech_azure_recognizer_is_configured(const ca_speech_azure_recognizer_t *r);
ca_speech_recognizer_t ca_speech_azure_recognizer_as_recognizer(ca_speech_azure_recognizer_t *r);

ca_speech_google_recognizer_t *ca_speech_google_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_google_stt_options_t *options);
void ca_speech_google_recognizer_destroy(ca_speech_google_recognizer_t *r);
bool ca_speech_google_recognizer_is_configured(const ca_speech_google_recognizer_t *r);
ca_speech_recognizer_t ca_speech_google_recognizer_as_recognizer(ca_speech_google_recognizer_t *r);

ca_speech_assemblyai_recognizer_t *ca_speech_assemblyai_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_assemblyai_options_t *options);
void ca_speech_assemblyai_recognizer_destroy(ca_speech_assemblyai_recognizer_t *r);
bool ca_speech_assemblyai_recognizer_is_configured(const ca_speech_assemblyai_recognizer_t *r);
ca_speech_recognizer_t ca_speech_assemblyai_recognizer_as_recognizer(ca_speech_assemblyai_recognizer_t *r);

ca_speech_cartesia_recognizer_t *ca_speech_cartesia_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_cartesia_stt_options_t *options);
void ca_speech_cartesia_recognizer_destroy(ca_speech_cartesia_recognizer_t *r);
bool ca_speech_cartesia_recognizer_is_configured(const ca_speech_cartesia_recognizer_t *r);
ca_speech_recognizer_t ca_speech_cartesia_recognizer_as_recognizer(ca_speech_cartesia_recognizer_t *r);

/* ===========================================================================
 * Synthesizers
 * =========================================================================== */

typedef struct ca_speech_openai_synthesizer    ca_speech_openai_synthesizer_t;
typedef struct ca_speech_deepgram_synthesizer  ca_speech_deepgram_synthesizer_t;
typedef struct ca_speech_azure_synthesizer     ca_speech_azure_synthesizer_t;
typedef struct ca_speech_google_synthesizer    ca_speech_google_synthesizer_t;
typedef struct ca_speech_cartesia_synthesizer  ca_speech_cartesia_synthesizer_t;
typedef struct ca_speech_elevenlabs_synthesizer ca_speech_elevenlabs_synthesizer_t;
typedef struct ca_speech_playht_synthesizer    ca_speech_playht_synthesizer_t;

ca_speech_openai_synthesizer_t *ca_speech_openai_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_openai_options_t *options);
void ca_speech_openai_synthesizer_destroy(ca_speech_openai_synthesizer_t *s);
bool ca_speech_openai_synthesizer_is_configured(const ca_speech_openai_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_openai_synthesizer_as_synthesizer(ca_speech_openai_synthesizer_t *s);

ca_speech_deepgram_synthesizer_t *ca_speech_deepgram_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_deepgram_tts_options_t *options);
void ca_speech_deepgram_synthesizer_destroy(ca_speech_deepgram_synthesizer_t *s);
bool ca_speech_deepgram_synthesizer_is_configured(const ca_speech_deepgram_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_deepgram_synthesizer_as_synthesizer(ca_speech_deepgram_synthesizer_t *s);

ca_speech_azure_synthesizer_t *ca_speech_azure_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_azure_tts_options_t *options);
void ca_speech_azure_synthesizer_destroy(ca_speech_azure_synthesizer_t *s);
bool ca_speech_azure_synthesizer_is_configured(const ca_speech_azure_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_azure_synthesizer_as_synthesizer(ca_speech_azure_synthesizer_t *s);

ca_speech_google_synthesizer_t *ca_speech_google_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_google_tts_options_t *options);
void ca_speech_google_synthesizer_destroy(ca_speech_google_synthesizer_t *s);
bool ca_speech_google_synthesizer_is_configured(const ca_speech_google_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_google_synthesizer_as_synthesizer(ca_speech_google_synthesizer_t *s);

ca_speech_cartesia_synthesizer_t *ca_speech_cartesia_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_cartesia_tts_options_t *options);
void ca_speech_cartesia_synthesizer_destroy(ca_speech_cartesia_synthesizer_t *s);
bool ca_speech_cartesia_synthesizer_is_configured(const ca_speech_cartesia_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_cartesia_synthesizer_as_synthesizer(ca_speech_cartesia_synthesizer_t *s);

ca_speech_elevenlabs_synthesizer_t *ca_speech_elevenlabs_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_elevenlabs_options_t *options);
void ca_speech_elevenlabs_synthesizer_destroy(ca_speech_elevenlabs_synthesizer_t *s);
bool ca_speech_elevenlabs_synthesizer_is_configured(const ca_speech_elevenlabs_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_elevenlabs_synthesizer_as_synthesizer(ca_speech_elevenlabs_synthesizer_t *s);

ca_speech_playht_synthesizer_t *ca_speech_playht_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_playht_options_t *options);
void ca_speech_playht_synthesizer_destroy(ca_speech_playht_synthesizer_t *s);
bool ca_speech_playht_synthesizer_is_configured(const ca_speech_playht_synthesizer_t *s);
ca_speech_synthesizer_t ca_speech_playht_synthesizer_as_synthesizer(ca_speech_playht_synthesizer_t *s);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SPEECH_CLOUD_PROVIDERS_H */
