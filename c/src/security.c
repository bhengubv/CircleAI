/*
 * security.c — ThreatVector + AnomalySignal implementations.
 *
 * Pure C11 plus a thin OS detection for the random source used by the
 * embedded UUID v4 helper:
 *   - Windows: BCryptGenRandom (bcrypt.lib) — RtlGenRandom (advapi32) fallback
 *   - POSIX:   /dev/urandom
 *   - Anything else: rand() fallback (NOT crypto-grade; logged at compile time
 *                    only because tests still need to run on exotic targets).
 *
 * AnomalySignal schema is the portable counterpart of the Go/Rust/Kotlin/Swift
 * ports — see security.h.
 */

#include "circle_ai/security.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* ---------------------------------------------------------------------------
 * Platform-specific random byte source
 * --------------------------------------------------------------------------- */

#if defined(_WIN32)
  /* Avoid Windows.h bloat; declare BCryptGenRandom directly. */
  #include <windows.h>
  #include <bcrypt.h>
  #pragma comment(lib, "bcrypt.lib")

  static int ca_random_bytes(unsigned char *buf, size_t n) {
      NTSTATUS s = BCryptGenRandom(NULL, buf, (ULONG)n,
                                   BCRYPT_USE_SYSTEM_PREFERRED_RNG);
      return (s == 0) ? 0 : -1;
  }

#elif defined(__unix__) || defined(__APPLE__) || defined(__linux__)
  #include <stdio.h>

  static int ca_random_bytes(unsigned char *buf, size_t n) {
      FILE *f = fopen("/dev/urandom", "rb");
      if (!f) return -1;
      size_t got = fread(buf, 1, n, f);
      fclose(f);
      return (got == n) ? 0 : -1;
  }

#else
  /* Fallback — not crypto-grade. Test-only targets. */
  static int ca_random_bytes(unsigned char *buf, size_t n) {
      static int seeded = 0;
      if (!seeded) { srand((unsigned int)time(NULL)); seeded = 1; }
      for (size_t i = 0; i < n; i++) buf[i] = (unsigned char)(rand() & 0xFF);
      return 0;
  }
#endif

/* ---------------------------------------------------------------------------
 * Last-resort fallback when ca_random_bytes itself fails
 * --------------------------------------------------------------------------- */

static void ca_random_bytes_or_fallback(unsigned char *buf, size_t n) {
    if (ca_random_bytes(buf, n) == 0) return;
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)time(NULL)); seeded = 1; }
    for (size_t i = 0; i < n; i++) buf[i] = (unsigned char)(rand() & 0xFF);
}

/* ---------------------------------------------------------------------------
 * UUID v4 — RFC 4122 §4.4
 *
 * 16 random bytes with:
 *   byte 6: top nibble forced to 0100 (version 4)
 *   byte 8: top two bits forced to 10  (variant 1)
 * Formatted as 8-4-4-4-12 lower-case hex with dashes.
 * --------------------------------------------------------------------------- */

void ca_uuid_v4(char out_buf[CA_UUID_STR_LEN]) {
    unsigned char b[16];
    ca_random_bytes_or_fallback(b, sizeof(b));

    b[6] = (unsigned char)((b[6] & 0x0F) | 0x40); /* version  = 4 */
    b[8] = (unsigned char)((b[8] & 0x3F) | 0x80); /* variant  = 10b */

    snprintf(out_buf, CA_UUID_STR_LEN,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        b[0], b[1], b[2],  b[3],
        b[4], b[5],
        b[6], b[7],
        b[8], b[9],
        b[10], b[11], b[12], b[13], b[14], b[15]);
}

/* ---------------------------------------------------------------------------
 * Internal helpers
 * --------------------------------------------------------------------------- */

static float ca_clamp01(float v) {
    if (v < 0.0f) return 0.0f;
    if (v > 1.0f) return 1.0f;
    return v;
}

static int64_t ca_unix_ms_now(void) {
#if defined(_WIN32)
    /* GetSystemTimeAsFileTime is monotonic-enough and avoids time_t/64 issues. */
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    /* FILETIME = 100-ns intervals since 1601-01-01; subtract Unix epoch offset. */
    /* 11644473600 seconds * 10,000,000 100-ns = 116444736000000000 */
    t -= 116444736000000000ULL;
    return (int64_t)(t / 10000ULL);  /* 100-ns → ms */
#else
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000LL + (int64_t)(ts.tv_nsec / 1000000L);
#endif
}

static void ca_copy_fixed(char *dst, size_t dst_size, const char *src) {
    if (dst_size == 0) return;
    if (!src) { dst[0] = '\0'; return; }
    size_t i = 0;
    while (i + 1 < dst_size && src[i] != '\0') {
        dst[i] = src[i];
        i++;
    }
    dst[i] = '\0';
}

/* ---------------------------------------------------------------------------
 * Factory
 * --------------------------------------------------------------------------- */

int ca_anomaly_signal_create(
    ca_threat_vector_t   vector,
    float                confidence,
    const char          *affected_module,
    const char          *description,
    ca_anomaly_signal_t *out_signal
) {
    if (!out_signal) return -1;

    memset(out_signal, 0, sizeof(*out_signal));

    ca_uuid_v4(out_signal->id);
    out_signal->vector              = vector;
    out_signal->confidence          = ca_clamp01(confidence);
    out_signal->detected_at_unix_ms = ca_unix_ms_now();

    ca_copy_fixed(out_signal->affected_module,
                  sizeof(out_signal->affected_module), affected_module);
    ca_copy_fixed(out_signal->description,
                  sizeof(out_signal->description), description);

    return 0;
}
