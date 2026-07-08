/*
 * test_herjarvis.c — HER/Jarvis companion contracts (C11).
 *
 * Covers the 16 remaining contracts + the voice listener bridge, ported from
 * the C# reference (HerJarvisRealImplementations.cs). Deterministic assertions:
 * where the C# reads a wall clock or randomised Guid, the C port takes the
 * instant / uses a reproducible id, so behaviour is fully reproducible here.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

static bool approx(double a, double b) { return fabs(a - b) < 1e-9; }

/* Monday 2021-06-14T13:00:00Z, Unix ms. */
#define MON_1300Z 1623675600000LL
#define DAY_MS    86400000LL

/* ========================================================================= */
static void test_always_on_presence(void) {
    ca_always_on_presence_t *p = ca_always_on_presence_create();
    assert(p);
    assert(!ca_always_on_presence_is_running(p));
    assert(ca_always_on_presence_heartbeats(p) == 0);

    ca_always_on_presence_start(p);
    assert(ca_always_on_presence_is_running(p));
    assert(ca_always_on_presence_heartbeats(p) == 1);   /* immediate heartbeat */

    ca_always_on_presence_start(p);                     /* idempotent */
    assert(ca_always_on_presence_heartbeats(p) == 1);

    assert(ca_always_on_presence_tick(p) == 2);
    assert(ca_always_on_presence_tick(p) == 3);

    ca_always_on_presence_stop(p);
    assert(!ca_always_on_presence_is_running(p));
    assert(ca_always_on_presence_tick(p) == 3);         /* no tick while stopped */

    ca_always_on_presence_start(p);                     /* monotonic across restart */
    assert(ca_always_on_presence_heartbeats(p) == 4);   /* +1 immediate */

    ca_always_on_presence_destroy(p);
    printf("  always_on_presence: ok\n");
}

/* ========================================================================= */
static void test_fused_perception(void) {
    ca_fused_perception_t *fp = ca_fused_perception_create();
    assert(fp);

    ca_fused_percept_t out;
    assert(!ca_fused_perception_read(fp, &out));   /* empty */

    ca_fused_percept_t p; memset(&p, 0, sizeof(p));
    p.at_ms = 100; p.vision = "cat"; p.text = "hello";
    char *keys[] = { "temp" }; double vals[] = { 21.5 };
    p.sensor_keys = keys; p.sensor_values = vals; p.sensor_count = 1;
    ca_fused_perception_publish(fp, &p);
    ca_fused_perception_publish(fp, &p);
    ca_fused_perception_publish(fp, NULL);          /* null → no-op */

    assert(ca_fused_perception_read(fp, &out));
    assert(out.at_ms == 100);
    assert(strcmp(out.vision, "cat") == 0);
    assert(out.audio == NULL);
    assert(strcmp(out.text, "hello") == 0);
    assert(out.sensor_count == 1);
    assert(strcmp(out.sensor_keys[0], "temp") == 0);
    assert(approx(out.sensor_values[0], 21.5));
    ca_fused_percept_free(&out);

    assert(ca_fused_perception_read(fp, &out));      /* second copy */
    ca_fused_percept_free(&out);
    assert(!ca_fused_perception_read(fp, &out));      /* drained */

    /* publish-after-complete is dropped */
    ca_fused_perception_complete(fp);
    ca_fused_perception_publish(fp, &p);
    assert(!ca_fused_perception_read(fp, &out));

    ca_fused_perception_destroy(fp);
    printf("  fused_perception: ok\n");
}

/* ========================================================================= */
static void test_continuous_learner(void) {
    assert(ca_continuous_learner_create(0.0) == NULL);
    assert(ca_continuous_learner_create(1.5) == NULL);

    ca_continuous_learner_t *l = ca_continuous_learner_create(0.2);
    assert(l);

    double avg;
    assert(!ca_continuous_learner_average(l, "x", &avg));
    assert(ca_continuous_learner_observations(l, "x") == 0);

    ca_continuous_learner_register(l, "x", 1.0, "{}");
    assert(ca_continuous_learner_average(l, "x", &avg) && approx(avg, 1.0));
    assert(ca_continuous_learner_observations(l, "x") == 1);

    /* EWA: 1.0*(0.8) + 0.0*0.2 = 0.8 */
    ca_continuous_learner_register(l, "x", 0.0, "{}");
    assert(ca_continuous_learner_average(l, "x", &avg) && approx(avg, 0.8));
    assert(ca_continuous_learner_observations(l, "x") == 2);

    /* 0.8*0.8 + 1.0*0.2 = 0.84 */
    ca_continuous_learner_register(l, "x", 1.0, "{}");
    assert(ca_continuous_learner_average(l, "x", &avg) && approx(avg, 0.84));

    ca_continuous_learner_register(l, "  ", 1.0, "{}");   /* blank id ignored */
    assert(!ca_continuous_learner_average(l, "  ", &avg));

    ca_continuous_learner_destroy(l);
    printf("  continuous_learner: ok\n");
}

