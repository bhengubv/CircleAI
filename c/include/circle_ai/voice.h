#ifndef CIRCLE_AI_VOICE_H
#define CIRCLE_AI_VOICE_H

/*
 * voice.h — CircleAI.Voice (C11 port).
 *
 * Ports the CircleAI.Voice pipeline surface 1:1 (the ONNX/Whisper engines are
 * injected model dependencies modelled as vtables; no real audio):
 *
 *   Records   : AudioFormat(SampleRate, Channels, BitsPerSample) + Pcm16Mono16k;
 *               TranscriptionResult(Text, Confidence, LanguageCode);
 *               PartialTranscription(Text, IsFinal, Confidence);
 *               VadSegment(Audio, IsSpeech);
 *               TtsSynthesisResult(AudioData, SampleRate, Channels, BitsPerSample);
 *               WakeWordDetectedEventArgs(WakeWord, DetectedAt, Confidence);
 *               TranscribedEventArgs(Result, CompletedAt);
 *               SpeechEmotionFrame(Label, Arousal, Valence, Probability);
 *               EnrolledSpeaker(UserId, Centroid, SampleCount).
 *   Capture   : IAudioCapture — NullAudioCapture (yields nothing) + a scripted
 *               in-memory capture (a preloaded list of PCM chunks).
 *   VAD       : IVoiceActivityDetector (stream) — NullVoiceActivityDetector
 *               (pass-through) + EnergyVadDetector (RMS framing + silence-run
 *               segmenting).
 *   Transcribe: IVoiceTranscriber — NullVoiceTranscriber + a deterministic
 *               keyword transcriber; single-shot + streaming.
 *   WakeWord  : IWakeWordDetector — NullWakeWordDetector + EnergyWakeWordDetector
 *               (capture -> VAD -> transcribe -> substring match). event ->
 *               subscriber cursors; pump() drains the scripted capture.
 *   TTS       : ITtsEngine — NullTtsEngine + a template engine.
 *   Emotion   : ISpeechEmotionDetector — deterministic detector over an injected
 *               logits runner + Russell-circumplex mapping.
 *   Speaker   : ISpeakerIdentity — deterministic cosine-centroid enroll/identify
 *               over an injected embedder runner (L2-normalised, VoxCeleb-style).
 *   Pipeline  : VoicePipeline — wake -> capture (+VAD) -> transcribe -> Transcribed.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Byte formats PCM-16 LE (BinaryPrimitives).
 * Timestamps Unix ms UTC, passed in.
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
 * AudioFormat(SampleRate, Channels, BitsPerSample)
 * =========================================================================== */

typedef struct {
    int sample_rate;
    int channels;
    int bits_per_sample;
} ca_voice_audio_format_t;

/* Pcm16Mono16k = (16000, 1, 16). */
ca_voice_audio_format_t ca_voice_audio_format_pcm16_mono16k(void);

/* ===========================================================================
 * Records: TranscriptionResult / PartialTranscription / VadSegment /
 * TtsSynthesisResult / SpeechEmotionFrame
 * =========================================================================== */

typedef struct {
    char *text;          /* owned, non-null */
    float confidence;
    char *language_code; /* owned, non-null (e.g. "und") */
} ca_voice_transcription_result_t;

typedef struct {
    char *text;       /* owned, non-null */
    bool  is_final;
    float confidence;
} ca_voice_partial_transcription_t;

typedef struct {
    uint8_t *audio;   /* owned (may be NULL when len 0) */
    size_t   audio_len;
    bool     is_speech;
} ca_voice_vad_segment_t;

typedef struct {
    uint8_t *audio_data; /* owned (may be NULL when len 0) */
    size_t   audio_len;
    int      sample_rate;
    int      channels;
    int      bits_per_sample;
} ca_voice_tts_result_t;

typedef struct {
    char  *wake_word;          /* owned */
    int64_t detected_at_utc_ms;
    float  confidence;
} ca_voice_wake_event_t;

typedef struct {
    char  *label;       /* owned */
    double arousal;
    double valence;
    double probability;
} ca_speech_emotion_frame_t;

