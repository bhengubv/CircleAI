/*
 * test_voice.c — CircleAI.Voice (C11 port).
 *
 * Verifies AudioFormat, IAudioCapture, IVoiceActivityDetector (stream),
 * IVoiceTranscriber, IWakeWordDetector (+pump), ITtsEngine,
 * ISpeechEmotionDetector, ISpeakerIdentity, and VoicePipeline against the
 * CircleAI.Voice reference (VoicePipeline.cs, EnergyVadDetector.cs,
 * EnergyWakeWordDetector.cs, OnnxSpeechEmotionDetector.cs, OnnxSpeakerIdentity.cs
 * and the Null* defaults).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

static int16_t rd16(const uint8_t *p) { return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8)); }
static void wr16(uint8_t *p, int16_t v) { p[0] = (uint8_t)(v & 0xFF); p[1] = (uint8_t)((v >> 8) & 0xFF); }

/* fill a byte buffer with a loud sine (speech-like) or zeros (silence). */
static void fill_loud(uint8_t *b, size_t nbytes) {
    size_t n = nbytes / 2;
    for (size_t i = 0; i < n; ++i) wr16(b + i*2, (int16_t)(12000.0 * sin(2.0*3.14159265*i/6.0)));
}
static void fill_quiet(uint8_t *b, size_t nbytes) { memset(b, 0, nbytes); }

/* ── AudioFormat + capture ──────────────────────────────────────────────── */

static void test_capture(void) {
    ca_voice_audio_format_t f = ca_voice_audio_format_pcm16_mono16k();
    assert(f.sample_rate == 16000 && f.channels == 1 && f.bits_per_sample == 16);

    /* null capture: yields nothing. */
    ca_audio_capture_t *n = ca_null_audio_capture_create();
    ca_voice_audio_format_t nf = ca_audio_capture_format(n);
    assert(nf.sample_rate == 16000);
    uint8_t *d; size_t l;
    assert(!ca_audio_capture_next(n, &d, &l));
    ca_audio_capture_destroy(n);

    /* scripted capture: yields pushed chunks, deep-copied, then ends; reset re-reads. */
    ca_audio_capture_t *c = ca_scripted_audio_capture_create(f);
    uint8_t a[4] = {1,2,3,4}, b[2] = {5,6};
    assert(ca_scripted_audio_capture_push(c, a, 4) == 0);
    assert(ca_scripted_audio_capture_push(c, b, 2) == 0);
    assert(ca_audio_capture_next(c, &d, &l) && l == 4 && memcmp(d, a, 4) == 0); free(d);
    assert(ca_audio_capture_next(c, &d, &l) && l == 2 && memcmp(d, b, 2) == 0); free(d);
    assert(!ca_audio_capture_next(c, &d, &l));
    ca_audio_capture_reset(c);
    assert(ca_audio_capture_next(c, &d, &l) && l == 4); free(d);
    ca_audio_capture_destroy(c);
    printf("  capture: ok\n");
}

/* ── VAD stream ─────────────────────────────────────────────────────────── */