/* ========================================================================= */
static void test_goal_pursuer(void) {
    ca_goal_pursuer_t *gp = ca_goal_pursuer_create();
    assert(gp);

    ca_long_horizon_goal_t g;
    /* deadline <= now rejected */
    assert(!ca_goal_pursuer_register(gp, "ship", MON_1300Z, MON_1300Z, &g));
    /* blank description rejected */
    assert(!ca_goal_pursuer_register(gp, "  ", MON_1300Z + DAY_MS, MON_1300Z, &g));

    /* 70-day horizon → milestones = clamp(70/14=5, 2, 8) = 5 */
    int64_t deadline = MON_1300Z + 70 * DAY_MS;
    assert(ca_goal_pursuer_register(gp, "learn spanish", deadline, MON_1300Z, &g));
    assert(strcmp(g.description, "learn spanish") == 0);
    assert(g.deadline_ms == deadline);
    assert(approx(g.progress_fraction, 0.0));
    /* plan contains 5 milestones */
    int count = 0; const char *pp = g.plan_json;
    while ((pp = strstr(pp, "\"index\":")) != NULL) { count++; pp += 8; }
    assert(count == 5);
    assert(strstr(g.plan_json, "\"description\":\"learn spanish\"") != NULL);
    /* exact ISO-8601 "O" milestone dates (14-day spacing from 2021-06-14T13:00Z) */
    assert(strstr(g.plan_json, "\"due\":\"2021-06-28T13:00:00.0000000+00:00\"") != NULL);
    assert(strstr(g.plan_json, "\"due\":\"2021-08-23T13:00:00.0000000+00:00\"") != NULL);
    char id[64]; strncpy(id, g.id, sizeof(id) - 1); id[sizeof(id) - 1] = '\0';
    ca_long_horizon_goal_free(&g);

    /* current fetch */
    assert(ca_goal_pursuer_current(gp, id, &g));
    assert(strcmp(g.id, id) == 0);
    ca_long_horizon_goal_free(&g);

    /* progress bounds */
    assert(!ca_goal_pursuer_progress(gp, id, -0.1));
    assert(!ca_goal_pursuer_progress(gp, id, 1.1));
    assert(ca_goal_pursuer_progress(gp, id, 0.5));
    assert(ca_goal_pursuer_current(gp, id, &g) && approx(g.progress_fraction, 0.5));
    ca_long_horizon_goal_free(&g);

    /* replan rebuilds plan from a new "now" (2 weeks later) */
    assert(ca_goal_pursuer_replan(gp, id, MON_1300Z + 14 * DAY_MS));
    assert(ca_goal_pursuer_current(gp, id, &g));
    /* still 5 milestones (56 days left / 14 = 4 → clamp min 2 → actually 4) */
    count = 0; pp = g.plan_json;
    while ((pp = strstr(pp, "\"index\":")) != NULL) { count++; pp += 8; }
    assert(count >= 2 && count <= 8);
    ca_long_horizon_goal_free(&g);

    /* unknown id */
    assert(!ca_goal_pursuer_current(gp, "nope", &g));
    assert(!ca_goal_pursuer_replan(gp, "nope", MON_1300Z));
    assert(!ca_goal_pursuer_progress(gp, "nope", 0.5));

    ca_goal_pursuer_destroy(gp);
    printf("  goal_pursuer: ok\n");
}

/* ========================================================================= */
/* Build a synthetic PCM16 tone buffer. */
static uint8_t *make_tone(double freq_hz, int sample_rate, int n_samples, size_t *out_bytes) {
    uint8_t *buf = (uint8_t *)malloc((size_t)n_samples * 2);
    for (int i = 0; i < n_samples; ++i) {
        double t = (double)i / sample_rate;
        short s = (short)(20000.0 * sin(2.0 * 3.14159265358979 * freq_hz * t));
        buf[i * 2] = (uint8_t)(s & 0xFF);
        buf[i * 2 + 1] = (uint8_t)((s >> 8) & 0xFF);
    }
    *out_bytes = (size_t)n_samples * 2;
    return buf;
}

