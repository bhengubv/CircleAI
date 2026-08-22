/*
 * voice_xsampa.c — X-SAMPA → IPA, and SentencePiece unigram encoding.
 *
 * C port of src/CircleAI.Voice/XsampaToIpa.cs and SentencePieceUnigram.cs.
 * See voice_xsampa.h for the contract, and fixtures/voice_*.json for the
 * answers this must reproduce exactly.
 */

#include "circle_ai/voice_xsampa.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ------------------------------------------------------------------------ */
/* UTF-8                                                                     */
/* ------------------------------------------------------------------------ */

/*
 * Length in bytes of the UTF-8 sequence starting at `s`.
 *
 * A continuation byte or an invalid lead returns 1 so the caller always makes
 * progress — a zero here would spin forever on malformed input, and this code
 * runs on text that arrives from a model.
 */
static size_t utf8_seq_len(const char *s)
{
    unsigned char c = (unsigned char)*s;
    if (c < 0x80) return 1;
    if ((c & 0xE0) == 0xC0) return 2;
    if ((c & 0xF0) == 0xE0) return 3;
    if ((c & 0xF8) == 0xF0) return 4;
    return 1;
}

/* Count code points in a NUL-terminated UTF-8 string. */
static size_t utf8_count(const char *s)
{
    size_t n = 0;
    while (*s) { s += utf8_seq_len(s); n++; }
    return n;
}

static char *dup_range(const char *start, size_t len)
{
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    memcpy(out, start, len);
    out[len] = '\0';
    return out;
}

/* ------------------------------------------------------------------------ */
/* The phone table                                                           */
/* ------------------------------------------------------------------------ */

/*
 * Every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
 *
 * Derived from the corpus, not from memory: exactly the distinct phones in
 * nchlt_afr.dict, with every IPA character checked against the target voice's
 * own token table.
 *
 * Note "g" -> U+0261 LATIN SMALL LETTER SCRIPT G (UTF-8 C9 A1), NOT ASCII 'g'.
 * The voice's vocabulary carries ɡ; a plain 'g' would miss and be dropped.
 *
 * "h\\" is C for the X-SAMPA token h\, which is ɦ — the voiced glottal
 * fricative Afrikaans uses in "hond". This voice has no ɦ, only h, so voicing
 * is lost. It is the ONLY approximation in the table.
 */
static const struct { const char *xsampa; const char *ipa; } PHONES[] = {
    /* Vowels */
    {"a", "a"}, {"A:", "ɑː"}, {"A:r", "ɑːr"},
    {"E", "ɛ"}, {"O", "ɔ"}, {"@", "ə"},
    {"i", "i"}, {"u", "u"}, {"y", "y"},
    {"9", "œ"}, {"2:", "øː"}, {"{", "æ"},

    /* Diphthongs — NCHLT gives one token, the voice wants both elements. */
    {"9y", "œy"}, {"@i", "əi"}, {"@u", "əu"},
    {"i@", "iə"}, {"u@", "uə"},

    /* Consonants */
    {"b", "b"}, {"d", "d"}, {"f", "f"},
    {"g", "ɡ"},
    {"j", "j"}, {"k", "k"}, {"l", "l"},
    {"m", "m"}, {"n", "n"}, {"N", "ŋ"},
    {"p", "p"}, {"r", "r"}, {"s", "s"},
    {"S", "ʃ"}, {"t", "t"}, {"v", "v"},
    {"w", "w"}, {"x", "x"}, {"z", "z"},
    {"Z", "ʒ"},
    {"h\\", "h"},
};

static const size_t PHONE_COUNT = sizeof(PHONES) / sizeof(PHONES[0]);

static const char *lookup_phone(const char *xsampa)
{
    for (size_t i = 0; i < PHONE_COUNT; i++)
        if (strcmp(PHONES[i].xsampa, xsampa) == 0) return PHONES[i].ipa;
    return NULL;
}

static int is_blank(const char *s)
{
    for (; *s; s++)
        if (*s != ' ' && *s != '\t' && *s != '\n' && *s != '\r') return 0;
    return 1;
}

size_t circle_voice_xsampa_known_phone_count(void) { return PHONE_COUNT; }

const char *circle_voice_xsampa_known_phone(size_t index)
{
    return index < PHONE_COUNT ? PHONES[index].xsampa : NULL;
}

