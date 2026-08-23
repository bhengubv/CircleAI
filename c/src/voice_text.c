/*
 * voice_text.c — the five text-side voice modules.
 *
 * C ports of src/CircleAI.Voice/SentenceSplitter.cs, LanguageSpanSplitter.cs,
 * GeezRomanizer.cs, ToneShaper.cs and NchltPhonemizer.cs. See voice_text.h for
 * the contract.
 */

#include "circle_ai/voice_text.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

/* ── UTF-8 / UTF-16 ──────────────────────────────────────────────────────── */

static size_t u8_len(const char *s)
{
    unsigned char c = (unsigned char)*s;
    if (c < 0x80) return 1;
    if ((c & 0xE0) == 0xC0) return 2;
    if ((c & 0xF0) == 0xE0) return 3;
    if ((c & 0xF8) == 0xF0) return 4;
    return 1;   /* never 0: a bad lead must still make progress */
}

static unsigned int u8_cp(const char *s, size_t len)
{
    unsigned char c = (unsigned char)s[0];
    if (len == 1) return c;
    if (len == 2) return ((c & 0x1Fu) << 6) | ((unsigned char)s[1] & 0x3Fu);
    if (len == 3) return ((c & 0x0Fu) << 12) | (((unsigned char)s[1] & 0x3Fu) << 6)
                       | ((unsigned char)s[2] & 0x3Fu);
    return ((c & 0x07u) << 18) | (((unsigned char)s[1] & 0x3Fu) << 12)
         | (((unsigned char)s[2] & 0x3Fu) << 6) | ((unsigned char)s[3] & 0x3Fu);
}

static size_t u8_encode(unsigned int cp, char *out)
{
    if (cp < 0x80) { out[0] = (char)cp; return 1; }
    if (cp < 0x800) {
        out[0] = (char)(0xC0 | (cp >> 6));
        out[1] = (char)(0x80 | (cp & 0x3F));
        return 2;
    }
    if (cp < 0x10000) {
        out[0] = (char)(0xE0 | (cp >> 12));
        out[1] = (char)(0x80 | ((cp >> 6) & 0x3F));
        out[2] = (char)(0x80 | (cp & 0x3F));
        return 3;
    }
    out[0] = (char)(0xF0 | (cp >> 18));
    out[1] = (char)(0x80 | ((cp >> 12) & 0x3F));
    out[2] = (char)(0x80 | ((cp >> 6) & 0x3F));
    out[3] = (char)(0x80 | (cp & 0x3F));
    return 4;
}

/*
 * UTF-16 code units, because the reference walks a C# string.
 *
 * Every terminator in the table is in the BMP, so the two agree on WHERE the
 * splits fall — but the over-long cut counts units, and a port that counted
 * codepoints or bytes would break emoji-bearing text in a different place.
 */
static unsigned short *to_utf16(const char *s, size_t *out_count)
{
    size_t cap = strlen(s) + 1, n = 0;
    unsigned short *units = (unsigned short *)malloc(cap * sizeof(unsigned short));
    if (!units) { *out_count = 0; return NULL; }

    for (const char *p = s; *p; ) {
        size_t len = u8_len(p);
        unsigned int cp = u8_cp(p, len);
        p += len;
        if (cp < 0x10000) {
            units[n++] = (unsigned short)cp;
        } else {
            cp -= 0x10000;
            units[n++] = (unsigned short)(0xD800 + (cp >> 10));
            units[n++] = (unsigned short)(0xDC00 + (cp & 0x3FF));
        }
    }
    *out_count = n;
    return units;
}

static char *from_utf16(const unsigned short *units, size_t n)
{
    char *out = (char *)malloc(n * 4 + 1);
    if (!out) return NULL;
    size_t w = 0;
    for (size_t i = 0; i < n; i++) {
        unsigned int cp = units[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < n
            && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (units[i + 1] - 0xDC00);
            i++;
        }
        w += u8_encode(cp, out + w);
    }
    out[w] = '\0';
    return out;
}

/* ── Character classes ───────────────────────────────────────────────────── */

/*
 * Unicode letter/digit, digit, whitespace, upper and lower — approximated by the
 * ranges this catalogue actually meets.
 *
 * NARROWER THAN THE REFERENCE ON PURPOSE. Linking a full Unicode database into
 * this port for five predicates would be a heavier dependency than the port is
 * worth, so the tables cover Latin, Greek, Cyrillic, the Indic and Arabic
 * blocks, CJK, Ethiopic, Khmer and Myanmar. Anything outside them is treated as
 * a separator, which is the safe direction: text splits more, never less, and no
 * word is silently merged with its neighbour.
 */

static int is_ws(unsigned int cp)
{
    return cp == ' ' || cp == '\t' || cp == '\n' || cp == '\r' || cp == '\v'
        || cp == '\f' || cp == 0x00A0 || cp == 0x2028 || cp == 0x2029
        || (cp >= 0x2000 && cp <= 0x200A) || cp == 0x3000;
}

static int is_digit_cp(unsigned int cp)
{
    return (cp >= '0' && cp <= '9')
        || (cp >= 0x0660 && cp <= 0x0669)   /* Arabic-Indic */
        || (cp >= 0x06F0 && cp <= 0x06F9)   /* Extended Arabic-Indic */
        || (cp >= 0x0966 && cp <= 0x096F)   /* Devanagari */
        || (cp >= 0x09E6 && cp <= 0x09EF)   /* Bengali */
        || (cp >= 0x17E0 && cp <= 0x17E9)   /* Khmer */
        || (cp >= 0x1040 && cp <= 0x1049)   /* Myanmar */
        || (cp >= 0xFF10 && cp <= 0xFF19);  /* fullwidth */
}

static int is_upper_cp(unsigned int cp)
{
    return (cp >= 'A' && cp <= 'Z')
        || (cp >= 0x00C0 && cp <= 0x00DE && cp != 0x00D7)
        || (cp >= 0x0391 && cp <= 0x03A9)   /* Greek */
        || (cp >= 0x0410 && cp <= 0x042F);  /* Cyrillic */
}

static int is_lower_cp(unsigned int cp)
{
    return (cp >= 'a' && cp <= 'z')
        || (cp >= 0x00DF && cp <= 0x00FF && cp != 0x00F7)
        || (cp >= 0x03B1 && cp <= 0x03C9)   /* Greek */
        || (cp >= 0x0430 && cp <= 0x044F);  /* Cyrillic */
}

