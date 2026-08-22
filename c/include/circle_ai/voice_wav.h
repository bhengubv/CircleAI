/*
 * voice_wav.h — minimal RIFF/WAVE reading and PCM-16 packing.
 *
 * C port of src/CircleAI.Voice/WavIo.cs, so a reference recording can become
 * the float samples a voice needs.
 *
 * Parity is asserted against fixtures/voice_wav_io.json.
 */

#ifndef CIRCLE_AI_VOICE_WAV_H
#define CIRCLE_AI_VOICE_WAV_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Mimi's sample rate — what circle_voice_wav_to_mono_24k resamples to. */
#define CIRCLE_VOICE_TARGET_RATE 24000

typedef enum {
    CIRCLE_WAV_OK = 0,
    CIRCLE_WAV_NOT_RIFF,
    CIRCLE_WAV_NO_CHUNK,
    CIRCLE_WAV_UNSUPPORTED,
    CIRCLE_WAV_NOMEM
} circle_wav_status;

/**
 * Decoded WAV. `samples` is heap-allocated interleaved float in [-1,1]; release
 * with circle_voice_wav_free.
 */
typedef struct {
    float *samples;
    size_t sample_count;
    int rate;
    int channels;
} circle_wav;

/**
 * Parse a RIFF/WAVE buffer.
 *
 * WALKS THE CHUNKS. A WAV written by anything other than the simplest encoder
 * carries LIST/fact/cue chunks before the data, and assuming data starts at
 * byte 44 reads metadata as audio.
 */
circle_wav_status circle_voice_wav_parse(const unsigned char *raw, size_t len, circle_wav *out);

/** Release a decoded WAV. Safe on a zeroed struct. */
void circle_voice_wav_free(circle_wav *wav);

/**
 * Downmix to mono, resample to 24 kHz and cap the length.
 *
 * Writes at most `out_capacity` samples and returns how many it WOULD have
 * written, so a caller can size a buffer by calling with capacity 0.
 */
size_t circle_voice_wav_to_mono_24k(const circle_wav *wav, int max_seconds,
                                    float *out, size_t out_capacity);

/**
 * Pack float samples in [-1,1] as little-endian signed 16-bit PCM.
 * Writes `count * 2` bytes; returns the byte count.
 */
size_t circle_voice_wav_to_pcm16(const float *samples, size_t count, unsigned char *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_WAV_H */
