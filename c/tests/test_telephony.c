/*
 * test_telephony.c — CircleAI.Telephony (+ .Twilio / .Telnyx / .Plivo) C11 port.
 *
 * Verifies the carrier-agnostic contract surface (primitives, DtmfToneGenerator,
 * IMediaStream Manual/Pending, ICallSession Test/Media, IInboundCallDispatcher,
 * IToolCallRegistry, ITelephonyCarrier Null/Fallback) and the three real carrier
 * bindings against the C# reference (Contracts.cs, Primitives.cs, IMediaStream.cs,
 * ToolCalling.cs, DtmfToneGenerator.cs, TestCallSession.cs, NullImplementations.cs,
 * ServiceCollectionExtensions.cs, {Twilio,Telnyx,Plivo}{Carrier,CallSession,
 * Options}.cs). The bindings run over a deterministic fake ca_tel_http_t — no real
 * network — that records each request and returns canned JSON so the exact path +
 * body + auth header the C# adapter would send are all asserted.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

/* ===========================================================================
 * primitives + sample rate
 * =========================================================================== */

static void test_primitives(void) {
    assert(ca_tel_sample_rate_of(CA_TEL_FMT_MULAW8000) == 8000);
    assert(ca_tel_sample_rate_of(CA_TEL_FMT_ALAW8000)  == 8000);
    assert(ca_tel_sample_rate_of(CA_TEL_FMT_PCM16000)  == 16000);
    assert(ca_tel_sample_rate_of(CA_TEL_FMT_PCM24000)  == 24000);

    ca_tel_call_info_t *ci = ca_tel_call_info_new("CA123", CA_TEL_DIR_OUTBOUND,
        "+27821234567", "+27827654321", "twilio", CA_TEL_FMT_MULAW8000, 1700000000000LL);
    assert(ci);
    assert(strcmp(ci->call_id, "CA123") == 0);
    assert(ci->direction == CA_TEL_DIR_OUTBOUND);
    assert(strcmp(ci->from, "+27821234567") == 0);
    ca_tel_call_info_t *cp = ca_tel_call_info_copy(ci);
    assert(cp && cp->call_id != ci->call_id && strcmp(cp->call_id, ci->call_id) == 0);

    ca_tel_call_snapshot_t *snap = ca_tel_call_snapshot_new(ci, CA_TEL_STATUS_TRANSFERRED,
        50000000LL, 1500000 /* 1.5 */, "+27829999999");
    assert(snap && snap->status == CA_TEL_STATUS_TRANSFERRED);
    assert(snap->cost_so_far == 1500000);
    assert(strcmp(snap->transfer_target, "+27829999999") == 0);
    ca_tel_call_snapshot_destroy(snap);

    /* snapshot with null transfer target */
    ca_tel_call_snapshot_t *snap2 = ca_tel_call_snapshot_new(ci, CA_TEL_STATUS_ACTIVE,
        0, 0, NULL);
    assert(snap2 && snap2->transfer_target == NULL);
    ca_tel_call_snapshot_destroy(snap2);

    ca_tel_call_info_destroy(ci);
    ca_tel_call_info_destroy(cp);
    printf("  primitives: ok\n");
}

/* ===========================================================================
 * DtmfToneGenerator
 * =========================================================================== */

