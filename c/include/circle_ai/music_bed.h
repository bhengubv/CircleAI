#ifndef CIRCLE_AI_MUSIC_BED_H
#define CIRCLE_AI_MUSIC_BED_H

/*
 * music_bed.h - CircleAI.Music (C11): background music, generated on the
 * device.
 *
 * A "bed" is music under something else - under a hold message, a video, a
 * meditation. It is not the point of the thing it is under, which is the entire
 * design constraint: it has to be unobtrusive, the right length exactly, and
 * available with no network and no download.
 *
 * THE PROCEDURAL BACKEND IS ALWAYS AVAILABLE AND IS THE DEFAULT. A neural music
 * model is hundreds of megabytes for something that plays under a voice. The
 * procedural synthesiser is arithmetic - no model, no download, works on the
 * cheapest phone this ships to - and for a bed it is genuinely good enough.
 * The neural backend exists for when somebody wants the music to be the point.
 *
 * MUSIC IS FULLY-FREE OR IT IS NOT HERE. Nothing generated needs a licence,
 * nothing is sampled from a recording, and there is no rights-holder to
 * negotiate with later. Procedural generation is not only the cheap option, it
 * is the one that stays free.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- pitch ---------------------------------------------------------------- */

/*
 * The twelve pitch classes, C = 0.
 *
 * Numbered so arithmetic on them is modulo 12 and transposition is addition.
 * Sharps only: F sharp and G flat are the same pitch class here, and carrying
 * both spellings would mean two names for one number with no musical
 * difference in a synthesiser that has no notion of key signature.
 */
typedef enum {
    CA_PITCH_C = 0,
    CA_PITCH_C_SHARP,
    CA_PITCH_D,
    CA_PITCH_D_SHARP,
    CA_PITCH_E,
    CA_PITCH_F,
    CA_PITCH_F_SHARP,
    CA_PITCH_G,
    CA_PITCH_G_SHARP,
    CA_PITCH_A,
    CA_PITCH_A_SHARP,
    CA_PITCH_B
} ca_pitch_class_t;

const char *ca_pitch_class_name(ca_pitch_class_t pitch);

/* A4 = 440 Hz, twelve-tone equal temperament. `octave` is scientific pitch
 * notation, so middle C is C4. */
double ca_pitch_class_frequency(ca_pitch_class_t pitch, int octave);

typedef enum {
    CA_SCALE_MAJOR = 0,
    CA_SCALE_NATURAL_MINOR,
    CA_SCALE_HARMONIC_MINOR,
    CA_SCALE_DORIAN,
    CA_SCALE_MIXOLYDIAN,
    /* Five notes, no semitones. The safest scale there is for a bed: any two
     * notes played together sound intentional, so a generator that picks badly
     * still does not sound wrong. */
    CA_SCALE_PENTATONIC,
    CA_SCALE_BLUES
} ca_scale_t;

const char *ca_scale_name(ca_scale_t scale);

typedef struct {
    ca_pitch_class_t root;
    ca_scale_t scale;
} ca_musical_key_t;

/* Semitone offsets from the root. Writes into `out` and returns the count;
 * `out` must hold at least 7. */
size_t ca_musical_key_degrees(ca_musical_key_t key, int *out);

/* Whether a pitch class is in the key. What keeps a procedural line from
 * wandering outside it, which is the one thing that makes generated music
 * immediately identifiable as generated. */
bool ca_musical_key_contains(ca_musical_key_t key, ca_pitch_class_t pitch);

/* -- what to make --------------------------------------------------------- */

typedef enum {
    CA_MOOD_CALM = 0,
    CA_MOOD_WARM,
    CA_MOOD_BRIGHT,
    CA_MOOD_TENSE,
    CA_MOOD_SOMBRE,
    CA_MOOD_PLAYFUL
} ca_mood_t;

const char *ca_mood_name(ca_mood_t mood);