static void test_voice_identity(void) {
    ca_voice_identity_t *v = ca_voice_identity_create();
    assert(v);

    size_t nb_a, nb_b, nb_bytes;
    uint8_t *a = make_tone(220.0, 16000, 8000, &nb_a);   /* speaker A */
    uint8_t *b = make_tone(440.0, 16000, 8000, &nb_b);   /* speaker B */

    ca_voice_identity_enroll(v, "alice", a, nb_a, 16000);
    ca_voice_identity_enroll(v, "bob", b, nb_b, 16000);
    ca_voice_identity_enroll(v, "  ", a, nb_a, 16000);   /* blank id no-op */

    /* re-present alice's audio → identifies alice (self-similarity == 1.0 > 0.85) */
    char *who = ca_voice_identity_identify(v, a, nb_a, 16000);
    assert(who && strcmp(who, "alice") == 0);
    free(who);

    who = ca_voice_identity_identify(v, b, nb_b, 16000);
    assert(who && strcmp(who, "bob") == 0);
    free(who);

    /* mfcc determinism + coefficient count */
    double c1[13], c2[13];
    assert(ca_voice_identity_mfcc(a, nb_a, 16000, c1) == 13);
    assert(ca_voice_identity_mfcc(a, nb_a, 16000, c2) == 13);
    for (int i = 0; i < 13; ++i) assert(approx(c1[i], c2[i]));

    /* too-short buffer → zero fingerprint (all coeffs 0) */
    uint8_t *tiny = make_tone(220.0, 16000, 100, &nb_bytes);   /* < 400 frame size */
    double ct[13];
    ca_voice_identity_mfcc(tiny, nb_bytes, 16000, ct);
    for (int i = 0; i < 13; ++i) assert(ct[i] == 0.0);
    free(tiny);

    free(a); free(b);
    ca_voice_identity_destroy(v);
    printf("  voice_identity: ok\n");
}

/* ========================================================================= */
static void test_calibrated_confidence(void) {
    ca_calibrated_confidence_t *c = ca_calibrated_confidence_create();
    assert(c);

    /* With < 5 samples calibrated = raw. rawScore for a plain answer:
     * len(trim)=... hedges=0, no context.
     * "The answer is 42" trimmed length 16 → log(16)/10 = 0.27725887...
     * calibrated = raw; half = max(0.05, 0.25 - raw*0.2). */
    ca_confidence_band_t band;
    assert(ca_calibrated_confidence_evaluate(c, "The answer is 42", NULL, &band));
    double raw = log(16.0) / 10.0;
    double half = 0.25 - raw * 0.2; if (half < 0.05) half = 0.05;
    double lo = raw - half; if (lo < 0) lo = 0;
    double hi = raw + half; if (hi > 1) hi = 1;
    assert(approx(band.lower, lo));
    assert(approx(band.upper, hi));

    /* hedges lower the raw score. "maybe perhaps" → 2 hedges → penalty 0.2 */
    assert(ca_calibrated_confidence_evaluate(c, "maybe perhaps", NULL, &band));
    /* len(trim)=13 → log(13)/10=0.2564.. - 0.2 = 0.0564.. */
    double raw2 = log(13.0) / 10.0 - 0.2; if (raw2 < 0) raw2 = 0;
    double half2 = 0.25 - raw2 * 0.2; if (half2 < 0.05) half2 = 0.05;
    assert(approx(band.lower, raw2 - half2 < 0 ? 0 : raw2 - half2));

    /* context bumps raw by 0.1 */
    assert(ca_calibrated_confidence_evaluate(c, "ok", "{\"k\":\"v\"}", &band));

    /* >= 5 samples → k-NN calibration. Record 5 outcomes at raw~0.28, 4 correct. */
    for (int i = 0; i < 4; ++i) ca_calibrated_confidence_record(c, 0.28, true);
    ca_calibrated_confidence_record(c, 0.28, false);
    assert(ca_calibrated_confidence_evaluate(c, "The answer is 42", NULL, &band));
    /* 5 nearest all at 0.28 → 4/5 correct = 0.8 calibrated */
    double cal = 0.8;
    double h = 0.25 - cal * 0.2; if (h < 0.05) h = 0.05;
    assert(approx(band.lower, cal - h));
    assert(approx(band.upper, cal + h > 1 ? 1 : cal + h));

    /* guards */
    assert(!ca_calibrated_confidence_evaluate(c, NULL, NULL, &band));
    assert(!ca_calibrated_confidence_evaluate(c, "x", NULL, NULL));

    ca_calibrated_confidence_destroy(c);
    printf("  calibrated_confidence: ok\n");
}

