/*
 * test_speech.c — CircleAI.Speech contract surface (C11 port).
 *
 * Verifies the Speech records + deterministic implementations against
 * Contracts.cs / NullImplementations.cs / VoiceActivityDetectors.cs /
 * EchoCancellers.cs / NoiseReducers.cs / EndOfTurnDetectors.cs /
 * AudioFormatConverter.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

static int16_t rd16(const uint8_t *p) { return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8)); }
static void wr16(uint8_t *p, int16_t v) { p[0] = (uint8_t)(v & 0xFF); p[1] = (uint8_t)((v >> 8) & 0xFF); }

/* Build a PCM-16 buffer of n samples with a callback value. */
static uint8_t *mk_pcm(size_t n, int16_t (*f)(size_t)) {
    uint8_t *b = (uint8_t *)malloc(n * 2);
    for (size_t i = 0; i < n; ++i) wr16(b + i * 2, f(i));
    return b;
}
static int16_t sine_val(size_t i) { return (int16_t)(10000.0 * sin(2.0 * 3.14159265 * i / 8.0)); }
static int16_t zero_val(size_t i) { (void)i; return 0; }

/* ── null impls ─────────────────────────────────────────────────────────── */

static void test_null_recognizer(void) {
    ca_null_speech_recognizer_t *r = ca_null_speech_recognizer_create();
    ca_speech_recognizer_t v = ca_null_speech_recognizer_as_recognizer(r);
    assert(strcmp(v.backend_id(v.self), "null") == 0);

    uint8_t audio[8] = {0};
    ca_transcription_result_t out;
    assert(v.transcribe(v.self, audio, sizeof(audio), 16000, "en", &out) == 0);
    assert(strcmp(out.text, "") == 0);
    assert(out.language && strcmp(out.language, "en") == 0);  /* echoes hint */
    assert(out.segment_count == 0 && out.segments == NULL);
    assert(out.total_duration_ms == 0);
    ca_transcription_result_free(&out);

    /* null hint stays null */
    assert(v.transcribe(v.self, audio, sizeof(audio), 16000, NULL, &out) == 0);
    assert(out.language == NULL);
    ca_transcription_result_free(&out);

    ca_null_speech_recognizer_destroy(r);
    printf("  null_recognizer: ok\n");
}

static void test_null_synthesizer(void) {
    ca_null_speech_synthesizer_t *s = ca_null_speech_synthesizer_create();
    ca_speech_synthesizer_t v = ca_null_speech_synthesizer_as_synthesizer(s);
    assert(strcmp(v.backend_id(v.self), "null") == 0);
    ca_synthesis_result_t out;
    assert(v.synthesize(v.self, "hello", NULL, NULL, &out) == 0);
    assert(out.audio_len == 0 && out.audio_pcm16_mono == NULL);
    assert(out.sample_rate_hz == 16000);
    assert(out.duration_ms == 0);
    ca_synthesis_result_free(&out);
    ca_null_speech_synthesizer_destroy(s);
    printf("  null_synthesizer: ok\n");
}

/* ── keyword recognizer ─────────────────────────────────────────────────── */

