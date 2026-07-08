/*
 * test_model_runtime.c — Core model-management runtime (C11).
 *
 * Exercises: SHA-256, in-memory source download (+ ModelScope host rule +
 * HuggingFace tombstone), ModelDownloader candidate fallback + registry-driven
 * download, LocalModelManager (sanitise id, checksum verify), LocalModelLoader
 * (single-file download/verify, bundle path resolution, model_exists),
 * SafeModelHandle release-once semantics.
 */

#include "circle_ai/model_runtime.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <time.h>
#include <sys/stat.h>

#if defined(_WIN32)
  #include <direct.h>
  #define MKDIR(p) _mkdir(p)
  #define SEP "\\"
#else
  #include <unistd.h>
  #include <sys/types.h>
  #define MKDIR(p) mkdir((p), 0777)
  #define SEP "/"
#endif

static const char *tmpdir(char *buf, size_t cap, const char *label) {
    const char *base =
#if defined(_WIN32)
        getenv("TEMP");
    if (!base) base = "C:\\Temp";
#else
        "/tmp";
#endif
    static int counter = 0;
    counter++;
    snprintf(buf, cap, "%s%scircleai-c-mr-%s-%d-%ld", base, SEP, label, counter, (long)time(NULL));
    MKDIR(buf);
    return buf;
}

static int file_exists(const char *p) { struct stat st; return stat(p, &st) == 0; }

