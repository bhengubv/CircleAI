/*
 * test_voice_piper.c — asserts the C PiperVoiceConfig / LexiconTokeniser /
 * AudioFormat ports against the same answers the C# reference generates.
 *
 * THE EXPECTED VALUES ARE TRANSCRIBED FROM THE FIXTURES, not parsed from them,
 * for the same reason as test_voice_parity.c: this port has no JSON reader and
 * will not vendor one. The other eight ports read
 * fixtures/voice_piper_config.json, voice_lexicon_tokeniser.json and
 * voice_audio_format.json directly, which is stronger. That makes this the one
 * port where a fixture can drift without a test failing — so if you change a
 * fixture, change these literals in the same commit.
 *
 * The two configs disagree about pad on purpose — 0 in the Piper-layout one, 3
 * in the MMS-layout one — so a port that hard-codes either fails the other.
 * That is THE PAD RULE, and getting it wrong is what made 42 MMS voices speak
 * fluent nonsense.
 */

#include "circle_ai/voice_piper.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures = 0;
static int checks = 0;

static void check(int cond, const char *what)
{
    checks++;
    if (!cond) { printf("  FAIL: %s\n", what); failures++; }
}

static void check_ids(const long long *actual, size_t actual_count,
                      const long long *expected, size_t expected_count, const char *what)
{
    checks++;
    if (actual_count != expected_count) {
        printf("  FAIL: %s — got %zu ids, want %zu\n", what, actual_count, expected_count);
        failures++;
        return;
    }
    for (size_t i = 0; i < expected_count; i++) {
        if (actual[i] != expected[i]) {
            printf("  FAIL: %s — id %zu is %lld, want %lld\n",
                   what, i, actual[i], expected[i]);
            failures++;
            return;
        }
    }
}

static void check_strv(char **actual, size_t actual_count,
                       const char *const *expected, size_t expected_count,
                       const char *what)
{
    checks++;
    if (actual_count != expected_count) {
        printf("  FAIL: %s — got %zu items, want %zu\n", what, actual_count, expected_count);
        failures++;
        return;
    }
    for (size_t i = 0; i < expected_count; i++) {
        if (strcmp(actual[i], expected[i]) != 0) {
            printf("  FAIL: %s — item %zu is \"%s\", want \"%s\"\n",
                   what, i, actual[i], expected[i]);
            failures++;
            return;
        }
    }
}

/* ---- the two vocabularies, from fixtures/voice_piper_config.json --------- */

/* "piper-like (pad=0, has BOS/EOS)", sampleRate 22050 */
static const long long piper_ids[][1] = {
    {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12}, {13},
};
static const circle_voice_phoneme_entry piper_entries[] = {
    {"_", piper_ids[0], 1},  {"^", piper_ids[1], 1},  {"$", piper_ids[2], 1},
    {" ", piper_ids[3], 1},  {"a", piper_ids[4], 1},  {"b", piper_ids[5], 1},
    {"k", piper_ids[6], 1},  {"s", piper_ids[7], 1},  {"t", piper_ids[8], 1},
    {"n", piper_ids[9], 1},  {"ŋ", piper_ids[10], 1}, {"ʃ", piper_ids[11], 1},
    {"d", piper_ids[12], 1}, {"ɡ", piper_ids[13], 1},
};

/* "mms-like (pad=3, no BOS/EOS)", sampleRate 16000. Note "_" is 3, not 0 —
 * the MMS exports put a real token at 0 and blank at 3. */
static const long long mms_ids[][1] = {
    {0}, {1}, {2}, {3}, {3}, {4}, {5}, {6}, {7}, {8}, {9},
};
static const circle_voice_phoneme_entry mms_entries[] = {
    {"<PAD>", mms_ids[0], 1}, {"<EOS>", mms_ids[1], 1}, {"<BOS>", mms_ids[2], 1},
    {"<BLNK>", mms_ids[3], 1}, {"_", mms_ids[4], 1},
    {"a", mms_ids[5], 1}, {"b", mms_ids[6], 1}, {"k", mms_ids[7], 1},
    {"s", mms_ids[8], 1}, {"t", mms_ids[9], 1}, {"n", mms_ids[10], 1},
};

