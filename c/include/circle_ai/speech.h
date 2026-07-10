#ifndef CIRCLE_AI_SPEECH_H
#define CIRCLE_AI_SPEECH_H

/*
 * speech.h — CircleAI.Speech contract surface (C11 port).
 *
 * Ports CircleAI.Speech 1:1 (Contracts.cs + the deterministic, no-model
 * implementations that ship in-box; the cloud HTTP backends and ONNX runners
 * are injected dependencies modelled as vtables):
 *
 *   Records   : TranscribedSegment, TranscriptionResult, SynthesisResult,
 *               OcrResult / OcrTextBlock, WakeWordEvent, EndOfTurnResult,
 *               VadFrameResult.
 *   Enums     : AudioCodec (Pcm16 / MuLaw / ALaw).
 *   ASR/TTS   : ISpeechRecognizer / ISpeechSynthesizer vtables + Null impls +
 *               a deterministic keyword recognizer and a template synthesizer.
 *   WakeWord  : IWakeWordDetector — Subscribe(handler)/Start/Stop; Null impl +
 *               a deterministic keyword detector fed manual audio frames.
 *   AEC       : IEchoCanceller — Null (pass-through), NLMS adaptive filter,
 *               WebRTC wrapper (falls back to NLMS when no runner wired).
 *   Denoise   : INoiseReducer — Null, SpectralSubtraction gate, Krisp /
 *               DeepFilterNet wrappers (fall back to spectral subtraction).
 *   EOT       : IEndOfTurnDetector — Null, RuleBased (punctuation + silence),
 *               SmartTurn wrapper (falls back to RuleBased).
 *   VAD       : IVoiceActivityDetector (per-frame) — Null (always speech),
 *               Energy (RMS + ZCR + hangover), Silero wrapper (falls back to
 *               Energy scoring).
 *   Convert   : AudioFormatConverter — mu-law / a-law <-> PCM-16, linear resample.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Byte formats (G.711, PCM-16 LE) match the
 * C# BinaryPrimitives paths exactly. Durations are milliseconds; timestamps
 * Unix ms UTC, passed in.
 *
 * Pure C11 + libc + libm.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Records
 * =========================================================================== */

/* TranscribedSegment(Text, Offset, Duration, Language?, Confidence). Offset and
 * Duration are TimeSpans expressed in milliseconds. */
typedef struct {
    char   *text;          /* owned, non-null */
    int64_t offset_ms;
    int64_t duration_ms;
    char   *language;      /* owned, NULL == null */
    float   confidence;
} ca_transcribed_segment_t;

/* TranscriptionResult(Text, Language?, Segments, TotalDuration_ms). */
typedef struct {
    char                     *text;          /* owned, non-null */
    char                     *language;      /* owned, NULL == null */
    ca_transcribed_segment_t *segments;      /* owned array (may be NULL/empty) */
    size_t                    segment_count;
    int64_t                   total_duration_ms;
} ca_transcription_result_t;

/* SynthesisResult(AudioPcm16Mono, SampleRateHz, Duration_ms). */
typedef struct {
    uint8_t *audio_pcm16_mono; /* owned (may be NULL when len 0) */
    size_t   audio_len;
    int      sample_rate_hz;
    int64_t  duration_ms;
} ca_synthesis_result_t;

/* OcrTextBlock(Text, X, Y, Width, Height, Confidence, Language?). */
typedef struct {
    char *text;        /* owned, non-null */
    int   x, y, width, height;
    float confidence;
    char *language;    /* owned, NULL == null */
} ca_ocr_text_block_t;

/* OcrResult(Text, Blocks). */
typedef struct {
    char                *text;        /* owned, non-null */
    ca_ocr_text_block_t *blocks;      /* owned array (may be NULL/empty) */
    size_t               block_count;
} ca_ocr_result_t;

/* WakeWordEvent(Keyword, Confidence, DetectedAtUtc_ms). */
typedef struct {
    char   *keyword;         /* owned, non-null */
    float   confidence;
    int64_t detected_at_utc_ms;
} ca_wake_word_event_t;

/* EndOfTurnResult(IsComplete, Confidence, WaitMoreMs). */
typedef struct {
    bool  is_complete;
    float confidence;
    int   wait_more_ms;
} ca_end_of_turn_result_t;