static int is_letter_cp(unsigned int cp)
{
    if (is_upper_cp(cp) || is_lower_cp(cp)) return 1;
    if (cp >= 0x0100 && cp <= 0x02AF) return 1;   /* Latin Extended + IPA */
    if (cp >= 0x0370 && cp <= 0x03FF) return 1;   /* Greek */
    if (cp >= 0x0400 && cp <= 0x052F) return 1;   /* Cyrillic */
    if (cp >= 0x0620 && cp <= 0x064A) return 1;   /* Arabic letters */
    if (cp >= 0x0671 && cp <= 0x06D3) return 1;   /* Arabic extended letters */
    if (cp >= 0x0900 && cp <= 0x097F) return 1;   /* Devanagari */
    if (cp >= 0x0980 && cp <= 0x09FF) return 1;   /* Bengali */
    if (cp >= 0x0E00 && cp <= 0x0E7F) return 1;   /* Thai */
    if (cp >= 0x1000 && cp <= 0x109F) return 1;   /* Myanmar */
    if (cp >= 0x1200 && cp <= 0x137F) return 1;   /* Ethiopic */
    if (cp >= 0x1780 && cp <= 0x17DD) return 1;   /* Khmer */
    if (cp >= 0x3040 && cp <= 0x30FF) return 1;   /* Kana */
    if (cp >= 0x4E00 && cp <= 0x9FFF) return 1;   /* CJK */
    if (cp >= 0xAC00 && cp <= 0xD7A3) return 1;   /* Hangul */
    return 0;
}

static int is_letter_or_digit_cp(unsigned int cp)
{
    return is_letter_cp(cp) || is_digit_cp(cp);
}

static unsigned int to_lower_cp(unsigned int cp)
{
    if (cp >= 'A' && cp <= 'Z') return cp - 'A' + 'a';
    if (cp >= 0x00C0 && cp <= 0x00DE && cp != 0x00D7) return cp + 0x20;
    if (cp >= 0x0391 && cp <= 0x03A9) return cp + 0x20;
    if (cp >= 0x0410 && cp <= 0x042F) return cp + 0x20;
    return cp;
}

/* ── SentenceSplitter ────────────────────────────────────────────────────── */

#define SENTENCE_PAUSE_MS  280
#define CLAUSE_PAUSE_MS    200
#define PARAGRAPH_PAUSE_MS 400
#define FORCED_PAUSE_MS    60

/*
 * Characters that end a sentence, across the scripts we speak.
 *
 * A Latin-only list silently under-splits every language that punctuates
 * differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
 * segments from the same five-sentence text that gave six in eleven other
 * languages, because Devanagari and Bengali end sentences with the danda and
 * Urdu with its own full stop — none of which were listed. The paragraph ran
 * together for about a billion people, and nothing failed loudly enough to
 * notice.
 */
static const unsigned int TERMINATORS[] = {
    '.', '!', '?', ':', ';',            /* Latin / Cyrillic / Greek */
    0x0964, 0x0965,                     /* danda, double danda */
    0x06D4, 0x061F, 0x061B,             /* Arabic script */
    0x3002, 0xFF01, 0xFF1F,             /* CJK ideographic + fullwidth */
    0xFF0E, 0xFF1A, 0xFF1B,             /* fullwidth */
    0x1362,                             /* Ethiopic */
    0x17D4,                             /* Khmer khan */
    0x104A, 0x104B,                     /* Myanmar little/section */
};

static int is_terminator(unsigned int cp)
{
    for (size_t i = 0; i < sizeof TERMINATORS / sizeof TERMINATORS[0]; i++)
        if (TERMINATORS[i] == cp) return 1;
    return 0;
}

/* Terminators that can appear inside a token, and so need a following space. */
static int may_occur_inside_a_token(unsigned int cp)
{
    return cp == '.' || cp == ':' || cp == ';';
}

static int is_closer(unsigned int cp)
{
    return cp == '"' || cp == '\'' || cp == ')' || cp == ']';
}

/*
 * True when the terminator at i really ends a sentence.
 *
 * A period between digits is a decimal ("3.5"), and one followed directly by a
 * letter is usually an abbreviation or a URL — splitting there would cut a word
 * in half and insert a pause inside it.
 */
static int ends_sentence(const unsigned short *units, size_t n, size_t i)
{
    size_t j = i + 1;
    while (j < n && (is_terminator(units[j]) || is_closer(units[j]))) j++;

    if (j >= n) return 1;   /* end of input */

    /* Only SOME terminators can appear inside a token. The rest cannot occur
     * mid-token in any script, and demanding a space after them would never
     * split Chinese, Japanese, Khmer, Thai or Burmese at all: those scripts
     * write without spaces, so their full stop is followed by the next letter. */
    if (!may_occur_inside_a_token(units[i])) return 1;

    if (!is_ws(units[j])) return 0;   /* 3.5, e.g., co.za */

    if (units[i] == '.' && i > 0 && is_digit_cp(units[i - 1])
        && j + 1 < n && is_digit_cp(units[j + 1]))
        return 0;

    return 1;
}

/* Trim ASCII and Unicode whitespace from both ends, in place, and report emptiness. */
static char *trim_utf8(char *s)
{
    char *start = s;
    while (*start) {
        size_t len = u8_len(start);
        if (!is_ws(u8_cp(start, len))) break;
        start += len;
    }

    size_t blen = strlen(start);
    while (blen > 0) {
        /* Walk back to the last character boundary. */
        size_t back = 1;
        while (back < blen && back < 4 && ((unsigned char)start[blen - back] & 0xC0) == 0x80)
            back++;
        size_t off = blen - back;
        if (!is_ws(u8_cp(start + off, u8_len(start + off)))) break;
        blen = off;
    }
    start[blen] = '\0';
    return start;
}

static int has_speech(const char *s)
{
    for (const char *p = s; *p; ) {
        size_t len = u8_len(p);
        if (is_letter_or_digit_cp(u8_cp(p, len))) return 1;
        p += len;
    }
    return 0;
}

typedef struct {
    circle_speech_segment *out;
    size_t capacity;
    size_t count;      /* how many we WOULD have written */
} seg_sink;

static void seg_push(seg_sink *sink, char *owned_text, int pause_ms)
{
    if (sink->count < sink->capacity) {
        sink->out[sink->count].text = owned_text;
        sink->out[sink->count].trailing_pause_ms = pause_ms;
    } else {
        free(owned_text);   /* over capacity: still counted, not leaked */
    }
    sink->count++;
}

