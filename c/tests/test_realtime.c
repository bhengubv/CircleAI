/*
 * test_realtime.c — CircleAI.Realtime + CircleAI.Realtime.Cloud (C11 port).
 *
 * Verifies the enums, records, RealtimeEvent union, LoopbackRealtimeService /
 * Session, the Null defaults, and the NullRealtimeTransportFactory against the
 * C# reference (Contracts.cs, LoopbackRealtimeService.cs, NullImplementations.cs,
 * IRealtimeTransport.cs).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

static void wr16(uint8_t *p, int16_t v) {
    p[0] = (uint8_t)(v & 0xFF); p[1] = (uint8_t)((v >> 8) & 0xFF);
}
static void fill_loud(uint8_t *b, size_t nbytes) {
    size_t n = nbytes / 2;
    for (size_t i = 0; i < n; ++i)
        wr16(b + i*2, (int16_t)(12000.0 * sin(2.0*3.14159265*i/6.0)));
}

/* ── sample rates ───────────────────────────────────────────────────────── */

static void test_sample_rates(void) {
    assert(ca_realtime_sample_rate_of(CA_REALTIME_PCM_16K) == 16000);
    assert(ca_realtime_sample_rate_of(CA_REALTIME_PCM_24K) == 24000);
    assert(ca_realtime_sample_rate_of(CA_REALTIME_MULAW_8K) == 8000);
    printf("  sample_rates: ok\n");
}

/* Build a config record on the stack with borrowed literals (loopback session
 * only reads AudioFormat and never frees the config). */
static ca_realtime_session_config_t cfg(ca_realtime_audio_format_t fmt) {
    ca_realtime_session_config_t c;
    memset(&c, 0, sizeof(c));
    c.model = (char *)"gpt-4o-realtime";
    c.audio_format = fmt;
    return c;
}

/* Drain all events into a caller array; returns count. */
static size_t drain_events(ca_realtime_session_t *s, ca_realtime_event_t *buf, size_t cap) {
    size_t n = 0;
    ca_realtime_event_t e;
    while (n < cap && ca_realtime_session_receive_event_next(s, &e)) buf[n++] = e;
    return n;
}

/* ── loopback: audio echo + speech transitions ──────────────────────────── */

static void test_loopback_audio(void) {
    ca_realtime_session_config_t c = cfg(CA_REALTIME_PCM_24K);
    ca_realtime_session_t *s = ca_realtime_loopback_session_create(&c, NULL, NULL);
    assert(s);
    /* SessionId = "loop-<32 hex>". */
    const char *sid = ca_realtime_session_id(s);
    assert(sid && strncmp(sid, "loop-", 5) == 0 && strlen(sid) == 5 + 32);

    /* Silent frame (>=64 bytes of zeros): IsSilent true, speaking stays false ->
     * no transition event; but the frame is still echoed. */
    uint8_t quiet[128]; memset(quiet, 0, sizeof(quiet));
    ca_realtime_audio_frame_t qf; memset(&qf, 0, sizeof(qf));
    qf.pcm = quiet; qf.pcm_len = sizeof(quiet); qf.format = CA_REALTIME_PCM_24K;
    assert(ca_realtime_session_send_audio(s, &qf) == 0);
    assert(ca_realtime_session_event_pending(s) == 0);   /* no speech transition */
    assert(ca_realtime_session_audio_pending(s) == 1);   /* echoed */

    /* drain the echoed quiet frame. */
    ca_realtime_audio_frame_t out;
    assert(ca_realtime_session_receive_audio_next(s, &out));
    assert(out.pcm_len == sizeof(quiet) && out.format == CA_REALTIME_PCM_24K);
    ca_realtime_audio_frame_free(&out);

    /* Loud frame: not silent -> SpeechStarted emitted, echoed. */
    uint8_t loud[128]; fill_loud(loud, sizeof(loud));
    ca_realtime_audio_frame_t lf; memset(&lf, 0, sizeof(lf));
    lf.pcm = loud; lf.pcm_len = sizeof(loud); lf.format = CA_REALTIME_PCM_24K;
    assert(ca_realtime_session_send_audio(s, &lf) == 0);
    ca_realtime_event_t e;
    assert(ca_realtime_session_receive_event_next(s, &e));
    assert(e.kind == CA_REALTIME_EVENT_SPEECH_STARTED);
    ca_realtime_event_free(&e);
    assert(ca_realtime_session_audio_pending(s) == 1);
    assert(ca_realtime_session_receive_audio_next(s, &out)); ca_realtime_audio_frame_free(&out);

    /* Another loud frame: still speaking -> no new transition. */
    assert(ca_realtime_session_send_audio(s, &lf) == 0);
    assert(ca_realtime_session_event_pending(s) == 0);
    assert(ca_realtime_session_receive_audio_next(s, &out)); ca_realtime_audio_frame_free(&out);

    /* Back to quiet: SpeechEnded. */
    assert(ca_realtime_session_send_audio(s, &qf) == 0);
    assert(ca_realtime_session_receive_event_next(s, &e));
    assert(e.kind == CA_REALTIME_EVENT_SPEECH_ENDED);
    ca_realtime_event_free(&e);
    assert(ca_realtime_session_receive_audio_next(s, &out)); ca_realtime_audio_frame_free(&out);

    /* Short frame (<64 bytes) counts as silent (no transition from silent). */
    uint8_t tiny[16]; fill_loud(tiny, sizeof(tiny));
    ca_realtime_audio_frame_t tf; memset(&tf, 0, sizeof(tf));
    tf.pcm = tiny; tf.pcm_len = sizeof(tiny); tf.format = CA_REALTIME_PCM_24K;
    assert(ca_realtime_session_send_audio(s, &tf) == 0);
    assert(ca_realtime_session_event_pending(s) == 0);
    assert(ca_realtime_session_receive_audio_next(s, &out)); ca_realtime_audio_frame_free(&out);

    ca_realtime_session_destroy(s);
    printf("  loopback_audio: ok\n");
}