void ca_voice_transcription_result_free(ca_voice_transcription_result_t *r);
void ca_voice_partial_transcription_free(ca_voice_partial_transcription_t *p);
void ca_voice_partial_transcription_free_array(ca_voice_partial_transcription_t *arr,
                                               size_t count);
void ca_voice_vad_segment_free(ca_voice_vad_segment_t *s);
void ca_voice_vad_segment_free_array(ca_voice_vad_segment_t *arr, size_t count);
void ca_voice_tts_result_free(ca_voice_tts_result_t *r);
void ca_voice_wake_event_free(ca_voice_wake_event_t *e);
void ca_speech_emotion_frame_free(ca_speech_emotion_frame_t *f);

/* ===========================================================================
 * IAudioCapture — scripted / null
 *
 * CaptureAsync yields ReadOnlyMemory<byte> chunks. Modelled as an opaque source
 * that a consumer drains chunk-by-chunk (ca_audio_capture_next). The scripted
 * source is loaded up-front; the null source yields nothing.
 * =========================================================================== */

typedef struct ca_audio_capture ca_audio_capture_t;

/* NullAudioCapture — Format Pcm16Mono16k; yields no chunks. */
ca_audio_capture_t *ca_null_audio_capture_create(void);
/* Scripted capture with the given Format. Push chunks with _push before draining. */
ca_audio_capture_t *ca_scripted_audio_capture_create(ca_voice_audio_format_t fmt);
void ca_audio_capture_destroy(ca_audio_capture_t *c);
ca_voice_audio_format_t ca_audio_capture_format(const ca_audio_capture_t *c);
/* Append a PCM chunk (deep-copied) to a scripted source. 0 / -1. */
int ca_scripted_audio_capture_push(ca_audio_capture_t *c, const uint8_t *data, size_t len);
/* Drain the next chunk into *out_data (freshly owned; caller frees) + *out_len.
 * Returns true if a chunk was produced, false at end of stream. */
bool ca_audio_capture_next(ca_audio_capture_t *c, uint8_t **out_data, size_t *out_len);
/* Rewind the read cursor to the start (so a source can be re-consumed). */
void ca_audio_capture_reset(ca_audio_capture_t *c);

/* ===========================================================================
 * IVoiceActivityDetector (stream) — VadSegment producing
 * =========================================================================== */

typedef struct {
    void *self;
    /* Consume the entire capture (drained here) and produce a fresh owned array
     * of VadSegments (*out_count). NULL + *out_count SIZE_MAX on error; a
     * zero-segment result returns NULL + 0. `capture` is drained but not freed. */
    ca_voice_vad_segment_t *(*detect)(void *self, ca_audio_capture_t *capture,
                                      size_t *out_count);
} ca_voice_vad_stream_t;

/* NullVoiceActivityDetector — passes every chunk through as IsSpeech=true. */
typedef struct ca_null_voice_vad_stream ca_null_voice_vad_stream_t;
ca_null_voice_vad_stream_t *ca_null_voice_vad_stream_create(void);
void ca_null_voice_vad_stream_destroy(ca_null_voice_vad_stream_t *v);
ca_voice_vad_stream_t ca_null_voice_vad_stream_as_stream(ca_null_voice_vad_stream_t *v);

/* EnergyVadDetector — RMS framing. energy_threshold (default 0.02), silence_frames
 * (default 15), frame_size_bytes (default 640). Buffers speech + trailing silence
 * and yields a segment after silence_frames consecutive below-threshold frames;
 * emits a final partial segment if the stream ends mid-speech. */
typedef struct ca_energy_vad_stream ca_energy_vad_stream_t;
ca_energy_vad_stream_t *ca_energy_vad_stream_create(float energy_threshold,
                                                    int silence_frames,
                                                    int frame_size_bytes);
void ca_energy_vad_stream_destroy(ca_energy_vad_stream_t *v);
ca_voice_vad_stream_t ca_energy_vad_stream_as_stream(ca_energy_vad_stream_t *v);