/* VadFrameResult(IsSpeech, SpeechProbability, Offset_ms). */
typedef struct {
    bool    is_speech;
    float   speech_probability;
    int64_t offset_ms;
} ca_vad_frame_result_t;

void ca_transcribed_segment_free(ca_transcribed_segment_t *s);
void ca_transcribed_segment_free_array(ca_transcribed_segment_t *arr, size_t count);
ca_transcribed_segment_t *ca_transcribed_segment_copy(ca_transcribed_segment_t *dst,
                                                      const ca_transcribed_segment_t *src);

void ca_transcription_result_free(ca_transcription_result_t *r);
ca_transcription_result_t *ca_transcription_result_copy(ca_transcription_result_t *dst,
                                                        const ca_transcription_result_t *src);

void ca_synthesis_result_free(ca_synthesis_result_t *r);

void ca_ocr_text_block_free(ca_ocr_text_block_t *b);
void ca_ocr_result_free(ca_ocr_result_t *r);

void ca_wake_word_event_free(ca_wake_word_event_t *e);

/* ===========================================================================
 * ISpeechRecognizer (vtable) + implementations
 *
 *   backend_id()                         : self-identification string (borrowed).
 *   transcribe(audio,len,rate,hint?,&out): synchronous ValueTask completion —
 *                                          fills *out (owned; caller frees with
 *                                          ca_transcription_result_free). 0 / -1.
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);
    int (*transcribe)(void *self, const uint8_t *audio_pcm16_mono, size_t len,
                      int sample_rate_hz, const char *language_hint,
                      ca_transcription_result_t *out);
} ca_speech_recognizer_t;

/* NullSpeechRecognizer — BackendId "null"; empty text, Language == hint, no
 * segments, zero duration. */
typedef struct ca_null_speech_recognizer ca_null_speech_recognizer_t;
ca_null_speech_recognizer_t *ca_null_speech_recognizer_create(void);
void ca_null_speech_recognizer_destroy(ca_null_speech_recognizer_t *r);
ca_speech_recognizer_t ca_null_speech_recognizer_as_recognizer(
    ca_null_speech_recognizer_t *r);

/* KeywordSpeechRecognizer — deterministic, hermetic recognizer. Maps the sample
 * count (len/2) to a canned phrase from a host-supplied ordered keyword table:
 * a segment fires for each phrase whose min_samples <= sampleCount. Produces one
 * TranscribedSegment per matched keyword (offset accumulated), joined by spaces
 * into Text. Duration = sampleCount / rate (ms). Language echoes the hint. When
 * no keyword matches, Text is empty. BackendId "keyword". */
typedef struct ca_keyword_speech_recognizer ca_keyword_speech_recognizer_t;
ca_keyword_speech_recognizer_t *ca_keyword_speech_recognizer_create(void);
void ca_keyword_speech_recognizer_destroy(ca_keyword_speech_recognizer_t *r);
/* Append a keyword rule: when the input has >= min_samples samples, `phrase`
 * fires with `confidence`. Rules match in insertion order. Returns 0 / -1. */
int ca_keyword_speech_recognizer_add(ca_keyword_speech_recognizer_t *r,
                                     size_t min_samples, const char *phrase,
                                     float confidence);
ca_speech_recognizer_t ca_keyword_speech_recognizer_as_recognizer(
    ca_keyword_speech_recognizer_t *r);

/* ===========================================================================
 * ISpeechSynthesizer (vtable) + implementations
 *
 *   backend_id()                        : self-identification string (borrowed).
 *   synthesize(text,voice?,hint?,&out)  : fills *out (owned; caller frees with
 *                                         ca_synthesis_result_free). 0 / -1.
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);
    int (*synthesize)(void *self, const char *text, const char *voice_id,
                      const char *language_hint, ca_synthesis_result_t *out);
} ca_speech_synthesizer_t;

/* NullSpeechSynthesizer — BackendId "null"; empty audio, SampleRateHz 16000,
 * zero duration. */
typedef struct ca_null_speech_synthesizer ca_null_speech_synthesizer_t;
ca_null_speech_synthesizer_t *ca_null_speech_synthesizer_create(void);
void ca_null_speech_synthesizer_destroy(ca_null_speech_synthesizer_t *s);
ca_speech_synthesizer_t ca_null_speech_synthesizer_as_synthesizer(
    ca_null_speech_synthesizer_t *s);

