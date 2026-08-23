/*
 * voice_piper.c — Piper phoneme→id mapping, lexicon tokenising, PCM format.
 *
 * C ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs and
 * AudioFormat.cs. See voice_piper.h for the contract.
 */

#include "circle_ai/voice_piper.h"

#include <stdlib.h>
#include <string.h>

/* Piper's special phoneme symbols (piper-phonemize defaults). */
#define PAD_SYMBOL "_"
#define BOS_SYMBOL "^"
#define EOS_SYMBOL "$"

/* ── UTF-8 ───────────────────────────────────────────────────────────────── */

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

static size_t u8_count(const char *s)
{
    size_t n = 0;
    while (*s) { s += u8_len(s); n++; }
    return n;
}

static char *dup_n(const char *s, size_t n)
{
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n);
    out[n] = '\0';
    return out;
}

/* ── AudioFormat ─────────────────────────────────────────────────────────── */

circle_voice_audio_format circle_voice_pcm16_mono_16k(void)
{
    circle_voice_audio_format f = { 16000, 1, 16 };
    return f;
}

/* ── PiperVoiceConfig ────────────────────────────────────────────────────── */

static const circle_voice_phoneme_entry *find(const circle_voice_piper_config *cfg,
                                              const char *symbol)
{
    for (size_t i = 0; i < cfg->entry_count; i++)
        if (strcmp(cfg->entries[i].symbol, symbol) == 0) return &cfg->entries[i];
    return NULL;
}

long long circle_voice_pad_id(const circle_voice_piper_config *cfg)
{
    const circle_voice_phoneme_entry *p = find(cfg, PAD_SYMBOL);
    return (p && p->id_count > 0) ? p->ids[0] : 0;
}

int circle_voice_has_phoneme_map(const circle_voice_piper_config *cfg)
{
    return cfg && cfg->entry_count > 0;
}

/* Lower-case ASCII only. The vocabularies this port meets are ASCII or IPA, and
 * a full Unicode case-fold would need a table this port deliberately avoids. */
static void ascii_lower(const char *in, char *out, size_t cap)
{
    size_t i = 0;
    for (; in[i] && i + 1 < cap; i++)
        out[i] = (in[i] >= 'A' && in[i] <= 'Z') ? (char)(in[i] - 'A' + 'a') : in[i];
    out[i] = '\0';
}

/* U+0300..U+036F and the other combining blocks this catalogue meets. */
static int is_combining(unsigned int cp)
{
    return (cp >= 0x0300 && cp <= 0x036F)
        || (cp >= 0x064B && cp <= 0x065F)   /* Arabic harakat */
        || (cp >= 0x0900 && cp <= 0x0903)   /* Devanagari signs */
        || (cp >= 0x093A && cp <= 0x094F)   /* Devanagari matras */
        || (cp >= 0x0E31 && cp <= 0x0E3A)   /* Thai vowel signs */
        || (cp >= 0x0E47 && cp <= 0x0E4E)
        || (cp >= 0x102B && cp <= 0x103E);  /* Burmese medials */
}

static int is_format_cp(unsigned int cp)
{
    return cp == 0x00AD || (cp >= 0x200B && cp <= 0x200F)
        || (cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2060 && cp <= 0x2064)
        || cp == 0xFEFF;
}

/*
 * Fold one precomposed Latin letter to its base: ṱ → t.
 *
 * AN EXPLICIT TABLE, NOT NFD. This port takes no Unicode normalisation library,
 * so only the letters this catalogue actually meets fold. That is NARROWER than
 * the reference — and narrower is the safe direction: an unfolded symbol is
 * skipped and REPORTED, where a wrongly folded one is silently mispronounced.
 */