/* ===========================================================================
 * IVoiceTranscriber — single-shot + streaming
 * =========================================================================== */

typedef struct {
    void *self;
    /* TranscribeAsync(pcmAudio) -> fills *out (owned). 0 / -1. */
    int (*transcribe)(void *self, const uint8_t *pcm, size_t len,
                      ca_voice_transcription_result_t *out);
    /* StreamTranscribeAsync(chunks) -> fresh owned array of PartialTranscription
     * (*out_count). The final element has is_final=true. NULL + SIZE_MAX on error;
     * NULL + 0 when nothing produced. `chunks` is drained but not freed. */
    ca_voice_partial_transcription_t *(*stream_transcribe)(void *self,
                                                           ca_audio_capture_t *chunks,
                                                           size_t *out_count);
} ca_voice_transcriber_t;

/* NullVoiceTranscriber — single-shot returns ("", 0, "und"); stream drains the
 * input and yields nothing. */
typedef struct ca_null_voice_transcriber ca_null_voice_transcriber_t;
ca_null_voice_transcriber_t *ca_null_voice_transcriber_create(void);
void ca_null_voice_transcriber_destroy(ca_null_voice_transcriber_t *t);
ca_voice_transcriber_t ca_null_voice_transcriber_as_transcriber(
    ca_null_voice_transcriber_t *t);

/* KeywordVoiceTranscriber — deterministic. Single-shot: emits `phrase` with
 * `confidence` + `language` when the buffer has >= min_samples samples, else
 * ("", 0, language). Streaming: accumulates chunk sample counts and, when the
 * total crosses min_samples, yields ONE partial (phrase, is_final=false) then a
 * final (phrase, is_final=true) at end of stream; if the threshold is never met
 * it yields a single final ("", true). */
typedef struct ca_keyword_voice_transcriber ca_keyword_voice_transcriber_t;
ca_keyword_voice_transcriber_t *ca_keyword_voice_transcriber_create(
    size_t min_samples, const char *phrase, float confidence, const char *language);
void ca_keyword_voice_transcriber_destroy(ca_keyword_voice_transcriber_t *t);
ca_voice_transcriber_t ca_keyword_voice_transcriber_as_transcriber(
    ca_keyword_voice_transcriber_t *t);

/* ===========================================================================
 * IWakeWordDetector — event(WakeWordDetected)/Start/Stop + pump()
 *
 * The C# EventHandler is modelled as subscriber cursors (unbounded buffers) +
 * optional synchronous handler callbacks. Start/Stop toggle IsListening
 * (idempotent). For EnergyWakeWordDetector, pump() runs the equivalent of the
 * background listen loop to completion over its scripted capture: capture -> VAD
 * -> transcribe each speech segment -> fire on a case-insensitive WakeWord match.
 * =========================================================================== */

typedef struct ca_voice_wake_detector ca_voice_wake_detector_t;
typedef struct ca_voice_wake_sub      ca_voice_wake_sub_t;

typedef void (*ca_voice_wake_handler_fn)(void *ctx, const ca_voice_wake_event_t *evt);

/* NullWakeWordDetector — WakeWord "Hey B" (or custom); tracks IsListening; never
 * fires. */
ca_voice_wake_detector_t *ca_null_voice_wake_detector_create(const char *wake_word);

/* EnergyWakeWordDetector(capture, transcriber, wake_word="hey b",
 * energy_threshold=0.02f). Owns an internal EnergyVadDetector(threshold,
 * silenceFrames:10, frameSizeBytes:640). The detector BORROWS the capture +
 * transcriber (caller keeps ownership). NULL on bad args / OOM. */
ca_voice_wake_detector_t *ca_energy_voice_wake_detector_create(
    ca_audio_capture_t *capture, ca_voice_transcriber_t transcriber,
    const char *wake_word, float energy_threshold);