/* TemplateSpeechSynthesizer — deterministic tone synthesizer. Emits
 * `samples_per_char` PCM-16 samples per UTF-8 code unit of `text` at
 * `sample_rate_hz`, each sample a fixed-amplitude square wave whose half-period
 * is derived from the char code (so identical text yields identical bytes).
 * Duration = totalSamples / rate (ms). BackendId "template". */
typedef struct ca_template_speech_synthesizer ca_template_speech_synthesizer_t;
ca_template_speech_synthesizer_t *ca_template_speech_synthesizer_create(
    int sample_rate_hz, int samples_per_char);
void ca_template_speech_synthesizer_destroy(ca_template_speech_synthesizer_t *s);
ca_speech_synthesizer_t ca_template_speech_synthesizer_as_synthesizer(
    ca_template_speech_synthesizer_t *s);

/* ===========================================================================
 * IWakeWordDetector — Subscribe(handler)/StartAsync/StopAsync (IAsyncDisposable)
 *
 * The C# Subscribe(Func<WakeWordEvent, ValueTask>) is modelled as a handler
 * callback registered under an owned subscription token. Start/Stop toggle a
 * listening flag (idempotent). Fires are delivered SYNCHRONOUSLY to every live
 * handler AND buffered on every subscription cursor (unbounded), so a fire
 * published before a poller drains is never lost.
 * =========================================================================== */

typedef struct ca_speech_wake_detector ca_speech_wake_detector_t;
typedef struct ca_speech_wake_sub      ca_speech_wake_sub_t;

/* Handler signature: receives a BORROWED event (do not free); return value
 * mirrors ValueTask completion and is ignored. */
typedef void (*ca_speech_wake_handler_fn)(void *ctx, const ca_wake_word_event_t *evt);

/* NullWakeWordDetector — BackendId "null"; Subscribe returns a live cursor,
 * Start/Stop are no-ops, no fire is ever raised. */
ca_speech_wake_detector_t *ca_speech_null_wake_detector_create(void);

/* KeywordWakeWordDetector — BackendId "keyword". A host feeds it text frames
 * with ca_speech_wake_detector_feed(); when a frame contains `keyword`
 * (case-insensitive substring) AND the detector is listening, a WakeWordEvent
 * fires (keyword, confidence, at_utc_ms). Not listening -> feeds are ignored. */
ca_speech_wake_detector_t *ca_speech_keyword_wake_detector_create(const char *keyword);

void ca_speech_wake_detector_destroy(ca_speech_wake_detector_t *d);
const char *ca_speech_wake_detector_backend_id(const ca_speech_wake_detector_t *d);
bool ca_speech_wake_detector_is_listening(const ca_speech_wake_detector_t *d);
void ca_speech_wake_detector_start(ca_speech_wake_detector_t *d);
void ca_speech_wake_detector_stop(ca_speech_wake_detector_t *d);

/* Subscribe: register `handler` (may be NULL to poll only) and get an owned
 * cursor. NULL on OOM / NULL detector. Unsubscribe removes the handler and
 * frees the cursor. */
ca_speech_wake_sub_t *ca_speech_wake_detector_subscribe(
    ca_speech_wake_detector_t *d, ca_speech_wake_handler_fn handler, void *ctx);
void ca_speech_wake_detector_unsubscribe(ca_speech_wake_detector_t *d,
                                         ca_speech_wake_sub_t *sub);
/* Drain one buffered event into *out (freshly owned; free with
 * ca_wake_word_event_free). true if produced. */
bool ca_speech_wake_sub_next(ca_speech_wake_sub_t *sub, ca_wake_word_event_t *out);
size_t ca_speech_wake_sub_pending(const ca_speech_wake_sub_t *sub);

/* Feed a text frame captured at at_utc_ms. Returns the number of handlers/
 * cursors a fire was delivered to (0 when not listening or no match). */
size_t ca_speech_wake_detector_feed(ca_speech_wake_detector_t *d,
                                    const char *frame_text, int64_t at_utc_ms);