static char fold_latin_cp(unsigned int cp)
{
    switch (cp) {
    case 0x00E1: case 0x00E0: case 0x00E2: case 0x00E4: case 0x00E3:
    case 0x00E5: case 0x0101: case 0x0103: case 0x0105: return 'a';
    case 0x00E9: case 0x00E8: case 0x00EA: case 0x00EB: case 0x0113:
    case 0x0115: case 0x0117: case 0x0119: case 0x011B: return 'e';
    case 0x00ED: case 0x00EC: case 0x00EE: case 0x00EF: case 0x0129:
    case 0x012B: case 0x012D: case 0x012F: return 'i';
    case 0x00F3: case 0x00F2: case 0x00F4: case 0x00F6: case 0x00F5:
    case 0x014D: case 0x014F: case 0x0151: return 'o';
    case 0x00FA: case 0x00F9: case 0x00FB: case 0x00FC: case 0x0169:
    case 0x016B: case 0x016D: case 0x016F: case 0x0171: case 0x0173: return 'u';
    case 0x00F1: case 0x0144: case 0x0146: case 0x0148:
    case 0x1E45: case 0x1E47: case 0x1E4B: return 'n';   /* ṅ ṇ ṋ */
    case 0x00E7: case 0x0107: case 0x0109: case 0x010B: case 0x010D: return 'c';
    case 0x0161: case 0x015B: case 0x015D: case 0x015F: case 0x1E63: return 's'; /* š ṣ */
    case 0x0165: case 0x0163: case 0x1E71: case 0x1E6D: return 't';              /* ṱ ṭ */
    case 0x010F: case 0x0111: case 0x1E13: case 0x1E0D: return 'd';              /* ḓ ḍ */
    case 0x017E: case 0x017A: case 0x017C: return 'z';
    case 0x00FD: case 0x00FF: case 0x0177: return 'y';
    case 0x011F: case 0x011D: case 0x0121: case 0x0123: return 'g';
    case 0x0142: case 0x013A: case 0x013C: case 0x013E: return 'l';
    case 0x0159: case 0x0155: case 0x0157: return 'r';
    default: return 0;
    }
}

/*
 * Nearest stand-ins for `symbol`, best first. Writes NUL-terminated strings into
 * `out` and returns how many.
 */
static size_t approximations(const char *symbol, char out[2][8])
{
    size_t n = 0;

    /* Where the vocabulary carries the true phoneme under a different spelling,
     * use it — Tshivenda's ṅ IS /ŋ/, so that substitution loses nothing. */
    if (strcmp(symbol, "\xE1\xB9\x85") == 0 || strcmp(symbol, "\xE1\xB9\x84") == 0)
        strcpy(out[n++], "\xC5\x8B");                        /* ṅ -> ŋ */
    if (strcmp(symbol, "\xC5\xA1") == 0 || strcmp(symbol, "\xC5\xA0") == 0)
        strcpy(out[n++], "\xCA\x83");                        /* š -> ʃ */

    /* Folding a diacritic away is only defensible where the mark modifies a
     * letter that still carries most of the sound without it — Latin š→s, ṱ→t.
     * In Thai, Burmese, Devanagari, Arabic and Vietnamese the marks ARE the
     * vowels and tones; dropping them does not approximate the word, it deletes
     * it. Thai measured 4.3 s instead of ~15 s because every vowel sign was
     * folded off a consonant and filed as a harmless approximation. */
    {
        char stripped[16];
        size_t w = 0;
        int changed = 0, latin_base = 1;
        for (const char *p = symbol; *p && w + 1 < sizeof stripped; ) {
            size_t len = u8_len(p);
            unsigned int cp = u8_cp(p, len);
            p += len;
            if (is_combining(cp)) { changed = 1; continue; }
            char folded = fold_latin_cp(cp);
            if (folded) { stripped[w++] = folded; changed = 1; }
            else if (cp <= 0x024F) { stripped[w++] = (char)cp; }
            else { latin_base = 0; break; }   /* Thai and friends: refuse */
        }
        stripped[w] = '\0';
        if (changed && latin_base && w > 0 && n < 2) strcpy(out[n++], stripped);
    }

    return n;
}

