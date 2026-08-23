/*
 * voice_text.h — the five text-side voice modules.
 *
 * C ports of src/CircleAI.Voice/SentenceSplitter.cs, LanguageSpanSplitter.cs,
 * GeezRomanizer.cs, ToneShaper.cs and NchltPhonemizer.cs.
 *
 * Parity is asserted against fixtures/voice_sentence_splitter.json,
 * voice_language_spans.json, voice_geez_romanizer.json, voice_tone_shaper.json
 * and voice_nchlt_phonemizer.json.
 *
 * All strings in and out are UTF-8. Where the reference indexes UTF-16 code
 * units, this port converts and indexes units too, so the two agree on where a
 * length-driven cut lands.
 */

#ifndef CIRCLE_AI_VOICE_TEXT_H
#define CIRCLE_AI_VOICE_TEXT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── SentenceSplitter ────────────────────────────────────────────────────── */

/**
 * One unit of speech, plus the silence that should follow it.
 *
 * The voices here were trained on text with the punctuation stripped out, so
 * their vocabularies hold no '.', ',', '?' or ':' at all. A paragraph fed in one
 * pass therefore comes back as one unbroken run of speech — there is no token
 * that could encode a pause. THE PAUSE HAS TO COME FROM OUTSIDE THE MODEL.
 */
typedef struct {
    /** UTF-8 text to synthesise. Never empty or whitespace. Owned. */
    char *text;
    /**
     * Silence to append after this segment, in milliseconds. 0 for the final
     * segment — trailing silence at the end of a passage serves nothing.
     */
    int trailing_pause_ms;
} circle_speech_segment;

/**
 * Beyond this many UTF-16 units a segment is cut even without punctuation. A
 * single unbroken clause of this size is already several seconds of audio, and
 * on a phone the whole segment must render before ANY of it can play.
 */
#define CIRCLE_MAX_CHARS_PER_SEGMENT 220

/**
 * Split text into sentence-sized units.
 *
 * Splits at SENTENCE boundaries only, never at commas: a VITS model ends every
 * utterance with falling, sentence-final prosody, so cutting at a comma makes
 * each clause land like a finished sentence — worse than the run-on it was
 * meant to fix.
 *
 * Writes at most out_capacity segments and returns how many it WOULD have
 * written. Release with circle_speech_segments_free.
 */
size_t circle_split_sentences(const char *text,
                              circle_speech_segment *out, size_t out_capacity);

void circle_speech_segments_free(circle_speech_segment *segments, size_t count);

/* ── LanguageSpanSplitter ────────────────────────────────────────────────── */

/**
 * A run of text to be spoken in one language.
 *
 * A multi-lingual model takes ONE language id per utterance, so mixed text has
 * to be cut where the language changes: read wholly in isiZulu, an embedded
 * English name comes out mangled, and the listener hears the machine fail at a
 * word they know perfectly well.
 */
typedef struct {
    /** UTF-8 words, with their spacing preserved. Owned. */
    char *text;
    /** 1 when this run is the embedded language (English), 0 for the host one. */
    int is_foreign;
} circle_language_span;

/**
 * Split text into language runs. Returns 1 span for single-language text, which
 * is the overwhelmingly common case.
 *
 * Writes at most out_capacity spans and returns how many it WOULD have written.
 * Release with circle_language_spans_free.
 */
size_t circle_split_language_spans(const char *text,
                                   circle_language_span *out, size_t out_capacity);

void circle_language_spans_free(circle_language_span *spans, size_t count);

/**
 * Is this token unmistakably foreign (English) inside African-language text?
 *
 * Two signals only, both chosen because native orthographies do not produce
 * them: internal capitals (CircleAI, WhatsApp) and short all-caps runs (GPS,
 * SMS). It does NOT guess at ordinary lowercase words — that needs a lexicon per
 * language pair, and mispronouncing a native word to "fix" a foreign one insults
 * the speaker in their own language.
 */
int circle_is_foreign_word(const char *word);

/**
 * Rewrite a run into the form a voice can actually pronounce, without changing
 * what is displayed: a compound is split at case boundaries and acronyms are
 * given full stops so they read as letters rather than as a word.
 *
 * Returns a heap-allocated UTF-8 string; release with free().
 */
char *circle_to_spoken_form(const char *text);

