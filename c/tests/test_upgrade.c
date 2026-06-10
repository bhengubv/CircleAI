/*
 * test_upgrade.c — Parity test: 7 upgrade-detection cases + correlation ID.
 *
 * Matches C# ModelUpgradeTests byte-for-byte semantics.
 */

#include "circle_ai/registry.h"
#include "circle_ai/agents.h"
#include "circle_ai/selector.h"
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

static int g_pass = 0, g_fail = 0;

#define CHECK(cond, msg) do { \
    if (cond) { g_pass++; } \
    else { g_fail++; fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); } \
} while (0)

static void mkpath(const char *p) {
    char buf[1024];
    snprintf(buf, sizeof(buf), "%s", p);
    for (char *q = buf + 1; *q; q++) {
        if (*q == '/' || *q == '\\') {
            char saved = *q; *q = 0;
            MKDIR(buf);
            *q = saved;
        }
    }
    MKDIR(buf);
}

static const char *unique_dir(char *buf, size_t cap, const char *label) {
    const char *base =
#if defined(_WIN32)
        getenv("TEMP");
    if (!base) base = "C:\\Temp";
#else
        "/tmp";
#endif
    static int counter = 0;
    counter++;
    snprintf(buf, cap, "%s%scircleai-c-up-%s-%d-%ld", base, SEP, label, counter, (long)time(NULL));
    mkpath(buf);
    return buf;
}

static int64_t now_ms(void) {
    return (int64_t)time(NULL) * 1000LL;
}

static ca_bundle_file_t mkfile(const char *name, const char *sha, int64_t size) {
    ca_bundle_file_t f = { name, sha, size };
    return f;
}

static ca_model_entry_t mkentry(const char *name, const char *version,
                                 ca_bundle_file_t *files, size_t n) {
    ca_model_entry_t e;
    memset(&e, 0, sizeof(e));
    e.name = name;
    e.version = version;
    e.quantization = "Q4";
    e.repo = "MNN/stub";
    e.bundle_files = files;
    e.bundle_count = n;
    for (size_t i = 0; i < n; i++) e.total_bytes += files[i].size_bytes;
    return e;
}

static void case1_not_installed_empty(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c1");
    ca_bundle_file_t files[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "def", 200),
    };
    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "1.0.0", files, 2);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 0, "case1: count == 0");
}

static void case2_no_manifest_unknown(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c2");
    char mdir[1100]; snprintf(mdir, sizeof(mdir), "%s%sQwen3-0.6B-MNN", d, SEP);
    mkpath(mdir);
    char stub[1200]; snprintf(stub, sizeof(stub), "%s%sconfig.json", mdir, SEP);
    FILE *fp = fopen(stub, "wb"); fputs("stub", fp); fclose(fp);

    ca_bundle_file_t files[] = { mkfile("config.json", "abc", 100) };
    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "1.0.0", files, 1);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 1, "case2: count == 1");
    CHECK(ups[0].reason == CA_UPGRADE_UNKNOWN, "case2: reason UNKNOWN");
    CHECK(ups[0].installed_version == NULL, "case2: no installed version");
}

static void case3_all_shas_match_empty(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c3");
    char mdir[1100]; snprintf(mdir, sizeof(mdir), "%s%sQwen3-0.6B-MNN", d, SEP);
    ca_bundle_file_t fs[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "def", 200),
    };
    CHECK(ca_write_installed_manifest(mdir, "Qwen3-0.6B-MNN", "1.0.0",
                                       "MNN/Qwen3-0.6B-MNN", fs, 2, now_ms()) == 0,
          "case3: write manifest");

    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "1.0.0", fs, 2);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 0, "case3: count == 0");
    for (size_t i = 0; i < n; i++) free((void *)ups[i].installed_version);
}

static void case4_version_drift_version_changed_zero_bytes(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c4");
    char mdir[1100]; snprintf(mdir, sizeof(mdir), "%s%sQwen3-0.6B-MNN", d, SEP);
    ca_bundle_file_t fs[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "def", 200),
    };
    ca_write_installed_manifest(mdir, "Qwen3-0.6B-MNN", "1.0.0",
                                 "MNN/Qwen3-0.6B-MNN", fs, 2, now_ms());

    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "1.1.0", fs, 2);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 1, "case4: count == 1");
    CHECK(ups[0].reason == CA_UPGRADE_VERSION_CHANGED, "case4: VERSION_CHANGED");
    CHECK(ups[0].estimated_download_bytes == 0, "case4: 0 bytes");
    for (size_t i = 0; i < n; i++) free((void *)ups[i].installed_version);
}