/* Look up one symbol, following the reference's order exactly. */
static const long long *map_symbol(const circle_voice_piper_config *cfg,
                                   const char *symbol, size_t *count, int *approximated,
                                   long long *scratch, size_t scratch_cap)
{
    *approximated = 0;

    const circle_voice_phoneme_entry *e = find(cfg, symbol);
    if (e) { *count = e->id_count; return e->ids; }

    /* A grapheme voice's vocabulary is built AFTER the model's own cleaner has
     * lower-cased the training text, so it holds no capitals at all — matching
     * on the raw character silently discarded every sentence-initial letter. */
    char lower[64];
    ascii_lower(symbol, lower, sizeof lower);
    if (strcmp(lower, symbol) != 0) {
        e = find(cfg, lower);
        if (e) { *count = e->id_count; return e->ids; }
    }

    /* A GRAPHEME CLUSTER the vocabulary stores as separate codepoints. Splitting
     * it back keeps every mark, so this must be tried BEFORE any approximation. */
    if (u8_count(symbol) > 1) {
        size_t w = 0;
        int whole = 1;
        for (const char *p = symbol; *p; ) {
            size_t len = u8_len(p);
            unsigned int cp = u8_cp(p, len);
            char one[8];
            memcpy(one, p, len);
            one[len] = '\0';
            p += len;

            /* Zero-width formatting characters shape how text is DRAWN and say
             * nothing about how it sounds — one invisible character was failing
             * the whole cluster. */
            if (is_format_cp(cp)) continue;

            const circle_voice_phoneme_entry *part = find(cfg, one);
            if (!part) {
                char onel[8];
                ascii_lower(one, onel, sizeof onel);
                part = find(cfg, onel);
            }
            if (!part) { whole = 0; break; }
            for (size_t i = 0; i < part->id_count && w < scratch_cap; i++)
                scratch[w++] = part->ids[i];
        }
        if (whole && w > 0) { *count = w; return scratch; }   /* exact — nothing lost */
    }

    /* A letter the voice never learned. An approximation is worth more than a
     * hole, so long as it is declared rather than passed off as correct. */
    char cands[2][8];
    size_t ncands = approximations(symbol, cands);
    for (size_t i = 0; i < ncands; i++) {
        e = find(cfg, cands[i]);
        if (!e) {
            char cl[8];
            ascii_lower(cands[i], cl, sizeof cl);
            e = find(cfg, cl);
        }
        if (e) { *approximated = 1; *count = e->id_count; return e->ids; }
    }

    *count = 0;
    return NULL;
}

static void push_unique(char ***list, size_t *count, const char *s)
{
    for (size_t i = 0; i < *count; i++)
        if (strcmp((*list)[i], s) == 0) return;
    char **grown = (char **)realloc(*list, (*count + 1) * sizeof(char *));
    if (!grown) return;
    *list = grown;
    (*list)[*count] = dup_n(s, strlen(s));
    if ((*list)[*count]) (*count)++;
}

size_t circle_voice_phonemes_to_ids(const circle_voice_piper_config *cfg,
                                    const char *const *phonemes, size_t phoneme_count,
                                    long long *out_ids, size_t out_capacity,
                                    circle_voice_mapping *out_mapping)
{
    if (out_mapping) memset(out_mapping, 0, sizeof(*out_mapping));
    if (!cfg) return 0;

    size_t written = 0;
    long long scratch[64];

    #define EMIT(v) do { if (written < out_capacity) out_ids[written] = (v); written++; } while (0)

    const circle_voice_phoneme_entry *bos = find(cfg, BOS_SYMBOL);
    if (bos) for (size_t i = 0; i < bos->id_count; i++) EMIT(bos->ids[i]);
    const circle_voice_phoneme_entry *pad = find(cfg, PAD_SYMBOL);
    if (pad) for (size_t i = 0; i < pad->id_count; i++) EMIT(pad->ids[i]);

    for (size_t p = 0; p < phoneme_count; p++) {
        size_t n = 0;
        int approximated = 0;
        const long long *ids = map_symbol(cfg, phonemes[p], &n, &approximated,
                                          scratch, sizeof scratch / sizeof scratch[0]);
        if (!ids) {
            if (out_mapping) {
                out_mapping->skipped++;
                push_unique(&out_mapping->skipped_symbols,
                            &out_mapping->skipped_symbol_count, phonemes[p]);
            }
            continue;
        }
        if (approximated && out_mapping)
            push_unique(&out_mapping->approximated_symbols,
                        &out_mapping->approximated_symbol_count, phonemes[p]);

        for (size_t i = 0; i < n; i++) EMIT(ids[i]);
        if (pad) for (size_t i = 0; i < pad->id_count; i++) EMIT(pad->ids[i]);
    }

    const circle_voice_phoneme_entry *eos = find(cfg, EOS_SYMBOL);
    if (eos) for (size_t i = 0; i < eos->id_count; i++) EMIT(eos->ids[i]);

    #undef EMIT
    return written;
}

void circle_voice_mapping_free(circle_voice_mapping *m)
{
    if (!m) return;
    for (size_t i = 0; i < m->skipped_symbol_count; i++) free(m->skipped_symbols[i]);
    free(m->skipped_symbols);
    for (size_t i = 0; i < m->approximated_symbol_count; i++) free(m->approximated_symbols[i]);
    free(m->approximated_symbols);
    memset(m, 0, sizeof(*m));
}