circle_voice_conversion circle_voice_xsampa_to_ipa(const char *const *xsampa, size_t count)
{
    circle_voice_conversion conv;
    memset(&conv, 0, sizeof(conv));
    if (count == 0) return conv;

    /* Worst case: every phone maps to a multi-code-point value. Four is beyond
     * anything in the table (the longest is A:r at three), so one allocation
     * suffices and no growth logic can get it wrong. */
    conv.ipa = (char **)calloc(count * 4 + 1, sizeof(char *));
    conv.unmapped = (char **)calloc(count + 1, sizeof(char *));
    if (!conv.ipa || !conv.unmapped) {
        circle_voice_conversion_free(&conv);
        return conv;
    }

    for (size_t i = 0; i < count; i++) {
        const char *phone = xsampa[i];
        if (!phone || is_blank(phone)) continue;

        const char *mapped = lookup_phone(phone);
        if (mapped) {
            /* Per CODE POINT: the voice tokenises ɑ, ː and r separately, so
             * "ɑːr" must arrive as three symbols, not one — and certainly not
             * as five raw bytes. */
            for (const char *p = mapped; *p; ) {
                size_t len = utf8_seq_len(p);
                char *sym = dup_range(p, len);
                if (!sym) break;
                conv.ipa[conv.ipa_count++] = sym;
                p += len;
            }
            continue;
        }

        int seen = 0;
        for (size_t u = 0; u < conv.unmapped_count; u++)
            if (strcmp(conv.unmapped[u], phone) == 0) { seen = 1; break; }
        if (!seen) {
            char *copy = dup_range(phone, strlen(phone));
            if (copy) conv.unmapped[conv.unmapped_count++] = copy;
        }
    }

    return conv;
}

void circle_voice_conversion_free(circle_voice_conversion *conv)
{
    if (!conv) return;
    if (conv->ipa) {
        for (size_t i = 0; i < conv->ipa_count; i++) free(conv->ipa[i]);
        free(conv->ipa);
    }
    if (conv->unmapped) {
        for (size_t i = 0; i < conv->unmapped_count; i++) free(conv->unmapped[i]);
        free(conv->unmapped);
    }
    memset(conv, 0, sizeof(*conv));
}

int circle_voice_xsampa_can_say_all(const char *const *xsampa, size_t count)
{
    for (size_t i = 0; i < count; i++) {
        const char *p = xsampa[i];
        if (!p || is_blank(p)) continue;
        if (!lookup_phone(p)) return 0;
    }
    return 1;
}

/* ------------------------------------------------------------------------ */
/* SentencePiece unigram                                                     */
/* ------------------------------------------------------------------------ */

/*
 * Cost charged for falling back to raw bytes.
 *
 * Any finite penalty works, because fallback only ever competes with "no path
 * at all". It must be worse than a real piece so the lattice never prefers it
 * where a piece exists, and finite so a path always exists.
 */
#define FALLBACK_PENALTY 10.0f
#define UNREACHABLE (-1e18f)

struct circle_voice_sp {
    const circle_voice_sp_entry *entries;
    size_t count;
    size_t max_piece_codepoints;
};

circle_voice_sp *circle_voice_sp_new(const circle_voice_sp_entry *entries, size_t count)
{
    circle_voice_sp *sp = (circle_voice_sp *)calloc(1, sizeof(circle_voice_sp));
    if (!sp) return NULL;
    sp->entries = entries;
    sp->count = count;
    sp->max_piece_codepoints = 1;
    for (size_t i = 0; i < count; i++) {
        size_t n = utf8_count(entries[i].piece);
        if (n > sp->max_piece_codepoints) sp->max_piece_codepoints = n;
    }
    return sp;
}

void circle_voice_sp_free(circle_voice_sp *sp) { free(sp); }

static const circle_voice_sp_entry *sp_find(const circle_voice_sp *sp,
                                            const char *piece, size_t len)
{
    for (size_t i = 0; i < sp->count; i++) {
        const char *p = sp->entries[i].piece;
        if (strlen(p) == len && memcmp(p, piece, len) == 0) return &sp->entries[i];
    }
    return NULL;
}

