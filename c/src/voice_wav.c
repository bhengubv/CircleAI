/*
 * voice_wav.c — minimal RIFF/WAVE reading and PCM-16 packing.
 *
 * C port of src/CircleAI.Voice/WavIo.cs. See voice_wav.h for the contract and
 * fixtures/voice_wav_io.json for the answers this must reproduce.
 */

#include "circle_ai/voice_wav.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

static unsigned int be32(const unsigned char *b)
{
    return ((unsigned int)b[0] << 24) | ((unsigned int)b[1] << 16)
         | ((unsigned int)b[2] << 8) | (unsigned int)b[3];
}

static unsigned int le32(const unsigned char *b)
{
    return (unsigned int)b[0] | ((unsigned int)b[1] << 8)
         | ((unsigned int)b[2] << 16) | ((unsigned int)b[3] << 24);
}

static unsigned int le16(const unsigned char *b)
{
    return (unsigned int)b[0] | ((unsigned int)b[1] << 8);
}

circle_wav_status circle_voice_wav_parse(const unsigned char *raw, size_t len, circle_wav *out)
{
    if (!raw || !out) return CIRCLE_WAV_NOT_RIFF;
    memset(out, 0, sizeof(*out));

    if (len < 12 || be32(raw) != 0x52494646u /* RIFF */
                 || be32(raw + 8) != 0x57415645u /* WAVE */)
        return CIRCLE_WAV_NOT_RIFF;

    unsigned int format = 0, channels = 0, bits = 0;
    int rate = 0;
    const unsigned char *data = NULL;
    size_t data_size = 0;
    size_t offset = 12;

    /* WALK THE CHUNKS. A LIST or fact chunk before the data is normal, and
     * assuming data starts at byte 44 reads metadata as audio. */
    while (offset + 8 <= len) {
        unsigned int id = be32(raw + offset);
        int declared = (int)le32(raw + offset + 4);
        size_t body = offset + 8;
        size_t size = (declared < 0 || body + (size_t)declared > len)
                    ? len - body : (size_t)declared;

        if (id == 0x666D7420u) {                 /* "fmt " */
            format = le16(raw + body);
            channels = le16(raw + body + 2);
            rate = (int)le32(raw + body + 4);
            bits = le16(raw + body + 14);
        } else if (id == 0x64617461u) {          /* "data" */
            data = raw + body;
            data_size = size;
        }

        offset = body + size + (size & 1);       /* chunks are word-aligned */
    }

    if (channels == 0 || rate == 0 || data == NULL || data_size == 0)
        return CIRCLE_WAV_NO_CHUNK;

    /* 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format lives
     * in a sub-chunk — treated as PCM here, which is what it is in every file
     * the voice stack has met. */
    int pcm = (format == 1 || format == 0xFFFE);
    size_t stride;
    if (pcm && bits == 8) stride = 1;
    else if (pcm && bits == 16) stride = 2;
    else if (pcm && bits == 24) stride = 3;
    else if ((pcm || format == 3) && bits == 32) stride = 4;
    else return CIRCLE_WAV_UNSUPPORTED;

    size_t count = data_size / stride;
    float *samples = (float *)malloc((count ? count : 1) * sizeof(float));
    if (!samples) return CIRCLE_WAV_NOMEM;

    for (size_t i = 0; i < count; i++) {
        const unsigned char *p = data + i * stride;
        if (pcm && bits == 8) {
            samples[i] = (float)((int)p[0] - 128) / 128.0f;
        } else if (pcm && bits == 16) {
            samples[i] = (float)(short)(unsigned short)le16(p) / 32768.0f;
        } else if (pcm && bits == 24) {
            int v = (int)p[0] | ((int)p[1] << 8) | ((int)p[2] << 16);
            samples[i] = (float)((v << 8) >> 8) / 8388608.0f;
        } else if (pcm) {
            samples[i] = (float)(int)le32(p) / 2147483648.0f;
        } else {
            unsigned int bitsv = le32(p);
            float f;
            memcpy(&f, &bitsv, sizeof(f));   /* type-pun without breaking aliasing */
            samples[i] = f;
        }
    }

    out->samples = samples;
    out->sample_count = count;
    out->rate = rate;
    out->channels = (int)channels;
    return CIRCLE_WAV_OK;
}

void circle_voice_wav_free(circle_wav *wav)
{
    if (!wav) return;
    free(wav->samples);
    memset(wav, 0, sizeof(*wav));
}

size_t circle_voice_wav_to_mono_24k(const circle_wav *wav, int max_seconds,
                                    float *out, size_t out_capacity)
{
    if (!wav || wav->channels <= 0) return 0;

    size_t frames = wav->sample_count / (size_t)wav->channels;
    float *mono = (float *)malloc((frames ? frames : 1) * sizeof(float));
    if (!mono) return 0;

    if (wav->channels > 1) {
        for (size_t i = 0; i < frames; i++) {
            float sum = 0.0f;
            for (int c = 0; c < wav->channels; c++)
                sum += wav->samples[i * (size_t)wav->channels + (size_t)c];
            mono[i] = sum / (float)wav->channels;
        }
    } else {
        memcpy(mono, wav->samples, frames * sizeof(float));
    }

    float *result = mono;
    size_t count = frames;

    if (wav->rate != CIRCLE_VOICE_TARGET_RATE && frames > 0) {
        /* Linear resample. Adequate here: the target is a speaker embedding,
         * not playback. */
        long n = lround((double)frames * CIRCLE_VOICE_TARGET_RATE / (double)wav->rate);
        size_t rcount = (size_t)(n < 1 ? 1 : n);
        float *res = (float *)malloc(rcount * sizeof(float));
        if (!res) { free(mono); return 0; }
        double denom = (double)(rcount > 1 ? rcount - 1 : 1);
        double step = (double)(frames - 1) / denom;
        for (size_t i = 0; i < rcount; i++) {
            double x = (double)i * step;
            size_t lo = (size_t)x;
            size_t hi = lo + 1 < frames ? lo + 1 : frames - 1;
            res[i] = (float)((double)mono[lo]
                   + ((double)mono[hi] - (double)mono[lo]) * (x - (double)lo));
        }
        free(mono);
        result = res;
        count = rcount;
    }

    size_t cap = (size_t)max_seconds * CIRCLE_VOICE_TARGET_RATE;
    if (count > cap) count = cap;

    for (size_t i = 0; i < count && i < out_capacity; i++) out[i] = result[i];
    free(result);
    return count;
}

size_t circle_voice_wav_to_pcm16(const float *samples, size_t count, unsigned char *out)
{
    for (size_t i = 0; i < count; i++) {
        float s = samples[i];
        if (s > 1.0f) s = 1.0f;
        if (s < -1.0f) s = -1.0f;
        short v = (short)(s * 32767.0f);
        out[i * 2] = (unsigned char)((unsigned short)v & 0xFF);
        out[i * 2 + 1] = (unsigned char)(((unsigned short)v >> 8) & 0xFF);
    }
    return count * 2;
}