/* ========================================================================= */
static void test_emotion_sensor(void) {
    ca_emotion_frame_t f;

    assert(ca_emotion_sensor_sense("{\"text\":\"I am so happy and excited\"}", &f));
    assert(strcmp(f.label, "joy") == 0);
    /* two joy hits (happy, excited) → arousal 0.8, valence 0.9 */
    assert(approx(f.arousal, 0.8) && approx(f.valence, 0.9));
    ca_emotion_frame_free(&f);

    assert(ca_emotion_sensor_sense("nothing here", &f));
    assert(strcmp(f.label, "neutral") == 0 && f.arousal == 0.0 && f.valence == 0.0);
    ca_emotion_frame_free(&f);

    /* mixed: 1 joy (arousal .8/val .9), 1 anger (.9/-.8) → weighted avg */
    assert(ca_emotion_sensor_sense("happy but angry", &f));
    assert(approx(f.arousal, (0.8 + 0.9) / 2.0));
    assert(approx(f.valence, (0.9 - 0.8) / 2.0));
    ca_emotion_frame_free(&f);

    /* word-boundary: "download" must NOT match "down" (sad) */
    assert(ca_emotion_sensor_sense("download complete", &f));
    assert(strcmp(f.label, "neutral") == 0);
    ca_emotion_frame_free(&f);

    assert(!ca_emotion_sensor_sense(NULL, &f));
    assert(!ca_emotion_sensor_sense("x", NULL));
    printf("  emotion_sensor: ok\n");
}

/* ========================================================================= */
static void test_skill_acquisition(void) {
    ca_skill_acquisition_t *sa = ca_skill_acquisition_create();
    assert(sa);

    size_t n = 0;
    ca_acquired_skill_t *list = ca_skill_acquisition_list(sa, &n);
    assert(n == 0 && list == NULL);

    ca_acquired_skill_t s;
    assert(ca_skill_acquisition_acquire(sa, "{\"name\":\"zebra\"}", &s));
    assert(strcmp(s.name, "zebra") == 0);
    assert(strcmp(s.description_json, "{\"name\":\"zebra\"}") == 0);
    assert(strlen(s.id) == 32);
    ca_acquired_skill_free(&s);

    /* no name field → "skill-"+id[..6] */
    assert(ca_skill_acquisition_acquire(sa, "{\"kind\":\"x\"}", &s));
    assert(strncmp(s.name, "skill-", 6) == 0 && strlen(s.name) == 12);
    ca_acquired_skill_free(&s);

    assert(ca_skill_acquisition_acquire(sa, "{\"name\":\"apple\"}", &s));
    ca_acquired_skill_free(&s);

    /* list sorted by name: apple, skill-XXXXXX, zebra */
    list = ca_skill_acquisition_list(sa, &n);
    assert(n == 3);
    assert(strcmp(list[0].name, "apple") == 0);
    assert(strcmp(list[2].name, "zebra") == 0);
    ca_acquired_skill_free_array(list, n);

    assert(!ca_skill_acquisition_acquire(sa, NULL, &s));
    assert(!ca_skill_acquisition_acquire(sa, "{}", NULL));

    ca_skill_acquisition_destroy(sa);
    printf("  skill_acquisition: ok\n");
}

/* ========================================================================= */
static void test_bio_signal_stream(void) {
    ca_bio_signal_stream_t *bs = ca_bio_signal_stream_create();
    assert(bs);

    ca_bio_signal_t out;
    assert(!ca_bio_signal_stream_read(bs, &out));

    ca_bio_signal_t s = { .kind = "hr", .value = 72.0, .at_ms = 500 };
    ca_bio_signal_stream_publish(bs, &s);
    assert(ca_bio_signal_stream_read(bs, &out));
    assert(strcmp(out.kind, "hr") == 0 && approx(out.value, 72.0) && out.at_ms == 500);
    ca_bio_signal_free(&out);

    ca_bio_signal_stream_complete(bs);
    ca_bio_signal_stream_publish(bs, &s);   /* dropped */
    assert(!ca_bio_signal_stream_read(bs, &out));

    ca_bio_signal_stream_destroy(bs);
    printf("  bio_signal_stream: ok\n");
}

/* ========================================================================= */
static void ok_handler(void *user, const ca_physical_command_t *cmd,
                       ca_physical_command_result_t *out) {
    (void)user;
    out->succeeded = true;
    out->error = NULL;
    /* echo the action into error field only on a specific action to prove args flow */
    if (cmd->action && strcmp(cmd->action, "fail") == 0) {
        out->succeeded = false;
        out->error = strdup("handler said no");
    }
}

static void test_physical_actuator(void) {
    ca_physical_actuator_t *a = ca_physical_actuator_create();
    assert(a);

    ca_physical_actuator_register(a, "lamp", ok_handler, NULL);
    ca_physical_actuator_register(a, "  ", ok_handler, NULL);   /* blank no-op */

    ca_physical_command_t cmd = { .device_id = "lamp", .action = "on" };
    ca_physical_command_result_t res;
    assert(ca_physical_actuator_invoke(a, &cmd, &res));
    assert(res.succeeded && res.error == NULL);
    ca_physical_command_result_free(&res);

    ca_physical_command_t bad = { .device_id = "lamp", .action = "fail" };
    assert(ca_physical_actuator_invoke(a, &bad, &res));
    assert(!res.succeeded && strcmp(res.error, "handler said no") == 0);
    ca_physical_command_result_free(&res);

    /* unknown device */
    ca_physical_command_t unk = { .device_id = "toaster", .action = "on" };
    assert(ca_physical_actuator_invoke(a, &unk, &res));
    assert(!res.succeeded && strcmp(res.error, "Unknown device 'toaster'") == 0);
    ca_physical_command_result_free(&res);

    ca_physical_actuator_destroy(a);
    printf("  physical_actuator: ok\n");
}