/* Returns the new length of `current` (always 0 — flush always drains it). */
static size_t seg_flush(seg_sink *sink, const unsigned short *current, size_t len,
                        int pause_ms)
{
    char *raw = from_utf16(current, len);
    if (!raw) return 0;
    char *s = trim_utf8(raw);

    /*
     * The terminator STAYS in the segment text, deliberately. It is tempting to
     * strip it — this module has already turned it into a pause, and the MMS
     * voices have no token for it. But the SA-11 voice's vocabulary DOES carry
     * '?' and '.', so it can render a real question rise that no inserted
     * silence could imitate. Stripping would have discarded that from all eleven
     * South African languages to tidy up a log line.
     *
     * A segment of nothing but punctuation has no sound to make, and the voice
     * has no token for it either.
     */
    if (*s == '\0' || !has_speech(s)) { free(raw); return 0; }

    char *owned = (char *)malloc(strlen(s) + 1);
    if (!owned) { free(raw); return 0; }
    strcpy(owned, s);
    free(raw);

    seg_push(sink, owned, pause_ms);
    return 0;
}

/*
 * Cut an over-long run at the last space, so the break lands between words
 * rather than inside one. With no space to use the run is left intact — a
 * mid-word cut would be audibly worse than a long segment.
 */
static size_t cut_at_word_boundary(seg_sink *sink, unsigned short *current, size_t len)
{
    size_t cut = 0;
    for (size_t i = len; i-- > 0; ) if (current[i] == ' ') { cut = i; break; }
    if (cut == 0) return len;

    char *raw = from_utf16(current, cut);
    if (raw) {
        char *head = trim_utf8(raw);
        if (*head) {
            char *owned = (char *)malloc(strlen(head) + 1);
            if (owned) { strcpy(owned, head); seg_push(sink, owned, FORCED_PAUSE_MS); }
        }
        free(raw);
    }

    size_t rest = len - (cut + 1);
    memmove(current, current + cut + 1, rest * sizeof(unsigned short));
    return rest;
}

size_t circle_split_sentences(const char *text,
                              circle_speech_segment *out, size_t out_capacity)
{
    seg_sink sink = { out, out_capacity, 0 };
    if (!text || !*text) return 0;

    size_t n = 0;
    unsigned short *units = to_utf16(text, &n);
    if (!units) return 0;

    /* Blank input produces nothing at all. */
    int any = 0;
    for (size_t i = 0; i < n; i++) if (!is_ws(units[i])) { any = 1; break; }
    if (!any) { free(units); return 0; }

    unsigned short *current = (unsigned short *)malloc((n + 1) * sizeof(unsigned short));
    if (!current) { free(units); return 0; }
    size_t clen = 0;
    const int pending = SENTENCE_PAUSE_MS;

    for (size_t i = 0; i < n; i++) {
        unsigned short c = units[i];

        if (c == '\r') continue;
        if (c == '\n') { clen = seg_flush(&sink, current, clen, PARAGRAPH_PAUSE_MS); continue; }

        current[clen++] = c;

        if (is_terminator(c) && ends_sentence(units, n, i)) {
            clen = seg_flush(&sink, current, clen,
                             (c == ':' || c == ';') ? CLAUSE_PAUSE_MS : SENTENCE_PAUSE_MS);
            continue;
        }

        if (clen >= CIRCLE_MAX_CHARS_PER_SEGMENT)
            clen = cut_at_word_boundary(&sink, current, clen);
    }

    seg_flush(&sink, current, clen, pending);

    /* Nothing should follow the last word — a trailing pause is dead air. */
    if (sink.count > 0 && sink.count <= out_capacity)
        out[sink.count - 1].trailing_pause_ms = 0;

    free(current);
    free(units);
    return sink.count;
}

void circle_speech_segments_free(circle_speech_segment *segments, size_t count)
{
    if (!segments) return;
    for (size_t i = 0; i < count; i++) { free(segments[i].text); segments[i].text = NULL; }
}

/* ── LanguageSpanSplitter ────────────────────────────────────────────────── */

int circle_is_foreign_word(const char *word)
{
    if (!word) return 0;

    size_t n = 0;
    unsigned short *units = to_utf16(word, &n);
    if (!units) return 0;
    if (n < 2) { free(units); return 0; }

    int upper = 0, lower = 0, has_internal_capital = 0;
    for (size_t i = 0; i < n; i++) {
        unsigned int cp = units[i];
        if (!is_letter_cp(cp)) continue;
        if (is_upper_cp(cp)) {
            upper++;
            if (i > 0) has_internal_capital = 1;
        } else {
            lower++;
        }
    }
    free(units);

    if (has_internal_capital && lower > 0) return 1;              /* CircleAI */
    if (upper >= 2 && lower == 0 && n <= 5) return 1;             /* GPS, SMS */
    return 0;
}

size_t circle_split_language_spans(const char *text,
                                   circle_language_span *out, size_t out_capacity)
{
    size_t written = 0;
    if (!text || !*text) return 0;

    size_t n = 0;
    unsigned short *units = to_utf16(text, &n);
    if (!units) return 0;

    int any = 0;
    for (size_t i = 0; i < n; i++) if (!is_ws(units[i])) { any = 1; break; }
    if (!any) { free(units); return 0; }

    unsigned short *current = (unsigned short *)malloc((n + 1) * sizeof(unsigned short));
    if (!current) { free(units); return 0; }
    size_t clen = 0;
    int current_is_foreign = -1;   /* -1 unset, 0 native, 1 foreign */

    #define EMIT_SPAN(flag) do {                                        \
        char *s = from_utf16(current, clen);                            \
        if (s) {                                                        \
            if (written < out_capacity) {                               \
                out[written].text = s;                                  \
                out[written].is_foreign = (flag);                       \
            } else free(s);                                             \
            written++;                                                  \
        }                                                               \
        clen = 0;                                                       \
    } while (0)

    size_t i = 0;
    while (i < n) {
        /* Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
         * along with whatever run they FOLLOW, so a language change never
         * strands a comma on its own or splits mid-punctuation. */
        if (!is_letter_or_digit_cp(units[i])) {
            while (i < n && !is_letter_or_digit_cp(units[i])) current[clen++] = units[i++];
            continue;
        }

        size_t word_start = i;
        while (i < n && is_letter_or_digit_cp(units[i])) i++;

        char *word = from_utf16(units + word_start, i - word_start);
        int foreign = word ? circle_is_foreign_word(word) : 0;
        free(word);

        if (current_is_foreign != -1 && current_is_foreign != foreign) {
            /* The run ends at the last word, not at the separators that follow
             * it — those have already been appended and belong to the join. */
            EMIT_SPAN(current_is_foreign);
        }

        current_is_foreign = foreign;
        for (size_t k = word_start; k < i; k++) current[clen++] = units[k];
    }

    if (clen > 0 && current_is_foreign != -1) EMIT_SPAN(current_is_foreign);

    #undef EMIT_SPAN

    free(current);
    free(units);
    return written;
}