static void test_keyword_recognizer(void) {
    ca_keyword_speech_recognizer_t *r = ca_keyword_speech_recognizer_create();
    assert(ca_keyword_speech_recognizer_add(r, 4, "hey", 0.9f) == 0);
    assert(ca_keyword_speech_recognizer_add(r, 8, "there", 0.8f) == 0);
    ca_speech_recognizer_t v = ca_keyword_speech_recognizer_as_recognizer(r);
    assert(strcmp(v.backend_id(v.self), "keyword") == 0);

    /* 10 samples (20 bytes) at 16 kHz -> both rules fire. */
    uint8_t *pcm = mk_pcm(10, zero_val);
    ca_transcription_result_t out;
    assert(v.transcribe(v.self, pcm, 20, 16000, "en", &out) == 0);
    assert(strcmp(out.text, "hey there") == 0);
    assert(out.segment_count == 2);
    assert(strcmp(out.segments[0].text, "hey") == 0 && out.segments[0].confidence == 0.9f);
    assert(strcmp(out.segments[1].text, "there") == 0);
    assert(out.segments[0].offset_ms == 0);
    assert(out.total_duration_ms == (int64_t)10 * 1000 / 16000);
    assert(out.language && strcmp(out.language, "en") == 0);
    ca_transcription_result_free(&out);
    free(pcm);

    /* 6 samples (12 bytes) -> only first rule (>=4) fires. */
    pcm = mk_pcm(6, zero_val);
    assert(v.transcribe(v.self, pcm, 12, 16000, NULL, &out) == 0);
    assert(strcmp(out.text, "hey") == 0 && out.segment_count == 1);
    ca_transcription_result_free(&out);
    free(pcm);

    /* 2 samples -> no rule fires; empty text. */
    pcm = mk_pcm(2, zero_val);
    assert(v.transcribe(v.self, pcm, 4, 16000, NULL, &out) == 0);
    assert(strcmp(out.text, "") == 0 && out.segment_count == 0);
    ca_transcription_result_free(&out);
    free(pcm);

    ca_keyword_speech_recognizer_destroy(r);
    printf("  keyword_recognizer: ok\n");
}

/* ── template synthesizer ───────────────────────────────────────────────── */

static void test_template_synthesizer(void) {
    ca_template_speech_synthesizer_t *s = ca_template_speech_synthesizer_create(16000, 100);
    ca_speech_synthesizer_t v = ca_template_speech_synthesizer_as_synthesizer(s);
    assert(strcmp(v.backend_id(v.self), "template") == 0);

    ca_synthesis_result_t a, b;
    assert(v.synthesize(v.self, "abc", NULL, NULL, &a) == 0);
    assert(a.audio_len == 3 * 100 * 2);
    assert(a.sample_rate_hz == 16000);
    assert(a.duration_ms == (int64_t)(3 * 100) * 1000 / 16000);

    /* Determinism: same text -> identical bytes. */
    assert(v.synthesize(v.self, "abc", NULL, NULL, &b) == 0);
    assert(a.audio_len == b.audio_len);
    assert(memcmp(a.audio_pcm16_mono, b.audio_pcm16_mono, a.audio_len) == 0);
    ca_synthesis_result_free(&a);
    ca_synthesis_result_free(&b);

    /* Empty text -> empty audio. */
    assert(v.synthesize(v.self, "", NULL, NULL, &a) == 0);
    assert(a.audio_len == 0 && a.duration_ms == 0);
    ca_synthesis_result_free(&a);

    ca_template_speech_synthesizer_destroy(s);
    printf("  template_synthesizer: ok\n");
}

/* ── wake detector ──────────────────────────────────────────────────────── */

static int g_wake_hits;
static void wake_cb(void *ctx, const ca_wake_word_event_t *evt) {
    (void)ctx;
    assert(evt && evt->keyword);
    g_wake_hits++;
}