/* ── GeezRomanizer ───────────────────────────────────────────────────────── */

/** True when text contains any Ethiopic character. */
int circle_is_ethiopic(const char *text);

/**
 * Ethiopic (Ge'ez) -> Latin, because the Amharic and Tigrinya voices are
 * is_uroman:true and hold 27-28 plain LATIN letters — they have never seen an
 * Ethiopic codepoint. Characters outside the script pass through untouched.
 *
 * Returns a heap-allocated UTF-8 string; release with free().
 */
char *circle_geez_romanize(const char *text);

/* ── ToneShaper ──────────────────────────────────────────────────────────── */

/** Biquad coefficients, already normalised by a0. */
typedef struct {
    double b[3];
    double a[3];
} circle_biquad_coefficients;

typedef struct {
    double low_shelf_hz;   /**< where the low shelf starts lifting */
    double low_shelf_db;   /**< how much to lift the bottom */
    double presence_hz;    /**< centre of the harshness dip */
    double presence_db;    /**< how much to cut there; negative cuts */
    double presence_q;     /**< width of the dip; lower is wider */
} circle_tone_shaper_settings;

/** The measured setting: warmer, with no cost to intelligibility. */
circle_tone_shaper_settings circle_tone_shaper_warm(void);

/** RBJ audio-cookbook low shelf, normalised by a0. */
circle_biquad_coefficients circle_low_shelf_coefficients(
    const circle_tone_shaper_settings *s, int rate);

/** RBJ audio-cookbook peaking EQ, normalised by a0. */
circle_biquad_coefficients circle_peaking_coefficients(
    const circle_tone_shaper_settings *s, int rate);

/**
 * Direct-form-I biquad, in place.
 *
 * THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
 * The filter memory never sees the float rounding, so the recursion is identical
 * everywhere; only what lands in the buffer is narrowed, which is what the next
 * stage then reads.
 */
void circle_biquad(float *x, size_t n, const circle_biquad_coefficients *c);

/**
 * Filter a waveform with a low shelf and a presence dip in series.
 *
 * PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a waveform
 * already near full scale would clip — heard as crackle and blamed on the
 * quantised model rather than on this.
 */
void circle_apply_tone_shaper(float *waveform, size_t n, int sample_rate,
                              const circle_tone_shaper_settings *s);

/* ── NchltPhonemizer ─────────────────────────────────────────────────────── */

typedef struct circle_nchlt_phonemizer circle_nchlt_phonemizer;

/**
 * Build from the file CONTENTS rather than paths, so a caller can load from an
 * embedded resource with no filesystem in reach. graph_map_text and gnulls_text
 * may be NULL. Returns NULL when the dictionary or rules are unusable.
 */
circle_nchlt_phonemizer *circle_nchlt_new(const char *dict_text,
                                          const char *rules_text,
                                          const char *phone_map_text,
                                          const char *graph_map_text,
                                          const char *gnulls_text);

void circle_nchlt_free(circle_nchlt_phonemizer *p);

/**
 * Turn text into the model's X-SAMPA phones. A word is either in the dictionary
 * (exact) or synthesised by the rules — there is no OOV gap, which is what makes
 * agglutinative isiZulu tractable.
 *
 * Writes at most out_capacity phones (borrowed, valid until the next call on
 * this phonemizer) and returns how many it WOULD have written.
 */
size_t circle_nchlt_phonemize(circle_nchlt_phonemizer *p, const char *text,
                              const char **out, size_t out_capacity);

/**
 * Predict one word from the rules alone, bypassing the dictionary.
 *
 * Does NOT clear the unknown-grapheme list, matching the reference: phonemize
 * owns the reset, so a direct call accumulates rather than hiding what an
 * earlier word already reported.
 */
size_t circle_nchlt_predict_word(circle_nchlt_phonemizer *p, const char *word,
                                 const char **out, size_t out_capacity);

/**
 * Words in the last phonemize call synthesised by the rules rather than found in
 * the dictionary. A coverage diagnostic, never a failure.
 */
size_t circle_nchlt_last_rule_predicted_words(const circle_nchlt_phonemizer *p);

/** Graphemes no rule covered on the last call. Skipped, never guessed. Borrowed. */
size_t circle_nchlt_last_unknown_graphemes(const circle_nchlt_phonemizer *p,
                                           const char *const **out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_TEXT_H */