void circle_language_spans_free(circle_language_span *spans, size_t count)
{
    if (!spans) return;
    for (size_t i = 0; i < count; i++) { free(spans[i].text); spans[i].text = NULL; }
}

char *circle_to_spoken_form(const char *text)
{
    if (!text) return NULL;
    if (!*text) { char *e = (char *)malloc(1); if (e) e[0] = '\0'; return e; }

    size_t n = 0;
    unsigned short *units = to_utf16(text, &n);
    if (!units) return NULL;

    /* 1. Break the compound into words at case boundaries, which is where the
     *    word boundaries genuinely are in this naming style. A compound is one
     *    token to a synthesiser and it has no idea where the words are, so it
     *    produces a mumble; split, it is things the voice already knows. */
    unsigned short *spaced = (unsigned short *)malloc((n * 2 + 2) * sizeof(unsigned short));
    if (!spaced) { free(units); return NULL; }
    size_t slen = 0;

    for (size_t i = 0; i < n; i++) {
        unsigned int c = units[i];
        if (i > 0 && is_upper_cp(c)) {
            unsigned int prev = units[i - 1];
            unsigned int next = (i + 1 < n) ? units[i + 1] : 0;

            int after_lower = is_lower_cp(prev);                     /* Circle|AI */
            int end_of_acronym = is_upper_cp(prev) && is_lower_cp(next);  /* API|Key */

            if (after_lower || end_of_acronym) spaced[slen++] = ' ';
        }
        spaced[slen++] = (unsigned short)c;
    }

    /* 2. Punctuate the acronyms. "AI" as a bare token gets read as a word —
     *    "ay" — where "A.I." is read as the letters, which is what it is. The
     *    full stops are for the voice, not the reader. */
    unsigned short *outu = (unsigned short *)malloc((slen * 2 + 2) * sizeof(unsigned short));
    if (!outu) { free(spaced); free(units); return NULL; }
    size_t olen = 0;

    for (size_t i = 0; i < slen; ) {
        if (!is_upper_cp(spaced[i])) { outu[olen++] = spaced[i++]; continue; }

        size_t start = i;
        while (i < slen && is_upper_cp(spaced[i])) i++;
        size_t run = i - start;

        /* A lone capital is an ordinary word opening ("Sawubona"), not an
         * acronym, and a run followed by lowercase was already split above. */
        if (run < 2) {
            for (size_t k = start; k < i; k++) outu[olen++] = spaced[k];
            continue;
        }

        for (size_t k = start; k < i; k++) { outu[olen++] = spaced[k]; outu[olen++] = '.'; }
    }

    char *result = from_utf16(outu, olen);
    free(outu);
    free(spaced);
    free(units);
    return result;
}

/* ── GeezRomanizer ───────────────────────────────────────────────────────── */

#define GEEZ_BASE 0x1200
#define GEEZ_ORDERS_PER_CONSONANT 8

/*
 * Last codepoint that follows the eight-orders-per-consonant layout. The
 * syllabary ends here; everything above is lone syllables, marks and numerals,
 * and treating any of it as a row invents a pronunciation.
 */
#define GEEZ_LAST_SYLLABLE 0x1357

/*
 * Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices hold
 * 27-28 plain Latin letters, so a transliteration carrying the Ethiopist
 * diacritics would be dropped as surely as the Ethiopic was.
 *
 * Six rows are LABIALISED — the consonant carries a built-in /w/. Writing them
 * plain turns "kwa" into "ka", which silently changes the word.
 */
static const char *const GEEZ_CONSONANTS[] = {
    "h",  "l",  "h",  "m",  "s",  "r",  "s",  "sh",
    "q",  "qw", "q",  "qw", "b",  "v",  "t",  "ch",
    "h",  "hw", "n",  "ny", "",   "k",  "kw", "k",
    "kw", "w",  "",   "z",  "zh", "y",  "d",  "d",
    "j",  "g",  "gw", "ng", "t",  "ch", "p",  "ts",
    "ts", "f",  "p",
};

/* Vowel per order. The sixth is SILENT — it marks a bare consonant. */
static const char *const GEEZ_VOWELS[] = { "e", "u", "i", "a", "e", "", "o", "wa" };

int circle_is_ethiopic(const char *text)
{
    if (!text) return 0;
    for (const char *p = text; *p; ) {
        size_t len = u8_len(p);
        unsigned int cp = u8_cp(p, len);
        if (cp >= 0x1200 && cp <= 0x139F) return 1;
        p += len;
    }
    return 0;
}

char *circle_geez_romanize(const char *text)
{
    if (!text) return NULL;

    size_t cap = strlen(text) * 3 + 2;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t w = 0;

    #define PUT(s) do { const char *_s = (s); size_t _l = strlen(_s); \
                        if (w + _l < cap) { memcpy(out + w, _s, _l); w += _l; } } while (0)

    for (const char *p = text; *p; ) {
        size_t len = u8_len(p);
        unsigned int cp = u8_cp(p, len);
        const char *raw = p;
        p += len;

        /* Ethiopic punctuation, mapped so sentence splitting still works. */
        switch (cp) {
        case 0x1360: PUT(" "); continue;   /* section */
        case 0x1361: PUT(" "); continue;   /* word separator */
        case 0x1362: PUT("."); continue;   /* full stop */
        case 0x1363: PUT(","); continue;   /* comma */
        case 0x1364: PUT(";"); continue;   /* semicolon */
        case 0x1365: PUT(":"); continue;   /* colon */
        case 0x1366: PUT(":"); continue;   /* preface colon */
        case 0x1367: PUT("?"); continue;   /* question mark */
        case 0x1368: PUT(" "); continue;   /* paragraph separator */
        default: break;
        }

        /*
         * THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check
         * has to stop with it. Beyond that the block is no longer a syllabary:
         * U+1358..U+135A are three LONE syllables already in their -a order,
         * U+135D..U+135F are combining marks, and U+1369 onward are the
         * numerals. Sizing the check off the consonant table instead swept seven
         * of those numerals back into the syllabary — and they came out as
         * sound, so nothing failed.
         *
         * A combining mark modifies the syllable before it and has no sound of
         * its own, so it is dropped rather than passed through.
         */
        if (cp >= 0x135D && cp <= 0x135F) continue;
        if (cp == 0x1358) { PUT("rya"); continue; }
        if (cp == 0x1359) { PUT("mya"); continue; }
        if (cp == 0x135A) { PUT("fya"); continue; }

        if (cp < GEEZ_BASE || cp > GEEZ_LAST_SYLLABLE) {
            /* Numerals and the rarely-used supplement blocks have no sound we
             * can render; anything else is not Ethiopic and is left alone. */
            if (cp >= 0x1369 && cp <= 0x137C) continue;
            if (w + len < cap) { memcpy(out + w, raw, len); w += len; }
            continue;
        }

        unsigned int i = cp - GEEZ_BASE;
        unsigned int row = i / GEEZ_ORDERS_PER_CONSONANT;
        unsigned int order = i % GEEZ_ORDERS_PER_CONSONANT;

        const char *consonant = GEEZ_CONSONANTS[row];
        const char *vowel = GEEZ_VOWELS[order];

        if (consonant[0] == '\0') {
            /* The glottal and pharyngeal rows write no consonant in Latin, so
             * the vowel IS the character. First order is heard as "a", and the
             * sixth — silent after a real consonant — must still sound here, or
             * the word-initial one disappears entirely. */
            if (order == 0) vowel = "a";
            else if (vowel[0] == '\0') vowel = "e";
        }

        PUT(consonant);
        PUT(vowel);
    }

    #undef PUT

    out[w] = '\0';
    return out;
}