typedef struct {
    const char *const *phonemes;
    size_t phoneme_count;
    const long long *ids;
    size_t id_count;
    size_t skipped;
    const char *const *skipped_symbols;
    size_t skipped_symbol_count;
    const char *const *approximated_symbols;
    size_t approximated_symbol_count;
} piper_case;

static void run_cases(const circle_voice_piper_config *cfg,
                      const piper_case *cases, size_t case_count, const char *name)
{
    for (size_t c = 0; c < case_count; c++) {
        long long got[64];
        circle_voice_mapping m;
        size_t n = circle_voice_phonemes_to_ids(cfg, cases[c].phonemes,
                                                cases[c].phoneme_count,
                                                got, 64, &m);
        char what[128];
        snprintf(what, sizeof what, "%s case %zu ids", name, c);
        check_ids(got, n, cases[c].ids, cases[c].id_count, what);

        snprintf(what, sizeof what, "%s case %zu skipped", name, c);
        check(m.skipped == cases[c].skipped, what);

        snprintf(what, sizeof what, "%s case %zu skippedSymbols", name, c);
        check_strv(m.skipped_symbols, m.skipped_symbol_count,
                   cases[c].skipped_symbols, cases[c].skipped_symbol_count, what);

        snprintf(what, sizeof what, "%s case %zu approximatedSymbols", name, c);
        check_strv(m.approximated_symbols, m.approximated_symbol_count,
                   cases[c].approximated_symbols, cases[c].approximated_symbol_count, what);

        circle_voice_mapping_free(&m);
    }
}

static const char *const p_bat[]  = {"b", "a", "t"};
static const char *const p_BAT[]  = {"B", "A", "T"};
static const char *const p_unk[]  = {"a", "ZZZ", "t"};
static const char *const p_ndot[] = {"ṅ", "a"};
static const char *const p_scar[] = {"š", "a"};
static const char *const p_tcir[] = {"ṱ", "a"};
static const char *const p_thai[] = {"ก", "a"};

static const char *const sym_zzz[]  = {"ZZZ"};
static const char *const sym_ndot[] = {"ṅ"};
static const char *const sym_scar[] = {"š"};
static const char *const sym_tcir[] = {"ṱ"};
static const char *const sym_thai[] = {"ก"};

static void test_piper_config(void)
{
    printf("PiperVoiceConfig\n");

    const circle_voice_piper_config piper = {
        piper_entries, sizeof piper_entries / sizeof piper_entries[0]
    };
    const circle_voice_piper_config mms = {
        mms_entries, sizeof mms_entries / sizeof mms_entries[0]
    };

    /* THE PAD RULE. The two configs disagree, so a hard-coded constant fails. */
    check(circle_voice_pad_id(&piper) == 0, "piper-like padId is 0");
    check(circle_voice_pad_id(&mms) == 3, "mms-like padId is 3");
    check(circle_voice_has_phoneme_map(&piper), "piper-like hasPhonemeMap");
    check(circle_voice_has_phoneme_map(&mms), "mms-like hasPhonemeMap");

    /* [BOS, PAD, id, PAD, ..., EOS] — BOS/EOS only because this vocab HAS them. */
    static const long long piper_bat[]  = {1, 0, 5, 0, 4, 0, 8, 0, 2};
    static const long long piper_unk[]  = {1, 0, 4, 0, 8, 0, 2};
    static const long long piper_ndot[] = {1, 0, 10, 0, 4, 0, 2};
    static const long long piper_scar[] = {1, 0, 11, 0, 4, 0, 2};
    static const long long piper_tcir[] = {1, 0, 8, 0, 4, 0, 2};
    static const long long piper_thai[] = {1, 0, 4, 0, 2};

    static const piper_case piper_cases[] = {
        {p_bat, 3, piper_bat, 9, 0, NULL, 0, NULL, 0},
        {p_BAT, 3, piper_bat, 9, 0, NULL, 0, NULL, 0},
        {p_unk, 3, piper_unk, 7, 1, sym_zzz, 1, NULL, 0},
        {p_ndot, 2, piper_ndot, 7, 0, NULL, 0, sym_ndot, 1},
        {p_scar, 2, piper_scar, 7, 0, NULL, 0, sym_scar, 1},
        {p_tcir, 2, piper_tcir, 7, 0, NULL, 0, sym_tcir, 1},
        {p_thai, 2, piper_thai, 5, 1, sym_thai, 1, NULL, 0},
    };
    run_cases(&piper, piper_cases, 7, "piper-like");

    /* No BOS/EOS in this vocabulary, and the two folds land on n and s because
     * it carries neither of the IPA letters the piper-layout one does. */
    static const long long mms_bat[]  = {3, 5, 3, 4, 3, 8, 3};
    static const long long mms_unk[]  = {3, 4, 3, 8, 3};
    static const long long mms_ndot[] = {3, 9, 3, 4, 3};
    static const long long mms_scar[] = {3, 7, 3, 4, 3};
    static const long long mms_tcir[] = {3, 8, 3, 4, 3};
    static const long long mms_thai[] = {3, 4, 3};

    static const piper_case mms_cases[] = {
        {p_bat, 3, mms_bat, 7, 0, NULL, 0, NULL, 0},
        {p_BAT, 3, mms_bat, 7, 0, NULL, 0, NULL, 0},
        {p_unk, 3, mms_unk, 5, 1, sym_zzz, 1, NULL, 0},
        {p_ndot, 2, mms_ndot, 5, 0, NULL, 0, sym_ndot, 1},
        {p_scar, 2, mms_scar, 5, 0, NULL, 0, sym_scar, 1},
        {p_tcir, 2, mms_tcir, 5, 0, NULL, 0, sym_tcir, 1},
        {p_thai, 2, mms_thai, 3, 1, sym_thai, 1, NULL, 0},
    };
    run_cases(&mms, mms_cases, 7, "mms-like");
}

