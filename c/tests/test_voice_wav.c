/*
 * test_voice_wav.c — asserts the C WAV reader against the same answers the C#
 * reference generates.
 *
 * THE FIXTURE BYTES ARE TRANSCRIBED, not parsed. The C port has no JSON reader;
 * every byte and expected sample below was generated mechanically from
 * fixtures/voice_wav_io.json. Change that fixture and regenerate these arrays
 * in the same commit.
 *
 * The LIST-chunk case is the one that matters: a reader that assumes data starts
 * at byte 44 reads metadata as audio.
 */

#include "circle_ai/voice_wav.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

/* pcm16-mono-plain — transcribed from fixtures/voice_wav_io.json */
static const unsigned char WAV_PCM16_MONO_PLAIN[] = {
        0x52, 0x49, 0x46, 0x46, 0x2A, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45
        0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00
        0xC0, 0x5D, 0x00, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00
        0x64, 0x61, 0x74, 0x61, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40
        0x00, 0xC0
};
static const float WANT_PCM16_MONO_PLAIN[] = { 0.0000000f, 0.5000000f, -0.5000000f };
static const size_t WANT_PCM16_MONO_PLAIN_COUNT = 3;

/* pcm16-mono-with-LIST-chunk — transcribed from fixtures/voice_wav_io.json */
static const unsigned char WAV_PCM16_MONO_WITH_LIST_CHUNK[] = {
        0x52, 0x49, 0x46, 0x46, 0x36, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45
        0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00
        0xC0, 0x5D, 0x00, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00
        0x4C, 0x49, 0x53, 0x54, 0x04, 0x00, 0x00, 0x00, 0x49, 0x4E, 0x46, 0x4F
        0x64, 0x61, 0x74, 0x61, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40
        0x00, 0xC0
};
static const float WANT_PCM16_MONO_WITH_LIST_CHUNK[] = { 0.0000000f, 0.5000000f, -0.5000000f };
static const size_t WANT_PCM16_MONO_WITH_LIST_CHUNK_COUNT = 3;

/* pcm16-stereo-averaged — transcribed from fixtures/voice_wav_io.json */
static const unsigned char WAV_PCM16_STEREO_AVERAGED[] = {
        0x52, 0x49, 0x46, 0x46, 0x2C, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45
        0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00
        0xC0, 0x5D, 0x00, 0x00, 0x00, 0x77, 0x01, 0x00, 0x04, 0x00, 0x10, 0x00
        0x64, 0x61, 0x74, 0x61, 0x08, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0xC0
        0x00, 0x40, 0x00, 0x40
};
static const float WANT_PCM16_STEREO_AVERAGED[] = { 0.0000000f, 0.5000000f };
static const size_t WANT_PCM16_STEREO_AVERAGED_COUNT = 2;

static int failures = 0;
static int checks = 0;

static void check(int cond, const char *what)
{
    checks++;
    if (!cond) { printf("  FAIL: %s
", what); failures++; }
}

static size_t decode(const unsigned char *raw, size_t len, float *out, size_t cap)
{
    circle_wav wav;
    circle_wav_status st = circle_voice_wav_parse(raw, len, &wav);
    if (st != CIRCLE_WAV_OK) { printf("  FAIL: parse returned %d
", (int)st); failures++; return 0; }
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
        printf("  FAIL: %s — got %zu samples, want %zu
", name, n, want_count);
        failures++;
        return;
    }
    for (size_t i = 0; i < want_count; i++) {
        if (fabsf(got[i] - want[i]) >= 1e-6f) {
            printf("  FAIL: %s — sample %zu is %f, want %f
", name, i, got[i], want[i]);
            failures++;
            return;
        }
    }
}

int main(void)
{
    printf("voice WAV parity (C)
");

    one("pcm16-mono-plain", WAV_PCM16_MONO_PLAIN, sizeof(WAV_PCM16_MONO_PLAIN), WANT_PCM16_MONO_PLAIN, WANT_PCM16_MONO_PLAIN_COUNT);
    one("pcm16-mono-with-LIST-chunk", WAV_PCM16_MONO_WITH_LIST_CHUNK, sizeof(WAV_PCM16_MONO_WITH_LIST_CHUNK), WANT_PCM16_MONO_WITH_LIST_CHUNK, WANT_PCM16_MONO_WITH_LIST_CHUNK_COUNT);
    one("pcm16-stereo-averaged", WAV_PCM16_STEREO_AVERAGED, sizeof(WAV_PCM16_STEREO_AVERAGED), WANT_PCM16_STEREO_AVERAGED, WANT_PCM16_STEREO_AVERAGED_COUNT);

    /* A LIST chunk before the data must not change the decoded audio. */
    {
        float a[64], b[64];
        size_t na = decode(WAV_PCM16_MONO_PLAIN, sizeof(WAV_PCM16_MONO_PLAIN), a, 64);
        size_t nb = decode(WAV_PCM16_MONO_WITH_LIST_CHUNK, sizeof(WAV_PCM16_MONO_WITH_LIST_CHUNK), b, 64);
        check(na == nb && memcmp(a, b, na * sizeof(float)) == 0,
              "a LIST chunk before the data changed the decoded audio");
    }

    printf("%d checks, %d failures
", checks, failures);
    return failures == 0 ? 0 : 1;
}
