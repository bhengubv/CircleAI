/*
 * test_video.c — CircleAI.Video (C11 port).
 *
 * Verifies VideoResolution presets, NullVideoGenerator (empty video/mp4),
 * NullStyleScript (echo SourceMessage, passthrough Style, zero duration), and
 * InMemoryStyleReference (register/get/list, deep copy incl. frames+attribution,
 * OrdinalIgnoreCase last-write-wins) + NullStyleReference.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_resolution(void) {
    ca_video_resolution_t p480 = ca_video_resolution_p480();
    ca_video_resolution_t p720 = ca_video_resolution_p720();
    ca_video_resolution_t p1080 = ca_video_resolution_p1080();
    assert(p480.width == 720 && p480.height == 480);
    assert(p720.width == 1280 && p720.height == 720);
    assert(p1080.width == 1920 && p1080.height == 1080);
    printf("  resolution: ok\n");
}

static void test_null_video_generator(void) {
    ca_video_generator_t g = ca_null_video_generator();
    assert(strcmp(ca_video_generator_backend_id(&g), "null") == 0);

    ca_video_generation_request_t req;
    ca_video_generation_request_init(&req, "hello", 5000, ca_video_resolution_p720());
    assert(req.frame_rate == 24);
    assert(!req.has_style_id && !req.has_seed);
    assert(req.reference_image == NULL && req.audio_track == NULL);

    ca_video_generation_result_t out;
    assert(ca_video_generator_generate(&g, &req, &out) == 0);
    assert(out.video_bytes == NULL && out.video_len == 0);   /* empty */
    assert(strcmp(out.mime_type, "video/mp4") == 0);
    assert(out.duration_ms == 0);
    assert(out.frame_count == 0);
    assert(out.resolution.width == 1280 && out.resolution.height == 720);  /* echoes req */
    assert(strcmp(out.backend_id, "null") == 0);
    ca_video_generation_result_free(&out);
    printf("  null_video_generator: ok\n");
}

static void test_null_style_script(void) {
    ca_style_script_t s = ca_null_style_script();
    assert(strcmp(ca_style_script_backend_id(&s), "null") == 0);

    ca_style_script_request_t req;
    req.source_message = "call me back";
    req.style = "noir-detective";
    req.speaker_hint = NULL;
    req.language_hint = NULL;

    ca_style_script_result_t out;
    assert(ca_style_script_rewrite(&s, &req, &out) == 0);
    assert(strcmp(out.rewritten_text, "call me back") == 0);   /* echo */
    assert(strcmp(out.style, "noir-detective") == 0);          /* passthrough */
    assert(out.voice_persona_id == NULL);
    assert(out.estimated_spoken_duration_ms == 0);
    ca_style_script_result_free(&out);
    printf("  null_style_script: ok\n");
}

/* Build a StyleReference with attribution + two frames + voice persona. */
static void build_style(ca_style_reference_t *s, const char *id, const char *persona) {
    memset(s, 0, sizeof(*s));
    s->id = (char *)id;
    s->display_name = (char *)"Storybook Watercolour";
    s->short_description = (char *)"soft painted storybook look";
    s->attribution.source = (char *)"Public Domain Illustrations";
    s->attribution.license = (char *)"CC0";
    s->attribution.url = (char *)"https://example.org/pd";
    s->voice_persona_id = (char *)persona;   /* may be NULL */

    static uint8_t f0[3] = { 0xFF, 0xD8, 0xFF };   /* jpeg magic */
    static uint8_t f1[4] = { 0x89, 'P', 'N', 'G' };
    static ca_style_reference_frame_t frames[2];
    memset(frames, 0, sizeof(frames));
    frames[0].image_bytes = f0; frames[0].image_len = 3;
    frames[0].mime_type = (char *)"image/jpeg"; frames[0].caption = (char *)"cover";
    frames[1].image_bytes = f1; frames[1].image_len = 4;
    frames[1].mime_type = (char *)"image/png"; frames[1].caption = NULL;
    s->frames = frames;
    s->frame_count = 2;
}