/* ========================================================================= */
static void test_agent_peer_network(void) {
    ca_agent_peer_network_t *n = ca_agent_peer_network_create();
    assert(n);

    ca_agent_peer_message_t out;
    assert(!ca_agent_peer_network_receive(n, "b", &out));   /* empty */

    ca_agent_peer_message_t m1 = { .from_agent_id = "a", .to_agent_id = "b", .payload = "hi", .at_ms = 1 };
    ca_agent_peer_message_t m2 = { .from_agent_id = "a", .to_agent_id = "b", .payload = "again", .at_ms = 2 };
    ca_agent_peer_network_send(n, &m1);
    ca_agent_peer_network_send(n, &m2);

    /* FIFO order */
    assert(ca_agent_peer_network_receive(n, "b", &out));
    assert(strcmp(out.payload, "hi") == 0 && strcmp(out.from_agent_id, "a") == 0);
    ca_agent_peer_message_free(&out);
    assert(ca_agent_peer_network_receive(n, "b", &out));
    assert(strcmp(out.payload, "again") == 0);
    ca_agent_peer_message_free(&out);
    assert(!ca_agent_peer_network_receive(n, "b", &out));

    /* other recipient has nothing */
    assert(!ca_agent_peer_network_receive(n, "c", &out));
    assert(!ca_agent_peer_network_receive(n, "  ", &out));   /* blank */

    ca_agent_peer_network_destroy(n);
    printf("  agent_peer_network: ok\n");
}

/* ========================================================================= */
static void test_federated_finetuner(void) {
    /* default trainer: unknown-path file → 100 steps → finishes at 1.0 */
    ca_federated_finetuner_t *ft = ca_federated_finetuner_create(NULL, NULL);
    assert(ft);

    assert(ca_federated_finetuner_start(ft, "", "path") == NULL);      /* blank base */
    assert(ca_federated_finetuner_start(ft, "base", "  ") == NULL);     /* blank path */

    char *job = ca_federated_finetuner_start(ft, "llama", "/no/such/file");
    assert(job);
    ca_finetune_status_t st;
    assert(ca_federated_finetuner_status(ft, job, &st));
    assert(strcmp(st.job_id, job) == 0);
    assert(approx(st.progress, 1.0));
    assert(st.error == NULL);
    ca_finetune_status_free(&st);
    free(job);

    /* unknown job */
    assert(ca_federated_finetuner_status(ft, "nope", &st));
    assert(approx(st.progress, 0.0) && strcmp(st.error, "unknown job") == 0);
    ca_finetune_status_free(&st);

    ca_federated_finetuner_destroy(ft);
    printf("  federated_finetuner: ok\n");
}

/* ========================================================================= */
static void test_first_token_optimizer(void) {
    assert(ca_first_token_optimizer_create(0, 10) == NULL);
    assert(ca_first_token_optimizer_create(100, 0) == NULL);

    ca_first_token_optimizer_t *o = ca_first_token_optimizer_create(100, 4);
    assert(o);

    ca_first_token_budget_t b;
    assert(ca_first_token_optimizer_current(o, &b));
    assert(b.target_ms == 100 && b.current_p50_ms == 0);   /* empty */

    ca_first_token_optimizer_record(o, 50);
    ca_first_token_optimizer_record(o, 100);
    ca_first_token_optimizer_record(o, 150);
    /* sorted [50,100,150], p50 = index 1 = 100 */
    assert(ca_first_token_optimizer_current(o, &b) && b.current_p50_ms == 100);

    ca_first_token_optimizer_record(o, -5);   /* negative ignored */
    assert(ca_first_token_optimizer_current(o, &b) && b.current_p50_ms == 100);

    /* window of 4: adding 4 more evicts old. Fill with 200s. */
    ca_first_token_optimizer_record(o, 200);   /* [50,100,150,200] */
    ca_first_token_optimizer_record(o, 200);   /* evict 50 → [100,150,200,200] */
    ca_first_token_optimizer_record(o, 200);   /* evict 100 → [150,200,200,200] */
    /* sorted [150,200,200,200], p50 index 2 = 200 */
    assert(ca_first_token_optimizer_current(o, &b) && b.current_p50_ms == 200);

    ca_first_token_optimizer_destroy(o);
    printf("  first_token_optimizer: ok\n");
}