static void test_thai_is_not_folded(void)
{
    printf("Thai is refused where Tshivenda is approximated\n");

    /* The asymmetry is the whole point. Latin t-with-a-mark still sounds like a
     * t once the mark is gone; in Thai the marks ARE the vowels, so folding
     * deletes the word rather than approximating it. */
    const circle_voice_piper_config piper = {
        piper_entries, sizeof piper_entries / sizeof piper_entries[0]
    };

    long long ids[16];
    circle_voice_mapping m;

    circle_voice_phonemes_to_ids(&piper, sym_tcir, 1, ids, 16, &m);
    check_strv(m.approximated_symbols, m.approximated_symbol_count, sym_tcir, 1,
               "the Tshivenda letter folds to a Latin base and is REPORTED");
    check(m.skipped_symbol_count == 0, "the Tshivenda letter is not skipped");
    circle_voice_mapping_free(&m);

    circle_voice_phonemes_to_ids(&piper, sym_thai, 1, ids, 16, &m);
    check_strv(m.skipped_symbols, m.skipped_symbol_count, sym_thai, 1,
               "Thai is skipped, not folded");
    check(m.approximated_symbol_count == 0, "Thai is not filed as an approximation");
    circle_voice_mapping_free(&m);
}

static void test_split_phoneme_string(void)
{
    printf("splitPhonemeString\n");

    static const char *const want_bat[] = {"b", "a", "t"};
    static const char *const want_acute[] = {"b", "á", "t"};
    static const char *const want_thai[] = {"กั", "b"};

    static const struct {
        const char *input;
        const char *const *want;
        size_t count;
    } cases[] = {
        {"bat", want_bat, 3},
        {"bát", want_acute, 3},
        {"กัb", want_thai, 2},   /* three codepoints, TWO written units */
    };

    for (size_t i = 0; i < 3; i++) {
        char **got = NULL;
        size_t n = circle_voice_split_phoneme_string(cases[i].input, &got);
        char what[128];
        snprintf(what, sizeof what, "clusters for %s", cases[i].input);
        check_strv(got, n, cases[i].want, cases[i].count, what);
        circle_voice_string_list_free(got, n);
    }
}

/* ---- LexiconTokeniser, from fixtures/voice_lexicon_tokeniser.json -------- */

static const char *TOKENS_TEXT =
    "<blank> 0\n" "a 1\n" "i 2\n" "s 3\n" "ts 4\n" "k 5\n"
    "w 6\n" "r 7\n" "u 8\n" "n 9\n" "o 10\n";

/* The fourth word carries a phoneme the token list does NOT have — it must
 * drop out of the ids rather than fail the whole entry. */