/* ── loopback: SendText -> transcript + silence audio + turn complete ───── */

static void test_loopback_text(void) {
    ca_realtime_session_config_t c = cfg(CA_REALTIME_PCM_16K);
    ca_realtime_session_t *s = ca_realtime_loopback_session_create(&c, NULL, NULL);
    assert(s);

    /* "hello world foo" = 3 words -> durMs = max(50, 240) = 240ms.
     * sr=16000 -> sampleCount = 16000*240/1000 = 3840 -> 7680 bytes. */
    assert(ca_realtime_session_send_text(s, "hello world foo") == 0);

    /* one outbound audio frame carrying the silence PCM. */
    assert(ca_realtime_session_audio_pending(s) == 1);
    ca_realtime_audio_frame_t f;
    assert(ca_realtime_session_receive_audio_next(s, &f));
    assert(f.pcm_len == 7680);
    assert(f.format == CA_REALTIME_PCM_16K);
    assert(f.offset_ms == 0);   /* first frame at offset 0 */
    /* silence: all zeros. */
    for (size_t i = 0; i < f.pcm_len; ++i) assert(f.pcm[i] == 0);
    ca_realtime_audio_frame_free(&f);

    /* events: TranscriptDelta(Outbound), TranscriptFinal(Outbound), TurnComplete. */
    ca_realtime_event_t evs[8];
    size_t n = drain_events(s, evs, 8);
    assert(n == 3);
    assert(evs[0].kind == CA_REALTIME_EVENT_TRANSCRIPT_DELTA &&
           evs[0].direction == CA_REALTIME_OUTBOUND &&
           strcmp(evs[0].text, "hello world foo") == 0);
    assert(evs[1].kind == CA_REALTIME_EVENT_TRANSCRIPT_FINAL &&
           evs[1].direction == CA_REALTIME_OUTBOUND &&
           strcmp(evs[1].text, "hello world foo") == 0);
    assert(evs[2].kind == CA_REALTIME_EVENT_TURN_COMPLETE);
    for (size_t i = 0; i < n; ++i) ca_realtime_event_free(&evs[i]);

    /* Second SendText advances the offset. "hi" = 1 word -> 80ms -> 16000*80/1000
     * = 1280 samples -> 2560 bytes. offset should now be the ticks of the first
     * frame's duration: 3840 samples / 16000 * 1000 = 240ms = 2,400,000 ticks. */
    assert(ca_realtime_session_send_text(s, "hi") == 0);
    assert(ca_realtime_session_receive_audio_next(s, &f));
    assert(f.pcm_len == 2560);
    assert(f.offset_ms == 2400000LL);
    ca_realtime_audio_frame_free(&f);
    /* drain its 3 events. */
    n = drain_events(s, evs, 8);
    assert(n == 3);
    for (size_t i = 0; i < n; ++i) ca_realtime_event_free(&evs[i]);

    /* Empty text -> durMs floors at 50ms -> 16000*50/1000 = 800 samples -> 1600
     * bytes (still a non-empty frame). */
    assert(ca_realtime_session_send_text(s, "") == 0);
    assert(ca_realtime_session_receive_audio_next(s, &f));
    assert(f.pcm_len == 1600);
    ca_realtime_audio_frame_free(&f);
    n = drain_events(s, evs, 8);
    assert(n == 3 && strcmp(evs[0].text, "") == 0);
    for (size_t i = 0; i < n; ++i) ca_realtime_event_free(&evs[i]);

    /* NULL text -> error. */
    assert(ca_realtime_session_send_text(s, NULL) == -1);

    ca_realtime_session_destroy(s);
    printf("  loopback_text: ok\n");
}

