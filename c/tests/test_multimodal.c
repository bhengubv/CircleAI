/*
 * test_multimodal.c — multimodal memory pipeline (C11).
 *
 * Mirrors the verified TypeScript multimodal.test.ts: HeuristicMultimodalCaptioner,
 * InMemoryMultimodalMemoryStore, MultimodalMemoryIngester (dedup + caption +
 * persist), plus the SHA-256 against known vectors. Bytes are synthesised inline.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── helpers (mirror the TS fakeJpeg / fakePng) ── */

static uint8_t *fake_jpeg(size_t extra, size_t *out_len) {
    size_t n = 2 + extra;
    uint8_t *b = (uint8_t *)malloc(n);
    b[0] = 0xff; b[1] = 0xd8;
    for (size_t i = 2; i < n; ++i) b[i] = (uint8_t)(i % 251);
    *out_len = n;
    return b;
}
static uint8_t *fake_png(size_t extra, size_t *out_len) {
    size_t n = 4 + extra;
    uint8_t *b = (uint8_t *)malloc(n);
    b[0]=0x89; b[1]=0x50; b[2]=0x4e; b[3]=0x47;
    for (size_t i = 4; i < n; ++i) b[i] = (uint8_t)(i % 251);
    *out_len = n;
    return b;
}

static bool caption_has(const char *cap, const char *needle) {
    return strstr(cap, needle) != NULL;
}

/* FakeRichCaptioner — only handles Image; returns rich caption + embedding. */
static bool rich_can(void *u, ca_media_modality_t m, const char *mime) {
    (void)u; (void)mime; return m == CA_MEDIA_IMAGE;
}
static bool rich_caption(void *u, ca_media_modality_t m, const uint8_t *b, size_t l,
                         const char *mime, ca_caption_result_t *out) {
    (void)u; (void)m; (void)b; (void)l; (void)mime;
    out->caption = strdup("A blue sky with two clouds.");
    out->embedding = (float *)malloc(3 * sizeof(float));
    out->embedding[0]=0.1f; out->embedding[1]=0.2f; out->embedding[2]=0.3f;
    out->embedding_len = 3;
    out->has_width = true; out->width_px = 1920;
    out->has_height = true; out->height_px = 1080;
    out->has_duration = false; out->duration_ms = 0;
    return true;
}