/* ===========================================================================
 * IEchoCanceller
 *
 *   cancel(near,far,rate,dst,dstcap,&written) : subtract far-end echo from the
 *       near-end mic. Both inputs must be equal length PCM-16. Returns 0 on
 *       success (writes near.len bytes, sets *written), -1 on bad args
 *       (mismatched length / dst too small / NULL).
 *   reset()                                    : clear adaptive state.
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);
    int (*cancel)(void *self, const uint8_t *near_end, size_t near_len,
                  const uint8_t *far_end, size_t far_len, int sample_rate_hz,
                  uint8_t *destination, size_t dst_cap, size_t *written);
    void (*reset)(void *self);
} ca_echo_canceller_t;

/* Injected AEC model runner (e.g. WebRTC AEC3). Returns bytes written, or -1. */
typedef struct {
    void *self;
    int (*process)(void *self, const uint8_t *near_end, size_t near_len,
                   const uint8_t *far_end, size_t far_len, int sample_rate_hz,
                   uint8_t *destination, size_t dst_cap);
    void (*reset)(void *self);
} ca_aec_model_runner_t;

/* NullEchoCanceller — BackendId "null"; copies near-end to destination. */
typedef struct ca_null_echo_canceller ca_null_echo_canceller_t;
ca_null_echo_canceller_t *ca_null_echo_canceller_create(void);
void ca_null_echo_canceller_destroy(ca_null_echo_canceller_t *c);
ca_echo_canceller_t ca_null_echo_canceller_as_canceller(ca_null_echo_canceller_t *c);

/* NlmsEchoCanceller — BackendId "nlms". filter_length taps (default 256),
 * step_size (0.4), epsilon (1e-6). Normalised-LMS adaptive filter, PCM-16 LE. */
typedef struct ca_nlms_echo_canceller ca_nlms_echo_canceller_t;
ca_nlms_echo_canceller_t *ca_nlms_echo_canceller_create(int filter_length,
                                                        float step_size,
                                                        float epsilon);
void ca_nlms_echo_canceller_destroy(ca_nlms_echo_canceller_t *c);
ca_echo_canceller_t ca_nlms_echo_canceller_as_canceller(ca_nlms_echo_canceller_t *c);

/* WebRtcEchoCanceller — BackendId "webrtc-aec3" (or "...(fallback)" when no
 * runner). Delegates to the runner when present; otherwise NLMS. has_runner
 * gates the injected runner. */
typedef struct ca_webrtc_echo_canceller ca_webrtc_echo_canceller_t;
ca_webrtc_echo_canceller_t *ca_webrtc_echo_canceller_create(bool has_runner,
                                                            ca_aec_model_runner_t runner);
void ca_webrtc_echo_canceller_destroy(ca_webrtc_echo_canceller_t *c);
ca_echo_canceller_t ca_webrtc_echo_canceller_as_canceller(ca_webrtc_echo_canceller_t *c);

/* ===========================================================================
 * INoiseReducer
 *
 *   reduce(audio,len,rate,dst,dstcap,&written) : clean one frame into dst
 *       (>= len). Returns 0 / -1 (dst too small / NULL).
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);
    bool (*is_available)(void *self);
    int (*reduce)(void *self, const uint8_t *audio_pcm16_mono, size_t len,
                  int sample_rate_hz, uint8_t *destination, size_t dst_cap,
                  size_t *written);
} ca_noise_reducer_t;

/* Injected DNN denoise runner. Returns bytes written, or -1. */
typedef struct {
    void *self;
    int (*process)(void *self, const uint8_t *audio_pcm16_mono, size_t len,
                   int sample_rate_hz, uint8_t *destination, size_t dst_cap);
} ca_noise_model_runner_t;

/* NullNoiseReducer — BackendId "null", IsAvailable true; pass-through. */
typedef struct ca_null_noise_reducer ca_null_noise_reducer_t;
ca_null_noise_reducer_t *ca_null_noise_reducer_create(void);
void ca_null_noise_reducer_destroy(ca_null_noise_reducer_t *r);
ca_noise_reducer_t ca_null_noise_reducer_as_reducer(ca_null_noise_reducer_t *r);

/* SpectralSubtractionNoiseReducer — BackendId "passthrough", IsAvailable true.
 * Time-domain gate: samples with |s| <= floor are attenuated by `attenuation`.
 * floor_estimate default 0.008, attenuation default 0.25. */