static int16_t rd16(const uint8_t *p) {
    return (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static void test_dtmf(void) {
    /* Generate('1', 8000, 150): samples = 8000*150/1000 = 1200 -> 2400 bytes. */
    size_t len = 0;
    uint8_t *buf = ca_tel_dtmf_generate('1', 8000, 150, 0.5f, &len);
    assert(buf && len == 2400);
    /* first sample at t=0: sin(0)+sin(0)=0. */
    assert(rd16(buf) == 0);
    /* non-trivial energy somewhere in the buffer (not all zeros). */
    long long energy = 0;
    for (size_t i = 0; i + 1 < len; i += 2) { int16_t s = rd16(buf + i); energy += (long long)s * s; }
    assert(energy > 0);
    free(buf);

    /* 24kHz, 100ms -> 2400 samples -> 4800 bytes. */
    buf = ca_tel_dtmf_generate('#', 24000, 100, 0.5f, &len);
    assert(buf && len == 4800);
    free(buf);

    /* unsupported digit -> NULL. */
    assert(ca_tel_dtmf_generate('Z', 8000, 150, 0.5f, &len) == NULL);
    /* bad sample rate / duration -> NULL. */
    assert(ca_tel_dtmf_generate('1', 0, 150, 0.5f, &len) == NULL);
    assert(ca_tel_dtmf_generate('1', 8000, 0, 0.5f, &len) == NULL);

    /* GenerateSequence("12", 8000, tone=150, gap=50):
     *   tone bytes = 1200*2 = 2400 each; gap = 400*2 = 800; total = 2*2400 + 800. */
    buf = ca_tel_dtmf_generate_sequence("12", 8000, 150, 50, 0.5f, &len);
    assert(buf && len == (size_t)(2 * 2400 + 800));
    /* gap region (bytes [2400,3200)) is silence. */
    for (size_t i = 2400; i < 3200; i += 2) assert(rd16(buf + i) == 0);
    free(buf);

    /* empty digits -> 0-length non-NULL sentinel. */
    buf = ca_tel_dtmf_generate_sequence("", 8000, 150, 50, 0.5f, &len);
    assert(buf && len == 0);
    free(buf);

    /* single digit: no trailing gap. */
    buf = ca_tel_dtmf_generate_sequence("5", 8000, 150, 50, 0.5f, &len);
    assert(buf && len == 2400);
    free(buf);

    /* unsupported digit in sequence -> NULL. */
    assert(ca_tel_dtmf_generate_sequence("1Z", 8000, 150, 50, 0.5f, &len) == NULL);

    printf("  dtmf: ok\n");
}

/* ===========================================================================
 * IToolCallRegistry — DefaultToolCallRegistry
 * =========================================================================== */

static int echo_handler(void *ctx, const char *args, char **out) {
    (void)ctx;
    /* echo back the arguments as the result */
    *out = strdup(args && args[0] ? args : "{}");
    return 0;
}
static int null_result_handler(void *ctx, const char *args, char **out) {
    (void)ctx; (void)args;
    *out = NULL;   /* -> "{}" */
    return 0;
}
static int throwing_handler(void *ctx, const char *args, char **out) {
    (void)ctx; (void)args; (void)out;
    return -1;     /* models a thrown exception */
}

/* fake webhook poster: records the last envelope + returns a programmed
 * status/body. */
typedef struct {
    char *last_url;
    char *last_body;
    int   next_status;
    const char *next_body;   /* borrowed literal */
    int   fail_transport;    /* if set, return -1 (connection failure) */
    int   calls;
} fake_poster_t;

static int fake_post(void *ctx, const char *url, const char *json_body,
                     int *out_status, char **out_body) {
    fake_poster_t *fp = (fake_poster_t *)ctx;
    fp->calls++;
    free(fp->last_url); free(fp->last_body);
    fp->last_url = strdup(url);
    fp->last_body = strdup(json_body);
    if (fp->fail_transport) return -1;
    *out_status = fp->next_status;
    *out_body = fp->next_body ? strdup(fp->next_body) : NULL;
    return 0;
}

static void test_tool_registry(void) {
    fake_poster_t fp; memset(&fp, 0, sizeof(fp));
    ca_tel_tool_registry_t *r = ca_tel_tool_registry_create(fake_post, &fp);
    assert(r);
    assert(ca_tel_tool_registry_definition_count(r) == 0);

    ca_tel_tool_definition_t def = { (char *)"lookup", (char *)"Look something up",
                                     (char *)"{\"type\":\"object\"}" };
    assert(ca_tel_tool_registry_register_local(r, &def, echo_handler, NULL) == 0);
    assert(ca_tel_tool_registry_definition_count(r) == 1);

    /* definitions deep-copy */
    size_t dn = 0;
    ca_tel_tool_definition_t *defs = ca_tel_tool_registry_definitions(r, &dn);
    assert(dn == 1 && defs && strcmp(defs[0].name, "lookup") == 0);
    ca_tel_tool_definition_free(&defs[0]); free(defs);

    /* invoke local: echoes args */
    ca_tel_tool_invocation_t inv = { (char *)"c1", (char *)"lookup",
                                     (char *)"{\"q\":\"weather\"}" };
    ca_tel_tool_result_t *res = ca_tel_tool_registry_invoke(r, &inv);
    assert(res && res->succeeded);
    assert(strcmp(res->call_id, "c1") == 0);
    assert(strcmp(res->result_json, "{\"q\":\"weather\"}") == 0);
    assert(res->error == NULL);
    ca_tel_tool_result_free(res); free(res);

    /* case-insensitive lookup (OrdinalIgnoreCase) */
    ca_tel_tool_invocation_t inv_ci = { (char *)"c2", (char *)"LOOKUP", (char *)"{}" };
    res = ca_tel_tool_registry_invoke(r, &inv_ci);
    assert(res && res->succeeded);
    ca_tel_tool_result_free(res); free(res);

    /* unregistered tool -> Succeeded=false, ResultJson="{}", Error set */
    ca_tel_tool_invocation_t inv_missing = { (char *)"c3", (char *)"nope", (char *)"{}" };
    res = ca_tel_tool_registry_invoke(r, &inv_missing);
    assert(res && !res->succeeded);
    assert(strcmp(res->result_json, "{}") == 0);
    assert(res->error && strstr(res->error, "not registered"));
    ca_tel_tool_result_free(res); free(res);

    /* null-result handler -> "{}" */
    ca_tel_tool_definition_t def_nr = { (char *)"nr", (char *)"", (char *)"{}" };
    assert(ca_tel_tool_registry_register_local(r, &def_nr, null_result_handler, NULL) == 0);
    ca_tel_tool_invocation_t inv_nr = { (char *)"c4", (char *)"nr", (char *)"{}" };
    res = ca_tel_tool_registry_invoke(r, &inv_nr);
    assert(res && res->succeeded && strcmp(res->result_json, "{}") == 0);
    ca_tel_tool_result_free(res); free(res);

    /* throwing handler -> Succeeded=false */
    ca_tel_tool_definition_t def_th = { (char *)"boom", (char *)"", (char *)"{}" };
    assert(ca_tel_tool_registry_register_local(r, &def_th, throwing_handler, NULL) == 0);
    ca_tel_tool_invocation_t inv_th = { (char *)"c5", (char *)"boom", (char *)"{}" };
    res = ca_tel_tool_registry_invoke(r, &inv_th);
    assert(res && !res->succeeded);
    ca_tel_tool_result_free(res); free(res);

    /* webhook tool: successful 200 with a body */
    ca_tel_tool_definition_t def_wh = { (char *)"remote", (char *)"", (char *)"{}" };
    assert(ca_tel_tool_registry_register_webhook(r, &def_wh, "https://example.com/hook") == 0);
    /* relative URL rejected */
    assert(ca_tel_tool_registry_register_webhook(r, &def_wh, "/relative") == -1);
    /* blank name rejected */
    ca_tel_tool_definition_t def_blank = { (char *)"  ", (char *)"", (char *)"{}" };
    assert(ca_tel_tool_registry_register_webhook(r, &def_blank, "https://x.com") == -1);

    fp.next_status = 200; fp.next_body = "{\"ok\":true}";
    ca_tel_tool_invocation_t inv_wh = { (char *)"c6", (char *)"remote",
                                        (char *)"{\"a\":1}" };
    res = ca_tel_tool_registry_invoke(r, &inv_wh);
    assert(res && res->succeeded && strcmp(res->result_json, "{\"ok\":true}") == 0);
    ca_tel_tool_result_free(res); free(res);
    /* envelope shape: {"call_id":"c6","tool":"remote","arguments":{"a":1}} */
    assert(fp.last_body && strstr(fp.last_body, "\"call_id\":\"c6\""));
    assert(strstr(fp.last_body, "\"tool\":\"remote\""));
    assert(strstr(fp.last_body, "\"arguments\":{\"a\":1}"));
    assert(strcmp(fp.last_url, "https://example.com/hook") == 0);

    /* webhook non-2xx -> Succeeded=false with "Webhook <status>:" */
    fp.next_status = 500; fp.next_body = "boom";
    res = ca_tel_tool_registry_invoke(r, &inv_wh);
    assert(res && !res->succeeded && strstr(res->error, "Webhook 500"));
    ca_tel_tool_result_free(res); free(res);

    /* webhook success with empty body -> "{}" */
    fp.next_status = 204; fp.next_body = NULL;
    res = ca_tel_tool_registry_invoke(r, &inv_wh);
    assert(res && res->succeeded && strcmp(res->result_json, "{}") == 0);
    ca_tel_tool_result_free(res); free(res);

    /* transport failure -> connection error */
    fp.fail_transport = 1;
    res = ca_tel_tool_registry_invoke(r, &inv_wh);
    assert(res && !res->succeeded && res->error);
    ca_tel_tool_result_free(res); free(res);
    fp.fail_transport = 0;

    /* LWW: re-register "lookup" as a webhook replaces the local handler */
    ca_tel_tool_definition_t def_lww = { (char *)"lookup", (char *)"now remote", (char *)"{}" };
    assert(ca_tel_tool_registry_register_webhook(r, &def_lww, "https://ex.com/lookup") == 0);
    assert(ca_tel_tool_registry_definition_count(r) == 4);  /* lookup,nr,boom,remote */

    free(fp.last_url); free(fp.last_body);
    ca_tel_tool_registry_destroy(r);
    printf("  tool_registry: ok\n");
}

/* ===========================================================================
 * IMediaStream — Manual + Pending
 * =========================================================================== */

typedef struct { int fires; ca_tel_call_status_t last; } status_rec_t;
static void on_status(void *ctx, ca_tel_call_status_t s) {
    status_rec_t *r = (status_rec_t *)ctx;
    r->fires++; r->last = s;
}

static void test_media_stream(void) {
    ca_tel_call_info_t *info = ca_tel_call_info_new("CID", CA_TEL_DIR_INBOUND,
        "+111", "+222", "twilio", CA_TEL_FMT_MULAW8000, 1700000000000LL);
    assert(info);

    /* Manual, native DTMF supported. */
    ca_tel_media_stream_t *m = ca_tel_manual_media_create(info, CA_TEL_STATUS_ACTIVE, true);
    assert(m);
    assert(ca_tel_media_stream_status(m) == CA_TEL_STATUS_ACTIVE);
    assert(ca_tel_media_stream_supports_native_dtmf(m));
    assert(strcmp(ca_tel_media_stream_info(m)->call_id, "CID") == 0);

    status_rec_t rec = {0, CA_TEL_STATUS_RINGING};
    ca_tel_status_sub_t *sub = ca_tel_media_stream_subscribe_status(m, on_status, &rec);
    assert(sub);

    /* inject inbound audio + drain */
    uint8_t pcm[16]; for (int i = 0; i < 16; ++i) pcm[i] = (uint8_t)i;
    ca_tel_audio_frame_t inj = { pcm, sizeof(pcm), CA_TEL_FMT_MULAW8000, 0 };
    assert(ca_tel_manual_media_inject_audio(m, &inj) == 0);
    assert(ca_tel_media_stream_audio_pending(m) == 1);
    ca_tel_audio_frame_t got; memset(&got, 0, sizeof(got));
    assert(ca_tel_media_stream_receive_audio_next(m, &got));
    assert(got.pcm_len == 16 && got.pcm[3] == 3 && got.pcm != pcm);
    ca_tel_audio_frame_free(&got);
    assert(ca_tel_media_stream_audio_pending(m) == 0);

    /* inject inbound DTMF + drain */
    ca_tel_dtmf_event_t dev = { '5', 1500000, 3000000 };
    assert(ca_tel_manual_media_inject_dtmf(m, &dev) == 0);
    ca_tel_dtmf_event_t dgot;
    assert(ca_tel_media_stream_receive_dtmf_next(m, &dgot));
    assert(dgot.digit == '5' && dgot.duration_ticks == 1500000);

    /* send audio -> captured to SentAudioFrames */
    ca_tel_audio_frame_t out = { pcm, 8, CA_TEL_FMT_MULAW8000, 42 };
    assert(ca_tel_media_stream_send_audio(m, &out) == 0);
    assert(ca_tel_media_stream_sent_audio_count(m) == 1);
    size_t sn = 0;
    ca_tel_audio_frame_t *sent = ca_tel_media_stream_sent_audio(m, &sn);
    assert(sn == 1 && sent[0].pcm_len == 8 && sent[0].offset_ticks == 42);
    ca_tel_audio_frame_free(&sent[0]); free(sent);

    /* native DTMF captured to SentDtmf */
    assert(ca_tel_media_stream_send_dtmf(m, "123") == 0);
    assert(ca_tel_media_stream_sent_dtmf_count(m) == 1);
    size_t dn = 0;
    char **sd = ca_tel_media_stream_sent_dtmf(m, &dn);
    assert(dn == 1 && strcmp(sd[0], "123") == 0);
    free(sd[0]); free(sd);

    /* status change fires handler (dedup: same status doesn't re-fire) */
    ca_tel_media_stream_set_status(m, CA_TEL_STATUS_ACTIVE);   /* unchanged */
    assert(rec.fires == 0);
    ca_tel_media_stream_set_status(m, CA_TEL_STATUS_ENDED_BY_CALLER);
    assert(rec.fires == 1 && rec.last == CA_TEL_STATUS_ENDED_BY_CALLER);

    /* EndAsync flips to EndedByAgent + fires */
    ca_tel_media_stream_end(m);
    assert(rec.fires == 2 && rec.last == CA_TEL_STATUS_ENDED_BY_AGENT);

    ca_tel_status_unsubscribe(sub);
    ca_tel_media_stream_destroy(m);

    /* Pending: SendAudio errors; ReceiveAudio empty; EndAsync -> EndedByAgent. */
    ca_tel_media_stream_t *pend = ca_tel_pending_media_create(info);
    assert(pend && ca_tel_media_stream_status(pend) == CA_TEL_STATUS_RINGING);
    assert(!ca_tel_media_stream_supports_native_dtmf(pend));
    ca_tel_audio_frame_t pf = { pcm, 4, CA_TEL_FMT_MULAW8000, 0 };
    assert(ca_tel_media_stream_send_audio(pend, &pf) == -1);   /* cannot send before attach */
    assert(ca_tel_media_stream_send_dtmf(pend, "1") == -1);
    ca_tel_audio_frame_t none;
    assert(!ca_tel_media_stream_receive_audio_next(pend, &none));
    status_rec_t prec = {0, CA_TEL_STATUS_RINGING};
    ca_tel_status_sub_t *psub = ca_tel_media_stream_subscribe_status(pend, on_status, &prec);
    ca_tel_media_stream_end(pend);
    assert(ca_tel_media_stream_status(pend) == CA_TEL_STATUS_ENDED_BY_AGENT);
    assert(prec.fires == 1);
    ca_tel_status_unsubscribe(psub);
    ca_tel_media_stream_destroy(pend);

    ca_tel_call_info_destroy(info);
    printf("  media_stream: ok\n");
}

/* ===========================================================================
 * ICallSession — TestCallSession
 * =========================================================================== */

static void test_test_call_session(void) {
    /* default CallInfo (info=NULL): Inbound, +15555550100 -> +15555550200,
     * carrier "test", Pcm16000, status Active. */
    ca_tel_call_session_t *s = ca_tel_test_call_session_create(NULL);
    assert(s);
    const ca_tel_call_info_t *info = ca_tel_call_session_info(s);
    assert(info->direction == CA_TEL_DIR_INBOUND);
    assert(strcmp(info->from, "+15555550100") == 0);
    assert(strcmp(info->to, "+15555550200") == 0);
    assert(strcmp(info->carrier_id, "test") == 0);
    assert(info->media_format == CA_TEL_FMT_PCM16000);
    assert(ca_tel_call_session_status(s) == CA_TEL_STATUS_ACTIVE);

    status_rec_t rec = {0, CA_TEL_STATUS_RINGING};
    ca_tel_status_sub_t *sub = ca_tel_call_session_subscribe_status(s, on_status, &rec);

    /* inject + receive inbound audio */
    uint8_t pcm[8] = {1,2,3,4,5,6,7,8};
    ca_tel_audio_frame_t inj = { pcm, 8, CA_TEL_FMT_PCM16000, 0 };
    assert(ca_tel_test_call_session_inject_audio(s, &inj) == 0);
    assert(ca_tel_call_session_audio_pending(s) == 1);
    ca_tel_audio_frame_t got;
    assert(ca_tel_call_session_receive_audio_next(s, &got));
    assert(got.pcm_len == 8 && got.pcm[0] == 1);
    ca_tel_audio_frame_free(&got);

    /* inject + receive inbound DTMF */
    ca_tel_dtmf_event_t dev = { '#', 1000000, 0 };
    assert(ca_tel_test_call_session_inject_dtmf(s, &dev) == 0);
    ca_tel_dtmf_event_t dgot;
    assert(ca_tel_call_session_receive_dtmf_next(s, &dgot) && dgot.digit == '#');

    /* SendAudio captured to SentAudioFrames */
    ca_tel_audio_frame_t out = { pcm, 4, CA_TEL_FMT_PCM16000, 7 };
    assert(ca_tel_call_session_send_audio(s, &out) == 0);
    assert(ca_tel_call_session_sent_audio_count(s) == 1);

    /* SendDtmf captured to SentDtmf (no media stream -> just recorded) */
    assert(ca_tel_call_session_send_dtmf(s, "42") == 0);
    assert(ca_tel_call_session_sent_dtmf_count(s) == 1);
    size_t dn = 0; char **sd = ca_tel_call_session_sent_dtmf(s, &dn);
    assert(dn == 1 && strcmp(sd[0], "42") == 0);
    free(sd[0]); free(sd);
    /* empty digits -> no-op success, nothing captured */
    assert(ca_tel_call_session_send_dtmf(s, "") == 0);
    assert(ca_tel_call_session_sent_dtmf_count(s) == 1);

    /* Transfer -> Transferred (fires) */
    assert(ca_tel_call_session_transfer(s, "+27821112222", CA_TEL_TRANSFER_WARM, "brief") == 0);
    assert(ca_tel_call_session_status(s) == CA_TEL_STATUS_TRANSFERRED);
    assert(rec.fires == 1 && rec.last == CA_TEL_STATUS_TRANSFERRED);

    /* TriggerStatusChange fires even for a repeat status */
    ca_tel_test_call_session_trigger_status(s, CA_TEL_STATUS_TRANSFERRED);
    assert(rec.fires == 2);

    /* HangUp -> EndedByAgent */
    assert(ca_tel_call_session_hangup(s) == 0);
    assert(ca_tel_call_session_status(s) == CA_TEL_STATUS_ENDED_BY_AGENT);
    assert(rec.last == CA_TEL_STATUS_ENDED_BY_AGENT);

    ca_tel_status_unsubscribe(sub);
    ca_tel_call_session_destroy(s);
    printf("  test_call_session: ok\n");
}

/* ===========================================================================
 * ICallSession — MediaCallSession (over a Manual media stream + a carrier)
 *
 * Exercises: Status folding driven by the media StatusChanged subscription,
 * native out-of-band DTMF routing, and the in-band DTMF tone fallback.
 * =========================================================================== */

static void test_media_call_session(void) {
    ca_tel_call_info_t *info = ca_tel_call_info_new("CIDm", CA_TEL_DIR_OUTBOUND,
        "+111", "+222", "test", CA_TEL_FMT_PCM16000, 1700000000000LL);
    assert(info);

    /* ---- native-DTMF path: media supports IDtmfSendable ---- */
    ca_tel_media_stream_t *media = ca_tel_manual_media_create(info,
        CA_TEL_STATUS_RINGING, /*native_dtmf*/true);
    assert(media);
    ca_tel_carrier_t *carrier = ca_tel_null_carrier_create();   /* end_call no-op */
    ca_tel_call_session_t *s = ca_tel_media_call_session_create(media, carrier);
    assert(s);
    /* Status folds: media Ringing + latch Ringing -> Ringing. */
    assert(ca_tel_call_session_status(s) == CA_TEL_STATUS_RINGING);

    /* subscribe to session status; a media flip must propagate. */
    status_rec_t rec = {0, CA_TEL_STATUS_RINGING};
    ca_tel_status_sub_t *sub = ca_tel_call_session_subscribe_status(s, on_status, &rec);
    ca_tel_media_stream_set_status(media, CA_TEL_STATUS_ACTIVE);
    assert(rec.fires == 1 && rec.last == CA_TEL_STATUS_ACTIVE);
    assert(ca_tel_call_session_status(s) == CA_TEL_STATUS_ACTIVE);   /* media reports Active */

    /* native DTMF -> captured to the media's SentDtmf, NOT synthesized to audio. */
    assert(ca_tel_call_session_send_dtmf(s, "789") == 0);
    assert(ca_tel_call_session_sent_dtmf_count(s) == 1);
    assert(ca_tel_call_session_sent_audio_count(s) == 0);   /* no in-band tones */
    size_t dn = 0; char **sd = ca_tel_call_session_sent_dtmf(s, &dn);
    assert(dn == 1 && strcmp(sd[0], "789") == 0);
    free(sd[0]); free(sd);

    ca_tel_status_unsubscribe(sub);
    ca_tel_call_session_destroy(s);   /* destroys media */
    ca_tel_carrier_destroy(carrier);

    /* ---- in-band fallback: media does NOT support native DTMF ---- */
    ca_tel_media_stream_t *media2 = ca_tel_manual_media_create(info,
        CA_TEL_STATUS_ACTIVE, /*native_dtmf*/false);
    ca_tel_carrier_t *carrier2 = ca_tel_null_carrier_create();
    ca_tel_call_session_t *s2 = ca_tel_media_call_session_create(media2, carrier2);
    assert(s2);
    /* SendDtmf synthesizes tones and pushes ONE outbound audio frame at the
     * format's sample rate (Pcm16000 -> 16000 Hz -> Pcm16000 frame). */
    assert(ca_tel_call_session_send_dtmf(s2, "12") == 0);
    assert(ca_tel_call_session_sent_dtmf_count(s2) == 0);   /* not recorded as DTMF */
    assert(ca_tel_call_session_sent_audio_count(s2) == 1);  /* one tone frame */
    size_t an = 0;
    ca_tel_audio_frame_t *sent = ca_tel_call_session_sent_audio(s2, &an);
    assert(an == 1 && sent[0].format == CA_TEL_FMT_PCM16000);
    /* sequence "12" at 16kHz: 2 tones (150ms=2400 samples each) + 1 gap
     * (50ms=800 samples) -> (2*2400 + 800)*2 bytes. */
    assert(sent[0].pcm_len == (size_t)((2 * 2400 + 800) * 2));
    ca_tel_audio_frame_free(&sent[0]); free(sent);

    /* empty digits -> no-op, nothing added. */
    assert(ca_tel_call_session_send_dtmf(s2, "") == 0);
    assert(ca_tel_call_session_sent_audio_count(s2) == 1);

    ca_tel_call_session_destroy(s2);
    ca_tel_carrier_destroy(carrier2);
    ca_tel_call_info_destroy(info);
    printf("  media_call_session: ok\n");
}

/* ===========================================================================
 * IInboundCallDispatcher — InMemory + Null
 * =========================================================================== */

typedef struct { int count; ca_tel_call_session_t *last; } dispatch_rec_t;
static void on_inbound(void *ctx, ca_tel_call_session_t *sess) {
    dispatch_rec_t *r = (dispatch_rec_t *)ctx;
    r->count++; r->last = sess;
}

static void test_dispatcher(void) {
    /* Null: never fires. */
    ca_tel_dispatcher_t *nd = ca_tel_null_dispatcher_create();
    assert(nd && strcmp(ca_tel_dispatcher_carrier_id(nd), "null") == 0);
    dispatch_rec_t nr = {0, NULL};
    ca_tel_dispatcher_sub_t *ns = ca_tel_dispatcher_subscribe(nd, on_inbound, &nr);
    assert(ns);
    ca_tel_call_session_t *sess0 = ca_tel_test_call_session_create(NULL);
    assert(ca_tel_dispatcher_publish(nd, sess0) == 0 && nr.count == 0);
    ca_tel_dispatcher_unsubscribe(ns);
    ca_tel_call_session_destroy(sess0);
    ca_tel_dispatcher_destroy(nd);

    /* InMemory: subscribe-then-publish delivers synchronously. */
    ca_tel_dispatcher_t *d = ca_tel_inmemory_dispatcher_create("twilio");
    assert(d && strcmp(ca_tel_dispatcher_carrier_id(d), "twilio") == 0);
    dispatch_rec_t r1 = {0, NULL};
    ca_tel_dispatcher_sub_t *s1 = ca_tel_dispatcher_subscribe(d, on_inbound, &r1);
    ca_tel_call_session_t *sess1 = ca_tel_test_call_session_create(NULL);
    assert(ca_tel_dispatcher_publish(d, sess1) == 1);
    assert(r1.count == 1 && r1.last == sess1);

    /* A subscriber attaching AFTER a publish still observes it (replay — the
     * unbounded-channel semantics; the race the concurrency note warns about). */
    dispatch_rec_t r2 = {0, NULL};
    ca_tel_dispatcher_sub_t *s2 = ca_tel_dispatcher_subscribe(d, on_inbound, &r2);
    assert(r2.count == 1 && r2.last == sess1);   /* replayed */

    /* A second publish reaches both current subscribers. */
    ca_tel_call_session_t *sess2 = ca_tel_test_call_session_create(NULL);
    assert(ca_tel_dispatcher_publish(d, sess2) == 2);
    assert(r1.count == 2 && r2.count == 2);

    /* Unsubscribe s1: next publish only reaches s2. */
    ca_tel_dispatcher_unsubscribe(s1);
    ca_tel_call_session_t *sess3 = ca_tel_test_call_session_create(NULL);
    assert(ca_tel_dispatcher_publish(d, sess3) == 1);
    assert(r2.count == 3);

    ca_tel_dispatcher_unsubscribe(s2);
    ca_tel_dispatcher_destroy(d);
    ca_tel_call_session_destroy(sess1);
    ca_tel_call_session_destroy(sess2);
    ca_tel_call_session_destroy(sess3);
    printf("  dispatcher: ok\n");
}

/* ===========================================================================
 * ITelephonyCarrier — Null + Fallback
 * =========================================================================== */

static void test_null_carrier(void) {
    ca_tel_carrier_t *c = ca_tel_null_carrier_create();
    assert(c);
    assert(strcmp(ca_tel_carrier_id(c), "null") == 0);
    assert(!ca_tel_carrier_is_configured(c));
    /* ProvisionNumber throws -> -1 */
    ca_tel_provisioned_number_t pn;
    assert(ca_tel_carrier_provision_number(c, "ZA", NULL, &pn) == -1);
    /* ConfigureInbound is a no-op success */
    assert(ca_tel_carrier_configure_inbound(c, "+27821234567", "https://x/w") == 0);
    /* Dial throws -> NULL */
    assert(ca_tel_carrier_dial(c, "+1", "+2", "wss://s", NULL) == NULL);
    /* ListNumbers empty */
    size_t n = 999;
    assert(ca_tel_carrier_list_numbers(c, &n) == NULL && n == 0);
    ca_tel_carrier_destroy(c);
    printf("  null_carrier: ok\n");
}

/* ===========================================================================
 * Fake ca_tel_http_t — records the last request, returns programmed responses.
 * Supports a small script of (status, body) pairs consumed in order.
 * =========================================================================== */

typedef struct {
    /* last request captured */
    char *method, *path, *auth, *content_type, *body;
    /* scripted responses */
    const int  *statuses;
    const char *const *bodies;
    size_t      script_len, script_idx;
    int         fail_transport;
    int         calls;
} fake_http_t;

static int fake_http_request(void *self, const char *method, const char *path,
                             const char *auth, const char *content_type,
                             const char *body, int *out_status, char **out_body) {
    fake_http_t *h = (fake_http_t *)self;
    h->calls++;
    free(h->method); free(h->path); free(h->auth); free(h->content_type); free(h->body);
    h->method = strdup(method);
    h->path = strdup(path);
    h->auth = auth ? strdup(auth) : NULL;
    h->content_type = content_type ? strdup(content_type) : NULL;
    h->body = body ? strdup(body) : NULL;
    if (h->fail_transport) return -1;
    size_t i = h->script_idx < h->script_len ? h->script_idx : (h->script_len ? h->script_len - 1 : 0);
    h->script_idx++;
    *out_status = h->script_len ? h->statuses[i] : 200;
    const char *b = h->script_len ? h->bodies[i] : NULL;
    *out_body = b ? strdup(b) : NULL;
    return 0;
}
static void fake_http_reset(fake_http_t *h) {
    free(h->method); free(h->path); free(h->auth); free(h->content_type); free(h->body);
    memset(h, 0, sizeof(*h));
}

/* ===========================================================================
 * Twilio binding
 * =========================================================================== */

static void test_twilio(void) {
    fake_http_t h; memset(&h, 0, sizeof(h));
    ca_tel_http_t http = { &h, fake_http_request };

    /* unconfigured (no creds): IsConfigured false, no auth header applied. */
    ca_tel_twilio_options_t empty = { NULL, NULL, NULL };
    ca_tel_carrier_t *cu = ca_tel_twilio_create(http, &empty);
    assert(cu && strcmp(ca_tel_carrier_id(cu), "twilio") == 0);
    assert(!ca_tel_carrier_is_configured(cu));
    /* provision on unconfigured -> -1, no request issued */
    ca_tel_provisioned_number_t pn;
    assert(ca_tel_carrier_provision_number(cu, "US", NULL, &pn) == -1);
    assert(h.calls == 0);
    ca_tel_carrier_destroy(cu);

    /* configured. */
    ca_tel_twilio_options_t opt = { NULL, "AC_test_sid", "tok_secret", };
    ca_tel_carrier_t *c = ca_tel_twilio_create(http, &opt);
    assert(c && ca_tel_carrier_is_configured(c));

    /* ---- ProvisionNumber: GET available -> POST reserve. ---- */
    static const int prov_status[] = { 200, 201 };
    static const char *const prov_body[] = {
        "{\"available_phone_numbers\":[{\"phone_number\":\"+15125551234\",\"price\":\"1.15\"}]}",
        "{\"sid\":\"PN123\"}"
    };
    h.statuses = prov_status; h.bodies = prov_body; h.script_len = 2; h.script_idx = 0;
    memset(&pn, 0, sizeof(pn));
    assert(ca_tel_carrier_provision_number(c, "US", "512", &pn) == 0);
    assert(strcmp(pn.phone_number, "+15125551234") == 0);
    assert(strcmp(pn.carrier_id, "twilio") == 0);
    assert(pn.monthly_recurring_cost == 1150000);   /* 1.15 * 1e6 */
    /* the FIRST request was the GET availability with AreaCode + Basic auth. */
    /* (h captured the LAST = reserve POST; assert the reserve path + auth) */
    assert(strcmp(h.method, "POST") == 0);
    assert(strstr(h.path, "/IncomingPhoneNumbers.json"));
    assert(h.auth && strncmp(h.auth, "Basic ", 6) == 0);
    assert(h.body && strstr(h.body, "PhoneNumber=%2B15125551234"));  /* form-encoded '+' */
    ca_tel_provisioned_number_free(&pn);

    /* provisioning with NO availability -> -1 */
    static const int none_status[] = { 200 };
    static const char *const none_body[] = { "{\"available_phone_numbers\":[]}" };
    h.statuses = none_status; h.bodies = none_body; h.script_len = 1; h.script_idx = 0;
    memset(&pn, 0, sizeof(pn));
    assert(ca_tel_carrier_provision_number(c, "US", NULL, &pn) == -1);

    /* ---- ConfigureInbound: GET list -> POST update. ---- */
    static const int cfg_status[] = { 200, 200 };
    static const char *const cfg_body[] = {
        "{\"incoming_phone_numbers\":[{\"sid\":\"PNabc\"}]}",
        "{}"
    };
    h.statuses = cfg_status; h.bodies = cfg_body; h.script_len = 2; h.script_idx = 0;
    assert(ca_tel_carrier_configure_inbound(c, "+15125551234", "https://host/voice") == 0);
    /* last request: POST IncomingPhoneNumbers/PNabc.json with VoiceUrl + VoiceMethod. */
    assert(strstr(h.path, "/IncomingPhoneNumbers/PNabc.json"));
    assert(h.body && strstr(h.body, "VoiceUrl="));
    assert(strstr(h.body, "VoiceMethod=POST"));

    /* ---- Dial: POST Calls.json -> session over PendingMediaStream. ---- */
    static const int dial_status[] = { 201 };
    static const char *const dial_body[] = { "{\"sid\":\"CAdeadbeef\"}" };
    h.statuses = dial_status; h.bodies = dial_body; h.script_len = 1; h.script_idx = 0;
    ca_tel_dial_options_t *o = ca_tel_dial_options_new();
    o->detect_answering_machine = true;
    o->ring_timeout_seconds = 20;
    ca_tel_call_session_t *sess = ca_tel_carrier_dial(c, "+15125550000",
        "+15125559999", "wss://host/stream", o);
    assert(sess);
    const ca_tel_call_info_t *si = ca_tel_call_session_info(sess);
    assert(strcmp(si->call_id, "CAdeadbeef") == 0);
    assert(si->direction == CA_TEL_DIR_OUTBOUND);
    assert(si->media_format == CA_TEL_FMT_MULAW8000);
    /* MediaCallSession over a Pending stream: Status folds to Ringing (media
     * still Ringing, no latch yet). */
    assert(ca_tel_call_session_status(sess) == CA_TEL_STATUS_RINGING);
    /* the dial POST body: From/To/Twiml(<Connect><Stream>)/Timeout/MachineDetection. */
    assert(strstr(h.path, "/Calls.json"));
    assert(h.body && strstr(h.body, "From=%2B15125550000"));
    assert(strstr(h.body, "To=%2B15125559999"));
    assert(strstr(h.body, "Twiml="));
    assert(strstr(h.body, "Timeout=20"));
    assert(strstr(h.body, "MachineDetection=Enable"));

    /* HangUp: latches EndedByAgent + issues Calls/{sid}.json Status=completed. */
    static const int hang_status[] = { 200 };
    static const char *const hang_body[] = { "{}" };
    h.statuses = hang_status; h.bodies = hang_body; h.script_len = 1; h.script_idx = 0;
    int before = h.calls;
    assert(ca_tel_call_session_hangup(sess) == 0);
    assert(ca_tel_call_session_status(sess) == CA_TEL_STATUS_ENDED_BY_AGENT);
    assert(h.calls == before + 1);
    assert(strstr(h.path, "/Calls/CAdeadbeef.json"));
    assert(h.body && strstr(h.body, "Status=completed"));

    ca_tel_dial_options_destroy(o);
    ca_tel_call_session_destroy(sess);

    /* ---- Transfer (cold) via a fresh dialed session. ---- */
    static const int d2_status[] = { 201 };
    static const char *const d2_body[] = { "{\"sid\":\"CAxfer\"}" };
    h.statuses = d2_status; h.bodies = d2_body; h.script_len = 1; h.script_idx = 0;
    ca_tel_call_session_t *sx = ca_tel_carrier_dial(c, "+1", "+2", "wss://s", NULL);
    assert(sx);
    static const int t_status[] = { 200 };
    static const char *const t_body[] = { "{}" };
    h.statuses = t_status; h.bodies = t_body; h.script_len = 1; h.script_idx = 0;
    assert(ca_tel_call_session_transfer(sx, "+15125553333", CA_TEL_TRANSFER_COLD, NULL) == 0);
    assert(ca_tel_call_session_status(sx) == CA_TEL_STATUS_TRANSFERRED);
    /* transfer POST: Twiml=<Response><Dial>+15125553333</Dial></Response> (html-encoded). */
    assert(strstr(h.path, "/Calls/CAxfer.json"));
    assert(h.body && strstr(h.body, "Twiml="));
    ca_tel_call_session_destroy(sx);

    /* ---- ListNumbers ---- */
    static const int list_status[] = { 200 };
    static const char *const list_body[] = {
        "{\"incoming_phone_numbers\":[{\"phone_number\":\"+15125551234\"},{\"phone_number\":\"+15125555678\"}]}"
    };
    h.statuses = list_status; h.bodies = list_body; h.script_len = 1; h.script_idx = 0;
    size_t ln = 0;
    ca_tel_provisioned_number_t *nums = ca_tel_carrier_list_numbers(c, &ln);
    assert(ln == 2 && nums);
    assert(strcmp(nums[0].phone_number, "+15125551234") == 0);
    assert(strcmp(nums[1].phone_number, "+15125555678") == 0);
    ca_tel_provisioned_number_free_array(nums, ln);

    /* ListNumbers on non-2xx -> empty (fail-soft). */
    static const int lerr_status[] = { 503 };
    static const char *const lerr_body[] = { "err" };
    h.statuses = lerr_status; h.bodies = lerr_body; h.script_len = 1; h.script_idx = 0;
    ln = 999;
    assert(ca_tel_carrier_list_numbers(c, &ln) == NULL && ln == 0);

    ca_tel_carrier_destroy(c);
    fake_http_reset(&h);
    printf("  twilio: ok\n");
}

/* ===========================================================================
 * Telnyx binding
 * =========================================================================== */

static void test_telnyx(void) {
    fake_http_t h; memset(&h, 0, sizeof(h));
    ca_tel_http_t http = { &h, fake_http_request };

    /* configured with a connection id. */
    ca_tel_telnyx_options_t opt = { NULL, "KEY_test", "conn_123" };
    ca_tel_carrier_t *c = ca_tel_telnyx_create(http, &opt);
    assert(c && strcmp(ca_tel_carrier_id(c), "telnyx") == 0);
    assert(ca_tel_carrier_is_configured(c));

    /* Provision: GET available -> POST number_orders; cost from cost_information. */
    static const int prov_status[] = { 200, 201 };
    static const char *const prov_body[] = {
        "{\"data\":[{\"phone_number\":\"+13105550000\",\"cost_information\":{\"monthly_cost\":\"2.00\"}}]}",
        "{\"data\":{\"id\":\"order1\"}}"
    };
    h.statuses = prov_status; h.bodies = prov_body; h.script_len = 2; h.script_idx = 0;
    ca_tel_provisioned_number_t pn; memset(&pn, 0, sizeof(pn));
    assert(ca_tel_carrier_provision_number(c, "US", NULL, &pn) == 0);
    assert(strcmp(pn.phone_number, "+13105550000") == 0);
    assert(pn.monthly_recurring_cost == 2000000);
    /* last request = POST number_orders with a JSON body + Bearer auth. */
    assert(strcmp(h.method, "POST") == 0 && strcmp(h.path, "/v2/number_orders") == 0);
    assert(h.auth && strcmp(h.auth, "Bearer KEY_test") == 0);
    assert(h.content_type && strcmp(h.content_type, "application/json") == 0);
    assert(h.body && strstr(h.body, "\"phone_number\":\"+13105550000\""));
    ca_tel_provisioned_number_free(&pn);

    /* Dial: POST /v2/calls -> data.call_control_id. Pcm16000. */
    static const int dial_status[] = { 200 };
    static const char *const dial_body[] = { "{\"data\":{\"call_control_id\":\"ccid_xyz\"}}" };
    h.statuses = dial_status; h.bodies = dial_body; h.script_len = 1; h.script_idx = 0;
    ca_tel_dial_options_t *o = ca_tel_dial_options_new();
    o->detect_answering_machine = true;
    ca_tel_call_session_t *sess = ca_tel_carrier_dial(c, "+13105551111",
        "+13105552222", "wss://host/stream", o);
    assert(sess);
    const ca_tel_call_info_t *si = ca_tel_call_session_info(sess);
    assert(strcmp(si->call_id, "ccid_xyz") == 0);
    assert(si->media_format == CA_TEL_FMT_PCM16000);
    assert(strcmp(h.path, "/v2/calls") == 0);
    assert(h.body && strstr(h.body, "\"connection_id\":\"conn_123\""));
    assert(strstr(h.body, "\"stream_track\":\"both_tracks\""));
    assert(strstr(h.body, "\"timeout_secs\":30"));
    assert(strstr(h.body, "\"answering_machine_detection\":\"detect\""));

    /* Transfer via Call Control action. */
    static const int t_status[] = { 200 };
    static const char *const t_body[] = { "{}" };
    h.statuses = t_status; h.bodies = t_body; h.script_len = 1; h.script_idx = 0;
    assert(ca_tel_call_session_transfer(sess, "+13105553333", CA_TEL_TRANSFER_COLD, NULL) == 0);
    assert(ca_tel_call_session_status(sess) == CA_TEL_STATUS_TRANSFERRED);
    assert(strcmp(h.path, "/v2/calls/ccid_xyz/actions/transfer") == 0);
    assert(h.body && strstr(h.body, "\"to\":\"+13105553333\""));

    /* HangUp via hangup action. */
    static const int hang_status[] = { 200 };
    static const char *const hang_body[] = { "{}" };
    h.statuses = hang_status; h.bodies = hang_body; h.script_len = 1; h.script_idx = 0;
    /* note: after Transferred, hangup still latches EndedByAgent + issues hangup. */
    assert(ca_tel_call_session_hangup(sess) == 0);
    assert(strcmp(h.path, "/v2/calls/ccid_xyz/actions/hangup") == 0);
    ca_tel_dial_options_destroy(o);
    ca_tel_call_session_destroy(sess);

    /* Dial without a connection id -> NULL (no request). */
    ca_tel_telnyx_options_t opt_noconn = { NULL, "KEY", NULL };
    ca_tel_carrier_t *c2 = ca_tel_telnyx_create(http, &opt_noconn);
    fake_http_reset(&h); http.self = &h;
    /* rebuild carrier over the reset transport handle for a clean call count */
    ca_tel_carrier_destroy(c2);
    c2 = ca_tel_telnyx_create(http, &opt_noconn);
    assert(ca_tel_carrier_dial(c2, "+1", "+2", "wss://s", NULL) == NULL);
    assert(h.calls == 0);
    ca_tel_carrier_destroy(c2);

    ca_tel_carrier_destroy(c);
    fake_http_reset(&h);
    printf("  telnyx: ok\n");
}

/* ===========================================================================
 * Plivo binding
 * =========================================================================== */

static void test_plivo(void) {
    fake_http_t h; memset(&h, 0, sizeof(h));
    ca_tel_http_t http = { &h, fake_http_request };

    ca_tel_plivo_options_t opt = { NULL, "MAAUTHID", "authtoken", "https://host/answer" };
    ca_tel_carrier_t *c = ca_tel_plivo_create(http, &opt);
    assert(c && strcmp(ca_tel_carrier_id(c), "plivo") == 0);
    assert(ca_tel_carrier_is_configured(c));

    /* Provision: GET PhoneNumber -> POST buy; cost from monthly_rental_rate. */
    static const int prov_status[] = { 200, 201 };
    static const char *const prov_body[] = {
        "{\"objects\":[{\"number\":\"27110001111\",\"monthly_rental_rate\":\"0.50\"}]}",
        "{\"status\":\"fulfilled\"}"
    };
    h.statuses = prov_status; h.bodies = prov_body; h.script_len = 2; h.script_idx = 0;
    ca_tel_provisioned_number_t pn; memset(&pn, 0, sizeof(pn));
    assert(ca_tel_carrier_provision_number(c, "ZA", NULL, &pn) == 0);
    assert(strcmp(pn.phone_number, "27110001111") == 0);
    assert(pn.monthly_recurring_cost == 500000);   /* 0.50 */
    /* last = POST buy PhoneNumber/{number}/ with Basic auth + app_id="". */
    assert(strcmp(h.method, "POST") == 0);
    assert(strstr(h.path, "/PhoneNumber/27110001111/"));
    assert(h.auth && strncmp(h.auth, "Basic ", 6) == 0);
    assert(h.body && strstr(h.body, "app_id="));
    ca_tel_provisioned_number_free(&pn);

    /* Dial: composes answer_url with ?stream=, POSTs Call/, reads request_uuid. */
    static const int dial_status[] = { 201 };
    static const char *const dial_body[] = { "{\"request_uuid\":\"req-abc-123\"}" };
    h.statuses = dial_status; h.bodies = dial_body; h.script_len = 1; h.script_idx = 0;
    ca_tel_call_session_t *sess = ca_tel_carrier_dial(c, "27110002222",
        "27110003333", "wss://host/stream", NULL);
    assert(sess);
    const ca_tel_call_info_t *si = ca_tel_call_session_info(sess);
    assert(strcmp(si->call_id, "req-abc-123") == 0);
    assert(si->media_format == CA_TEL_FMT_MULAW8000);
    assert(strstr(h.path, "/Call/"));
    /* answer_url is form-encoded and contains a percent-encoded stream= param
     * (the wss URL is Uri.EscapeDataString'd, then the whole answer_url is
     * form-encoded, so "stream" appears as literal text and ':' as %3A). */
    assert(h.body && strstr(h.body, "answer_url="));
    assert(strstr(h.body, "from=27110002222"));
    assert(strstr(h.body, "ring_timeout=30"));

    /* Transfer: aleg_url data:xml Dial. */
    static const int t_status[] = { 200 };
    static const char *const t_body[] = { "{}" };
    h.statuses = t_status; h.bodies = t_body; h.script_len = 1; h.script_idx = 0;
    assert(ca_tel_call_session_transfer(sess, "27110004444", CA_TEL_TRANSFER_COLD, NULL) == 0);
    assert(ca_tel_call_session_status(sess) == CA_TEL_STATUS_TRANSFERRED);
    assert(strstr(h.path, "/Call/req-abc-123/"));
    assert(h.body && strstr(h.body, "aleg_url="));
    assert(strstr(h.body, "aleg_method=POST"));

    /* HangUp: DELETE Call/{uuid}/. */
    static const int hang_status[] = { 204 };
    static const char *const hang_body[] = { NULL };
    h.statuses = hang_status; h.bodies = hang_body; h.script_len = 1; h.script_idx = 0;
    assert(ca_tel_call_session_hangup(sess) == 0);
    assert(strcmp(h.method, "DELETE") == 0);
    assert(strstr(h.path, "/Call/req-abc-123/"));
    ca_tel_call_session_destroy(sess);

    /* Dial without AnswerUrlBase -> NULL. */
    ca_tel_plivo_options_t opt_nobase = { NULL, "MAID", "tok", NULL };
    ca_tel_carrier_t *c2 = ca_tel_plivo_create(http, &opt_nobase);
    fake_http_reset(&h); http.self = &h;
    ca_tel_carrier_destroy(c2);
    c2 = ca_tel_plivo_create(http, &opt_nobase);
    assert(ca_tel_carrier_dial(c2, "+1", "+2", "wss://s", NULL) == NULL);
    assert(h.calls == 0);
    ca_tel_carrier_destroy(c2);

    ca_tel_carrier_destroy(c);
    fake_http_reset(&h);
    printf("  plivo: ok\n");
}

/* ===========================================================================
 * CarrierFallback — picks the first configured carrier.
 * =========================================================================== */

static void test_carrier_fallback(void) {
    fake_http_t h; memset(&h, 0, sizeof(h));
    ca_tel_http_t http = { &h, fake_http_request };

    /* Twilio unconfigured, Telnyx configured -> fallback picks Telnyx. */
    ca_tel_twilio_options_t tw_empty = { NULL, NULL, NULL };
    ca_tel_telnyx_options_t tx_opt = { NULL, "KEY", "conn" };
    ca_tel_carrier_t *tw = ca_tel_twilio_create(http, &tw_empty);
    ca_tel_carrier_t *tx = ca_tel_telnyx_create(http, &tx_opt);
    ca_tel_carrier_t *carr[2] = { tw, tx };
    ca_tel_carrier_t *fb = ca_tel_carrier_fallback_create(carr, 2);
    assert(fb);
    assert(strcmp(ca_tel_carrier_id(fb), "fallback(2)") == 0);
    assert(ca_tel_carrier_is_configured(fb));   /* Telnyx is configured */

    /* A dial should route to Telnyx (its /v2/calls path). */
    static const int dial_status[] = { 200 };
    static const char *const dial_body[] = { "{\"data\":{\"call_control_id\":\"cc1\"}}" };
    h.statuses = dial_status; h.bodies = dial_body; h.script_len = 1; h.script_idx = 0;
    ca_tel_call_session_t *sess = ca_tel_carrier_dial(fb, "+1", "+2", "wss://s", NULL);
    assert(sess);
    assert(strcmp(ca_tel_call_session_info(sess)->carrier_id, "telnyx") == 0);
    assert(strcmp(h.path, "/v2/calls") == 0);
    ca_tel_call_session_destroy(sess);

    ca_tel_carrier_destroy(fb);   /* destroys tw + tx too */
    fake_http_reset(&h);

    /* All unconfigured -> fallback uses the Null carrier (dial NULL). */
    fake_http_t h2; memset(&h2, 0, sizeof(h2));
    ca_tel_http_t http2 = { &h2, fake_http_request };
    ca_tel_carrier_t *tw2 = ca_tel_twilio_create(http2, &tw_empty);
    ca_tel_carrier_t *carr2[1] = { tw2 };
    ca_tel_carrier_t *fb2 = ca_tel_carrier_fallback_create(carr2, 1);
    assert(fb2 && !ca_tel_carrier_is_configured(fb2));
    assert(ca_tel_carrier_dial(fb2, "+1", "+2", "wss://s", NULL) == NULL);
    ca_tel_carrier_destroy(fb2);
    fake_http_reset(&h2);
    printf("  carrier_fallback: ok\n");
}

/* ===========================================================================
 * main
 * =========================================================================== */

int main(void) {
    printf("test_telephony:\n");
    test_primitives();
    test_dtmf();
    test_tool_registry();
    test_media_stream();
    test_test_call_session();
    test_media_call_session();
    test_dispatcher();
    test_null_carrier();
    test_twilio();
    test_telnyx();
    test_plivo();
    test_carrier_fallback();
    printf("test_telephony: ALL PASS\n");
    return 0;
}