size_t circle_voice_split_phoneme_string(const char *text, char ***out_elements)
{
    if (out_elements) *out_elements = NULL;
    if (!text || !*text) return 0;

    char **list = NULL;
    size_t count = 0;

    for (const char *p = text; *p; ) {
        const char *start = p;
        p += u8_len(p);

        /* Swallow every following mark. A combining mark is not a character on
         * its own — it modifies the one before it, and separating them is what
         * turned Thai vowels into unmapped symbols. */
        while (*p) {
            size_t len = u8_len(p);
            if (!is_combining(u8_cp(p, len))) break;
            p += len;
        }

        char **grown = (char **)realloc(list, (count + 1) * sizeof(char *));
        if (!grown) break;
        list = grown;
        list[count] = dup_n(start, (size_t)(p - start));
        if (!list[count]) break;
        count++;
    }

    if (out_elements) *out_elements = list; else circle_voice_string_list_free(list, count);
    return count;
}

void circle_voice_string_list_free(char **list, size_t count)
{
    if (!list) return;
    for (size_t i = 0; i < count; i++) free(list[i]);
    free(list);
}

/* ── LexiconTokeniser ────────────────────────────────────────────────────── */
/*
 * strtok_r is POSIX-only and MSVC spells it strtok_s, so neither name is
 * portable enough for this port. Split by hand: skip leading spaces, cut at the
 * next one, and leave the cursor NULL once the line is exhausted.
 */
static char *split_field(char **cursor)
{
    char *s = *cursor;
    if (!s) return NULL;
    while (*s == ' ') s++;
    if (!*s) { *cursor = NULL; return NULL; }
    char *start = s;
    while (*s && *s != ' ') s++;
    if (*s) { *s = '\0'; *cursor = s + 1; } else { *cursor = NULL; }
    return start;
}


typedef struct {
    char *word;
    long long *ids;
    size_t id_count;
    size_t char_count;
} lex_word;

struct circle_voice_lexicon {
    lex_word *words;
    size_t word_count;
    size_t longest;
    long long blank;
    char **unmapped;
    size_t unmapped_count;
};

circle_voice_lexicon *circle_voice_lexicon_new(const char *tokens_text,
                                               const char *lexicon_text,
                                               long long blank)
{
    if (!tokens_text || !lexicon_text) return NULL;

    /* tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE, so
     * split on the LAST space rather than the first. */
    typedef struct { char *sym; long long id; } tok;
    tok *toks = NULL;
    size_t ntoks = 0;

    for (const char *line = tokens_text; *line; ) {
        const char *end = strchr(line, '\n');
        size_t len = end ? (size_t)(end - line) : strlen(line);
        while (len && (line[len - 1] == '\r')) len--;

        const char *cut = NULL;
        for (size_t i = 0; i < len; i++) if (line[i] == ' ') cut = line + i;
        if (cut && cut > line) {
            char idbuf[32];
            size_t idlen = (size_t)(line + len - cut - 1);
            if (idlen > 0 && idlen < sizeof idbuf) {
                memcpy(idbuf, cut + 1, idlen);
                idbuf[idlen] = '\0';
                char *endp = NULL;
                long long id = strtoll(idbuf, &endp, 10);
                if (endp && *endp == '\0') {
                    tok *g = (tok *)realloc(toks, (ntoks + 1) * sizeof(tok));
                    if (g) {
                        toks = g;
                        toks[ntoks].sym = dup_n(line, (size_t)(cut - line));
                        toks[ntoks].id = id;
                        if (toks[ntoks].sym) ntoks++;
                    }
                }
            }
        }
        if (!end) break;
        line = end + 1;
    }
    if (ntoks == 0) { free(toks); return NULL; }

    /* lexicon.txt is "<word> <phoneme> <phoneme> ...". */
    lex_word *words = NULL;
    size_t nwords = 0, longest = 1;

    for (const char *line = lexicon_text; *line; ) {
        const char *end = strchr(line, '\n');
        size_t len = end ? (size_t)(end - line) : strlen(line);
        while (len && line[len - 1] == '\r') len--;

        char buf[512];
        if (len > 0 && len < sizeof buf) {
            memcpy(buf, line, len);
            buf[len] = '\0';

            char *save = buf;
            char *word = split_field(&save);
            if (word) {
                long long ids[64];
                size_t nids = 0;
                for (char *p = split_field(&save); p && nids < 64;
                     p = split_field(&save)) {
                    for (size_t i = 0; i < ntoks; i++)
                        if (strcmp(toks[i].sym, p) == 0) { ids[nids++] = toks[i].id; break; }
                }
                if (nids > 0) {
                    lex_word *g = (lex_word *)realloc(words, (nwords + 1) * sizeof(lex_word));
                    if (g) {
                        words = g;
                        words[nwords].word = dup_n(word, strlen(word));
                        words[nwords].ids = (long long *)malloc(nids * sizeof(long long));
                        if (words[nwords].word && words[nwords].ids) {
                            memcpy(words[nwords].ids, ids, nids * sizeof(long long));
                            words[nwords].id_count = nids;
                            words[nwords].char_count = u8_count(words[nwords].word);
                            if (words[nwords].char_count > longest) longest = words[nwords].char_count;
                            nwords++;
                        }
                    }
                }
            }
        }
        if (!end) break;
        line = end + 1;
    }

    for (size_t i = 0; i < ntoks; i++) free(toks[i].sym);
    free(toks);
    if (nwords == 0) { free(words); return NULL; }

    circle_voice_lexicon *lex = (circle_voice_lexicon *)calloc(1, sizeof(circle_voice_lexicon));
    if (!lex) return NULL;
    lex->words = words;
    lex->word_count = nwords;
    lex->longest = longest;
    lex->blank = blank;
    return lex;
}