typedef struct ca_spectral_noise_reducer ca_spectral_noise_reducer_t;
ca_spectral_noise_reducer_t *ca_spectral_noise_reducer_create(float floor_estimate,
                                                              float attenuation);
void ca_spectral_noise_reducer_destroy(ca_spectral_noise_reducer_t *r);
ca_noise_reducer_t ca_spectral_noise_reducer_as_reducer(ca_spectral_noise_reducer_t *r);

/* KrispNoiseReducer / DeepFilterNetNoiseReducer — BackendId "krisp"/
 * "deepfilternet" (or "...(fallback)"). Delegate to the runner when present,
 * else spectral subtraction. IsAvailable true. */
typedef struct ca_krisp_noise_reducer ca_krisp_noise_reducer_t;
ca_krisp_noise_reducer_t *ca_krisp_noise_reducer_create(bool has_runner,
                                                        ca_noise_model_runner_t runner);
void ca_krisp_noise_reducer_destroy(ca_krisp_noise_reducer_t *r);
ca_noise_reducer_t ca_krisp_noise_reducer_as_reducer(ca_krisp_noise_reducer_t *r);

typedef struct ca_deepfilternet_noise_reducer ca_deepfilternet_noise_reducer_t;
ca_deepfilternet_noise_reducer_t *ca_deepfilternet_noise_reducer_create(
    bool has_runner, ca_noise_model_runner_t runner);
void ca_deepfilternet_noise_reducer_destroy(ca_deepfilternet_noise_reducer_t *r);
ca_noise_reducer_t ca_deepfilternet_noise_reducer_as_reducer(
    ca_deepfilternet_noise_reducer_t *r);

/* ===========================================================================
 * IEndOfTurnDetector
 *
 *   predict(partial, trailing_silence_ms, &out) : classify; fills *out. 0 / -1.
 *   reset()                                      : fresh-turn reset.
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);
    int (*predict)(void *self, const char *partial_transcript,
                   int64_t trailing_silence_ms, ca_end_of_turn_result_t *out);
    void (*reset)(void *self);
} ca_end_of_turn_detector_t;

/* Injected semantic turn model. Returns completion probability 0..1. */
typedef struct {
    void *self;
    float (*score_completion)(void *self, const char *partial_transcript,
                              int64_t trailing_silence_ms);
} ca_turn_model_runner_t;

/* NullEndOfTurnDetector — BackendId "null"; always complete, conf 1, wait 0. */
typedef struct ca_null_eot_detector ca_null_eot_detector_t;
ca_null_eot_detector_t *ca_null_eot_detector_create(void);
void ca_null_eot_detector_destroy(ca_null_eot_detector_t *d);
ca_end_of_turn_detector_t ca_null_eot_detector_as_detector(ca_null_eot_detector_t *d);

/* RuleBasedEndOfTurnDetector — BackendId "rules". Silence thresholds in ms
 * (defaults 400 / 900 / 2500 for min / hanging / max). */
typedef struct ca_rule_eot_detector ca_rule_eot_detector_t;
ca_rule_eot_detector_t *ca_rule_eot_detector_create(int64_t min_silence_ms,
                                                    int64_t hanging_silence_ms,
                                                    int64_t max_silence_ms);
void ca_rule_eot_detector_destroy(ca_rule_eot_detector_t *d);
ca_end_of_turn_detector_t ca_rule_eot_detector_as_detector(ca_rule_eot_detector_t *d);

/* SmartTurnDetector — BackendId "smart-turn-v2" (or "smart-turn (fallback)").
 * Uses the runner when present; else RuleBased. threshold default 0.5. */
typedef struct ca_smart_turn_detector ca_smart_turn_detector_t;
ca_smart_turn_detector_t *ca_smart_turn_detector_create(bool has_runner,
                                                        ca_turn_model_runner_t runner,
                                                        float threshold);
void ca_smart_turn_detector_destroy(ca_smart_turn_detector_t *d);
ca_end_of_turn_detector_t ca_smart_turn_detector_as_detector(ca_smart_turn_detector_t *d);