size_t circle_voice_sp_encode(const circle_voice_sp *sp, const char *text,
                              int *out_ids, size_t out_capacity)
{
    if (!sp || !text || !*text) return 0;

    /* SentencePiece's own normalisation: spaces become U+2581, with one
     * prepended so the first word is marked word-initial too.
     *
     * NFKC IS NOT APPLIED. C has no Unicode normaliser in the standard library
     * and this port will not vendor one for a step no fixture exercises. On
     * already-normalised input this is byte-identical to the reference; on
     * denormalised input it can differ. Recorded here rather than hidden.
     */
    static const char SEP[] = "▁";
    const size_t sep_len = sizeof(SEP) - 1;

    size_t text_len = strlen(text);
    size_t buf_cap = text_len * sep_len + sep_len + 1;
    char *norm = (char *)malloc(buf_cap);
    if (!norm) return 0;

    size_t w = 0;
    memcpy(norm + w, SEP, sep_len); w += sep_len;
    for (size_t i = 0; i < text_len; i++) {
        if (text[i] == ' ') { memcpy(norm + w, SEP, sep_len); w += sep_len; }
        else norm[w++] = text[i];
    }
    norm[w] = '\0';

    /* Byte offset of each code point boundary, so the lattice indexes code
     * points while the pieces stay plain byte ranges. */
    size_t n = utf8_count(norm);
    size_t *off = (size_t *)malloc((n + 1) * sizeof(size_t));
    if (!off) { free(norm); return 0; }
    {
        size_t k = 0, b = 0;
        while (norm[b]) { off[k++] = b; b += utf8_seq_len(norm + b); }
        off[n] = w;
    }

    float *best = (float *)malloc((n + 1) * sizeof(float));
    size_t *from = (size_t *)calloc(n + 1, sizeof(size_t));
    int *piece_id = (int *)malloc((n + 1) * sizeof(int));
    int *has_piece = (int *)calloc(n + 1, sizeof(int));
    if (!best || !from || !piece_id || !has_piece) {
        free(norm); free(off); free(best); free(from); free(piece_id); free(has_piece);
        return 0;
    }
    for (size_t i = 0; i <= n; i++) { best[i] = UNREACHABLE; piece_id[i] = -1; }
    best[0] = 0.0f;

    for (size_t i = 0; i < n; i++) {
        if (best[i] <= UNREACHABLE / 2.0f) continue;

        size_t limit = sp->max_piece_codepoints;
        if (n - i < limit) limit = n - i;
        for (size_t len = 1; len <= limit; len++) {
            const char *start = norm + off[i];
            size_t blen = off[i + len] - off[i];
            const circle_voice_sp_entry *e = sp_find(sp, start, blen);
            if (!e) continue;
            float score = best[i] + e->score;
            if (score > best[i + len]) {
                best[i + len] = score;
                from[i + len] = i;
                piece_id[i + len] = e->id;
                has_piece[i + len] = 1;
            }
        }

        /* Byte fallback for this ONE code point, so no input is ever silent. */
        size_t end = i + 1;
        float fallback = best[i] - FALLBACK_PENALTY;
        if (fallback > best[end]) {
            best[end] = fallback;
            from[end] = i;
            has_piece[end] = 0;
        }
    }

    /* Backtrack into a scratch buffer, then reverse. */
    int *rev = (int *)malloc((n * 4 + 1) * sizeof(int));
    size_t rev_count = 0;
    if (rev) {
        size_t i = n;
        while (i > 0) {
            size_t start = from[i];
            if (has_piece[i]) {
                rev[rev_count++] = piece_id[i];
            } else {
                /* BACKWARDS, because this whole list is built backwards. The
                 * lattice is walked from the end and reversed at the bottom, so
                 * bytes pushed in forward order come out byte-reversed: é is
                 * UTF-8 C3 A9 and would be emitted A9 C3. Nothing crashes —
                 * those are real pieces with real ids — so the model simply
                 * says a different character. */
                size_t b_end = off[i], b_start = off[start];
                for (size_t b = b_end; b > b_start; b--) {
                    unsigned char byte = (unsigned char)norm[b - 1];
                    char key[9];
                    snprintf(key, sizeof(key), "<0x%02X>", byte);
                    const circle_voice_sp_entry *e = sp_find(sp, key, strlen(key));
                    if (e) rev[rev_count++] = e->id;
                }
            }
            i = start;
        }

        for (size_t k = 0; k < rev_count && k < out_capacity; k++)
            out_ids[k] = rev[rev_count - 1 - k];
    }

    size_t result = rev_count;
    free(rev); free(norm); free(off); free(best); free(from); free(piece_id); free(has_piece);
    return result;
}