/* ── loopback: tool result + cancel ─────────────────────────────────────── */

static void test_loopback_tool_and_cancel(void) {
    ca_realtime_session_config_t c = cfg(CA_REALTIME_PCM_24K);
    ca_realtime_session_t *s = ca_realtime_loopback_session_create(&c, NULL, NULL);
    assert(s);

    /* short result -> "[tool call-1: {\"ok\":true}]" (no ellipsis). */
    assert(ca_realtime_session_send_tool_result(s, "call-1", "{\"ok\":true}") == 0);
    ca_realtime_event_t e;
    assert(ca_realtime_session_receive_event_next(s, &e));
    assert(e.kind == CA_REALTIME_EVENT_TRANSCRIPT_DELTA && e.direction == CA_REALTIME_OUTBOUND);
    assert(strcmp(e.text, "[tool call-1: {\"ok\":true}]") == 0);
    ca_realtime_event_free(&e);

    /* long result (>60 chars) -> truncated to 60 + U+2026 ellipsis. */
    char big[128];
    for (int i = 0; i < 100; ++i) big[i] = 'x';
    big[100] = '\0';
    assert(ca_realtime_session_send_tool_result(s, "c2", big) == 0);
    assert(ca_realtime_session_receive_event_next(s, &e));
    /* expected: "[tool c2: " + 60*'x' + "\xE2\x80\xA6" + "]". */
    char expect[128];
    int off = snprintf(expect, sizeof(expect), "[tool c2: ");
    for (int i = 0; i < 60; ++i) expect[off++] = 'x';
    expect[off++] = (char)0xE2; expect[off++] = (char)0x80; expect[off++] = (char)0xA6;
    expect[off++] = ']'; expect[off] = '\0';
    assert(strcmp(e.text, expect) == 0);
    ca_realtime_event_free(&e);

    /* validation: whitespace callId, NULL resultJson. */
    assert(ca_realtime_session_send_tool_result(s, "  ", "x") == -1);
    assert(ca_realtime_session_send_tool_result(s, "c", NULL) == -1);

    /* Cancel -> TurnComplete. */
    assert(ca_realtime_session_cancel_response(s) == 0);
    assert(ca_realtime_session_receive_event_next(s, &e));
    assert(e.kind == CA_REALTIME_EVENT_TURN_COMPLETE);
    ca_realtime_event_free(&e);

    ca_realtime_session_destroy(s);
    printf("  loopback_tool_and_cancel: ok\n");
}

/* ── custom TTS seam ────────────────────────────────────────────────────── */

static int fixed_tts(void *ctx, const char *text, ca_realtime_audio_format_t fmt,
                     uint8_t **out_pcm, size_t *out_len) {
    (void)ctx; (void)text; (void)fmt;
    *out_len = 10;
    *out_pcm = (uint8_t *)calloc(10, 1);
    return *out_pcm ? 0 : -1;
}

static void test_custom_tts(void) {
    ca_realtime_session_config_t c = cfg(CA_REALTIME_PCM_24K);
    ca_realtime_session_t *s = ca_realtime_loopback_session_create(&c, fixed_tts, NULL);
    assert(s);
    assert(ca_realtime_session_send_text(s, "anything") == 0);
    ca_realtime_audio_frame_t f;
    assert(ca_realtime_session_receive_audio_next(s, &f));
    assert(f.pcm_len == 10);   /* the injected synthesiser's output */
    ca_realtime_audio_frame_free(&f);
    ca_realtime_session_destroy(s);
    printf("  custom_tts: ok\n");
}

/* ── null session ───────────────────────────────────────────────────────── */

