/*
 * test_wearable_biosignals.c — CircleAI.Wearable.Biosignals (C11 port)
 * verification against BiosignalKind / BiosignalSample / IBiosignalSource /
 * NullBiosignalSource / RecordedBiosignalSource.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_enum_and_make(void) {
    /* Stable integer values (must not renumber). */
    assert(CA_BIOSIGNAL_HEART_RATE == 0);
    assert(CA_BIOSIGNAL_STEPS == 6);
    assert(CA_BIOSIGNAL_UNKNOWN == 8);

    /* Create clamps confidence to [0,1]. */
    ca_biosignal_sample_t s;
    assert(ca_bio_sample_make(&s, "id-1", CA_BIOSIGNAL_HEART_RATE, 72.0f, "bpm",
                              1.5f, false, 1000) == 0);
    assert(s.confidence == 1.0f && s.value == 72.0f && s.kind == CA_BIOSIGNAL_HEART_RATE);
    assert(strcmp(s.unit, "bpm") == 0 && s.measured_at_ms == 1000);
    ca_biosignal_sample_free(&s);

    assert(ca_bio_sample_make(&s, "id-2", CA_BIOSIGNAL_STEPS, 100.0f, "count",
                              -0.3f, true, 2000) == 0);
    assert(s.confidence == 0.0f && s.is_cumulative);
    ca_biosignal_sample_free(&s);

    printf("  enum_and_make: ok\n");
}

static void test_null_source(void) {
    ca_biosignal_source_t *src = ca_biosignal_null_source_create();
    assert(src);
    size_t n = 99;
    ca_biosignal_kind_t *k = ca_biosignal_source_supported_kinds(src, &n);
    assert(k == NULL && n == 0);
    assert(!ca_biosignal_source_is_supported(src, CA_BIOSIGNAL_HEART_RATE));

    ca_biosignal_stream_t *st = ca_biosignal_source_stream(src);
    assert(st);
    ca_biosignal_sample_t out;
    assert(!ca_biosignal_stream_next(st, &out)); /* yields nothing */
    ca_biosignal_stream_destroy(st);
    ca_biosignal_source_destroy(src);
    printf("  null_source: ok\n");
}

static void test_recorded_source(void) {
    ca_biosignal_sample_t samples[3];
    memset(samples, 0, sizeof(samples));
    samples[0].id = (char *)"s0"; samples[0].kind = CA_BIOSIGNAL_HEART_RATE;
    samples[0].value = 70; samples[0].unit = (char *)"bpm"; samples[0].confidence = 0.9f;
    samples[0].measured_at_ms = 100;
    samples[1].id = (char *)"s1"; samples[1].kind = CA_BIOSIGNAL_STEPS;
    samples[1].value = 500; samples[1].unit = (char *)"count"; samples[1].confidence = 1.0f;
    samples[1].is_cumulative = true; samples[1].measured_at_ms = 200;
    samples[2].id = (char *)"s2"; samples[2].kind = CA_BIOSIGNAL_HEART_RATE; /* dup kind */
    samples[2].value = 72; samples[2].unit = (char *)"bpm"; samples[2].confidence = 0.8f;
    samples[2].measured_at_ms = 300;

    ca_biosignal_source_t *src = ca_biosignal_recorded_source_create(samples, 3);
    assert(src);

    /* SupportedKinds first-seen distinct: HeartRate, Steps. */
    size_t n = 0;
    ca_biosignal_kind_t *k = ca_biosignal_source_supported_kinds(src, &n);
    assert(n == 2 && k[0] == CA_BIOSIGNAL_HEART_RATE && k[1] == CA_BIOSIGNAL_STEPS);
    free(k);

    assert(ca_biosignal_source_is_supported(src, CA_BIOSIGNAL_HEART_RATE));
    assert(ca_biosignal_source_is_supported(src, CA_BIOSIGNAL_STEPS));
    assert(!ca_biosignal_source_is_supported(src, CA_BIOSIGNAL_OXYGEN_SATURATION));

    /* Stream replays in order: s0, s1, s2. */
    ca_biosignal_stream_t *st = ca_biosignal_source_stream(src);
    ca_biosignal_sample_t out;
    assert(ca_biosignal_stream_next(st, &out) && strcmp(out.id, "s0") == 0 &&
           out.value == 70);
    ca_biosignal_sample_free(&out);
    assert(ca_biosignal_stream_next(st, &out) && strcmp(out.id, "s1") == 0 &&
           out.is_cumulative);
    ca_biosignal_sample_free(&out);
    assert(ca_biosignal_stream_next(st, &out) && strcmp(out.id, "s2") == 0);
    ca_biosignal_sample_free(&out);
    assert(!ca_biosignal_stream_next(st, &out)); /* exhausted */
    ca_biosignal_stream_destroy(st);

    /* A second independent stream restarts from the beginning. */
    ca_biosignal_stream_t *st2 = ca_biosignal_source_stream(src);
    assert(ca_biosignal_stream_next(st2, &out) && strcmp(out.id, "s0") == 0);
    ca_biosignal_sample_free(&out);
    ca_biosignal_stream_destroy(st2);

    ca_biosignal_source_destroy(src);
    printf("  recorded_source: ok\n");
}

int main(void) {
    test_enum_and_make();
    test_null_source();
    test_recorded_source();
    printf("test_wearable_biosignals: all assertions passed\n");
    return 0;
}