/* ── ToneShaper ──────────────────────────────────────────────────────────── */

#define LOW_SHELF_SLOPE 0.9

circle_tone_shaper_settings circle_tone_shaper_warm(void)
{
    circle_tone_shaper_settings s = { 320.0, 4.0, 3200.0, -4.0, 0.8 };
    return s;
}

static circle_biquad_coefficients normalise(double b[3], double a[3])
{
    circle_biquad_coefficients c;
    double a0 = a[0];
    for (int i = 0; i < 3; i++) { c.b[i] = b[i] / a0; c.a[i] = a[i] / a0; }
    return c;
}

circle_biquad_coefficients circle_low_shelf_coefficients(
    const circle_tone_shaper_settings *s, int rate)
{
    double amp = pow(10, s->low_shelf_db / 40);
    double w0 = 2 * 3.14159265358979323846 * s->low_shelf_hz / rate;
    double alpha = sin(w0) / 2 * sqrt((amp + 1 / amp) * (1 / LOW_SHELF_SLOPE - 1) + 2);
    double c = cos(w0);
    double s2 = 2 * sqrt(amp) * alpha;

    double b[3] = {
        amp * ((amp + 1) - (amp - 1) * c + s2),
        2 * amp * ((amp - 1) - (amp + 1) * c),
        amp * ((amp + 1) - (amp - 1) * c - s2),
    };
    double a[3] = {
        (amp + 1) + (amp - 1) * c + s2,
        -2 * ((amp - 1) + (amp + 1) * c),
        (amp + 1) + (amp - 1) * c - s2,
    };
    return normalise(b, a);
}

circle_biquad_coefficients circle_peaking_coefficients(
    const circle_tone_shaper_settings *s, int rate)
{
    double amp = pow(10, s->presence_db / 40);
    double w0 = 2 * 3.14159265358979323846 * s->presence_hz / rate;
    double alpha = sin(w0) / (2 * s->presence_q);
    double c = cos(w0);

    double b[3] = { 1 + alpha * amp, -2 * c, 1 - alpha * amp };
    double a[3] = { 1 + alpha / amp, -2 * c, 1 - alpha / amp };
    return normalise(b, a);
}

void circle_biquad(float *x, size_t n, const circle_biquad_coefficients *c)
{
    double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    for (size_t i = 0; i < n; i++) {
        double xn = x[i];
        double yn = c->b[0] * xn + c->b[1] * x1 + c->b[2] * x2 - c->a[1] * y1 - c->a[2] * y2;
        x2 = x1; x1 = xn;
        y2 = y1; y1 = yn;
        x[i] = (float)yn;
    }
}

static float peak_of(const float *x, size_t n)
{
    float p = 0;
    for (size_t i = 0; i < n; i++) { float a = x[i] < 0 ? -x[i] : x[i]; if (a > p) p = a; }
    return p;
}

void circle_apply_tone_shaper(float *waveform, size_t n, int sample_rate,
                              const circle_tone_shaper_settings *s)
{
    if (!waveform || n == 0 || sample_rate <= 0 || !s) return;

    float before = peak_of(waveform, n);
    if (before <= 0.0f) return;   /* a silent buffer, and dividing by that peak is NaN */

    circle_biquad_coefficients ls = circle_low_shelf_coefficients(s, sample_rate);
    circle_biquad(waveform, n, &ls);
    circle_biquad_coefficients pk = circle_peaking_coefficients(s, sample_rate);
    circle_biquad(waveform, n, &pk);

    float after = peak_of(waveform, n);
    if (after > 0.0f && after > before) {
        /* float division, because the reference divides two FLOATS here.
         * Widening to double makes the gain a few ULP different and the whole
         * tail of the waveform drifts with it. */
        float g = before / after;
        for (size_t i = 0; i < n; i++) waveform[i] *= g;
    }
}

/* ── NchltPhonemizer ─────────────────────────────────────────────────────── */

typedef struct { int order; char *left; char *right; char *code; } nchlt_rule;
typedef struct { char *word; char **phones; size_t phone_count; } dict_entry;
typedef struct { unsigned int grapheme; nchlt_rule *rules; size_t count; } rule_group;

struct circle_nchlt_phonemizer {
    dict_entry *dict;
    size_t dict_count;
    rule_group *groups;
    size_t group_count;

    unsigned int *phone_codes;   /* single-codepoint rule codes */
    char **phone_values;
    size_t phone_count;

    unsigned int *graph_from;    /* std -> funny */
    unsigned int *graph_to;
    size_t graph_count;

    char **gnull_from;
    char **gnull_to;
    size_t gnull_count;

    size_t last_rule_predicted_words;
    char **unknown;
    size_t unknown_count;

    char **phones;               /* result buffer, owned, reused per call */
    size_t phones_count;
};

static char *dup_str(const char *s, size_t n)
{
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n);
    out[n] = '\0';
    return out;
}

/* Iterate lines the way a StreamReader does, so a CRLF file parses identically. */
typedef struct { const char *p; } line_iter;

static int next_line(line_iter *it, const char **start, size_t *len)
{
    if (!it->p) return 0;
    const char *end = strchr(it->p, '\n');
    if (end) { *start = it->p; *len = (size_t)(end - it->p); it->p = end + 1; }
    else     { *start = it->p; *len = strlen(it->p); it->p = NULL; }
    while (*len && (*start)[*len - 1] == '\r') (*len)--;
    return 1;
}

