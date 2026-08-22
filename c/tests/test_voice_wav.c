/*
 * test_voice_wav.c — asserts the C WAV reader against the same answers the C#
 * reference generates.
 *
 * THE FIXTURE BYTES ARE TRANSCRIBED, not parsed. The C port has no JSON reader;
 * every byte and expected sample below was generated mechanically from
 * fixtures/voice_wav_io.json. Change that fixture and regenerate these arrays in
 * the same commit — this is the one port where a fixture can drift silently.
 *
 * The LIST-chunk case is the one that matters: a reader that assumes data starts
 * at byte 44 reads metadata as audio.
 */

#include "circle_ai/voice_wav.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

/* pcm16-mono-plain */
static const unsigned char WAV_PLAIN[] = {
    0x52, 0x49, 0x46, 0x46, 0x2A, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45,
    0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
    0xC0, 0x5D, 0x00, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00,
    0x64, 0x61, 0x74, 0x61, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40,
    0x00, 0xC0
};
static const float WANT_PLAIN[] = { 0.0f, 0.5f, -0.5f };

/* pcm16-mono-with-LIST-chunk — same audio, a LIST chunk before the data */
static const unsigned char WAV_LIST[] = {
    0x52, 0x49, 0x46, 0x46, 0x36, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45,
    0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
    0xC0, 0x5D, 0x00, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00,
    0x4C, 0x49, 0x53, 0x54, 0x04, 0x00, 0x00, 0x00, 0x49, 0x4E, 0x46, 0x4F,
    0x64, 0x61, 0x74, 0x61, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40,
    0x00, 0xC0
};
static const float WANT_LIST[] = { 0.0f, 0.5f, -0.5f };

/* pcm16-stereo-averaged — two channels, averaged to mono */
static const unsigned char WAV_STEREO[] = {
    0x52, 0x49, 0x46, 0x46, 0x2C, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45,
    0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00,
    0xC0, 0x5D, 0x00, 0x00, 0x00, 0x77, 0x01, 0x00, 0x04, 0x00, 0x10, 0x00,
    0x64, 0x61, 0x74, 0x61, 0x08, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0xC0,
    0x00, 0x40, 0x00, 0x40
};
static const float WANT_STEREO[] = { 0.0f, 0.5f };

static int failures = 0;
static int checks = 0;

static size_t decode(const unsigned char *raw, size_t len, float *out, size_t cap)
{
    circle_wav wav;
    circle_wav_status st = circle_voice_wav_parse(raw, len, &wav);
    if (st != CIRCLE_WAV_OK) {
        printf("  FAIL: parse returned %d\n", (int)st);
        failures++;
        return 0;
    }
    size_t n = circle_voice_wav_to_mono_24k(&wav, 30, out, cap);
    circle_voice_wav_free(&wav);
    return n;
}

static void one(const char *name, const unsigned char *raw, size_t len,
                const float *want, size_t want_count)
{
    float got[64];
    size_t n = decode(raw, len, got, 64);
    checks++;
    if (n != want_count) {
        printf("  FAIL: %s - got %zu samples, want %zu\n", name, n, want_count);
        failures++;
        return;
    }
    for (size_t i = 0; i < want_count; i++) {
        if (fabsf(got[i] - want[i]) >= 1e-6f) {
            printf("  FAIL: %s - sample %zu is %f, want %f\n", name, i, got[i], want[i]);
            failures++;
            return;
        }
    }
}

int main(void)
{
    printf("voice WAV parity (C)\n");

    one("pcm16-mono-plain", WAV_PLAIN, sizeof(WAV_PLAIN), WANT_PLAIN, 3);
    one("pcm16-mono-with-LIST-chunk", WAV_LIST, sizeof(WAV_LIST), WANT_LIST, 3);
    one("pcm16-stereo-averaged", WAV_STEREO, sizeof(WAV_STEREO), WANT_STEREO, 2);

    /* A LIST chunk before the data must not change the decoded audio. */
    {
        float a[64], b[64];
        size_t na = decode(WAV_PLAIN, sizeof(WAV_PLAIN), a, 64);
        size_t nb = decode(WAV_LIST, sizeof(WAV_LIST), b, 64);
        checks++;
        if (na != nb || memcmp(a, b, na * sizeof(float)) != 0) {
            printf("  FAIL: a LIST chunk before the data changed the decoded audio\n");
            failures++;
        }
    }

    printf("%d checks, %d failures\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