static void test_inmemory_style_reference(void) {
    ca_style_reference_store_t *store = ca_inmemory_style_reference_create();
    assert(strcmp(ca_inmemory_style_reference_backend_id(store), "in-memory") == 0);
    assert(ca_inmemory_style_reference_count(store) == 0);

    ca_style_reference_t s;
    build_style(&s, "storybook-watercolour", "narrator-warm");
    assert(ca_inmemory_style_reference_register(store, &s) == 0);
    assert(ca_inmemory_style_reference_count(store) == 1);

    /* Get (case-insensitive) -> deep copy. */
    ca_style_reference_t got;
    assert(ca_inmemory_style_reference_get(store, "STORYBOOK-Watercolour", &got));
    assert(strcmp(got.id, "storybook-watercolour") == 0);
    assert(strcmp(got.display_name, "Storybook Watercolour") == 0);
    assert(strcmp(got.attribution.source, "Public Domain Illustrations") == 0);
    assert(strcmp(got.attribution.license, "CC0") == 0);
    assert(got.attribution.url && strcmp(got.attribution.url, "https://example.org/pd") == 0);
    assert(got.voice_persona_id && strcmp(got.voice_persona_id, "narrator-warm") == 0);
    assert(got.frame_count == 2);
    assert(got.frames != s.frames);   /* deep copy */
    assert(got.frames[0].image_len == 3 && got.frames[0].image_bytes[0] == 0xFF);
    assert(strcmp(got.frames[0].mime_type, "image/jpeg") == 0);
    assert(strcmp(got.frames[0].caption, "cover") == 0);
    assert(got.frames[1].image_len == 4 && got.frames[1].caption == NULL);
    ca_style_reference_free(&got);

    /* Missing id -> false. */
    ca_style_reference_t miss;
    assert(!ca_inmemory_style_reference_get(store, "does-not-exist", &miss));

    /* last-write-wins on a case-insensitively equal id. */
    ca_style_reference_t s2;
    build_style(&s2, "Storybook-Watercolour", NULL);   /* different case, null persona */
    s2.display_name = (char *)"Updated Name";
    assert(ca_inmemory_style_reference_register(store, &s2) == 0);
    assert(ca_inmemory_style_reference_count(store) == 1);   /* replaced, not added */
    assert(ca_inmemory_style_reference_get(store, "storybook-watercolour", &got));
    assert(strcmp(got.display_name, "Updated Name") == 0);
    assert(got.voice_persona_id == NULL);   /* new record has no persona */
    ca_style_reference_free(&got);

    /* Register a second, distinct style; List returns both in insertion order. */
    ca_style_reference_t s3;
    build_style(&s3, "noir-detective", "gumshoe");
    assert(ca_inmemory_style_reference_register(store, &s3) == 0);
    assert(ca_inmemory_style_reference_count(store) == 2);

    size_t n = 0;
    ca_style_reference_t *list = ca_inmemory_style_reference_list(store, &n);
    assert(n == 2 && list);
    assert(strcmp(list[0].id, "Storybook-Watercolour") == 0);   /* preserves stored id casing */
    assert(strcmp(list[1].id, "noir-detective") == 0);
    ca_style_reference_free_array(list, n);

    ca_inmemory_style_reference_destroy(store);
    printf("  inmemory_style_reference: ok\n");
}

static void test_null_style_reference(void) {
    ca_null_style_reference_t *s = ca_null_style_reference_create();
    assert(strcmp(ca_null_style_reference_backend_id(s), "null") == 0);

    ca_style_reference_t sr;
    build_style(&sr, "x", NULL);
    assert(ca_null_style_reference_register(s, &sr) == 0);   /* no-op */

    ca_style_reference_t out;
    assert(!ca_null_style_reference_get(s, "x", &out));      /* always misses */

    size_t n = 42;
    ca_style_reference_t *list = ca_null_style_reference_list(s, &n);
    assert(list == NULL && n == 0);                          /* always empty */

    ca_null_style_reference_destroy(s);
    printf("  null_style_reference: ok\n");
}

int main(void) {
    test_resolution();
    test_null_video_generator();
    test_null_style_script();
    test_inmemory_style_reference();
    test_null_style_reference();
    printf("test_video: all assertions passed\n");
    return 0;
}