static void test_vad_stream(void) {
    ca_voice_audio_format_t f = ca_voice_audio_format_pcm16_mono16k();

    /* null VAD: passes every chunk as speech. */
    ca_audio_capture_t *cap = ca_scripted_audio_capture_create(f);
    uint8_t chunk[640]; fill_loud(chunk, 640);
    ca_scripted_audio_capture_push(cap, chunk, 640);
    ca_scripted_audio_capture_push(cap, chunk, 640);
    ca_null_voice_vad_stream_t *nv = ca_null_voice_vad_stream_create();
    ca_voice_vad_stream_t nvs = ca_null_voice_vad_stream_as_stream(nv);
    size_t sc = 0;
    ca_voice_vad_segment_t *segs = nvs.detect(nvs.self, cap, &sc);
    assert(sc == 2 && segs[0].is_speech && segs[0].audio_len == 640);
    ca_voice_vad_segment_free_array(segs, sc);
    ca_null_voice_vad_stream_destroy(nv);
    ca_audio_capture_destroy(cap);

    /* energy VAD: speech run followed by silence_frames of silence -> 1 segment.
     * frame=640 bytes, silence_frames=3. Feed 4 loud frames + 3 quiet frames. */
    cap = ca_scripted_audio_capture_create(f);
    uint8_t loud[640], quiet[640];
    fill_loud(loud, 640); fill_quiet(quiet, 640);
    for (int i = 0; i < 4; ++i) ca_scripted_audio_capture_push(cap, loud, 640);
    for (int i = 0; i < 3; ++i) ca_scripted_audio_capture_push(cap, quiet, 640);
    ca_energy_vad_stream_t *ev = ca_energy_vad_stream_create(0.02f, 3, 640);
    ca_voice_vad_stream_t evs = ca_energy_vad_stream_as_stream(ev);
    segs = evs.detect(evs.self, cap, &sc);
    assert(sc == 1);
    assert(segs[0].is_speech);
    /* segment = 4 speech + 3 trailing silence frames = 7 * 640 bytes. */
    assert(segs[0].audio_len == 7 * 640);
    ca_voice_vad_segment_free_array(segs, sc);
    ca_energy_vad_stream_destroy(ev);
    ca_audio_capture_destroy(cap);

    /* energy VAD: stream ends mid-speech -> final partial segment emitted. */
    cap = ca_scripted_audio_capture_create(f);
    for (int i = 0; i < 2; ++i) ca_scripted_audio_capture_push(cap, loud, 640);
    ev = ca_energy_vad_stream_create(0.02f, 5, 640);
    evs = ca_energy_vad_stream_as_stream(ev);
    segs = evs.detect(evs.self, cap, &sc);
    assert(sc == 1 && segs[0].audio_len == 2 * 640);
    ca_voice_vad_segment_free_array(segs, sc);
    ca_energy_vad_stream_destroy(ev);
    ca_audio_capture_destroy(cap);

    /* energy VAD: all silence -> no segments. */
    cap = ca_scripted_audio_capture_create(f);
    for (int i = 0; i < 5; ++i) ca_scripted_audio_capture_push(cap, quiet, 640);
    ev = ca_energy_vad_stream_create(0.02f, 3, 640);
    evs = ca_energy_vad_stream_as_stream(ev);
    segs = evs.detect(evs.self, cap, &sc);
    assert(sc == 0 && segs == NULL);
    ca_energy_vad_stream_destroy(ev);
    ca_audio_capture_destroy(cap);

    printf("  vad_stream: ok\n");
}

/* ── transcribers ───────────────────────────────────────────────────────── */

static void test_transcribers(void) {
    ca_voice_audio_format_t f = ca_voice_audio_format_pcm16_mono16k();

    /* null transcriber: single-shot ("",0,"und"); stream yields nothing. */
    ca_null_voice_transcriber_t *n = ca_null_voice_transcriber_create();
    ca_voice_transcriber_t nv = ca_null_voice_transcriber_as_transcriber(n);
    uint8_t buf[64] = {0};
    ca_voice_transcription_result_t res;
    assert(nv.transcribe(nv.self, buf, sizeof(buf), &res) == 0);
    assert(strcmp(res.text, "") == 0 && res.confidence == 0.0f && strcmp(res.language_code, "und") == 0);
    ca_voice_transcription_result_free(&res);

    ca_audio_capture_t *cap = ca_scripted_audio_capture_create(f);
    ca_scripted_audio_capture_push(cap, buf, 64);
    size_t np = 0;
    ca_voice_partial_transcription_t *parts = nv.stream_transcribe(nv.self, cap, &np);
    assert(np == 0 && parts == NULL);
    ca_audio_capture_destroy(cap);
    ca_null_voice_transcriber_destroy(n);

    /* keyword transcriber: >= min_samples -> phrase. */
    ca_keyword_voice_transcriber_t *k = ca_keyword_voice_transcriber_create(10, "hey b", 0.88f, "en");
    ca_voice_transcriber_t kv = ca_keyword_voice_transcriber_as_transcriber(k);
    uint8_t big[40] = {0};   /* 20 samples >= 10 */
    assert(kv.transcribe(kv.self, big, sizeof(big), &res) == 0);
    assert(strcmp(res.text, "hey b") == 0 && res.confidence == 0.88f && strcmp(res.language_code, "en") == 0);
    ca_voice_transcription_result_free(&res);

    uint8_t small[8] = {0};  /* 4 samples < 10 */
    assert(kv.transcribe(kv.self, small, sizeof(small), &res) == 0);
    assert(strcmp(res.text, "") == 0 && res.confidence == 0.0f);
    ca_voice_transcription_result_free(&res);

    /* keyword transcriber streaming: crosses threshold -> interim + final. */
    cap = ca_scripted_audio_capture_create(f);
    ca_scripted_audio_capture_push(cap, small, 8);   /* 4 samples: below */
    ca_scripted_audio_capture_push(cap, small, 8);   /* 8 samples: below */
    ca_scripted_audio_capture_push(cap, small, 8);   /* 12 samples: crosses 10 */
    parts = kv.stream_transcribe(kv.self, cap, &np);
    assert(np == 2);
    assert(!parts[0].is_final && strcmp(parts[0].text, "hey b") == 0);
    assert(parts[1].is_final && strcmp(parts[1].text, "hey b") == 0);
    ca_voice_partial_transcription_free_array(parts, np);
    ca_audio_capture_destroy(cap);

    /* streaming below threshold -> single empty final. */
    cap = ca_scripted_audio_capture_create(f);
    ca_scripted_audio_capture_push(cap, small, 8);
    parts = kv.stream_transcribe(kv.self, cap, &np);
    assert(np == 1 && parts[0].is_final && strcmp(parts[0].text, "") == 0);
    ca_voice_partial_transcription_free_array(parts, np);
    ca_audio_capture_destroy(cap);

    ca_keyword_voice_transcriber_destroy(k);
    printf("  transcribers: ok\n");
}