static void test_wake_detector(void) {
    /* null detector: never fires, listening toggles. */
    ca_speech_wake_detector_t *n = ca_speech_null_wake_detector_create();
    assert(strcmp(ca_speech_wake_detector_backend_id(n), "null") == 0);
    assert(!ca_speech_wake_detector_is_listening(n));
    ca_speech_wake_detector_start(n);
    assert(ca_speech_wake_detector_is_listening(n));
    ca_speech_wake_sub_t *ns = ca_speech_wake_detector_subscribe(n, NULL, NULL);
    assert(ca_speech_wake_detector_feed(n, "hey b hello", 100) == 0); /* null never matches */
    assert(ca_speech_wake_sub_pending(ns) == 0);
    ca_speech_wake_detector_unsubscribe(n, ns);
    ca_speech_wake_detector_destroy(n);

    /* keyword detector. */
    ca_speech_wake_detector_t *d = ca_speech_keyword_wake_detector_create("hey b");
    assert(strcmp(ca_speech_wake_detector_backend_id(d), "keyword") == 0);

    g_wake_hits = 0;
    /* Subscribe SYNCHRONOUSLY before any feed (no message lost). */
    ca_speech_wake_sub_t *s = ca_speech_wake_detector_subscribe(d, wake_cb, NULL);

    /* Not listening -> feed ignored. */
    assert(ca_speech_wake_detector_feed(d, "please say hey b now", 100) == 0);
    assert(ca_speech_wake_sub_pending(s) == 0);
    assert(g_wake_hits == 0);

    ca_speech_wake_detector_start(d);
    /* Case-insensitive substring match fires. */
    size_t delivered = ca_speech_wake_detector_feed(d, "okay HEY B go", 250);
    assert(delivered == 1);
    assert(g_wake_hits == 1);                 /* handler fired synchronously */
    assert(ca_speech_wake_sub_pending(s) == 1); /* AND buffered on the cursor */

    ca_wake_word_event_t e;
    assert(ca_speech_wake_sub_next(s, &e));
    assert(strcmp(e.keyword, "hey b") == 0);
    assert(e.confidence == 1.0f);
    assert(e.detected_at_utc_ms == 250);
    ca_wake_word_event_free(&e);
    assert(ca_speech_wake_sub_pending(s) == 0);

    /* No match -> no fire. */
    assert(ca_speech_wake_detector_feed(d, "nothing here", 300) == 0);

    ca_speech_wake_detector_stop(d);
    assert(!ca_speech_wake_detector_is_listening(d));

    ca_speech_wake_detector_unsubscribe(d, s);
    ca_speech_wake_detector_destroy(d);
    printf("  wake_detector: ok\n");
}

/* ── echo cancellers ────────────────────────────────────────────────────── */

static void test_echo_cancellers(void) {
    /* null: passes near-end through unchanged. */
    ca_null_echo_canceller_t *nc = ca_null_echo_canceller_create();
    ca_echo_canceller_t nv = ca_null_echo_canceller_as_canceller(nc);
    assert(strcmp(nv.backend_id(nv.self), "null") == 0);
    uint8_t near[8], far[8], dst[8]; size_t w = 0;
    for (int i = 0; i < 4; ++i) { wr16(near + i*2, (int16_t)(1000 + i)); wr16(far + i*2, 0); }
    assert(nv.cancel(nv.self, near, 8, far, 8, 16000, dst, 8, &w) == 0);
    assert(w == 8 && memcmp(near, dst, 8) == 0);
    nv.reset(nv.self);
    ca_null_echo_canceller_destroy(nc);

    /* NLMS: converges to remove a scaled copy of the reference. Feed
     * near == far (pure echo, no near speech): error should shrink over time. */
    ca_nlms_echo_canceller_t *c = ca_nlms_echo_canceller_create(64, 0.5f, 1e-6f);
    ca_echo_canceller_t v = ca_nlms_echo_canceller_as_canceller(c);
    assert(strcmp(v.backend_id(v.self), "nlms") == 0);

    size_t N = 512;
    uint8_t *sig = mk_pcm(N, sine_val);
    uint8_t *out = (uint8_t *)malloc(N * 2);
    assert(v.cancel(v.self, sig, N*2, sig, N*2, 16000, out, N*2, &w) == 0);
    assert(w == N*2);
    /* Late-window residual energy should be far below the input energy. */
    double in_e = 0, out_e = 0; size_t start = N - 64;
    for (size_t i = start; i < N; ++i) {
        double s = rd16(sig + i*2), o = rd16(out + i*2);
        in_e += s*s; out_e += o*o;
    }
    assert(out_e < in_e * 0.25);   /* adaptive filter cancelled most echo */

    /* Mismatched length -> -1. */
    assert(v.cancel(v.self, sig, N*2, sig, N*2 - 2, 16000, out, N*2, &w) == -1);
    /* dst too small -> -1. */
    assert(v.cancel(v.self, sig, N*2, sig, N*2, 16000, out, 2, &w) == -1);

    v.reset(v.self);
    free(sig); free(out);
    ca_nlms_echo_canceller_destroy(c);

    /* WebRTC fallback (no runner) -> NLMS, backend id shows "(fallback)". */
    ca_aec_model_runner_t none; memset(&none, 0, sizeof(none));
    ca_webrtc_echo_canceller_t *wc = ca_webrtc_echo_canceller_create(false, none);
    ca_echo_canceller_t wv = ca_webrtc_echo_canceller_as_canceller(wc);
    assert(strcmp(wv.backend_id(wv.self), "webrtc-aec3 (fallback)") == 0);
    ca_webrtc_echo_canceller_destroy(wc);

    printf("  echo_cancellers: ok\n");
}