static void parse_dict_text(circle_nchlt_phonemizer *p, const char *text)
{
    line_iter it = { text };
    const char *line; size_t len;
    while (next_line(&it, &line, &len)) {
        if (len == 0) continue;
        const char *tab = memchr(line, '\t', len);
        if (!tab || tab == line) continue;

        size_t wlen = (size_t)(tab - line);
        const char *pron = tab + 1;
        size_t plen = len - wlen - 1;
        while (plen && (pron[0] == ' ' || pron[0] == '\t')) { pron++; plen--; }
        while (plen && (pron[plen - 1] == ' ' || pron[plen - 1] == '\t')) plen--;
        if (plen == 0) continue;

        /* keep the FIRST variant */
        int seen = 0;
        for (size_t i = 0; i < p->dict_count; i++)
            if (strlen(p->dict[i].word) == wlen && memcmp(p->dict[i].word, line, wlen) == 0)
                { seen = 1; break; }
        if (seen) continue;

        char **phones = NULL;
        size_t pc = 0;
        for (size_t i = 0; i < plen; ) {
            while (i < plen && pron[i] == ' ') i++;
            size_t s = i;
            while (i < plen && pron[i] != ' ') i++;
            if (i > s) {
                char **g = (char **)realloc(phones, (pc + 1) * sizeof(char *));
                if (!g) break;
                phones = g;
                phones[pc] = dup_str(pron + s, i - s);
                if (phones[pc]) pc++;
            }
        }

        dict_entry *g = (dict_entry *)realloc(p->dict, (p->dict_count + 1) * sizeof(dict_entry));
        if (!g) break;
        p->dict = g;
        p->dict[p->dict_count].word = dup_str(line, wlen);
        p->dict[p->dict_count].phones = phones;
        p->dict[p->dict_count].phone_count = pc;
        if (p->dict[p->dict_count].word) p->dict_count++;
    }
}

static int parse_int_strict(const char *s, size_t len, int *out)
{
    while (len && (*s == ' ' || *s == '\t')) { s++; len--; }
    while (len && (s[len - 1] == ' ' || s[len - 1] == '\t')) len--;
    if (len == 0) return 0;

    size_t i = 0;
    int sign = 1;
    if (s[0] == '+' || s[0] == '-') { sign = (s[0] == '-') ? -1 : 1; i = 1; }
    if (i >= len) return 0;

    long v = 0;
    for (; i < len; i++) {
        if (s[i] < '0' || s[i] > '9') return 0;
        v = v * 10 + (s[i] - '0');
    }
    *out = (int)(sign * v);
    return 1;
}

static rule_group *group_for(circle_nchlt_phonemizer *p, unsigned int g, int create)
{
    for (size_t i = 0; i < p->group_count; i++)
        if (p->groups[i].grapheme == g) return &p->groups[i];
    if (!create) return NULL;

    rule_group *grown = (rule_group *)realloc(p->groups,
                                              (p->group_count + 1) * sizeof(rule_group));
    if (!grown) return NULL;
    p->groups = grown;
    p->groups[p->group_count].grapheme = g;
    p->groups[p->group_count].rules = NULL;
    p->groups[p->group_count].count = 0;
    return &p->groups[p->group_count++];
}

static void parse_rules_text(circle_nchlt_phonemizer *p, const char *text)
{
    line_iter it = { text };
    const char *line; size_t len;
    while (next_line(&it, &line, &len)) {
        if (len == 0) continue;

        /* grapheme ; left ; right ; code ; order [ ; count ] */
        const char *f[6]; size_t fl[6];
        size_t nf = 0, start = 0;
        for (size_t i = 0; i <= len && nf < 6; i++) {
            if (i == len || line[i] == ';') {
                f[nf] = line + start; fl[nf] = i - start; nf++;
                start = i + 1;
                if (i == len) break;
            }
        }
        if (nf < 5 || fl[0] == 0) continue;

        int order;
        if (!parse_int_strict(f[4], fl[4], &order)) continue;

        unsigned int g = u8_cp(f[0], u8_len(f[0]));
        rule_group *grp = group_for(p, g, 1);
        if (!grp) continue;

        nchlt_rule *grown = (nchlt_rule *)realloc(grp->rules,
                                                  (grp->count + 1) * sizeof(nchlt_rule));
        if (!grown) continue;
        grp->rules = grown;
        grp->rules[grp->count].order = order;
        grp->rules[grp->count].left = dup_str(f[1], fl[1]);
        grp->rules[grp->count].right = dup_str(f[2], fl[2]);
        grp->rules[grp->count].code = dup_str(f[3], fl[3]);
        grp->count++;
    }

    /*
     * STABLE sort, descending by order. Two rules of equal order must stay in
     * FILE order — the reference uses LINQ's OrderByDescending, which is stable,
     * and qsort is NOT, so this is an insertion sort rather than a library call.
     * The dense rule sets are exactly where ties are common, so an unstable sort
     * would disagree on the rules that fire most.
     */
    for (size_t gi = 0; gi < p->group_count; gi++) {
        nchlt_rule *r = p->groups[gi].rules;
        for (size_t i = 1; i < p->groups[gi].count; i++) {
            nchlt_rule key = r[i];
            size_t j = i;
            while (j > 0 && r[j - 1].order < key.order) { r[j] = r[j - 1]; j--; }
            r[j] = key;
        }
    }
}

static void parse_phone_map_text(circle_nchlt_phonemizer *p, const char *text)
{
    line_iter it = { text };
    const char *line; size_t len;
    while (next_line(&it, &line, &len)) {
        if (len == 0) continue;
        const char *tab = memchr(line, '\t', len);
        if (!tab || tab == line) continue;

        size_t clen = (size_t)(tab - line);
        if (u8_len(line) != clen) continue;   /* code must be a SINGLE character */

        unsigned int *gc = (unsigned int *)realloc(p->phone_codes,
                                                   (p->phone_count + 1) * sizeof(unsigned int));
        char **gv = (char **)realloc(p->phone_values, (p->phone_count + 1) * sizeof(char *));
        if (!gc || !gv) { if (gc) p->phone_codes = gc; if (gv) p->phone_values = gv; break; }
        p->phone_codes = gc;
        p->phone_values = gv;
        p->phone_codes[p->phone_count] = u8_cp(line, clen);
        p->phone_values[p->phone_count] = dup_str(tab + 1, len - clen - 1);
        if (p->phone_values[p->phone_count]) p->phone_count++;
    }
}