/* ── wake detector + pump ───────────────────────────────────────────────── */

static int g_hits;
static void wake_cb(void *ctx, const ca_voice_wake_event_t *e) {
    (void)ctx; assert(e && e->wake_word); g_hits++;
}

static void test_wake_and_pump(void) {
    ca_voice_audio_format_t f = ca_voice_audio_format_pcm16_mono16k();

    /* null detector: default wake word, tracks IsListening, never fires. */
    ca_voice_wake_detector_t *n = ca_null_voice_wake_detector_create(NULL);
    assert(strcmp(ca_voice_wake_detector_wake_word(n), "Hey B") == 0);
    assert(!ca_voice_wake_detector_is_listening(n));
    ca_voice_wake_detector_start(n);
    assert(ca_voice_wake_detector_is_listening(n));
    assert(ca_voice_wake_detector_pump(n) == 0);  /* null pump no-op */
    ca_voice_wake_detector_stop(n);
    ca_voice_wake_detector_destroy(n);

    /* custom null wake word. */
    ca_voice_wake_detector_t *n2 = ca_null_voice_wake_detector_create("computer");
    assert(strcmp(ca_voice_wake_detector_wake_word(n2), "computer") == 0);
    ca_voice_wake_detector_destroy(n2);

    /* energy detector: capture with a loud speech segment; keyword transcriber
     * returns "hey b" for a segment with enough samples -> fires on pump. */
    ca_audio_capture_t *cap = ca_scripted_audio_capture_create(f);
    uint8_t loud[640], quiet[640];
    fill_loud(loud, 640); fill_quiet(quiet, 640);
    /* build a speech run (>=1 frame) then silence to close the segment
     * (VAD silenceFrames=10 inside the energy detector). */
    for (int i = 0; i < 4; ++i) ca_scripted_audio_capture_push(cap, loud, 640);
    for (int i = 0; i < 10; ++i) ca_scripted_audio_capture_push(cap, quiet, 640);

    /* transcriber that recognises the wake word once the segment is big enough.
     * 4 loud + 10 quiet frames = 14*320 = 4480 samples; min_samples 100. */
    ca_keyword_voice_transcriber_t *tr =
        ca_keyword_voice_transcriber_create(100, "hey b", 0.9f, "en");
    ca_voice_transcriber_t trv = ca_keyword_voice_transcriber_as_transcriber(tr);

    ca_voice_wake_detector_t *d =
        ca_energy_voice_wake_detector_create(cap, trv, "hey b", 0.02f);
    assert(d);
    assert(strcmp(ca_voice_wake_detector_wake_word(d), "hey b") == 0);

    g_hits = 0;
    ca_voice_wake_sub_t *s = ca_voice_wake_detector_subscribe(d, wake_cb, NULL);

    /* Not listening -> pump does nothing. */
    assert(ca_voice_wake_detector_pump(d) == 0);
    assert(g_hits == 0 && ca_voice_wake_sub_pending(s) == 0);

    ca_voice_wake_detector_start(d);
    size_t fires = ca_voice_wake_detector_pump(d);
    assert(fires == 1);
    assert(g_hits == 1);                        /* handler fired */
    assert(ca_voice_wake_sub_pending(s) == 1);  /* buffered on cursor */

    ca_voice_wake_event_t e;
    assert(ca_voice_wake_sub_next(s, &e));
    assert(strcmp(e.wake_word, "hey b") == 0);
    assert(e.confidence == 0.9f);   /* transcription confidence */
    ca_voice_wake_event_free(&e);

    ca_voice_wake_detector_unsubscribe(d, s);
    ca_voice_wake_detector_destroy(d);
    ca_keyword_voice_transcriber_destroy(tr);
    ca_audio_capture_destroy(cap);
    printf("  wake_and_pump: ok\n");
}