static void test_null_session(void) {
    ca_realtime_session_t *s = ca_realtime_null_session_create();
    assert(s);
    assert(strcmp(ca_realtime_session_id(s), "null") == 0);

    uint8_t buf[128] = {0};
    ca_realtime_audio_frame_t f; memset(&f, 0, sizeof(f));
    f.pcm = buf; f.pcm_len = sizeof(buf); f.format = CA_REALTIME_PCM_24K;
    assert(ca_realtime_session_send_audio(s, &f) == 0);
    assert(ca_realtime_session_send_text(s, "hi") == 0);
    assert(ca_realtime_session_send_tool_result(s, "c", "{}") == 0);
    assert(ca_realtime_session_cancel_response(s) == 0);
    /* both streams yield nothing. */
    assert(ca_realtime_session_audio_pending(s) == 0);
    assert(ca_realtime_session_event_pending(s) == 0);
    ca_realtime_audio_frame_t o; ca_realtime_event_t e;
    assert(!ca_realtime_session_receive_audio_next(s, &o));
    assert(!ca_realtime_session_receive_event_next(s, &e));
    ca_realtime_session_destroy(s);
    printf("  null_session: ok\n");
}

/* ── services ───────────────────────────────────────────────────────────── */

static void test_services(void) {
    /* Loopback service. */
    ca_realtime_service_t *lp = ca_realtime_loopback_service_create(NULL, NULL);
    assert(lp);
    assert(strcmp(ca_realtime_service_provider_id(lp), "loopback") == 0);
    assert(ca_realtime_service_is_configured(lp));

    ca_realtime_session_config_t c = cfg(CA_REALTIME_PCM_24K);
    ca_realtime_session_t *s = ca_realtime_service_start_session(lp, &c);
    assert(s);
    assert(strncmp(ca_realtime_session_id(s), "loop-", 5) == 0);
    /* session works end to end. */
    assert(ca_realtime_session_send_text(s, "one two") == 0);
    assert(ca_realtime_session_audio_pending(s) == 1);
    ca_realtime_session_destroy(s);

    /* start with NULL config -> NULL. */
    assert(ca_realtime_service_start_session(lp, NULL) == NULL);
    ca_realtime_service_destroy(lp);

    /* Null service. */
    ca_realtime_service_t *nul = ca_realtime_null_service_create();
    assert(nul);
    assert(strcmp(ca_realtime_service_provider_id(nul), "null") == 0);
    assert(!ca_realtime_service_is_configured(nul));
    /* StartSession refuses (C# throws) -> NULL. */
    assert(ca_realtime_service_start_session(nul, &c) == NULL);
    ca_realtime_service_destroy(nul);
    printf("  services: ok\n");
}

/* ── null transport factory ─────────────────────────────────────────────── */

static void test_null_transport_factory(void) {
    ca_realtime_transport_factory_t f = ca_realtime_null_transport_factory();
    assert(f.connect);
    ca_realtime_transport_t t;
    /* Connect always fails (no host wired). */
    int rc = f.connect(f.self, "wss://example/realtime", NULL, NULL, 0, &t);
    assert(rc == -1);
    printf("  null_transport_factory: ok\n");
}

/* ── record free-safety (owned strings) ─────────────────────────────────── */

static void test_records(void) {
    ca_realtime_tool_t t;
    memset(&t, 0, sizeof(t));
    t.name = strdup("get_weather");
    t.description = strdup("look up weather");
    t.json_schema = strdup("{}");
    ca_realtime_tool_free(&t);
    assert(t.name == NULL);

    ca_realtime_session_config_t c;
    memset(&c, 0, sizeof(c));
    c.model = strdup("m");
    c.voice_id = strdup("alloy");
    c.system_prompt = NULL;      /* optional */
    c.language_hint = strdup("en-US");
    c.tools = (ca_realtime_tool_t *)calloc(1, sizeof(ca_realtime_tool_t));
    c.tools[0].name = strdup("t");
    c.tools[0].description = strdup("d");
    c.tools[0].json_schema = strdup("{}");
    c.tool_count = 1;
    ca_realtime_session_config_free(&c);
    assert(c.tools == NULL && c.tool_count == 0);

    ca_realtime_event_t e;
    memset(&e, 0, sizeof(e));
    e.kind = CA_REALTIME_EVENT_TOOL_CALL;
    e.call_id = strdup("id");
    e.tool_name = strdup("tool");
    e.arguments_json = strdup("{}");
    ca_realtime_event_free(&e);
    assert(e.call_id == NULL);
    printf("  records: ok\n");
}

int main(void) {
    test_sample_rates();
    test_loopback_audio();
    test_loopback_text();
    test_loopback_tool_and_cancel();
    test_custom_tts();
    test_null_session();
    test_services();
    test_null_transport_factory();
    test_records();
    printf("test_realtime: all assertions passed\n");
    return 0;
}