/* known SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad */
static void test_sha256(void) {
    uint8_t d[32];
    ca_mr_sha256((const uint8_t *)"abc", 3, d);
    char hex[65];
    ca_mr_sha256_hex(d, hex);
    assert(strcmp(hex, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad") == 0);

    uint8_t e[32];
    ca_mr_sha256((const uint8_t *)"", 0, e);
    char hex2[65];
    ca_mr_sha256_hex(e, hex2);
    assert(strcmp(hex2, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855") == 0);
}

static void test_inmemory_source(void) {
    ca_model_source_t *s = ca_inmemory_model_source_create("Test");
    assert(s);
    assert(strcmp(ca_model_source_name(s), "Test") == 0);
    assert(ca_model_source_is_available(s));
    ca_inmemory_model_source_set_available(s, false);
    assert(!ca_model_source_is_available(s));

    const char *url = "https://host.test/model.bin";
    assert(ca_inmemory_model_source_add(s, url, (const uint8_t *)"HELLO", 5));

    char dir[1024]; tmpdir(dir, sizeof(dir), "src");
    char out[1200]; snprintf(out, sizeof(out), "%s%sout.bin", dir, SEP);
    assert(ca_model_source_download(s, url, out, NULL, NULL));
    assert(file_exists(out));
    FILE *f = fopen(out, "rb"); char b[8] = {0}; size_t n = fread(b, 1, 5, f); fclose(f);
    assert(n == 5 && memcmp(b, "HELLO", 5) == 0);

    /* unknown URL fails */
    assert(!ca_model_source_download(s, "https://host.test/nope", out, NULL, NULL));
    ca_model_source_destroy(s);
}

static void test_modelscope_hostrule_and_tombstone(void) {
    ca_model_source_t *ms = ca_modelscope_source_create();
    assert(ms && strcmp(ca_model_source_name(ms), "ModelScope") == 0);
    /* off-host URL rejected by the host rule even if registered */
    assert(ca_inmemory_model_source_add(ms, "https://evil.com/x", (const uint8_t *)"X", 1));
    char dir[1024]; tmpdir(dir, sizeof(dir), "ms");
    char out[1200]; snprintf(out, sizeof(out), "%s%sx.bin", dir, SEP);
    assert(!ca_model_source_download(ms, "https://evil.com/x", out, NULL, NULL));
    /* on-host URL allowed */
    assert(ca_inmemory_model_source_add(ms, "https://modelscope.cn/m.bin", (const uint8_t *)"OK", 2));
    assert(ca_model_source_download(ms, "https://modelscope.cn/m.bin", out, NULL, NULL));
    ca_model_source_destroy(ms);

    /* HuggingFace tombstone never constructs */
    assert(ca_huggingface_source_create() == NULL);
}

static void test_downloader_candidates_and_registry(void) {
    /* One ModelScope source (mirrors the real single-source design). Primary URL
     * is unregistered (download fails -> fall through); fallback URL is
     * registered on the same source, so the candidate walk lands on it. */
    ca_model_source_t *ms = ca_modelscope_source_create();
    const char *purl = "https://api.modelscope.cn/a.bin"; /* not registered */
    const char *furl = "https://cdn.modelscope.cn/a.bin";
    ca_inmemory_model_source_add(ms, furl, (const uint8_t *)"DATA", 4);

    ca_model_source_t *sources[1] = { ms };
    ca_model_downloader_t *d = ca_model_downloader_create(sources, 1, true, NULL, 0);
    assert(d);

    char dir[1024]; tmpdir(dir, sizeof(dir), "dl");
    char out[1200]; snprintf(out, sizeof(out), "%s%sa.bin", dir, SEP);
    const char *cands[2] = { purl, furl };
    char *winner = NULL;
    assert(ca_model_downloader_download_from_candidates(d, cands, 2, out, &winner, NULL, NULL));
    assert(file_exists(out));
    assert(winner != NULL && strcmp(winner, "ModelScope") == 0);
    free(winner);
    ca_model_downloader_destroy(d); /* owns + frees the source */

    /* registry-driven download_model */
    ca_model_source_t *ms2 = ca_modelscope_source_create();
    ca_inmemory_model_source_add(ms2, "https://modelscope.cn/w.bin", (const uint8_t *)"WEIGHTS", 7);
    ca_model_info_t reg[1];
    memset(reg, 0, sizeof(reg));
    reg[0].name = "MyModel";
    reg[0].file_name = "w.bin";
    reg[0].primary_url = "https://modelscope.cn/w.bin";
    ca_model_source_t *srcs2[1] = { ms2 };
    ca_model_downloader_t *d2 = ca_model_downloader_create(srcs2, 1, true, reg, 1);
    char mdir[1024]; snprintf(mdir, sizeof(mdir), "%s%smodeldir", dir, SEP);
    assert(ca_model_downloader_download_model(d2, "MyModel", mdir));
    char wpath[1200]; snprintf(wpath, sizeof(wpath), "%s%sw.bin", mdir, SEP);
    assert(file_exists(wpath));
    /* unknown model -> false */
    assert(!ca_model_downloader_download_model(d2, "Ghost", mdir));
    ca_model_downloader_destroy(d2);
}

static void test_local_model_manager(void) {
    char dir[1024]; tmpdir(dir, sizeof(dir), "mgr");

    /* prepare a source that serves pytorch_model.bin, and a downloader over a
     * registry keyed to sanitise "org/model" -> "org_model". */
    ca_model_source_t *ms = ca_modelscope_source_create();
    const char *url = "https://modelscope.cn/pt.bin";
    ca_inmemory_model_source_add(ms, url, (const uint8_t *)"PTDATA", 6);

    /* download_model writes <dir>/<file_name>; the manager needs the file named
     * pytorch_model.bin inside the model dir. Configure file_name accordingly. */
    ca_model_info_t reg[1];
    memset(reg, 0, sizeof(reg));
    reg[0].name = "org/model";
    reg[0].file_name = "pytorch_model.bin";
    reg[0].primary_url = url;
    ca_model_source_t *srcs[1] = { ms };
    ca_model_downloader_t *d = ca_model_downloader_create(srcs, 1, true, reg, 1);

    ca_local_model_manager_t *m = ca_local_model_manager_create(dir, d);
    assert(m);

    char *path = NULL;
    assert(ca_local_model_manager_get_model_path(m, "org/model", NULL, &path));
    assert(path != NULL);
    /* sanitised subdir org_model exists with pytorch_model.bin */
    char expect[1200]; snprintf(expect, sizeof(expect), "%s%sorg_model", dir, SEP);
    assert(strcmp(path, expect) == 0);
    char pt[1300]; snprintf(pt, sizeof(pt), "%s%spytorch_model.bin", path, SEP);
    assert(file_exists(pt));
    free(path);

    /* checksum verify: correct hash passes, wrong fails */
    uint8_t digest[32];
    ca_mr_sha256((const uint8_t *)"PTDATA", 6, digest);
    assert(ca_local_model_manager_verify(pt, digest));
    char *path2 = NULL;
    assert(ca_local_model_manager_get_model_path(m, "org/model", digest, &path2));
    free(path2);
    uint8_t wrong[32]; memset(wrong, 0xAB, 32);
    assert(!ca_local_model_manager_verify(pt, wrong));
    char *path3 = NULL;
    assert(!ca_local_model_manager_get_model_path(m, "org/model", wrong, &path3));
    assert(path3 == NULL);

    ca_local_model_manager_destroy(m);
    ca_model_downloader_destroy(d);
}

static void test_local_model_loader(void) {
    char dir[1024]; tmpdir(dir, sizeof(dir), "loader");

    /* payload + known checksum */
    const uint8_t payload[] = "MODELBYTES";
    size_t plen = sizeof(payload) - 1;
    uint8_t digest[32]; ca_mr_sha256(payload, plen, digest);
    char hex[65]; ca_mr_sha256_hex(digest, hex);
    char checksum[80]; snprintf(checksum, sizeof(checksum), "sha256:%s", hex);

    ca_model_source_t *src = ca_inmemory_model_source_create("ModelScope");
    const char *url = "https://modelscope.cn/single.bin";
    ca_inmemory_model_source_add(src, url, payload, plen);

    /* single-file registry entry */
    ca_model_info_t reg[2];
    memset(reg, 0, sizeof(reg));
    reg[0].name = "single";
    reg[0].file_name = "single.bin";
    reg[0].primary_url = url;
    reg[0].checksum = checksum;

    /* bundle entry */
    ca_bundle_file_t bundle_files[2];
    /* anchor sha is the digest of "ANCHOR" */
    uint8_t anchor_digest[32]; ca_mr_sha256((const uint8_t *)"ANCHOR", 6, anchor_digest);
    char anchor_hex[65]; ca_mr_sha256_hex(anchor_digest, anchor_hex);
    bundle_files[0] = (ca_bundle_file_t){ "config.json", "deadbeef", 10 };
    bundle_files[1] = (ca_bundle_file_t){ "llm.mnn.weight", anchor_hex, 100 };
    reg[1].name = "bundlemodel";
    reg[1].repo = "MNN/bundlemodel";
    reg[1].bundle_files = bundle_files;
    reg[1].bundle_count = 2;

    ca_local_model_loader_t *l = ca_local_model_loader_create(dir, reg, 2, src);
    assert(l);

    /* get_model_path (single) */
    char *sp = NULL;
    assert(ca_local_model_loader_get_model_path(l, "single", &sp));
    char expect_s[1200]; snprintf(expect_s, sizeof(expect_s), "%s%ssingle.bin", dir, SEP);
    assert(strcmp(sp, expect_s) == 0);
    free(sp);

    /* get_model_path (bundle -> <dir>/<name>/llm.mnn.weight) */
    char *bp = NULL;
    assert(ca_local_model_loader_get_model_path(l, "bundlemodel", &bp));
    char expect_b[1300]; snprintf(expect_b, sizeof(expect_b), "%s%sbundlemodel%sllm.mnn.weight", dir, SEP, SEP);
    assert(strcmp(bp, expect_b) == 0);
    free(bp);

    /* unknown model */
    char *xp = NULL;
    assert(!ca_local_model_loader_get_model_path(l, "ghost", &xp));

    /* model_exists false before download */
    assert(!ca_local_model_loader_model_exists(l, "single"));

    /* download single-file -> verifies checksum */
    char *dlp = NULL;
    assert(ca_local_model_loader_download_model(l, "single", &dlp));
    assert(file_exists(dlp));
    free(dlp);

    /* now model_exists true (checksum matches) */
    assert(ca_local_model_loader_model_exists(l, "single"));

    /* bundle download routes elsewhere -> false */
    char *bdl = NULL;
    assert(!ca_local_model_loader_download_model(l, "bundlemodel", &bdl));

    /* case-insensitive registry key */
    char *cp = NULL;
    assert(ca_local_model_loader_get_model_path(l, "SINGLE", &cp));
    free(cp);

    ca_local_model_loader_destroy(l);
    ca_model_source_destroy(src);
}

static int g_release_count = 0;
static void my_release(void *h) { (void)h; g_release_count++; }

static void test_safe_model_handle(void) {
    /* null release callback rejected */
    assert(ca_safe_model_handle_create((void *)0x1234, NULL) == NULL);

    int dummy = 5;
    ca_safe_model_handle_t *h = ca_safe_model_handle_create(&dummy, my_release);
    assert(h);
    assert(!ca_safe_model_handle_is_invalid(h));
    assert(ca_safe_model_handle_get(h) == &dummy);
    g_release_count = 0;
    ca_safe_model_handle_destroy(h); /* fires release once */
    assert(g_release_count == 1);

    /* invalid (NULL) handle: release does NOT fire */
    ca_safe_model_handle_t *h2 = ca_safe_model_handle_create(NULL, my_release);
    assert(h2);
    assert(ca_safe_model_handle_is_invalid(h2));
    g_release_count = 0;
    ca_safe_model_handle_destroy(h2);
    assert(g_release_count == 0);
}

int main(void) {
    test_sha256();
    test_inmemory_source();
    test_modelscope_hostrule_and_tombstone();
    test_downloader_candidates_and_registry();
    test_local_model_manager();
    test_local_model_loader();
    test_safe_model_handle();
    printf("test_model_runtime: all assertions passed\n");
    return 0;
}