/* ── TTS ────────────────────────────────────────────────────────────────── */

static void test_tts(void) {
    ca_null_voice_tts_t *n = ca_null_voice_tts_create();
    ca_voice_tts_engine_t nv = ca_null_voice_tts_as_engine(n);
    ca_voice_tts_result_t r;
    assert(nv.synthesise(nv.self, "hello", &r) == 0);
    assert(r.audio_len == 0 && r.sample_rate == 24000 && r.channels == 1 && r.bits_per_sample == 16);
    ca_voice_tts_result_free(&r);
    ca_null_voice_tts_destroy(n);

    ca_template_voice_tts_t *t = ca_template_voice_tts_create(24000, 50);
    ca_voice_tts_engine_t tv = ca_template_voice_tts_as_engine(t);
    ca_voice_tts_result_t a, b;
    assert(tv.synthesise(tv.self, "hi", &a) == 0);
    assert(a.audio_len == 2 * 50 * 2 && a.sample_rate == 24000);
    assert(tv.synthesise(tv.self, "hi", &b) == 0);
    assert(a.audio_len == b.audio_len && memcmp(a.audio_data, b.audio_data, a.audio_len) == 0);
    ca_voice_tts_result_free(&a);
    ca_voice_tts_result_free(&b);
    assert(tv.synthesise(tv.self, "", &a) == 0 && a.audio_len == 0);
    ca_voice_tts_result_free(&a);
    ca_template_voice_tts_destroy(t);
    printf("  tts: ok\n");
}

/* ── speech emotion ─────────────────────────────────────────────────────── */

/* Injected logits runner: 4-class, returns logits that make the class chosen by
 * the mean-sample-sign deterministic. Emits [neutral, happy, angry, sad]. */
static int emotion_runner(void *self, const float *win, size_t n, float *out, size_t cap) {
    (void)self;
    if (cap < 4) return -1;
    /* pick "happy" (index 1) when the mean amplitude is high, else neutral. */
    double sum = 0; for (size_t i = 0; i < n; ++i) sum += fabs(win[i]);
    double mean = n ? sum / n : 0;
    out[0] = 0.1f; out[1] = 0.1f; out[2] = 0.1f; out[3] = 0.1f;
    if (mean > 0.1) out[1] = 5.0f;   /* happy dominates */
    else out[0] = 5.0f;              /* neutral dominates */
    return 4;
}

static void test_emotion(void) {
    ca_emotion_logits_runner_t run = { NULL, emotion_runner };
    ca_speech_emotion_detector_t *d =
        ca_speech_emotion_detector_create(run, NULL, 0, 16000, 8000);
    assert(d);

    size_t N = 1600;   /* 100 ms */
    uint8_t *loud = (uint8_t *)malloc(N*2); fill_loud(loud, N*2);
    ca_speech_emotion_frame_t fr;
    assert(ca_speech_emotion_detector_sense(d, loud, N*2, 16000, &fr));
    assert(strcmp(fr.label, "happy") == 0);
    /* circumplex happy = (0.55, 0.81). */
    assert(fabs(fr.arousal - 0.55) < 1e-9 && fabs(fr.valence - 0.81) < 1e-9);
    assert(fr.probability > 0.9);   /* softmax dominated by the 5.0 logit */
    ca_speech_emotion_frame_free(&fr);
    free(loud);

    uint8_t *quiet = (uint8_t *)calloc(N*2, 1);
    assert(ca_speech_emotion_detector_sense(d, quiet, N*2, 16000, &fr));
    assert(strcmp(fr.label, "neutral") == 0);
    assert(fabs(fr.arousal) < 1e-9 && fabs(fr.valence) < 1e-9);
    ca_speech_emotion_frame_free(&fr);
    free(quiet);

    /* empty audio -> null (false). */
    assert(!ca_speech_emotion_detector_sense(d, NULL, 0, 16000, &fr));
    /* sample-rate mismatch -> null. */
    uint8_t small[16] = {0};
    assert(!ca_speech_emotion_detector_sense(d, small, 16, 8000, &fr));

    ca_speech_emotion_detector_destroy(d);
    printf("  emotion: ok\n");
}