static void parse_graph_map_text(circle_nchlt_phonemizer *p, const char *text)
{
    /* File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap). */
    line_iter it = { text };
    const char *line; size_t len;
    while (next_line(&it, &line, &len)) {
        if (len == 0) continue;
        const char *tab = memchr(line, '\t', len);
        if (!tab) continue;

        size_t alen = (size_t)(tab - line);
        size_t blen = len - alen - 1;
        if (alen == 0 || blen == 0) continue;
        if (u8_len(line) != alen || u8_len(tab + 1) != blen) continue;

        unsigned int a = u8_cp(line, alen), b = u8_cp(tab + 1, blen);
        if (a == b) continue;

        unsigned int *gf = (unsigned int *)realloc(p->graph_from,
                                                   (p->graph_count + 1) * sizeof(unsigned int));
        unsigned int *gt = (unsigned int *)realloc(p->graph_to,
                                                   (p->graph_count + 1) * sizeof(unsigned int));
        if (!gf || !gt) { if (gf) p->graph_from = gf; if (gt) p->graph_to = gt; break; }
        p->graph_from = gf;
        p->graph_to = gt;
        p->graph_from[p->graph_count] = b;
        p->graph_to[p->graph_count] = a;
        p->graph_count++;
    }
}

static void parse_gnulls_text(circle_nchlt_phonemizer *p, const char *text)
{
    line_iter it = { text };
    const char *line; size_t len;
    while (next_line(&it, &line, &len)) {
        if (len == 0) continue;
        const char *semi = memchr(line, ';', len);
        if (!semi) continue;
        /* Exactly two fields, like the reference's Split(';').Length == 2. */
        if (memchr(semi + 1, ';', len - (size_t)(semi - line) - 1)) continue;

        char **gf = (char **)realloc(p->gnull_from, (p->gnull_count + 1) * sizeof(char *));
        char **gt = (char **)realloc(p->gnull_to, (p->gnull_count + 1) * sizeof(char *));
        if (!gf || !gt) { if (gf) p->gnull_from = gf; if (gt) p->gnull_to = gt; break; }
        p->gnull_from = gf;
        p->gnull_to = gt;
        p->gnull_from[p->gnull_count] = dup_str(line, (size_t)(semi - line));
        p->gnull_to[p->gnull_count] = dup_str(semi + 1, len - (size_t)(semi - line) - 1);
        p->gnull_count++;
    }
}

circle_nchlt_phonemizer *circle_nchlt_new(const char *dict_text,
                                          const char *rules_text,
                                          const char *phone_map_text,
                                          const char *graph_map_text,
                                          const char *gnulls_text)
{
    if (!dict_text || !rules_text || !phone_map_text) return NULL;

    circle_nchlt_phonemizer *p =
        (circle_nchlt_phonemizer *)calloc(1, sizeof(circle_nchlt_phonemizer));
    if (!p) return NULL;

    parse_dict_text(p, dict_text);
    parse_rules_text(p, rules_text);
    parse_phone_map_text(p, phone_map_text);
    if (graph_map_text && *graph_map_text) parse_graph_map_text(p, graph_map_text);
    if (gnulls_text && *gnulls_text) parse_gnulls_text(p, gnulls_text);

    return p;
}

void circle_nchlt_free(circle_nchlt_phonemizer *p)
{
    if (!p) return;

    for (size_t i = 0; i < p->dict_count; i++) {
        free(p->dict[i].word);
        for (size_t k = 0; k < p->dict[i].phone_count; k++) free(p->dict[i].phones[k]);
        free(p->dict[i].phones);
    }
    free(p->dict);

    for (size_t i = 0; i < p->group_count; i++) {
        for (size_t k = 0; k < p->groups[i].count; k++) {
            free(p->groups[i].rules[k].left);
            free(p->groups[i].rules[k].right);
            free(p->groups[i].rules[k].code);
        }
        free(p->groups[i].rules);
    }
    free(p->groups);

    for (size_t i = 0; i < p->phone_count; i++) free(p->phone_values[i]);
    free(p->phone_codes);
    free(p->phone_values);

    free(p->graph_from);
    free(p->graph_to);

    for (size_t i = 0; i < p->gnull_count; i++) { free(p->gnull_from[i]); free(p->gnull_to[i]); }
    free(p->gnull_from);
    free(p->gnull_to);

    for (size_t i = 0; i < p->unknown_count; i++) free(p->unknown[i]);
    free(p->unknown);

    for (size_t i = 0; i < p->phones_count; i++) free(p->phones[i]);
    free(p->phones);

    free(p);
}

static void push_unknown(circle_nchlt_phonemizer *p, const char *sym)
{
    for (size_t i = 0; i < p->unknown_count; i++)
        if (strcmp(p->unknown[i], sym) == 0) return;
    char **g = (char **)realloc(p->unknown, (p->unknown_count + 1) * sizeof(char *));
    if (!g) return;
    p->unknown = g;
    p->unknown[p->unknown_count] = dup_str(sym, strlen(sym));
    if (p->unknown[p->unknown_count]) p->unknown_count++;
}

static void push_phone(circle_nchlt_phonemizer *p, const char *value)
{
    char **g = (char **)realloc(p->phones, (p->phones_count + 1) * sizeof(char *));
    if (!g) return;
    p->phones = g;
    p->phones[p->phones_count] = dup_str(value, strlen(value));
    if (p->phones[p->phones_count]) p->phones_count++;
}

static void clear_phones(circle_nchlt_phonemizer *p)
{
    for (size_t i = 0; i < p->phones_count; i++) free(p->phones[i]);
    free(p->phones);
    p->phones = NULL;
    p->phones_count = 0;
}

static char *map_and_gnull(circle_nchlt_phonemizer *p, const char *word)
{
    /* Grapheme remap (usually identity) then grapheme-null insertion. */
    size_t wlen = strlen(word);
    char *mapped = (char *)malloc(wlen * 4 + 1);
    if (!mapped) return NULL;
    size_t w = 0;

    for (const char *q = word; *q; ) {
        size_t len = u8_len(q);
        unsigned int cp = u8_cp(q, len);
        unsigned int repl = cp;
        for (size_t i = 0; i < p->graph_count; i++)
            if (p->graph_from[i] == cp) { repl = p->graph_to[i]; break; }
        w += u8_encode(repl, mapped + w);
        q += len;
    }
    mapped[w] = '\0';

    for (size_t i = 0; i < p->gnull_count; i++) {
        const char *from = p->gnull_from[i], *to = p->gnull_to[i];
        size_t flen = strlen(from), tlen = strlen(to);
        if (flen == 0) continue;

        char *acc = (char *)malloc(strlen(mapped) + 1);
        if (!acc) break;
        size_t aw = 0;
        for (const char *q = mapped; *q; ) {
            if (strncmp(q, from, flen) == 0) {
                memcpy(acc + aw, to, tlen); aw += tlen; q += flen;
            } else {
                acc[aw++] = *q++;
            }
        }
        acc[aw] = '\0';
        free(mapped);
        mapped = acc;
    }

    return mapped;
}

