/*
 * voice_piper.h — Piper phoneme→id mapping, lexicon tokenising, and the PCM
 * audio format.
 *
 * C ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs and
 * AudioFormat.cs.
 *
 * Parity is asserted against fixtures/voice_piper_config.json,
 * fixtures/voice_lexicon_tokeniser.json and fixtures/voice_audio_format.json.
 */

#ifndef CIRCLE_AI_VOICE_PIPER_H
#define CIRCLE_AI_VOICE_PIPER_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── AudioFormat ─────────────────────────────────────────────────────────── */

/** A PCM audio format expected or produced by voice components. */
typedef struct {
    int sample_rate;
    int channels;
    int bits_per_sample;
} circle_voice_audio_format;

/**
 * Canonical input format: PCM signed 16-bit, mono, 16 kHz. Most open-source ASR
 * engines (sherpa-onnx, Vosk) accept this directly.
 */
circle_voice_audio_format circle_voice_pcm16_mono_16k(void);

/* ── PiperVoiceConfig ────────────────────────────────────────────────────── */

/** One vocabulary entry: a symbol and the ids it maps to. */
typedef struct {
    const char *symbol;
    const long long *ids;
    size_t id_count;
} circle_voice_phoneme_entry;

/** A Piper-layout voice's phoneme→id vocabulary. Entries are BORROWED. */
typedef struct {
    const circle_voice_phoneme_entry *entries;
    size_t entry_count;
} circle_voice_piper_config;

/**
 * THE PAD RULE: the id THIS voice uses for blank.
 *
 * It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing it at
 * an ordinary vocabulary entry is what made 42 MMS voices speak fluent nonsense.
 * Never assume a constant — read it from the model. Falls back to 0 only when
 * the vocabulary has no "_" at all.
 */
long long circle_voice_pad_id(const circle_voice_piper_config *cfg);

/** True when this config has a usable phoneme→id map. */
int circle_voice_has_phoneme_map(const circle_voice_piper_config *cfg);

/** What a circle_voice_phonemes_to_ids call did, beyond the ids. */
typedef struct {
    /** How many symbols the vocabulary had no entry for. */
    size_t skipped;
    /**
     * WHICH symbols were dropped, and which were APPROXIMATED. Both are
     * heap-allocated; release with circle_voice_mapping_free. A dropped symbol
     * is inaudible, so these lists are the only evidence a front-end is broken,
     * and an approximation is a compromise rather than a success.
     */
    char **skipped_symbols;
    size_t skipped_symbol_count;
    char **approximated_symbols;
    size_t approximated_symbol_count;
} circle_voice_mapping;

/**
 * Turn a phoneme sequence into model token ids, in piper-phonemize's exact
 * layout with interspersed pad:
 *
 *   [BOS, PAD, id(p1), PAD, id(p2), PAD, ..., id(pN), PAD, EOS]
 *
 * BOS and EOS appear only when the vocabulary HAS them — the MMS-family exports
 * do not. Unknown symbols are SKIPPED and REPORTED, never fatal.
 *
 * Writes at most out_capacity ids and returns how many it WOULD have written.
 */
size_t circle_voice_phonemes_to_ids(const circle_voice_piper_config *cfg,
                                    const char *const *phonemes, size_t phoneme_count,
                                    long long *out_ids, size_t out_capacity,
                                    circle_voice_mapping *out_mapping);

void circle_voice_mapping_free(circle_voice_mapping *m);

/**
 * Split UTF-8 text into GRAPHEME CLUSTERS, not codepoints.
 *
 * "กัb" is three codepoints but two written units, and a vocabulary keyed on
 * written units matches nothing when the mark arrives on its own. Each element
 * is a base character plus the combining marks that belong to it.
 *
 * Elements are heap-allocated; release with circle_voice_string_list_free.
 */
size_t circle_voice_split_phoneme_string(const char *text, char ***out_elements);

/** Free a heap-allocated string list (elements and the array itself). */
void circle_voice_string_list_free(char **list, size_t count);

/* ── LexiconTokeniser ────────────────────────────────────────────────────── */

typedef struct circle_voice_lexicon circle_voice_lexicon;

/**
 * Build from a voice's tokens.txt and lexicon.txt content. Returns NULL when
 * either is unusable — absence is the normal case for most voices.
 */
circle_voice_lexicon *circle_voice_lexicon_new(const char *tokens_text,
                                               const char *lexicon_text,
                                               long long blank);

void circle_voice_lexicon_free(circle_voice_lexicon *lex);

/**
 * Segment UTF-8 text and return the model's tokens.
 *
 * LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
 * overlap: taking the shortest would pronounce a different word.
 *
 * Writes at most out_capacity ids and returns how many it WOULD have written.
 */
size_t circle_voice_lexicon_encode(circle_voice_lexicon *lex, const char *text,
                                   int interleave_blank,
                                   long long *out_ids, size_t out_capacity);

/** Symbols the lexicon had no entry for on the last encode. Borrowed. */
size_t circle_voice_lexicon_unmapped(const circle_voice_lexicon *lex,
                                     const char *const **out_symbols);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_PIPER_H */