/* ── noise reducers ─────────────────────────────────────────────────────── */

static void test_noise_reducers(void) {
    /* null: pass-through. */
    ca_null_noise_reducer_t *nr = ca_null_noise_reducer_create();
    ca_noise_reducer_t nv = ca_null_noise_reducer_as_reducer(nr);
    assert(strcmp(nv.backend_id(nv.self), "null") == 0 && nv.is_available(nv.self));
    uint8_t in[8], out[8]; size_t w = 0;
    for (int i = 0; i < 4; ++i) wr16(in + i*2, (int16_t)(500 + i));
    assert(nv.reduce(nv.self, in, 8, 16000, out, 8, &w) == 0);
    assert(w == 8 && memcmp(in, out, 8) == 0);
    ca_null_noise_reducer_destroy(nr);

    /* spectral: attenuates below-floor samples, keeps loud ones. */
    ca_spectral_noise_reducer_t *sr = ca_spectral_noise_reducer_create(0.008f, 0.25f);
    ca_noise_reducer_t sv = ca_spectral_noise_reducer_as_reducer(sr);
    assert(strcmp(sv.backend_id(sv.self), "passthrough") == 0);
    int floor_v = (int)(0.008f * 32767.0f);
    uint8_t sin[8], sout[8];
    wr16(sin + 0, (int16_t)(floor_v / 2));   /* below floor -> *0.25 */
    wr16(sin + 2, (int16_t)20000);           /* above floor -> unchanged */
    wr16(sin + 4, (int16_t)(-(floor_v / 2)));
    wr16(sin + 6, (int16_t)(-20000));
    assert(sv.reduce(sv.self, sin, 8, 16000, sout, 8, &w) == 0);
    assert(rd16(sout + 0) == (int16_t)(int)((floor_v/2) * 0.25f));
    assert(rd16(sout + 2) == (int16_t)20000);
    assert(rd16(sout + 6) == (int16_t)-20000);
    ca_spectral_noise_reducer_destroy(sr);

    /* krisp fallback (no runner). */
    ca_noise_model_runner_t none; memset(&none, 0, sizeof(none));
    ca_krisp_noise_reducer_t *kr = ca_krisp_noise_reducer_create(false, none);
    ca_noise_reducer_t kv = ca_krisp_noise_reducer_as_reducer(kr);
    assert(strcmp(kv.backend_id(kv.self), "krisp (fallback)") == 0);
    assert(kv.reduce(kv.self, sin, 8, 16000, sout, 8, &w) == 0 && w == 8);
    ca_krisp_noise_reducer_destroy(kr);

    ca_deepfilternet_noise_reducer_t *dr = ca_deepfilternet_noise_reducer_create(false, none);
    ca_noise_reducer_t dv = ca_deepfilternet_noise_reducer_as_reducer(dr);
    assert(strcmp(dv.backend_id(dv.self), "deepfilternet (fallback)") == 0);
    ca_deepfilternet_noise_reducer_destroy(dr);

    printf("  noise_reducers: ok\n");
}