/* ── speaker identity ───────────────────────────────────────────────────── */

/* Injected embedder: 4-D embedding derived from coarse waveform stats so that
 * two clips from the "same speaker" (same generator) land close together. We key
 * the embedding on the sign pattern of the first few samples. */
static int embed_runner(void *self, const float *win, size_t n, float *out, size_t cap) {
    (void)self;
    if (cap < 4) return -1;
    double e = 0; for (size_t i = 0; i < n; ++i) e += win[i]*win[i];
    double rms = n ? sqrt(e / n) : 0;
    /* direction encodes "speaker A" vs "speaker B" via the leading half-cycle
     * polarity (sum of the first few samples — robust to a zero at i==0). */
    double lead = 0; size_t m = n < 4 ? n : 4;
    for (size_t i = 0; i < m; ++i) lead += win[i];
    double dir = (lead >= 0) ? 1.0 : -1.0;
    out[0] = (float)(dir);
    out[1] = (float)(dir * 0.5);
    out[2] = (float)(rms);
    out[3] = (float)(0.2);
    return 4;
}

static void test_speaker(void) {
    ca_speaker_embedder_runner_t run = { NULL, embed_runner };
    ca_speaker_identity_t *s =
        ca_speaker_identity_create(run, 4, 16000, 1000, 8000, 0.55);
    assert(s);
    assert(ca_speaker_identity_enrolled_count(s) == 0);

    size_t N = 16000;  /* 1 s meets min_utterance_ms */
    uint8_t *a = (uint8_t *)malloc(N*2);
    for (size_t i = 0; i < N; ++i) wr16(a + i*2, (int16_t)(9000.0 * sin(2.0*3.14159265*i/8.0)));  /* starts >= 0 */
    uint8_t *b = (uint8_t *)malloc(N*2);
    for (size_t i = 0; i < N; ++i) wr16(b + i*2, (int16_t)(-9000.0 * sin(2.0*3.14159265*i/8.0))); /* starts <= 0 */

    /* identify before enroll -> null. */
    char *who = NULL;
    assert(!ca_speaker_identity_identify(s, a, N*2, 16000, &who) && who == NULL);

    /* enroll two speakers. */
    assert(ca_speaker_identity_enroll(s, "alice", a, N*2, 16000) == 0);
    assert(ca_speaker_identity_enroll(s, "bob", b, N*2, 16000) == 0);
    assert(ca_speaker_identity_enrolled_count(s) == 2);
    assert(ca_speaker_identity_sample_count(s, "alice") == 1);

    /* identify alice's clip -> alice. */
    assert(ca_speaker_identity_identify(s, a, N*2, 16000, &who));
    assert(who && strcmp(who, "alice") == 0);
    free(who); who = NULL;

    /* identify bob's clip -> bob. */
    assert(ca_speaker_identity_identify(s, b, N*2, 16000, &who));
    assert(who && strcmp(who, "bob") == 0);
    free(who); who = NULL;

    /* re-enroll alice -> running mean, sample_count increments. */
    assert(ca_speaker_identity_enroll(s, "alice", a, N*2, 16000) == 0);
    assert(ca_speaker_identity_sample_count(s, "alice") == 2);

    /* too-short clip -> enroll fails, identify null. */
    uint8_t tiny[16] = {0};
    assert(ca_speaker_identity_enroll(s, "carol", tiny, 16, 16000) == -1);
    assert(!ca_speaker_identity_identify(s, tiny, 16, 16000, &who));

    /* empty user id / audio -> -1. */
    assert(ca_speaker_identity_enroll(s, "", a, N*2, 16000) == -1);
    assert(ca_speaker_identity_enroll(s, "x", NULL, 0, 16000) == -1);

    free(a); free(b);
    ca_speaker_identity_destroy(s);
    printf("  speaker: ok\n");
}

/* ── VoicePipeline ──────────────────────────────────────────────────────── */

static ca_voice_transcription_result_t g_last;
static int g_transcribed;
static void on_transcribed(void *ctx, const ca_voice_transcription_result_t *r, int64_t at) {
    (void)ctx; (void)at;
    g_transcribed++;
    free(g_last.text); free(g_last.language_code);
    g_last.text = r->text ? strdup(r->text) : NULL;
    g_last.language_code = r->language_code ? strdup(r->language_code) : NULL;
    g_last.confidence = r->confidence;
}