int main(void) {
    size_t len;

    /* ── SHA-256 against FIPS 180-4 known vectors ── */
    {
        char h[65];
        ca_sha256_hex((const uint8_t*)"", 0, h);
        assert(strcmp(h, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")==0);
        ca_sha256_hex((const uint8_t*)"abc", 3, h);
        assert(strcmp(h, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")==0);
    }

    /* ── HeuristicMultimodalCaptioner ── */
    {
        ca_captioner_t c = ca_heuristic_captioner();
        assert(c.can_caption(c.user, CA_MEDIA_IMAGE, "image/jpeg"));
        assert(c.can_caption(c.user, CA_MEDIA_AUDIO, NULL));
        assert(c.can_caption(c.user, CA_MEDIA_VIDEO, "video/mp4"));
        assert(c.can_caption(c.user, CA_MEDIA_TEXT_DOCUMENT, "application/pdf"));

        /* JPEG magic → image/jpeg, no embedding */
        uint8_t *jpg = fake_jpeg(100, &len);
        ca_caption_result_t r; memset(&r,0,sizeof(r));
        assert(c.caption(c.user, CA_MEDIA_IMAGE, jpg, len, NULL, &r));
        assert(caption_has(r.caption, "image/jpeg"));
        assert(r.embedding == NULL);
        assert(caption_has(r.caption, "no captioner wired"));
        char bytebuf[32]; snprintf(bytebuf, sizeof(bytebuf), "%zu bytes", len);
        assert(caption_has(r.caption, bytebuf));
        assert(r.caption[0]=='[' && r.caption[1]=='I'); /* "[Image" */
        ca_caption_result_free(&r);
        free(jpg);

        /* PNG / GIF / WAV / PDF magic */
        uint8_t *png = fake_png(100, &len);
        memset(&r,0,sizeof(r)); assert(c.caption(c.user, CA_MEDIA_IMAGE, png, len, NULL, &r));
        assert(caption_has(r.caption, "image/png")); ca_caption_result_free(&r); free(png);

        uint8_t gif[] = {0x47,0x49,0x46,0x38};
        memset(&r,0,sizeof(r)); assert(c.caption(c.user, CA_MEDIA_IMAGE, gif, 4, NULL, &r));
        assert(caption_has(r.caption, "image/gif")); ca_caption_result_free(&r);

        uint8_t wav[] = {0x52,0x49,0x46,0x46};
        memset(&r,0,sizeof(r)); assert(c.caption(c.user, CA_MEDIA_AUDIO, wav, 4, NULL, &r));
        assert(caption_has(r.caption, "audio/wav")); ca_caption_result_free(&r);

        uint8_t pdf[] = {0x25,0x50,0x44,0x46};
        memset(&r,0,sizeof(r)); assert(c.caption(c.user, CA_MEDIA_TEXT_DOCUMENT, pdf, 4, NULL, &r));
        assert(caption_has(r.caption, "application/pdf")); ca_caption_result_free(&r);

        /* unknown magic → octet-stream */
        uint8_t unk[] = {1,2,3,4};
        memset(&r,0,sizeof(r)); assert(c.caption(c.user, CA_MEDIA_AUDIO, unk, 4, NULL, &r));
        assert(caption_has(r.caption, "application/octet-stream")); ca_caption_result_free(&r);

        /* declared MIME wins */
        uint8_t *png2 = fake_png(100, &len);
        memset(&r,0,sizeof(r)); assert(c.caption(c.user, CA_MEDIA_IMAGE, png2, len, "image/heic", &r));
        assert(caption_has(r.caption, "image/heic")); ca_caption_result_free(&r); free(png2);

        /* modality labels */
        uint8_t *j2 = fake_jpeg(100, &len);
        memset(&r,0,sizeof(r)); c.caption(c.user, CA_MEDIA_IMAGE, j2, len, NULL, &r);
        assert(strncmp(r.caption, "[Image", 6)==0); ca_caption_result_free(&r);
        memset(&r,0,sizeof(r)); c.caption(c.user, CA_MEDIA_AUDIO, j2, len, "audio/wav", &r);
        assert(strncmp(r.caption, "[Audio", 6)==0); ca_caption_result_free(&r);
        memset(&r,0,sizeof(r)); c.caption(c.user, CA_MEDIA_VIDEO, j2, len, "video/mp4", &r);
        assert(strncmp(r.caption, "[Video", 6)==0); ca_caption_result_free(&r);
        memset(&r,0,sizeof(r)); c.caption(c.user, CA_MEDIA_TEXT_DOCUMENT, j2, len, "application/pdf", &r);
        assert(strncmp(r.caption, "[Document", 9)==0); ca_caption_result_free(&r);
        free(j2);
    }

    /* ── Ingester: happy path ── */
    {
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_captioner_t caps[] = { ca_heuristic_captioner() };
        ca_multimodal_ingester_t *ing = ca_multimodal_ingester_create(caps, 1, store);
        assert(ing);

        uint8_t *jpg = fake_jpeg(100, &len);
        ca_ingest_options_t opts = {0}; opts.mime_type = "image/jpeg";
        ca_ingestion_result_t r;
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, jpg, len, &opts, &r));
        assert(!r.was_deduplicated);
        assert(ca_multimodal_store_count(store) == 1);
        assert(r.entry.source_byte_count == (int64_t)len);
        assert(strcmp(r.entry.source_mime_type, "image/jpeg")==0);
        assert(r.entry.source_sha256 && strlen(r.entry.source_sha256) == 64);
        ca_ingestion_result_free(&r);

        /* second time same bytes → dedup + reinforce */
        ca_ingestion_result_t r2;
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, jpg, len, &opts, &r2));
        assert(r2.was_deduplicated);
        assert(ca_multimodal_store_count(store) == 1);
        assert(r2.entry.reference_count == 2);
        ca_ingestion_result_free(&r2);
        free(jpg);

        ca_multimodal_ingester_destroy(ing);
        ca_multimodal_store_destroy(store);
    }

    /* different bytes → distinct entries */
    {
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_captioner_t caps[] = { ca_heuristic_captioner() };
        ca_multimodal_ingester_t *ing = ca_multimodal_ingester_create(caps, 1, store);
        size_t l1, l2;
        uint8_t *a = fake_jpeg(50, &l1);
        uint8_t *b = fake_jpeg(60, &l2);
        ca_ingestion_result_t ra, rb;
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, a, l1, NULL, &ra));
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, b, l2, NULL, &rb));
        assert(strcmp(ra.entry.source_sha256, rb.entry.source_sha256) != 0);
        assert(ca_multimodal_store_count(store) == 2);
        ca_ingestion_result_free(&ra); ca_ingestion_result_free(&rb);
        free(a); free(b);
        ca_multimodal_ingester_destroy(ing);
        ca_multimodal_store_destroy(store);
    }

    /* empty bytes throw (return false) */
    {
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_captioner_t caps[] = { ca_heuristic_captioner() };
        ca_multimodal_ingester_t *ing = ca_multimodal_ingester_create(caps, 1, store);
        ca_ingestion_result_t r;
        assert(!ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, (const uint8_t*)"", 0, NULL, &r));
        ca_multimodal_ingester_destroy(ing);
        ca_multimodal_store_destroy(store);
    }

    /* records source URI and tags */
    {
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_captioner_t caps[] = { ca_heuristic_captioner() };
        ca_multimodal_ingester_t *ing = ca_multimodal_ingester_create(caps, 1, store);
        uint8_t *png = fake_png(100, &len);
        const char *tk[] = {"location","person"};
        const char *tv[] = {"home","alex"};
        ca_ingest_options_t opts = {0};
        opts.mime_type = "image/png";
        opts.source_uri = "file:///photos/IMG_001.png";
        opts.tag_keys = tk; opts.tag_values = tv; opts.tag_count = 2;
        ca_ingestion_result_t r;
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, png, len, &opts, &r));
        assert(strcmp(r.entry.source_uri, "file:///photos/IMG_001.png")==0);
        assert(strcmp(ca_multimodal_entry_get_tag(&r.entry, "location"), "home")==0);
        assert(strcmp(ca_multimodal_entry_get_tag(&r.entry, "person"), "alex")==0);
        ca_ingestion_result_free(&r);
        free(png);
        ca_multimodal_ingester_destroy(ing);
        ca_multimodal_store_destroy(store);
    }

    /* ── captioner selection ── */
    {
        /* prefer the rich captioner over heuristic */
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_captioner_t rich = { NULL, rich_can, rich_caption };
        ca_captioner_t caps[] = { rich, ca_heuristic_captioner() };
        ca_multimodal_ingester_t *ing = ca_multimodal_ingester_create(caps, 2, store);
        uint8_t *jpg = fake_jpeg(100, &len);
        ca_ingest_options_t opts = {0}; opts.mime_type = "image/jpeg";
        ca_ingestion_result_t r;
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_IMAGE, jpg, len, &opts, &r));
        assert(strcmp(r.entry.caption, "A blue sky with two clouds.")==0);
        assert(r.entry.embedding && r.entry.embedding_len == 3);
        assert(r.entry.has_width && r.entry.width_px == 1920);
        assert(r.entry.has_height && r.entry.height_px == 1080);
        ca_ingestion_result_free(&r);
        free(jpg);

        /* rich captioner declines Audio → heuristic fallback */
        uint8_t *png = fake_png(100, &len);
        ca_ingest_options_t o2 = {0}; o2.mime_type = "audio/wav";
        ca_ingestion_result_t r2;
        assert(ca_multimodal_ingester_ingest(ing, CA_MEDIA_AUDIO, png, len, &o2, &r2));
        assert(caption_has(r2.entry.caption, "no captioner wired"));
        assert(r2.entry.embedding == NULL);
        ca_ingestion_result_free(&r2);
        free(png);
        ca_multimodal_ingester_destroy(ing);
        ca_multimodal_store_destroy(store);

        /* zero captioners rejected */
        ca_multimodal_store_t *s2 = ca_multimodal_store_create();
        assert(ca_multimodal_ingester_create(NULL, 0, s2) == NULL);
        ca_multimodal_store_destroy(s2);
    }

    /* ── Store: search / recency / prune / reinforce ── */
    {
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        float e1[] = {1.0f,0.1f,0.0f};
        float e2[] = {0.0f,0.0f,1.0f};
        ca_multimodal_entry_t a = {0}, b = {0};
        a.id="near"; a.recorded_at_ms=1000; a.modality=CA_MEDIA_IMAGE; a.caption="near"; a.embedding=e1; a.embedding_len=3; a.source_sha256="near"; a.reference_count=1;
        b.id="far"; b.recorded_at_ms=1001; b.modality=CA_MEDIA_IMAGE; b.caption="far"; b.embedding=e2; b.embedding_len=3; b.source_sha256="far"; b.reference_count=1;
        assert(ca_multimodal_store_add(store, &a));
        assert(ca_multimodal_store_add(store, &b));
        float q[] = {1.0f,0.0f,0.0f};
        size_t n;
        ca_multimodal_entry_t *ranked = ca_multimodal_store_search(store, q, 3, 2, &n);
        assert(n == 2);
        assert(strcmp(ranked[0].source_sha256, "near")==0);
        assert(strcmp(ranked[1].source_sha256, "far")==0);
        ca_multimodal_entry_free_array(ranked, n);

        /* null query → recency */
        ca_multimodal_entry_t *recent = ca_multimodal_store_search(store, NULL, 0, 2, &n);
        assert(n == 2);
        assert(strcmp(recent[0].source_sha256, "far")==0); /* newer recorded_at */
        ca_multimodal_entry_free_array(recent, n);

        /* case-insensitive hash lookup */
        ca_multimodal_entry_t got;
        assert(ca_multimodal_store_get_by_hash(store, "NEAR", &got));
        ca_multimodal_entry_free(&got);

        ca_multimodal_store_destroy(store);
    }
    {
        /* prune removes older-than-cutoff */
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_multimodal_entry_t o = {0}, nw = {0};
        o.id="old"; o.recorded_at_ms=1000; o.modality=CA_MEDIA_IMAGE; o.caption="old"; o.source_sha256="old"; o.reference_count=1;
        nw.id="new"; nw.recorded_at_ms=9000; nw.modality=CA_MEDIA_IMAGE; nw.caption="new"; nw.source_sha256="new"; nw.reference_count=1;
        ca_multimodal_store_add(store, &o);
        ca_multimodal_store_add(store, &nw);
        size_t removed = ca_multimodal_store_prune_older_than(store, 5000);
        assert(removed == 1);
        assert(ca_multimodal_store_count(store) == 1);
        ca_multimodal_entry_t got;
        assert(ca_multimodal_store_get_by_hash(store, "new", &got)); ca_multimodal_entry_free(&got);
        assert(!ca_multimodal_store_get_by_hash(store, "old", &got));
        ca_multimodal_store_destroy(store);
    }
    {
        /* reinforce increments; unknown hash is a no-op; add without hash fails */
        ca_multimodal_store_t *store = ca_multimodal_store_create();
        ca_multimodal_entry_t e = {0};
        e.id="x"; e.recorded_at_ms=1000; e.modality=CA_MEDIA_IMAGE; e.caption="x"; e.source_sha256="x"; e.reference_count=1;
        ca_multimodal_store_add(store, &e);
        ca_multimodal_store_reinforce(store, "x");
        ca_multimodal_store_reinforce(store, "x");
        ca_multimodal_entry_t got;
        assert(ca_multimodal_store_get_by_hash(store, "x", &got));
        assert(got.reference_count == 3);
        ca_multimodal_entry_free(&got);
        ca_multimodal_store_reinforce(store, "missing"); /* no-op */
        assert(ca_multimodal_store_count(store) == 1);

        ca_multimodal_entry_t noHash = {0};
        noHash.id="y"; noHash.modality=CA_MEDIA_IMAGE; noHash.caption="x"; noHash.source_sha256=""; noHash.reference_count=1;
        assert(!ca_multimodal_store_add(store, &noHash));
        ca_multimodal_store_destroy(store);
    }

    printf("test_multimodal: all assertions passed\n");
    return 0;
}