/* ========================================================================= */
static void test_crypto_delegation(void) {
    ca_crypto_delegation_t *d = ca_crypto_delegation_create(NULL, NULL, 0);
    assert(d);

    ca_delegation_credential_t cred;
    /* blank subject/scope, non-positive lifetime rejected */
    assert(!ca_crypto_delegation_issue(d, "  ", "read", 1000, MON_1300Z, &cred));
    assert(!ca_crypto_delegation_issue(d, "sub", "  ", 1000, MON_1300Z, &cred));
    assert(!ca_crypto_delegation_issue(d, "sub", "read", 0, MON_1300Z, &cred));

    assert(ca_crypto_delegation_issue(d, "sub", "read", 3600000, MON_1300Z, &cred));
    assert(strcmp(cred.issuer, "circleai-companion") == 0);
    assert(strcmp(cred.subject_id, "sub") == 0);
    assert(strcmp(cred.scope, "read") == 0);
    assert(cred.expires_at_ms == MON_1300Z + 3600000);
    assert(cred.signature_b64 && strlen(cred.signature_b64) > 0);

    /* verify before expiry */
    assert(ca_crypto_delegation_verify(d, &cred, MON_1300Z));
    /* after expiry */
    assert(!ca_crypto_delegation_verify(d, &cred, cred.expires_at_ms + 1));

    /* tamper the scope → verify fails */
    char *saved = cred.scope;
    cred.scope = strdup("write");
    assert(!ca_crypto_delegation_verify(d, &cred, MON_1300Z));
    free(cred.scope); cred.scope = saved;

    /* wrong issuer → fails */
    char *si = cred.issuer;
    cred.issuer = strdup("someone-else");
    assert(!ca_crypto_delegation_verify(d, &cred, MON_1300Z));
    free(cred.issuer); cred.issuer = si;

    ca_delegation_credential_free(&cred);

    /* a delegation with a different key must NOT verify a credential from d */
    ca_delegation_credential_t c2;
    assert(ca_crypto_delegation_issue(d, "s", "read", 1000000, MON_1300Z, &c2));
    uint8_t otherkey[16] = { 9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9 };
    ca_crypto_delegation_t *d2 = ca_crypto_delegation_create(NULL, otherkey, sizeof(otherkey));
    assert(!ca_crypto_delegation_verify(d2, &c2, MON_1300Z));
    assert(ca_crypto_delegation_verify(d, &c2, MON_1300Z));   /* original still verifies */
    ca_delegation_credential_free(&c2);
    ca_crypto_delegation_destroy(d2);

    /* custom issuer */
    ca_crypto_delegation_t *d3 = ca_crypto_delegation_create("my-issuer", NULL, 0);
    ca_delegation_credential_t c3;
    assert(ca_crypto_delegation_issue(d3, "s", "x", 1000000, MON_1300Z, &c3));
    assert(strcmp(c3.issuer, "my-issuer") == 0);
    assert(ca_crypto_delegation_verify(d3, &c3, MON_1300Z));
    ca_delegation_credential_free(&c3);
    ca_crypto_delegation_destroy(d3);

    ca_crypto_delegation_destroy(d);
    printf("  crypto_delegation: ok\n");
}

/* ========================================================================= */
static void test_code_generation_loop(void) {
    /* balance check */
    assert(ca_code_is_syntactically_balanced("a{b(c)[d]}") == true);
    assert(ca_code_is_syntactically_balanced("a{b(c}") == false);
    assert(ca_code_is_syntactically_balanced(")(") == false);
    assert(ca_code_is_syntactically_balanced("") == false);

    ca_code_generation_loop_t *l = ca_code_generation_loop_create(NULL, NULL, NULL, NULL, NULL, NULL);
    assert(l);

    ca_codegen_job_t j;
    assert(ca_code_generation_loop_run(l, "add two numbers", &j));
    assert(strcmp(j.prompt, "add two numbers") == 0);
    /* default snippet: "// (3.3.0) generated from: add two numbers\nreturn 0;" — balanced */
    assert(strstr(j.output_snippet, "generated from: add two numbers") != NULL);
    assert(j.tests_pass == true);
    /* no "public class" → "run inline" */
    assert(j.deploy_hint && strcmp(j.deploy_hint, "run inline") == 0);
    assert(strlen(j.id) == 32);
    ca_codegen_job_free(&j);

    assert(!ca_code_generation_loop_run(l, "  ", &j));
    assert(!ca_code_generation_loop_run(l, "x", NULL));

    ca_code_generation_loop_destroy(l);
    printf("  code_generation_loop: ok\n");
}