/* ===========================================================================
 * IVoiceActivityDetector (per-frame; CircleAI.Speech variant)
 *
 *   classify(audio,len,rate,offset_ms,&out) : classify one frame; fills *out.
 *   reset()                                  : clear hangover state.
 *   speech_threshold()                       : probability threshold.
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*backend_id)(void *self);
    float (*speech_threshold)(void *self);
    int (*classify)(void *self, const uint8_t *audio_pcm16_mono, size_t len,
                    int sample_rate_hz, int64_t offset_ms,
                    ca_vad_frame_result_t *out);
    void (*reset)(void *self);
} ca_speech_vad_t;

/* Injected VAD model runner. Returns per-frame score 0..1. */
typedef struct {
    void *self;
    float (*score_frame)(void *self, const uint8_t *audio_pcm16_mono, size_t len,
                         int sample_rate_hz);
} ca_vad_model_runner_t;

/* NullVoiceActivityDetector — BackendId "null", threshold 0.5; always speech. */
typedef struct ca_null_speech_vad ca_null_speech_vad_t;
ca_null_speech_vad_t *ca_null_speech_vad_create(void);
void ca_null_speech_vad_destroy(ca_null_speech_vad_t *v);
ca_speech_vad_t ca_null_speech_vad_as_vad(ca_null_speech_vad_t *v);

/* EnergyVoiceActivityDetector — BackendId "energy". RMS + ZCR + hangover.
 * Defaults: speech_threshold 0.55, energy_threshold 0.012, hangover_frames 8. */
typedef struct ca_energy_speech_vad ca_energy_speech_vad_t;
ca_energy_speech_vad_t *ca_energy_speech_vad_create(float speech_threshold,
                                                    float energy_threshold,
                                                    int hangover_frames);
void ca_energy_speech_vad_destroy(ca_energy_speech_vad_t *v);
ca_speech_vad_t ca_energy_speech_vad_as_vad(ca_energy_speech_vad_t *v);

/* SileroVoiceActivityDetector — BackendId "silero" (or "silero (fallback)").
 * Uses the runner when present; else delegates to Energy scoring.
 * speech_threshold default 0.5, hangover_frames default 8. */
typedef struct ca_silero_speech_vad ca_silero_speech_vad_t;
ca_silero_speech_vad_t *ca_silero_speech_vad_create(bool has_runner,
                                                    ca_vad_model_runner_t runner,
                                                    float speech_threshold,
                                                    int hangover_frames);
void ca_silero_speech_vad_destroy(ca_silero_speech_vad_t *v);
ca_speech_vad_t ca_silero_speech_vad_as_vad(ca_silero_speech_vad_t *v);

/* ===========================================================================
 * AudioFormatConverter (stateless)
 * =========================================================================== */

typedef enum {
    CA_AUDIO_CODEC_PCM16 = 0,
    CA_AUDIO_CODEC_MULAW = 1,
    CA_AUDIO_CODEC_ALAW  = 2
} ca_audio_codec_t;

/* Convert (codec,rate) -> (codec,rate). Returns a freshly malloc'd buffer (caller
 * frees) and sets *out_len. NULL + *out_len SIZE_MAX on bad args
 * (rate <= 0 / unknown codec / OOM); a zero-length result returns NULL + *out_len 0. */
uint8_t *ca_audio_convert(const uint8_t *input, size_t input_len,
                          ca_audio_codec_t input_codec, int input_sample_rate_hz,
                          ca_audio_codec_t output_codec, int output_sample_rate_hz,
                          size_t *out_len);

/* Direct codec helpers (each returns owned buffer + *out_len; NULL+SIZE_MAX on
 * OOM, NULL+0 on empty). */
uint8_t *ca_audio_mulaw_to_pcm16(const uint8_t *mulaw, size_t len, size_t *out_len);
uint8_t *ca_audio_pcm16_to_mulaw(const uint8_t *pcm, size_t len, size_t *out_len);
uint8_t *ca_audio_alaw_to_pcm16(const uint8_t *alaw, size_t len, size_t *out_len);
uint8_t *ca_audio_pcm16_to_alaw(const uint8_t *pcm, size_t len, size_t *out_len);
uint8_t *ca_audio_resample_pcm16_linear(const uint8_t *pcm, size_t len,
                                        int from_hz, int to_hz, size_t *out_len);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SPEECH_H */