typedef struct {
    ca_mood_t mood;
    int tempo_bpm;
    /* EXACT. A bed is under something of a known length, and music that runs
     * three seconds long has to be faded out mid-phrase - which is audible and
     * reads as a mistake. The generator ends on a bar boundary at or before
     * this, then holds. */
    int64_t duration_ms;
    ca_musical_key_t key;
} ca_music_spec_t;

ca_music_spec_t ca_music_spec_default(ca_mood_t mood, int64_t duration_ms);

/* -- audio ---------------------------------------------------------------- */

typedef struct {
    int sample_rate;
    int channels;
    int bits_per_sample;
} ca_audio_pcm_format_t;

ca_audio_pcm_format_t ca_audio_pcm_format_default(void);

size_t ca_audio_pcm_format_bytes_per_frame(ca_audio_pcm_format_t format);
size_t ca_audio_pcm_format_bytes_for_ms(ca_audio_pcm_format_t format, int64_t ms);

typedef struct {
    uint8_t *pcm;
    size_t len;
    ca_audio_pcm_format_t format;
    ca_music_spec_t spec;
    /* Which backend produced it. Recorded because a bed that came from a
     * downloaded model and one that was synthesised are different things to
     * cache, to attribute, and to reproduce. */
    char *backend_id;
} ca_music_bed_t;

void ca_music_bed_free(ca_music_bed_t *bed);

/* -- generators ----------------------------------------------------------- */

typedef enum {
    /* Pure arithmetic. Always available, zero dependencies. */
    CA_MUSIC_BED_BACKEND_PROCEDURAL = 0,
    /* A downloaded neural music model selected from the catalogue. */
    CA_MUSIC_BED_BACKEND_NEURAL
} ca_music_bed_backend_t;

const char *ca_music_bed_backend_name(ca_music_bed_backend_t backend);

typedef struct ca_music_bed_generator {
    void *state;
    const char *(*backend_id)(void *state);
    bool (*is_available)(void *state);
    /* Caller frees. */
    ca_music_bed_t *(*generate)(void *state, const ca_music_spec_t *spec);
    void (*free_fn)(void *state);
} ca_music_bed_generator_t;

void ca_music_bed_generator_free(ca_music_bed_generator_t *generator);

/* Generates silence of exactly the right length. Silence rather than NULL: a
 * caller mixing a bed under a voice should get a track that is quiet, not one
 * that is missing and has to be branched around at every call site. */
ca_music_bed_generator_t *ca_null_music_bed_generator_new(void);

/*
 * The procedural synthesiser.
 *
 * A pad, a bass line on the root and fifth, and a sparse melody constrained to
 * the key. Envelopes are long and the attack is soft, because a bed with
 * transients pulls attention - which is exactly what a bed must not do.
 *
 * DETERMINISTIC FOR A GIVEN SEED AND SPEC. The same request produces the same
 * bytes, on every platform and in every port, which is what makes it cacheable
 * and testable at all.
 */
ca_music_bed_generator_t *ca_procedural_music_bed_generator_new(uint64_t seed);

typedef struct ca_music_bed_generator_resolver ca_music_bed_generator_resolver_t;

/* Picks a backend, falling back to procedural whenever the neural one is
 * absent, too large for the device, or would need a download on a metered
 * link. Falling back is the NORMAL path, not the error path. */
ca_music_bed_generator_resolver_t *ca_music_bed_generator_resolver_new(void);
void ca_music_bed_generator_resolver_free(ca_music_bed_generator_resolver_t *resolver);

bool ca_music_bed_generator_resolver_register(ca_music_bed_generator_resolver_t *resolver,
                                              ca_music_bed_generator_t *generator);

/* Borrowed; never NULL - the procedural generator is always available. */
ca_music_bed_generator_t *ca_music_bed_generator_resolver_resolve(
    ca_music_bed_generator_resolver_t *resolver, ca_music_bed_backend_t preferred);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MUSIC_BED_H */