static void case5_sha_drift_sha_changed_only_drifted_bytes(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c5");
    char mdir[1100]; snprintf(mdir, sizeof(mdir), "%s%sQwen3-0.6B-MNN", d, SEP);
    ca_bundle_file_t inst[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "OLD", 200),
    };
    ca_write_installed_manifest(mdir, "Qwen3-0.6B-MNN", "1.0.0",
                                 "MNN/Qwen3-0.6B-MNN", inst, 2, now_ms());

    ca_bundle_file_t avail[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "NEW", 200),
    };
    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "1.0.0", avail, 2);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 1, "case5: count == 1");
    CHECK(ups[0].reason == CA_UPGRADE_SHA_CHANGED, "case5: SHA_CHANGED");
    CHECK(ups[0].estimated_download_bytes == 200, "case5: 200 bytes");
    for (size_t i = 0; i < n; i++) free((void *)ups[i].installed_version);
}

static void case6_version_and_sha_drift_both_total_bytes(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c6");
    char mdir[1100]; snprintf(mdir, sizeof(mdir), "%s%sQwen3-0.6B-MNN", d, SEP);
    ca_bundle_file_t inst[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "OLD", 200),
    };
    ca_write_installed_manifest(mdir, "Qwen3-0.6B-MNN", "1.0.0",
                                 "MNN/Qwen3-0.6B-MNN", inst, 2, now_ms());

    ca_bundle_file_t avail[] = {
        mkfile("config.json", "abc2", 100),
        mkfile("llm.mnn",     "NEW",  200),
    };
    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "2.0.0", avail, 2);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 1, "case6: count == 1");
    CHECK(ups[0].reason == CA_UPGRADE_BOTH, "case6: BOTH");
    CHECK(ups[0].estimated_download_bytes == 300, "case6: 300 bytes");
    for (size_t i = 0; i < n; i++) free((void *)ups[i].installed_version);
}

static void case7_write_installed_manifest_round_trip_empty(void) {
    char d[1024]; unique_dir(d, sizeof(d), "c7");
    char mdir[1100]; snprintf(mdir, sizeof(mdir), "%s%sQwen3-0.6B-MNN", d, SEP);
    ca_bundle_file_t fs[] = {
        mkfile("config.json", "abc", 100),
        mkfile("llm.mnn",     "def", 200),
    };
    ca_write_installed_manifest(mdir, "Qwen3-0.6B-MNN", "1.0.0",
                                 "MNN/Qwen3-0.6B-MNN", fs, 2, now_ms());

    ca_model_entry_t e = mkentry("Qwen3-0.6B-MNN", "1.0.0", fs, 2);
    ca_model_registry_t r = { "stub", now_ms(), &e, 1 };
    ca_upgrade_info_t ups[4]; size_t n = 0;
    ca_check_for_upgrades(&r, d, now_ms(), ups, &n);
    CHECK(n == 0, "case7: count == 0");
}

static int hex_count(const char *s, size_t n) {
    size_t i = 0;
    for (; i < n && s[i]; i++) {
        char c = s[i];
        int ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (!ok) return -1;
    }
    return (int)i;
}

static void test_agent_message_correlation_id_autosynth(void) {
    uint8_t p[] = { 1, 2, 3 }, s[] = { 4, 5, 6 };
    ca_agent_message_t m1;
    ca_agent_message_init(&m1, CA_AGENT_GREET, "a", "b", "text/plain",
                          p, 3, s, 3, NULL, now_ms());
    int n1 = hex_count(m1.correlation_id, 33);
    CHECK(n1 == 32, "agent: correlation 32 hex when null");

    ca_agent_message_t m2;
    ca_agent_message_init(&m2, CA_AGENT_GREET, "a", "b", "text/plain",
                          p, 3, s, 3, "trace-abc", now_ms());
    CHECK(strcmp(m2.correlation_id, "trace-abc") == 0, "agent: correlation honoured");

    ca_agent_message_t m3;
    ca_agent_message_init(&m3, CA_AGENT_GREET, "a", "b", "text/plain",
                          p, 3, s, 3, NULL, now_ms());
    CHECK(strcmp(m1.correlation_id, m3.correlation_id) != 0,
          "agent: distinct correlations between calls");
}

static void test_capability_parse(void) {
    uint32_t c = ca_parse_capabilities("Text,Tools,Vision");
    CHECK((c & CA_CHAT_CAP_TEXT) != 0, "cap: Text");
    CHECK((c & CA_CHAT_CAP_TOOLS) != 0, "cap: Tools");
    CHECK((c & CA_CHAT_CAP_VISION) != 0, "cap: Vision");
    CHECK((c & CA_CHAT_CAP_AUDIO) == 0, "cap: Audio absent");
}

int main(void) {
    case1_not_installed_empty();
    case2_no_manifest_unknown();
    case3_all_shas_match_empty();
    case4_version_drift_version_changed_zero_bytes();
    case5_sha_drift_sha_changed_only_drifted_bytes();
    case6_version_and_sha_drift_both_total_bytes();
    case7_write_installed_manifest_round_trip_empty();
    test_agent_message_correlation_id_autosynth();
    test_capability_parse();

    fprintf(stderr, "test_upgrade: %d pass, %d fail\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