void ca_voice_wake_detector_destroy(ca_voice_wake_detector_t *d);
const char *ca_voice_wake_detector_wake_word(const ca_voice_wake_detector_t *d);
bool ca_voice_wake_detector_is_listening(const ca_voice_wake_detector_t *d);
void ca_voice_wake_detector_start(ca_voice_wake_detector_t *d);
void ca_voice_wake_detector_stop(ca_voice_wake_detector_t *d);

ca_voice_wake_sub_t *ca_voice_wake_detector_subscribe(
    ca_voice_wake_detector_t *d, ca_voice_wake_handler_fn handler, void *ctx);
void ca_voice_wake_detector_unsubscribe(ca_voice_wake_detector_t *d,
                                        ca_voice_wake_sub_t *sub);
bool ca_voice_wake_sub_next(ca_voice_wake_sub_t *sub, ca_voice_wake_event_t *out);
size_t ca_voice_wake_sub_pending(const ca_voice_wake_sub_t *sub);

/* Run the detector's listen loop once over its scripted capture (Energy only;
 * no-op for Null). Fires events into every live subscriber. Only fires while
 * IsListening. Returns the number of fires produced. */
size_t ca_voice_wake_detector_pump(ca_voice_wake_detector_t *d);

/* ===========================================================================
 * ITtsEngine — null / template
 * =========================================================================== */

typedef struct {
    void *self;
    /* SynthesiseAsync(text) -> fills *out (owned). 0 / -1. */
    int (*synthesise)(void *self, const char *text, ca_voice_tts_result_t *out);
} ca_voice_tts_engine_t;

/* NullTtsEngine — EmptyResult (empty audio, 24000, 1, 16). */
typedef struct ca_null_voice_tts ca_null_voice_tts_t;
ca_null_voice_tts_t *ca_null_voice_tts_create(void);
void ca_null_voice_tts_destroy(ca_null_voice_tts_t *e);
ca_voice_tts_engine_t ca_null_voice_tts_as_engine(ca_null_voice_tts_t *e);

/* TemplateTtsEngine — samples_per_char PCM-16 samples/char at sample_rate;
 * mono, 16-bit; deterministic square wave per char (same as Speech template). */
typedef struct ca_template_voice_tts ca_template_voice_tts_t;
ca_template_voice_tts_t *ca_template_voice_tts_create(int sample_rate, int samples_per_char);
void ca_template_voice_tts_destroy(ca_template_voice_tts_t *e);
ca_voice_tts_engine_t ca_template_voice_tts_as_engine(ca_template_voice_tts_t *e);

/* ===========================================================================
 * ISpeechEmotionDetector — deterministic over an injected logits runner
 *
 * SenseAsync(audioPcm16, rate): when rate != model rate OR audio empty OR too
 * few samples -> null (returns false). Otherwise runs the injected runner to get
 * class logits, softmaxes, picks the top class, maps its lowercased label to the
 * Russell circumplex (arousal, valence), and returns the frame.
 * =========================================================================== */

/* Injected logits runner: writes up to `cap` logits for the windowed float
 * samples; returns the number written (== NClasses), or -1. The window is the
 * PCM-16 clip normalised to float [-1,1). */
typedef struct {
    void *self;
    int (*infer)(void *self, const float *window, size_t n_samples,
                 float *out_logits, size_t cap);
} ca_emotion_logits_runner_t;

typedef struct ca_speech_emotion_detector ca_speech_emotion_detector_t;

/* labels: owned copy of the class-label table (index -> label). sample_rate_hz
 * default 16000, max_clip_ms default 8000. NULL on OOM / no runner. */
ca_speech_emotion_detector_t *ca_speech_emotion_detector_create(
    ca_emotion_logits_runner_t runner,
    const char *const *labels, size_t label_count,
    int sample_rate_hz, int max_clip_ms);
void ca_speech_emotion_detector_destroy(ca_speech_emotion_detector_t *d);
/* Fills *out on success (returns true). Returns false when C# returns null. */
bool ca_speech_emotion_detector_sense(ca_speech_emotion_detector_t *d,
                                      const uint8_t *audio_pcm16, size_t len,
                                      int sample_rate_hz,
                                      ca_speech_emotion_frame_t *out);

