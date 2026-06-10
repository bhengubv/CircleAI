/*
 * agents_v15.c — AgentMessage with auto-synth correlation ID.
 *
 * Named agents_v15.c so it does not collide with any existing agents
 * implementation. C ABI: ca_agent_message_init.
 */

#include "circle_ai/agents.h"
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

#if defined(_WIN32)
  #define WIN32_LEAN_AND_MEAN
  #include <windows.h>
  #include <bcrypt.h>
  #pragma comment(lib, "bcrypt.lib")
#else
  #include <fcntl.h>
  #include <unistd.h>
#endif

static void fill_random_bytes(uint8_t *out, size_t n) {
#if defined(_WIN32)
    if (BCryptGenRandom(NULL, out, (ULONG)n, BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0)
        return;
#else
    int fd = open("/dev/urandom", O_RDONLY);
    if (fd >= 0) {
        ssize_t got = read(fd, out, n);
        close(fd);
        if (got == (ssize_t)n) return;
    }
#endif
    /* fallback — non-cryptographic */
    static unsigned int seed = 0;
    if (seed == 0) { seed = (unsigned int)time(NULL) ^ 0x5BD1E995u; srand(seed); }
    for (size_t i = 0; i < n; i++) out[i] = (uint8_t)(rand() & 0xff);
}

static void synth_uuid_v4(char out[37]) {
    uint8_t b[16];
    fill_random_bytes(b, 16);
    b[6] = (b[6] & 0x0F) | 0x40;
    b[8] = (b[8] & 0x3F) | 0x80;
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],
        b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15]);
}

static void synth_hex32(char out[33]) {
    uint8_t b[16];
    fill_random_bytes(b, 16);
    for (size_t i = 0; i < 16; i++) {
        snprintf(out + i * 2, 3, "%02x", b[i]);
    }
    out[32] = 0;
}

void ca_agent_message_init(
    ca_agent_message_t      *m,
    ca_agent_message_kind_t  kind,
    const char              *from_uhid,
    const char              *to_uhid,
    const char              *content_type,
    const uint8_t           *payload,
    size_t                   payload_len,
    const uint8_t           *signature,
    size_t                   signature_len,
    const char              *correlation_id_in,
    int64_t                  now_unix_ms)
{
    if (!m) return;
    memset(m, 0, sizeof(*m));
    synth_uuid_v4(m->id);
    m->kind = kind;
    m->from_uhid = from_uhid;
    m->to_uhid = to_uhid;
    m->content_type = content_type;
    m->payload = payload;
    m->payload_len = payload_len;
    m->signature = signature;
    m->signature_len = signature_len;
    m->sent_at_unix_ms = now_unix_ms;
    if (correlation_id_in && correlation_id_in[0]) {
        size_t n = strlen(correlation_id_in);
        if (n > 32) n = 32;
        memcpy(m->correlation_id, correlation_id_in, n);
        m->correlation_id[n] = 0;
    } else {
        synth_hex32(m->correlation_id);
    }
}