/* ========================================================================= */
/* deterministic bench + propose seams for the tracking loop */
static double bench_fixed_high(void *user, const char *id) { (void)user; (void)id; return 0.9; }
static double bench_fixed_low(void *user, const char *id) { (void)user; (void)id; return 0.3; }
static char *propose_marker(void *user, const char *id, double cur) {
    (void)user; (void)id; (void)cur; return strdup("tuned");
}

static void test_self_improvement_loop(void) {
    /* rising bench: 0.9 first cycle → "new best"; second cycle 0.9 == best → "no regression" */
    ca_self_improvement_loop_t *l = ca_self_improvement_loop_create(bench_fixed_high, NULL, NULL, NULL);
    assert(l);
    ca_self_improvement_verdict_t v;
    assert(ca_self_improvement_loop_cycle(l, "suite", &v));
    assert(strcmp(v.improvements_applied, "new best") == 0 && approx(v.new_bench_score, 0.9));
    ca_self_improvement_verdict_free(&v);
    assert(ca_self_improvement_loop_cycle(l, "suite", &v));
    assert(strcmp(v.improvements_applied, "no regression") == 0);
    ca_self_improvement_verdict_free(&v);
    assert(approx(ca_self_improvement_loop_best_score(l, "suite"), 0.9));
    ca_self_improvement_loop_destroy(l);

    /* falling bench: seed a high best via one high cycle, then a low bench proposes */
    ca_self_improvement_loop_t *l2 = ca_self_improvement_loop_create(bench_fixed_low, NULL, propose_marker, NULL);
    assert(l2);
    /* first cycle: baseline 0 → 0.3 >= 0 → "new best" */
    assert(ca_self_improvement_loop_cycle(l2, "s", &v));
    assert(strcmp(v.improvements_applied, "new best") == 0);
    ca_self_improvement_verdict_free(&v);
    /* manually can't lower bench; instead use a loop whose best is pre-seeded higher:
     * cycle again — 0.3 == best → "no regression" (still not a regression path). */
    assert(ca_self_improvement_loop_cycle(l2, "s", &v));
    assert(strcmp(v.improvements_applied, "no regression") == 0);
    ca_self_improvement_verdict_free(&v);
    ca_self_improvement_loop_destroy(l2);

    /* Regression path: default bench is deterministic per id; use two ids where the
     * second cycle sees a lower score than a pre-recorded best is hard without a
     * mutable seam. Use custom seams: high then low via a stateful counter. */
    assert(!ca_self_improvement_loop_cycle(NULL, "s", &v));
    printf("  self_improvement_loop: ok\n");
}

/* stateful bench: returns high once then low, to exercise the regression branch */
typedef struct { int calls; } bench_state;
static double bench_high_then_low(void *user, const char *id) {
    (void)id;
    bench_state *st = (bench_state *)user;
    return (st->calls++ == 0) ? 0.9 : 0.2;
}

static void test_self_improvement_regression(void) {
    bench_state st = { 0 };
    ca_self_improvement_loop_t *l =
        ca_self_improvement_loop_create(bench_high_then_low, &st, propose_marker, NULL);
    assert(l);
    ca_self_improvement_verdict_t v;
    assert(ca_self_improvement_loop_cycle(l, "s", &v));   /* 0.9 → new best */
    assert(strcmp(v.improvements_applied, "new best") == 0);
    ca_self_improvement_verdict_free(&v);
    assert(ca_self_improvement_loop_cycle(l, "s", &v));   /* 0.2 < 0.9 → propose */
    assert(strcmp(v.improvements_applied, "tuned") == 0 && approx(v.new_bench_score, 0.2));
    ca_self_improvement_verdict_free(&v);
    /* best unchanged */
    assert(approx(ca_self_improvement_loop_best_score(l, "s"), 0.9));
    ca_self_improvement_loop_destroy(l);
    printf("  self_improvement_regression: ok\n");
}

/* ========================================================================= */
/* SelfBench A/B loop seams */
static size_t suite_count_2(void *user, const char *id) { (void)user; (void)id; return 2; }
static size_t suite_count_0(void *user, const char *id) { (void)user; (void)id; return 0; }
static int promote_calls = 0;
static void on_promote(void *user, const ca_ab_verdict_t *v) { (void)user; (void)v; promote_calls++; }
static bool ab_promote(void *user, const char *id, size_t tc, ca_ab_verdict_t *out) {
    (void)user; (void)id; (void)tc;
    out->candidate_mean_score = 0.77;
    out->should_promote = true;
    out->reason = strdup("candidate beat baseline");
    return true;
}
static bool ab_reject(void *user, const char *id, size_t tc, ca_ab_verdict_t *out) {
    (void)user; (void)id; (void)tc;
    out->candidate_mean_score = 0.40;
    out->should_promote = false;
    out->reason = strdup("regressed");
    return true;
}