/* ===========================================================================
 * ISpeakerIdentity — deterministic cosine-centroid over an injected embedder
 *
 * EnrollAsync averages observed embeddings per user (running mean, then
 * L2-normalise) — first enrollment stores the raw embedding. IdentifyAsync
 * returns the enrolled user with the highest cosine similarity when it clears
 * MatchThreshold, else null. Empty audio / no enrollments / too-short clip ->
 * null. The embedder is injected (raw waveform or log-mel is the host's concern);
 * the C emits a fixed-dim embedding via the runner, L2-normalised.
 * =========================================================================== */

/* Injected embedder: writes up to `cap` embedding floats for the float window
 * [-1,1); returns the embedding dim written, or -1. */
typedef struct {
    void *self;
    int (*embed)(void *self, const float *window, size_t n_samples,
                 float *out_embedding, size_t cap);
} ca_speaker_embedder_runner_t;

typedef struct ca_speaker_identity ca_speaker_identity_t;

/* sample_rate_hz default 16000, min_utterance_ms default 1000, max_utterance_ms
 * default 8000, match_threshold default 0.55. embed_dim caps the runner output.
 * NULL on OOM. */
ca_speaker_identity_t *ca_speaker_identity_create(
    ca_speaker_embedder_runner_t runner, size_t embed_dim,
    int sample_rate_hz, int min_utterance_ms, int max_utterance_ms,
    double match_threshold);
void ca_speaker_identity_destroy(ca_speaker_identity_t *s);
/* IdentifyAsync -> writes the winning user id into *out_user (freshly owned;
 * caller frees) and returns true; returns false when C# returns null. */
bool ca_speaker_identity_identify(ca_speaker_identity_t *s,
                                  const uint8_t *audio_pcm16, size_t len,
                                  int sample_rate_hz, char **out_user);
/* EnrollAsync. Returns 0 on success, -1 on bad args / embedding failure. */
int ca_speaker_identity_enroll(ca_speaker_identity_t *s, const char *user_id,
                               const uint8_t *audio_pcm16, size_t len,
                               int sample_rate_hz);
/* Number of enrolled speakers. */
size_t ca_speaker_identity_enrolled_count(const ca_speaker_identity_t *s);
/* SampleCount observed for a user (0 if unknown). */
int ca_speaker_identity_sample_count(const ca_speaker_identity_t *s,
                                     const char *user_id);

/* ===========================================================================
 * VoicePipeline — composition
 *
 * On a wake fire the pipeline runs an activation: capture -> (optional VAD) ->
 * transcriber.StreamTranscribe -> final result -> Transcribed event. The C
 * pipeline BORROWS its collaborators. run_activation() performs one activation
 * synchronously (the C# fires it on a background task after each wake event).
 * =========================================================================== */

typedef struct ca_voice_pipeline ca_voice_pipeline_t;

typedef void (*ca_voice_transcribed_fn)(void *ctx,
                                        const ca_voice_transcription_result_t *result,
                                        int64_t completed_at_utc_ms);

/* wake + transcriber required; capture optional (NULL -> a Null capture);
 * has_vad gates the VAD stream. NULL on bad args / OOM. */
ca_voice_pipeline_t *ca_voice_pipeline_create(
    ca_voice_wake_detector_t *wake, ca_voice_transcriber_t transcriber,
    ca_audio_capture_t *capture, bool has_vad, ca_voice_vad_stream_t vad);
void ca_voice_pipeline_destroy(ca_voice_pipeline_t *p);
/* Register the Transcribed handler (ctx passed through). */
void ca_voice_pipeline_on_transcribed(ca_voice_pipeline_t *p,
                                      ca_voice_transcribed_fn handler, void *ctx);
/* Run one activation over the capture (as if a wake fired). completed_at_utc_ms
 * stamps the event. Returns true if a final transcription was produced (and the
 * handler fired), false if the stream yielded no final result. */
bool ca_voice_pipeline_run_activation(ca_voice_pipeline_t *p, int64_t completed_at_utc_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_H */