void circle_voice_lexicon_free(circle_voice_lexicon *lex)
{
    if (!lex) return;
    for (size_t i = 0; i < lex->word_count; i++) { free(lex->words[i].word); free(lex->words[i].ids); }
    free(lex->words);
    for (size_t i = 0; i < lex->unmapped_count; i++) free(lex->unmapped[i]);
    free(lex->unmapped);
    free(lex);
}

size_t circle_voice_lexicon_encode(circle_voice_lexicon *lex, const char *text,
                                   int interleave_blank,
                                   long long *out_ids, size_t out_capacity)
{
    if (!lex || !text) return 0;

    for (size_t i = 0; i < lex->unmapped_count; i++) free(lex->unmapped[i]);
    free(lex->unmapped);
    lex->unmapped = NULL;
    lex->unmapped_count = 0;

    /* Byte offset of each character boundary, so the scan indexes CHARACTERS
     * while the words stay plain byte ranges — a byte index would cut a CJK
     * character in half and match nothing. */
    size_t n = u8_count(text);
    size_t *off = (size_t *)malloc((n + 1) * sizeof(size_t));
    if (!off) return 0;
    { size_t k = 0, b = 0; while (text[b]) { off[k++] = b; b += u8_len(text + b); } off[n] = b; }

    long long bare[512];
    size_t nbare = 0;

    for (size_t i = 0; i < n; ) {
        size_t taken = 0;
        size_t max = lex->longest < (n - i) ? lex->longest : (n - i);
        for (size_t len = max; len > 0 && taken == 0; len--) {
            size_t blen = off[i + len] - off[i];
            for (size_t w = 0; w < lex->word_count; w++) {
                if (lex->words[w].char_count != len) continue;
                if (strlen(lex->words[w].word) != blen) continue;
                if (memcmp(lex->words[w].word, text + off[i], blen) != 0) continue;
                for (size_t k = 0; k < lex->words[w].id_count && nbare < 512; k++)
                    bare[nbare++] = lex->words[w].ids[k];
                taken = len;
                break;
            }
        }
        if (taken == 0) {
            size_t blen = off[i + 1] - off[i];
            char one[8];
            memcpy(one, text + off[i], blen);
            one[blen] = '\0';
            if (!(blen == 1 && (one[0] == ' ' || one[0] == '\t' || one[0] == '\n' || one[0] == '\r')))
                push_unique(&lex->unmapped, &lex->unmapped_count, one);
            taken = 1;
        }
        i += taken;
    }
    free(off);

    if (!interleave_blank) {
        for (size_t i = 0; i < nbare && i < out_capacity; i++) out_ids[i] = bare[i];
        return nbare;
    }

    /* add_blank: a blank opens the utterance and follows every token. */
    size_t written = 0;
    #define PUT(v) do { if (written < out_capacity) out_ids[written] = (v); written++; } while (0)
    PUT(lex->blank);
    for (size_t i = 0; i < nbare; i++) { PUT(bare[i]); PUT(lex->blank); }
    #undef PUT
    return written;
}

size_t circle_voice_lexicon_unmapped(const circle_voice_lexicon *lex,
                                     const char *const **out_symbols)
{
    if (!lex) return 0;
    if (out_symbols) *out_symbols = (const char *const *)lex->unmapped;
    return lex->unmapped_count;
}