static void test_selfbench_improvement_loop(void) {
    assert(ca_selfbench_improvement_loop_create(NULL, NULL, ab_promote, NULL, NULL, NULL) == NULL);
    assert(ca_selfbench_improvement_loop_create(suite_count_2, NULL, NULL, NULL, NULL, NULL) == NULL);

    /* empty suite → skipped */
    ca_selfbench_improvement_loop_t *le =
        ca_selfbench_improvement_loop_create(suite_count_0, NULL, ab_promote, NULL, NULL, NULL);
    ca_self_improvement_verdict_t v;
    assert(ca_selfbench_improvement_loop_cycle(le, "s", &v));
    assert(strcmp(v.improvements_applied, "skipped: no tasks in suite") == 0);
    assert(approx(v.new_bench_score, 0.0));
    ca_self_improvement_verdict_free(&v);
    ca_selfbench_improvement_loop_destroy(le);

    /* promote path */
    promote_calls = 0;
    ca_selfbench_improvement_loop_t *lp =
        ca_selfbench_improvement_loop_create(suite_count_2, NULL, ab_promote, NULL, on_promote, NULL);
    assert(ca_selfbench_improvement_loop_cycle(lp, "s", &v));
    assert(strcmp(v.improvements_applied, "promoted candidate (candidate beat baseline)") == 0);
    assert(approx(v.new_bench_score, 0.77));
    assert(promote_calls == 1);
    assert(approx(ca_selfbench_improvement_loop_best_score(lp, "s"), 0.77));
    ca_self_improvement_verdict_free(&v);
    ca_selfbench_improvement_loop_destroy(lp);

    /* reject path (blank suite → "default") */
    ca_selfbench_improvement_loop_t *lr =
        ca_selfbench_improvement_loop_create(suite_count_2, NULL, ab_reject, NULL, on_promote, NULL);
    assert(ca_selfbench_improvement_loop_cycle(lr, "  ", &v));
    assert(strcmp(v.improvements_applied, "rejected (regressed)") == 0);
    assert(approx(v.new_bench_score, 0.40));
    ca_self_improvement_verdict_free(&v);
    ca_selfbench_improvement_loop_destroy(lr);
    printf("  selfbench_improvement_loop: ok\n");
}

/* ========================================================================= */
/* voice listener bridge */
static int utt_calls = 0, resp_calls = 0;
static char last_reply[64];
static void on_utt(void *user, const ca_utterance_detected_event_t *e) {
    (void)user; utt_calls++;
    assert(e->confidence >= 0.0f);
}
static void on_resp(void *user, const ca_response_ready_event_t *e) {
    (void)user; resp_calls++;
    strncpy(last_reply, e->text, sizeof(last_reply) - 1);
}
static char *session_echo(void *user, const char *text) {
    (void)user;
    char *r = (char *)malloc(strlen(text) + 8);
    sprintf(r, "re: %s", text);
    return r;
}
static char *session_fail(void *user, const char *text) { (void)user; (void)text; return NULL; }

static void test_voice_listener(void) {
    assert(ca_voice_listener_create(NULL, NULL, NULL, NULL, NULL, NULL) == NULL);

    utt_calls = resp_calls = 0;
    ca_voice_listener_t *l = ca_voice_listener_create(session_echo, NULL, on_utt, NULL, on_resp, NULL);
    assert(l);

    bool fired = ca_voice_listener_on_transcribed(l, "hello", 0.9f, 1000, 2000);
    assert(fired);
    assert(utt_calls == 1 && resp_calls == 1);
    assert(strcmp(last_reply, "re: hello") == 0);
    ca_voice_listener_destroy(l);

    /* failing session → utterance raised, response NOT raised */
    utt_calls = resp_calls = 0;
    ca_voice_listener_t *lf = ca_voice_listener_create(session_fail, NULL, on_utt, NULL, on_resp, NULL);
    assert(!ca_voice_listener_on_transcribed(lf, "hi", 0.5f, 1, 2));
    assert(utt_calls == 1 && resp_calls == 0);
    ca_voice_listener_destroy(lf);

    printf("  voice_listener: ok\n");
}

int main(void) {
    test_always_on_presence();
    test_fused_perception();
    test_continuous_learner();
    test_goal_pursuer();
    test_voice_identity();
    test_calibrated_confidence();
    test_emotion_sensor();
    test_skill_acquisition();
    test_bio_signal_stream();
    test_physical_actuator();
    test_agent_peer_network();
    test_federated_finetuner();
    test_first_token_optimizer();
    test_crypto_delegation();
    test_code_generation_loop();
    test_self_improvement_loop();
    test_self_improvement_regression();
    test_selfbench_improvement_loop();
    test_voice_listener();
    printf("test_herjarvis: all assertions passed\n");
    return 0;
}
