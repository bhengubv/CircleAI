/*
 * voice_xsampa.h — X-SAMPA → IPA, and SentencePiece unigram encoding.
 *
 * C port of src/CircleAI.Voice/XsampaToIpa.cs and SentencePieceUnigram.cs.
 *
 * Parity is asserted against fixtures/voice_xsampa_to_ipa.json and
 * fixtures/voice_sentencepiece_unigram.json, which the C# reference generates.
 *
 * EVERYTHING HERE WORKS IN UTF-8 BYTES AND CODE POINTS, never in "characters".
 * IPA is outside ASCII throughout — ɑ, ː, ŋ, ʒ and the script-g ɡ are all
 * multi-byte — so a byte-indexed loop splits them into meaningless fragments.
 */

#ifndef CIRCLE_AI_VOICE_XSAMPA_H
#define CIRCLE_AI_VOICE_XSAMPA_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Result of a conversion. Both arrays are heap-allocated NUL-terminated UTF-8
 * strings; free with circle_voice_conversion_free.
 *
 * THE MISSES COME BACK WITH THE RESULT. An unmapped phone produces NO SOUND and
 * the audio is merely shorter — every acoustic measure still passes — so a
 * caller that cannot see the misses cannot refuse.
 */
typedef struct {
    char **ipa;
    size_t ipa_count;
    char **unmapped;
    size_t unmapped_count;
} circle_voice_conversion;

/*
 * Convert X-SAMPA phone tokens to a flat IPA symbol list.
 *
 * LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character (A:r, @i,
 * 9y) and NCHLT emits them as single tokens; matching on the token — never
 * character by character — is what keeps A:r from becoming A + : + r.
 *
 * Each emitted IPA element is ONE CODE POINT, because the voice tokenises
 * ɑ, ː and r separately.
 */
circle_voice_conversion circle_voice_xsampa_to_ipa(const char *const *xsampa, size_t count);

/* Release everything in a conversion. Safe on a zeroed struct. */
void circle_voice_conversion_free(circle_voice_conversion *conv);

/* True when every phone has a mapping. */
int circle_voice_xsampa_can_say_all(const char *const *xsampa, size_t count);

/* Number of phones the table knows (38). */
size_t circle_voice_xsampa_known_phone_count(void);

/* The i'th known phone, or NULL when out of range. Not owned by the caller. */
const char *circle_voice_xsampa_known_phone(size_t index);

/* ------------------------------------------------------------------------ */
/* SentencePiece unigram                                                     */
/* ------------------------------------------------------------------------ */

/* One vocabulary entry: piece, id, and unigram log-probability. */
typedef struct {
    const char *piece;
    int id;
    float score;
} circle_voice_sp_entry;

typedef struct circle_voice_sp circle_voice_sp;

/*
 * Build a tokeniser over `entries`. The entries are BORROWED — they must
 * outlive the tokeniser.
 */
circle_voice_sp *circle_voice_sp_new(const circle_voice_sp_entry *entries, size_t count);

void circle_voice_sp_free(circle_voice_sp *sp);

/*
 * Encode UTF-8 `text` to token ids.
 *
 * VITERBI, NOT GREEDY LONGEST-MATCH. Unigram scores are not monotone in piece
 * length — a long piece can score worse than the two short pieces covering the
 * same span — so greedy silently produces plausible-but-wrong segmentations.
 *
 * Writes at most `out_capacity` ids and returns how many it WOULD have written,
 * so a caller can size a buffer by calling with capacity 0. Returns 0 for empty
 * input.
 */
size_t circle_voice_sp_encode(const circle_voice_sp *sp, const char *text,
                              int *out_ids, size_t out_capacity);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_XSAMPA_H */