static void test_pipeline(void) {
    ca_voice_audio_format_t f = ca_voice_audio_format_pcm16_mono16k();

    /* capture with enough audio to cross the transcriber threshold. */
    ca_audio_capture_t *cap = ca_scripted_audio_capture_create(f);
    uint8_t frame[640]; fill_loud(frame, 640);
    for (int i = 0; i < 3; ++i) ca_scripted_audio_capture_push(cap, frame, 640); /* 960 samples */

    ca_keyword_voice_transcriber_t *tr =
        ca_keyword_voice_transcriber_create(100, "hello world", 0.77f, "en");
    ca_voice_transcriber_t trv = ca_keyword_voice_transcriber_as_transcriber(tr);

    ca_voice_wake_detector_t *wake = ca_null_voice_wake_detector_create("hey b");

    /* No VAD: forward all captured audio. */
    ca_voice_vad_stream_t no_vad; memset(&no_vad, 0, sizeof(no_vad));
    ca_voice_pipeline_t *p = ca_voice_pipeline_create(wake, trv, cap, false, no_vad);
    assert(p);
    g_transcribed = 0; memset(&g_last, 0, sizeof(g_last));
    ca_voice_pipeline_on_transcribed(p, on_transcribed, NULL);

    assert(ca_voice_pipeline_run_activation(p, 5000));
    assert(g_transcribed == 1);
    assert(strcmp(g_last.text, "hello world") == 0);
    assert(strcmp(g_last.language_code, "und") == 0);  /* ToFinalAsync sets "und" */
    assert(g_last.confidence == 0.77f);
    ca_voice_pipeline_destroy(p);

    /* With VAD: pipe through energy VAD, still reaches the transcriber. Add a
     * trailing silence run so the VAD closes a segment. */
    ca_audio_capture_reset(cap);
    ca_audio_capture_t *cap2 = ca_scripted_audio_capture_create(f);
    uint8_t quiet[640]; fill_quiet(quiet, 640);
    for (int i = 0; i < 3; ++i) ca_scripted_audio_capture_push(cap2, frame, 640);
    for (int i = 0; i < 3; ++i) ca_scripted_audio_capture_push(cap2, quiet, 640);
    ca_energy_vad_stream_t *ev = ca_energy_vad_stream_create(0.02f, 3, 640);
    ca_voice_vad_stream_t evs = ca_energy_vad_stream_as_stream(ev);
    ca_voice_pipeline_t *p2 = ca_voice_pipeline_create(wake, trv, cap2, true, evs);
    g_transcribed = 0;
    ca_voice_pipeline_on_transcribed(p2, on_transcribed, NULL);
    assert(ca_voice_pipeline_run_activation(p2, 6000));
    assert(g_transcribed == 1 && strcmp(g_last.text, "hello world") == 0);
    ca_voice_pipeline_destroy(p2);
    ca_energy_vad_stream_destroy(ev);
    ca_audio_capture_destroy(cap2);

    /* Null transcriber -> no final result -> no event. */
    ca_audio_capture_reset(cap);
    ca_null_voice_transcriber_t *nt = ca_null_voice_transcriber_create();
    ca_voice_transcriber_t ntv = ca_null_voice_transcriber_as_transcriber(nt);
    ca_voice_pipeline_t *p3 = ca_voice_pipeline_create(wake, ntv, cap, false, no_vad);
    g_transcribed = 0;
    ca_voice_pipeline_on_transcribed(p3, on_transcribed, NULL);
    assert(!ca_voice_pipeline_run_activation(p3, 7000));
    assert(g_transcribed == 0);
    ca_voice_pipeline_destroy(p3);
    ca_null_voice_transcriber_destroy(nt);

    free(g_last.text); free(g_last.language_code);
    ca_keyword_voice_transcriber_destroy(tr);
    ca_voice_wake_detector_destroy(wake);
    ca_audio_capture_destroy(cap);
    printf("  pipeline: ok\n");
}

int main(void) {
    test_capture();
    test_vad_stream();
    test_transcribers();
    test_wake_and_pump();
    test_tts();
    test_emotion();
    test_speaker();
    test_pipeline();
    printf("test_voice: all assertions passed\n");
    return 0;
}