/* ── end-of-turn detectors ──────────────────────────────────────────────── */

static void test_eot_detectors(void) {
    /* null: always complete. */
    ca_null_eot_detector_t *n = ca_null_eot_detector_create();
    ca_end_of_turn_detector_t nv = ca_null_eot_detector_as_detector(n);
    assert(strcmp(nv.backend_id(nv.self), "null") == 0);
    ca_end_of_turn_result_t out;
    assert(nv.predict(nv.self, "anything", 0, &out) == 0);
    assert(out.is_complete && out.confidence == 1.0f && out.wait_more_ms == 0);
    ca_null_eot_detector_destroy(n);

    /* rules. */
    ca_rule_eot_detector_t *r = ca_rule_eot_detector_create(0, 0, 0); /* defaults 400/900/2500 */
    ca_end_of_turn_detector_t rv = ca_rule_eot_detector_as_detector(r);
    assert(strcmp(rv.backend_id(rv.self), "rules") == 0);

    /* silence >= max (2500) -> complete conf .7 regardless of text. */
    assert(rv.predict(rv.self, "and", 3000, &out) == 0);
    assert(out.is_complete && fabs(out.confidence - 0.7f) < 1e-6 && out.wait_more_ms == 0);

    /* empty text, silence < min -> incomplete, wait = max(150, min-silence). */
    assert(rv.predict(rv.self, "   ", 100, &out) == 0);
    assert(!out.is_complete && fabs(out.confidence - 0.2f) < 1e-6);
    assert(out.wait_more_ms == 300);  /* 400-100 */

    /* terminal punctuation + silence >= min -> complete conf .9. */
    assert(rv.predict(rv.self, "Hello there.", 500, &out) == 0);
    assert(out.is_complete && fabs(out.confidence - 0.9f) < 1e-6);

    /* hanging word ("and") + silence < hanging (900) -> incomplete conf .4. */
    assert(rv.predict(rv.self, "I went to the store and", 200, &out) == 0);
    assert(!out.is_complete && fabs(out.confidence - 0.4f) < 1e-6);
    assert(out.wait_more_ms == 700);  /* 900-200 */

    /* hanging word + silence >= hanging -> complete conf .6. */
    assert(rv.predict(rv.self, "so", 1000, &out) == 0);
    assert(out.is_complete && fabs(out.confidence - 0.6f) < 1e-6);

    /* non-terminal, non-hanging, silence >= min -> complete conf .75. */
    assert(rv.predict(rv.self, "hello world", 500, &out) == 0);
    assert(out.is_complete && fabs(out.confidence - 0.75f) < 1e-6);

    /* non-terminal, silence < min -> incomplete conf .6, wait max(50, min-sil). */
    assert(rv.predict(rv.self, "hello world", 100, &out) == 0);
    assert(!out.is_complete && fabs(out.confidence - 0.6f) < 1e-6 && out.wait_more_ms == 300);

    /* CJK terminal punctuation recognised. */
    assert(rv.predict(rv.self, "\xE4\xBD\xA0\xE5\xA5\xBD\xE3\x80\x82", 500, &out) == 0); /* 你好。*/
    assert(out.is_complete && fabs(out.confidence - 0.9f) < 1e-6);

    ca_rule_eot_detector_destroy(r);

    /* smart-turn fallback (no runner) uses rules; backend id shows fallback. */
    ca_turn_model_runner_t none; memset(&none, 0, sizeof(none));
    ca_smart_turn_detector_t *st = ca_smart_turn_detector_create(false, none, 0.5f);
    ca_end_of_turn_detector_t sv = ca_smart_turn_detector_as_detector(st);
    assert(strcmp(sv.backend_id(sv.self), "smart-turn (fallback)") == 0);
    assert(sv.predict(sv.self, "Hello.", 500, &out) == 0 && out.is_complete);
    ca_smart_turn_detector_destroy(st);

    printf("  eot_detectors: ok\n");
}