static void predict_into(circle_nchlt_phonemizer *p, const char *word)
{
    if (!word || !*word) return;

    char *w = map_and_gnull(p, word);
    if (!w) return;

    /* Offsets of every character boundary, so left/right contexts are cut on
     * character boundaries rather than mid-sequence. */
    size_t n = 0;
    for (const char *q = w; *q; q += u8_len(q)) n++;
    size_t *off = (size_t *)malloc((n + 1) * sizeof(size_t));
    if (!off) { free(w); return; }
    { size_t k = 0, b = 0; while (w[b]) { off[k++] = b; b += u8_len(w + b); } off[n] = b; }

    size_t wlen = off[n];
    char *pat = (char *)malloc(wlen + 8);
    char *needle = (char *)malloc(wlen * 2 + 16);
    if (!pat || !needle) { free(pat); free(needle); free(off); free(w); return; }

    for (size_t i = 0; i < n; i++) {
        size_t glen = off[i + 1] - off[i];
        unsigned int g = u8_cp(w + off[i], glen);

        rule_group *grp = group_for(p, g, 0);
        if (!grp) {
            /* Skip an unknown grapheme rather than fabricate a phone for it. */
            char sym[8];
            memcpy(sym, w + off[i], glen);
            sym[glen] = '\0';
            push_unknown(p, sym);
            continue;
        }

        /* pat = " " + left-context + "-" + g + "-" + right-context + " " */
        size_t pw = 0;
        pat[pw++] = ' ';
        memcpy(pat + pw, w, off[i]); pw += off[i];
        pat[pw++] = '-';
        memcpy(pat + pw, w + off[i], glen); pw += glen;
        pat[pw++] = '-';
        memcpy(pat + pw, w + off[i + 1], wlen - off[i + 1]); pw += wlen - off[i + 1];
        pat[pw++] = ' ';
        pat[pw] = '\0';

        /* Rules are pre-sorted most-specific-first; the first match wins. */
        unsigned int code = '0';
        for (size_t r = 0; r < grp->count; r++) {
            size_t nw = 0;
            size_t ll = strlen(grp->rules[r].left), rl = strlen(grp->rules[r].right);
            memcpy(needle + nw, grp->rules[r].left, ll); nw += ll;
            needle[nw++] = '-';
            memcpy(needle + nw, w + off[i], glen); nw += glen;
            needle[nw++] = '-';
            memcpy(needle + nw, grp->rules[r].right, rl); nw += rl;
            needle[nw] = '\0';

            if (strstr(pat, needle)) {
                const char *c = grp->rules[r].code;
                code = (c && *c) ? u8_cp(c, u8_len(c)) : '0';
                break;
            }
        }
        if (code == '0') continue;

        /* Remap the single-character code to its X-SAMPA symbol. */
        const char *value = NULL;
        for (size_t k = 0; k < p->phone_count; k++)
            if (p->phone_codes[k] == code) { value = p->phone_values[k]; break; }

        if (value) {
            push_phone(p, value);
        } else {
            char raw[8];
            raw[u8_encode(code, raw)] = '\0';
            push_phone(p, raw);
        }
    }

    free(needle);
    free(pat);
    free(off);
    free(w);
}

static size_t copy_out(char **src, size_t count, const char **out, size_t out_capacity)
{
    for (size_t i = 0; i < count && i < out_capacity; i++) out[i] = src[i];
    return count;
}

size_t circle_nchlt_phonemize(circle_nchlt_phonemizer *p, const char *text,
                              const char **out, size_t out_capacity)
{
    if (!p) return 0;

    p->last_rule_predicted_words = 0;
    for (size_t i = 0; i < p->unknown_count; i++) free(p->unknown[i]);
    free(p->unknown);
    p->unknown = NULL;
    p->unknown_count = 0;
    clear_phones(p);

    if (!text) return 0;
    int any = 0;
    for (const char *q = text; *q; ) {
        size_t len = u8_len(q);
        if (!is_ws(u8_cp(q, len))) { any = 1; break; }
        q += len;
    }
    if (!any) return 0;

    /*
     * Lower-case and split into word tokens on anything that is not a letter.
     * Diacritics are preserved (Afrikaans e-circumflex and friends are real
     * graphemes); digits and punctuation become separators. Number and
     * abbreviation expansion is out of scope and belongs upstream.
     */
    size_t cap = strlen(text) * 4 + 1;
    char *word = (char *)malloc(cap);
    if (!word) return 0;
    size_t wlen = 0;

    for (const char *q = text; ; ) {
        unsigned int cp = 0;
        size_t len = 0;
        if (*q) { len = u8_len(q); cp = u8_cp(q, len); }

        if (*q && is_letter_cp(cp)) {
            wlen += u8_encode(to_lower_cp(cp), word + wlen);
            q += len;
            continue;
        }

        if (wlen > 0) {
            word[wlen] = '\0';

            const dict_entry *hit = NULL;
            for (size_t i = 0; i < p->dict_count; i++)
                if (strcmp(p->dict[i].word, word) == 0) { hit = &p->dict[i]; break; }

            if (hit) {
                for (size_t k = 0; k < hit->phone_count; k++) push_phone(p, hit->phones[k]);
            } else {
                predict_into(p, word);
                p->last_rule_predicted_words++;
            }
            wlen = 0;
        }

        if (!*q) break;
        q += len;
    }

    free(word);
    return copy_out(p->phones, p->phones_count, out, out_capacity);
}

size_t circle_nchlt_predict_word(circle_nchlt_phonemizer *p, const char *word,
                                 const char **out, size_t out_capacity)
{
    if (!p) return 0;
    clear_phones(p);
    predict_into(p, word);
    return copy_out(p->phones, p->phones_count, out, out_capacity);
}

size_t circle_nchlt_last_rule_predicted_words(const circle_nchlt_phonemizer *p)
{
    return p ? p->last_rule_predicted_words : 0;
}

size_t circle_nchlt_last_unknown_graphemes(const circle_nchlt_phonemizer *p,
                                           const char *const **out)
{
    if (!p) return 0;
    if (out) *out = (const char *const *)p->unknown;
    return p->unknown_count;
}