static const char *LEXICON_TEXT =
    "あ a\n"
    "あい a i\n"
    "あいさつ a i s a ts u\n"
    "あいかわらず a i k a w a r a z u\n"
    "ん n\n";

typedef struct {
    const char *text;
    const long long *ids;
    size_t id_count;
    const long long *with_blank;
    size_t with_blank_count;
    const char *const *unmapped;
    size_t unmapped_count;
} lex_case;

static void test_lexicon(void)
{
    printf("LexiconTokeniser\n");

    circle_voice_lexicon *lex = circle_voice_lexicon_new(TOKENS_TEXT, LEXICON_TEXT, 0);
    check(lex != NULL, "fixture lexicon loads");
    if (!lex) return;

    static const long long ids0[] = {1, 2, 3, 1, 4, 8};
    static const long long blk0[] = {0, 1, 0, 2, 0, 3, 0, 1, 0, 4, 0, 8, 0};
    static const long long ids1[] = {1, 2};
    static const long long blk1[] = {0, 1, 0, 2, 0};
    static const long long ids2[] = {1};
    static const long long blk2[] = {0, 1, 0};
    static const long long ids3[] = {1, 2, 5, 1, 6, 1, 7, 1, 8};
    static const long long blk3[] = {0, 1, 0, 2, 0, 5, 0, 1, 0, 6, 0, 1, 0, 7, 0, 1, 0, 8, 0};
    static const long long ids4[] = {1, 2, 9};
    static const long long blk4[] = {0, 1, 0, 2, 0, 9, 0};
    static const long long ids5[] = {1};
    static const long long blk5[] = {0, 1, 0};
    static const char *const unmapped5[] = {"X", "い"};

    static const lex_case cases[] = {
        {"あいさつ", ids0, 6, blk0, 13, NULL, 0},
        {"あい", ids1, 2, blk1, 5, NULL, 0},
        {"あ", ids2, 1, blk2, 3, NULL, 0},
        {"あいかわらず", ids3, 9, blk3, 19, NULL, 0},
        {"あい ん", ids4, 3, blk4, 7, NULL, 0},   /* whitespace is not "unmapped" */
        {"あXい", ids5, 1, blk5, 3, unmapped5, 2},
    };

    for (size_t i = 0; i < 6; i++) {
        long long got[64];
        char what[128];

        size_t n = circle_voice_lexicon_encode(lex, cases[i].text, 0, got, 64);
        snprintf(what, sizeof what, "ids for %s", cases[i].text);
        check_ids(got, n, cases[i].ids, cases[i].id_count, what);

        const char *const *unmapped = NULL;
        size_t un = circle_voice_lexicon_unmapped(lex, &unmapped);
        snprintf(what, sizeof what, "unmapped for %s", cases[i].text);
        check_strv((char **)unmapped, un,
                   cases[i].unmapped, cases[i].unmapped_count, what);

        n = circle_voice_lexicon_encode(lex, cases[i].text, 1, got, 64);
        snprintf(what, sizeof what, "idsWithBlank for %s", cases[i].text);
        check_ids(got, n, cases[i].with_blank, cases[i].with_blank_count, what);
    }

    /* Three of the fixture words start with the same two characters. Taking the
     * shortest match pronounces a DIFFERENT word. */
    long long full[64], prefix[64];
    size_t nf = circle_voice_lexicon_encode(lex, "あいさつ", 0, full, 64);
    size_t np = circle_voice_lexicon_encode(lex, "あい", 0, prefix, 64);
    check(nf > np, "the longer word matched only its prefix — this is shortest-match");

    circle_voice_lexicon_free(lex);
}

/* ---- AudioFormat, from fixtures/voice_audio_format.json ------------------ */

static void test_audio_format(void)
{
    printf("AudioFormat\n");
    circle_voice_audio_format f = circle_voice_pcm16_mono_16k();
    check(f.sample_rate == 16000, "sampleRate is 16000");
    check(f.channels == 1, "channels is 1");
    check(f.bits_per_sample == 16, "bitsPerSample is 16");
}

int main(void)
{
    printf("=== voice piper/lexicon/audio parity ===\n");
    test_piper_config();
    test_thai_is_not_folded();
    test_split_phoneme_string();
    test_lexicon();
    test_audio_format();
    printf("%d checks, %d failures\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