/* ── VAD (per-frame) ────────────────────────────────────────────────────── */

static void test_vad(void) {
    /* null: always speech. */
    ca_null_speech_vad_t *n = ca_null_speech_vad_create();
    ca_speech_vad_t nv = ca_null_speech_vad_as_vad(n);
    assert(strcmp(nv.backend_id(nv.self), "null") == 0 && nv.speech_threshold(nv.self) == 0.5f);
    ca_vad_frame_result_t out;
    assert(nv.classify(nv.self, NULL, 0, 16000, 123, &out) == 0);
    assert(out.is_speech && out.speech_probability == 1.0f && out.offset_ms == 123);
    ca_null_speech_vad_destroy(n);

    /* energy: loud voiced frame -> speech; silence -> not (after hangover). */
    ca_energy_speech_vad_t *e = ca_energy_speech_vad_create(0.55f, 0.012f, 2);
    ca_speech_vad_t ev = ca_energy_speech_vad_as_vad(e);
    assert(strcmp(ev.backend_id(ev.self), "energy") == 0);

    size_t N = 160;
    uint8_t *loud = mk_pcm(N, sine_val);
    assert(ev.classify(ev.self, loud, N*2, 16000, 0, &out) == 0);
    assert(out.is_speech && out.speech_probability >= 0.55f);
    free(loud);

    /* silence frames: first two ride hangover=2, then drop to non-speech. */
    uint8_t *quiet = mk_pcm(N, zero_val);
    ca_vad_frame_result_t r1, r2, r3;
    ev.classify(ev.self, quiet, N*2, 16000, 10, &r1);
    ev.classify(ev.self, quiet, N*2, 16000, 20, &r2);
    ev.classify(ev.self, quiet, N*2, 16000, 30, &r3);
    assert(r1.is_speech && r2.is_speech);   /* hangover keeps 2 frames alive */
    assert(!r3.is_speech);                  /* hangover exhausted */
    free(quiet);
    ev.reset(ev.self);
    ca_energy_speech_vad_destroy(e);

    /* tiny frame (<2 bytes) -> not speech, prob 0. */
    ca_energy_speech_vad_t *e2 = ca_energy_speech_vad_create(0.55f, 0.012f, 8);
    ca_speech_vad_t ev2 = ca_energy_speech_vad_as_vad(e2);
    uint8_t one[1] = {0};
    assert(ev2.classify(ev2.self, one, 1, 16000, 5, &out) == 0);
    assert(!out.is_speech && out.speech_probability == 0.0f && out.offset_ms == 5);
    ca_energy_speech_vad_destroy(e2);

    /* silero fallback (no runner) delegates to energy scoring. */
    ca_vad_model_runner_t none; memset(&none, 0, sizeof(none));
    ca_silero_speech_vad_t *s = ca_silero_speech_vad_create(false, none, 0.5f, 8);
    ca_speech_vad_t svd = ca_silero_speech_vad_as_vad(s);
    assert(strcmp(svd.backend_id(svd.self), "silero (fallback)") == 0);
    assert(svd.speech_threshold(svd.self) == 0.5f);
    uint8_t *loud2 = mk_pcm(160, sine_val);
    assert(svd.classify(svd.self, loud2, 320, 16000, 0, &out) == 0 && out.is_speech);
    free(loud2);
    ca_silero_speech_vad_destroy(s);

    printf("  vad: ok\n");
}

/* ── audio format converter ─────────────────────────────────────────────── */

static void test_audio_converter(void) {
    /* mu-law round trip: decode(encode(x)) is a quantised-but-monotone version.
     * Verify the classic mu-law identity: encode then decode reproduces the same
     * mu-law byte on re-encode (idempotent quantiser). */
    size_t N = 64;
    uint8_t *pcm = mk_pcm(N, sine_val);
    size_t mlen = 0, plen = 0;
    uint8_t *mu = ca_audio_pcm16_to_mulaw(pcm, N*2, &mlen);
    assert(mu && mlen == N);
    uint8_t *back = ca_audio_mulaw_to_pcm16(mu, mlen, &plen);
    assert(back && plen == N*2);
    /* Re-encode the decoded pcm -> identical mu-law bytes (idempotent). */
    size_t mlen2 = 0;
    uint8_t *mu2 = ca_audio_pcm16_to_mulaw(back, plen, &mlen2);
    assert(mlen2 == mlen && memcmp(mu, mu2, mlen) == 0);
    free(mu); free(back); free(mu2);

    /* a-law idempotent quantiser. */
    uint8_t *al = ca_audio_pcm16_to_alaw(pcm, N*2, &mlen);
    uint8_t *aback = ca_audio_alaw_to_pcm16(al, mlen, &plen);
    uint8_t *al2 = ca_audio_pcm16_to_alaw(aback, plen, &mlen2);
    assert(mlen2 == mlen && memcmp(al, al2, mlen) == 0);
    free(al); free(aback); free(al2);

    /* Known mu-law encodings: silence (0) -> 0xFF; full-scale checks stay in range. */
    uint8_t z2[2]; wr16(z2, 0);
    uint8_t *mz = ca_audio_pcm16_to_mulaw(z2, 2, &mlen);
    assert(mlen == 1 && mz[0] == 0xFF);
    free(mz);

    /* resample 8k -> 16k doubles the sample count (approx). */
    size_t rlen = 0;
    uint8_t *rs = ca_audio_resample_pcm16_linear(pcm, N*2, 8000, 16000, &rlen);
    assert(rs && rlen == (size_t)((long long)N * 16000 / 8000) * 2);
    assert(rlen == N * 2 * 2);
    free(rs);

    /* resample same rate -> owned copy, equal bytes. */
    rs = ca_audio_resample_pcm16_linear(pcm, N*2, 16000, 16000, &rlen);
    assert(rlen == N*2 && memcmp(rs, pcm, N*2) == 0);
    free(rs);

    /* full pipeline: mu-law 8k -> pcm 16k (decode + upsample). */
    uint8_t *mu8 = ca_audio_pcm16_to_mulaw(pcm, N*2, &mlen);
    size_t clen = 0;
    uint8_t *conv = ca_audio_convert(mu8, mlen, CA_AUDIO_CODEC_MULAW, 8000,
                                     CA_AUDIO_CODEC_PCM16, 16000, &clen);
    assert(conv && clen == N * 2 * 2);   /* N mu-law samples -> 2N pcm samples -> 4N bytes */
    free(mu8); free(conv);

    /* pcm16 -> pcm16 same rate = copy. */
    conv = ca_audio_convert(pcm, N*2, CA_AUDIO_CODEC_PCM16, 16000,
                            CA_AUDIO_CODEC_PCM16, 16000, &clen);
    assert(conv && clen == N*2 && memcmp(conv, pcm, N*2) == 0);
    free(conv);

    /* bad rate -> NULL + SIZE_MAX. */
    conv = ca_audio_convert(pcm, N*2, CA_AUDIO_CODEC_PCM16, 0,
                            CA_AUDIO_CODEC_PCM16, 16000, &clen);
    assert(conv == NULL && clen == SIZE_MAX);

    free(pcm);
    printf("  audio_converter: ok\n");
}

int main(void) {
    test_null_recognizer();
    test_null_synthesizer();
    test_keyword_recognizer();
    test_template_synthesizer();
    test_wake_detector();
    test_echo_cancellers();
    test_noise_reducers();
    test_eot_detectors();
    test_vad();
    test_audio_converter();
    printf("test_speech: all assertions passed\n");
    return 0;
}
