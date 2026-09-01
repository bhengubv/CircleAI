/*
 * test_voice_text.c — asserts the C SentenceSplitter / LanguageSpanSplitter /
 * GeezRomanizer / ToneShaper / NchltPhonemizer ports against the same answers
 * the C# reference generates.
 *
 * THE EXPECTED VALUES LIVE IN voice_text_expected.h AS LITERALS, not parsed from
 * the fixtures. This port has no JSON reader and will not vendor one; the other
 * eight ports read the fixtures directly, which is stronger. That makes this the
 * one port where a fixture can drift without a test failing.
 *
 * The header is GENERATED rather than hand-typed, because 300 lines of
 * transcription is 300 chances to mistype a codepoint. Regenerate it after any
 * fixture change, IN THE SAME COMMIT:
 *
 *   python tools/gen_c_voice_expected.py fixtures c/tests/voice_text_expected.h
 *
 * The cases are adversarial: a decimal point and a domain name that must NOT
 * split next to a danda and a CJK stop that must; the Ethiopic numerals that
 * used to romanise as syllables; and a tone fixture that separates the biquad
 * (bit-reproducible) from the coefficient derivation (pow/sin/cos, which no
 * language guarantees to the last bit).
 */

#include "circle_ai/voice_text.h"

#include <math.h>
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

static void check_str(const char *got, const char *want, const char *what)
{
    checks++;
    if (!got || strcmp(got, want) != 0) {
        printf("  FAIL: %s — got \"%s\", want \"%s\"\n", what, got ? got : "(null)", want);
        failures++;
    }
}

static void check_close(double got, double want, double tol, const char *what)
{
    checks++;
    double scale = fabs(want) > 1.0 ? fabs(want) : 1.0;
    if (fabs(got - want) > tol * scale) {
        printf("  FAIL: %s — got %.17g, want %.17g\n", what, got, want);
        failures++;
    }
}

/* Shapes the generated header fills in. */
typedef struct { const char *text; int pause; } seg_expect;
typedef struct {
    const char *name; const char *text;
    const seg_expect *segments; size_t count;
} splitter_case;

typedef struct { const char *text; int is_foreign; } span_expect;
typedef struct { const char *text; const span_expect *spans; size_t count; } spans_case;

typedef struct { const char *input; const char *output; } pair_case;
typedef struct { const char *input; int flag; } flag_case;

typedef struct {
    double low_shelf_hz, low_shelf_db, presence_hz, presence_db, presence_q, slope;
} settings_expect;
typedef struct {
    int rate;
    double ls_b[3], ls_a[3], pk_b[3], pk_a[3];
} coeff_case;

typedef struct {
    const char *name; const char *text;
    const char *const *phones; size_t phone_count;
    int rule_predicted; const char *const *unknown; size_t unknown_count;
} nchlt_case;
typedef struct {
    const char *word; const char *const *phones; size_t phone_count;
} predict_case;

#include "voice_text_expected.h"

#define COUNT(a) (sizeof (a) / sizeof (a)[0])

/* ---- SentenceSplitter --------------------------------------------------- */

static void test_sentence_splitter(void)
{
    printf("SentenceSplitter\n");

    check(CA_MAX_CHARS_PER_SEGMENT == SPLITTER_MAX_CHARS, "MaxCharsPerSegment");

    for (size_t c = 0; c < COUNT(SPLITTER_CASES); c++) {
        ca_speech_segment_t got[32];
        size_t n = ca_split_sentences(SPLITTER_CASES[c].text, got, 32);

        char what[160];
        snprintf(what, sizeof what, "%s segment count", SPLITTER_CASES[c].name);
        checks++;
        if (n != SPLITTER_CASES[c].count) {
            printf("  FAIL: %s — got %zu segments, want %zu\n",
                   what, n, SPLITTER_CASES[c].count);
            failures++;
            ca_speech_segments_free(got, n < 32 ? n : 32);
            continue;
        }

        for (size_t i = 0; i < n; i++) {
            snprintf(what, sizeof what, "%s segment %zu text", SPLITTER_CASES[c].name, i);
            check_str(got[i].text, SPLITTER_CASES[c].segments[i].text, what);
            snprintf(what, sizeof what, "%s segment %zu pause", SPLITTER_CASES[c].name, i);
            check(got[i].trailing_pause_ms == SPLITTER_CASES[c].segments[i].pause, what);
        }
        ca_speech_segments_free(got, n);
    }
}

static const splitter_case *find_case(const char *name)
{
    for (size_t i = 0; i < COUNT(SPLITTER_CASES); i++)
        if (strcmp(SPLITTER_CASES[i].name, name) == 0) return &SPLITTER_CASES[i];
    return NULL;
}

static void test_splits_non_latin_scripts(void)
{
    printf("Scripts that do not punctuate in Latin still split\n");

    /* A Latin-only terminator list under-splits for about a billion people and
     * fails silently — the paragraph simply runs together. */
    static const char *const names[] = {
        "devanagari-danda", "urdu-full-stop", "cjk-no-space", "khmer-khan",
    };

    for (size_t i = 0; i < COUNT(names); i++) {
        const splitter_case *c = find_case(names[i]);
        checks++;
        if (!c) { printf("  FAIL: no case named %s\n", names[i]); failures++; continue; }

        ca_speech_segment_t got[32];
        size_t n = ca_split_sentences(c->text, got, 32);
        if (n < 2) { printf("  FAIL: %s produced %zu segments\n", names[i], n); failures++; }
        ca_speech_segments_free(got, n < 32 ? n : 32);
    }
}

static void test_does_not_split_decimal_or_domain(void)
{
    printf("A decimal point and a domain name do not split\n");

    static const char *const names[] = { "decimal-point", "domain-name" };
    for (size_t i = 0; i < COUNT(names); i++) {
        const splitter_case *c = find_case(names[i]);
        checks++;
        if (!c) { printf("  FAIL: no case named %s\n", names[i]); failures++; continue; }

        ca_speech_segment_t got[32];
        size_t n = ca_split_sentences(c->text, got, 32);
        if (n != 2) { printf("  FAIL: %s produced %zu segments, want 2\n", names[i], n); failures++; }
        ca_speech_segments_free(got, n < 32 ? n : 32);
    }
}

/* ---- LanguageSpanSplitter ----------------------------------------------- */

static void test_language_spans(void)
{
    printf("LanguageSpanSplitter\n");

    for (size_t c = 0; c < COUNT(SPANS_CASES); c++) {
        ca_language_span_t got[16];
        size_t n = ca_split_language_spans(SPANS_CASES[c].text, got, 16);

        char what[160];
        snprintf(what, sizeof what, "span count for %s", SPANS_CASES[c].text);
        checks++;
        if (n != SPANS_CASES[c].count) {
            printf("  FAIL: %s — got %zu, want %zu\n", what, n, SPANS_CASES[c].count);
            failures++;
            ca_language_spans_free(got, n < 16 ? n : 16);
            continue;
        }

        for (size_t i = 0; i < n; i++) {
            snprintf(what, sizeof what, "span %zu text of %s", i, SPANS_CASES[c].text);
            check_str(got[i].text, SPANS_CASES[c].spans[i].text, what);
            snprintf(what, sizeof what, "span %zu flag of %s", i, SPANS_CASES[c].text);
            check(got[i].is_foreign == SPANS_CASES[c].spans[i].is_foreign, what);
        }
        ca_language_spans_free(got, n);
    }

    for (size_t i = 0; i < COUNT(SPOKEN_CASES); i++) {
        char *got = ca_to_spoken_form(SPOKEN_CASES[i].input);
        char what[160];
        snprintf(what, sizeof what, "spoken form of %s", SPOKEN_CASES[i].input);
        check_str(got, SPOKEN_CASES[i].output, what);
        free(got);
    }

    for (size_t i = 0; i < COUNT(FOREIGN_CASES); i++) {
        char what[160];
        snprintf(what, sizeof what, "isForeignWord(%s)", FOREIGN_CASES[i].input);
        check(ca_is_foreign_word(FOREIGN_CASES[i].input) == FOREIGN_CASES[i].flag, what);
    }

    /* The conservatism is the contract, not an accident: guessing wrong
     * mispronounces a native word to fix a foreign one. */
    check(!ca_is_foreign_word("hello"), "an ordinary word is not foreign");
    check(!ca_is_foreign_word("Ngiyabonga"), "a capitalised native word is not foreign");
}

/* ---- GeezRomanizer ------------------------------------------------------ */

static void test_geez(void)
{
    printf("GeezRomanizer\n");

    for (size_t i = 0; i < COUNT(ETHIOPIC_CASES); i++) {
        char what[160];
        snprintf(what, sizeof what, "isEthiopic(%s)", ETHIOPIC_CASES[i].input);
        check(ca_is_ethiopic(ETHIOPIC_CASES[i].input) == ETHIOPIC_CASES[i].flag, what);
    }

    for (size_t i = 0; i < COUNT(ROMANIZE_CASES); i++) {
        char *got = ca_geez_romanize(ROMANIZE_CASES[i].input);
        char what[160];
        snprintf(what, sizeof what, "romanize(%s)", ROMANIZE_CASES[i].input);
        check_str(got, ROMANIZE_CASES[i].output, what);
        free(got);
    }

    /* The eight-per-consonant layout stops at U+1357. Sizing the range check off
     * the consonant table swept seven numerals back into the syllabary, and they
     * came out as sound, so nothing failed. */
    char *numerals = ca_geez_romanize("\xe1\x8d\xa9\xe1\x8d\xaa\xe1\x8d\xab");
    check_str(numerals, "", "the numerals have no sound to render");
    free(numerals);

    char *lone = ca_geez_romanize("\xe1\x8d\x98\xe1\x8d\x99\xe1\x8d\x9a");
    check_str(lone, "ryamyafya", "the three LONE syllables are not a row of eight");
    free(lone);
}

/* ---- ToneShaper --------------------------------------------------------- */

static float peak_of(const float *x, size_t n)
{
    float p = 0;
    for (size_t i = 0; i < n; i++) { float a = x[i] < 0 ? -x[i] : x[i]; if (a > p) p = a; }
    return p;
}

static void test_tone_shaper(void)
{
    printf("ToneShaper\n");

    ca_tone_shaper_t warm = ca_tone_shaper_warm();
    check(warm.low_shelf_hz == TONE_SETTINGS.low_shelf_hz, "lowShelfHz");
    check(warm.low_shelf_db == TONE_SETTINGS.low_shelf_db, "lowShelfDb");
    check(warm.presence_hz == TONE_SETTINGS.presence_hz, "presenceHz");
    check(warm.presence_db == TONE_SETTINGS.presence_db, "presenceDb");
    check(warm.presence_q == TONE_SETTINGS.presence_q, "presenceQ");
    check(TONE_SETTINGS.slope == 0.9, "the shelf slope is fixed at 0.9");

    /* 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
     * languages, and pretending otherwise makes a flaky test, not a strict one. */
    for (size_t i = 0; i < COUNT(COEFF_CASES); i++) {
        ca_biquad_coefficients_t ls = ca_low_shelf_coefficients(&warm, COEFF_CASES[i].rate);
        ca_biquad_coefficients_t pk = ca_peaking_coefficients(&warm, COEFF_CASES[i].rate);
        for (int k = 0; k < 3; k++) {
            char what[96];
            snprintf(what, sizeof what, "lowShelf b[%d] at %d", k, COEFF_CASES[i].rate);
            check_close(ls.b[k], COEFF_CASES[i].ls_b[k], TONE_COEFFICIENT_TOLERANCE, what);
            snprintf(what, sizeof what, "lowShelf a[%d] at %d", k, COEFF_CASES[i].rate);
            check_close(ls.a[k], COEFF_CASES[i].ls_a[k], TONE_COEFFICIENT_TOLERANCE, what);
            snprintf(what, sizeof what, "peaking b[%d] at %d", k, COEFF_CASES[i].rate);
            check_close(pk.b[k], COEFF_CASES[i].pk_b[k], TONE_COEFFICIENT_TOLERANCE, what);
            snprintf(what, sizeof what, "peaking a[%d] at %d", k, COEFF_CASES[i].rate);
            check_close(pk.a[k], COEFF_CASES[i].pk_a[k], TONE_COEFFICIENT_TOLERANCE, what);
        }
    }

    /* The biquad is add and multiply on doubles, so THIS half is expected to
     * agree everywhere. Driving it from the fixture's own coefficients keeps the
     * transcendental functions out of the comparison. */
    const coeff_case *entry = NULL;
    for (size_t i = 0; i < COUNT(COEFF_CASES); i++)
        if (COEFF_CASES[i].rate == TONE_SAMPLE_RATE) { entry = &COEFF_CASES[i]; break; }
    check(entry != NULL, "coefficients for the waveform's sample rate");
    if (!entry) return;

    size_t n = COUNT(TONE_INPUT);
    float *x = (float *)malloc(n * sizeof(float));
    if (!x) { check(0, "allocation"); return; }
    for (size_t i = 0; i < n; i++) x[i] = (float)TONE_INPUT[i];

    ca_biquad_coefficients_t ls, pk;
    memcpy(ls.b, entry->ls_b, sizeof ls.b);
    memcpy(ls.a, entry->ls_a, sizeof ls.a);
    memcpy(pk.b, entry->pk_b, sizeof pk.b);
    memcpy(pk.a, entry->pk_a, sizeof pk.a);

    float before = peak_of(x, n);
    ca_biquad(x, n, &ls);
    ca_biquad(x, n, &pk);
    float after = peak_of(x, n);
    if (after > 0.0f && after > before) {
        float g = before / after;
        for (size_t i = 0; i < n; i++) x[i] *= g;
    }

    for (size_t i = 0; i < COUNT(TONE_OUTPUT); i++) {
        char what[64];
        snprintf(what, sizeof what, "sample %zu", i);
        check_close(x[i], TONE_OUTPUT[i], TONE_WAVEFORM_TOLERANCE, what);
    }

    /* A port that dropped the presence dip would still change the waveform, so
     * "it moved" proves nothing — the two stages must differ from each other. */
    float *both = (float *)malloc(n * sizeof(float));
    float *only_shelf = (float *)malloc(n * sizeof(float));
    if (both && only_shelf) {
        for (size_t i = 0; i < n; i++) both[i] = only_shelf[i] = (float)TONE_INPUT[i];
        ca_apply_tone_shaper(both, n, TONE_SAMPLE_RATE, &warm);
        ca_biquad_coefficients_t derived = ca_low_shelf_coefficients(&warm, TONE_SAMPLE_RATE);
        ca_biquad(only_shelf, n, &derived);

        int differs = 0;
        for (size_t i = 0; i < n; i++)
            if (fabs((double)both[i] - only_shelf[i]) > 1e-4) { differs = 1; break; }
        check(differs, "the presence dip was applied, not just the shelf");
    }
    free(both);
    free(only_shelf);
    free(x);

    /* A silent buffer must come back untouched: Apply bails when the peak is 0,
     * and a port that divided by that peak would produce NaN. */
    float silence[TONE_SILENCE_COUNT] = { 0 };
    ca_apply_tone_shaper(silence, TONE_SILENCE_COUNT, TONE_SAMPLE_RATE, &warm);
    int quiet = 1;
    for (size_t i = 0; i < TONE_SILENCE_COUNT; i++) if (silence[i] != 0.0f) quiet = 0;
    check(quiet, "silence stays silent rather than dividing by its peak");
}

/* ---- NchltPhonemizer ---------------------------------------------------- */

static ca_nchlt_phonemizer *make_phonemizer(void)
{
    return ca_nchlt_new(NCHLT_DICT, NCHLT_RULES, NCHLT_PHONE_MAP,
                            NCHLT_GRAPH_MAP, NCHLT_GNULLS);
}

static void test_nchlt(void)
{
    printf("NchltPhonemizer\n");

    for (size_t c = 0; c < COUNT(NCHLT_CASES); c++) {
        ca_nchlt_phonemizer *p = make_phonemizer();
        check(p != NULL, "phonemizer loads");
        if (!p) return;

        const char *got[32];
        size_t n = ca_nchlt_phonemize(p, NCHLT_CASES[c].text, got, 32);

        char what[160];
        snprintf(what, sizeof what, "%s phone count", NCHLT_CASES[c].name);
        checks++;
        if (n != NCHLT_CASES[c].phone_count) {
            printf("  FAIL: %s — got %zu phones, want %zu\n",
                   what, n, NCHLT_CASES[c].phone_count);
            failures++;
        } else {
            for (size_t i = 0; i < n; i++) {
                snprintf(what, sizeof what, "%s phone %zu", NCHLT_CASES[c].name, i);
                check_str(got[i], NCHLT_CASES[c].phones[i], what);
            }
        }

        snprintf(what, sizeof what, "%s rulePredictedWords", NCHLT_CASES[c].name);
        check((int)ca_nchlt_last_rule_predicted_words(p) == NCHLT_CASES[c].rule_predicted,
              what);

        const char *const *unknown = NULL;
        size_t un = ca_nchlt_last_unknown_graphemes(p, &unknown);
        snprintf(what, sizeof what, "%s unknown count", NCHLT_CASES[c].name);
        checks++;
        if (un != NCHLT_CASES[c].unknown_count) {
            printf("  FAIL: %s — got %zu unknown, want %zu\n",
                   what, un, NCHLT_CASES[c].unknown_count);
            failures++;
        } else {
            for (size_t i = 0; i < un; i++) {
                snprintf(what, sizeof what, "%s unknown %zu", NCHLT_CASES[c].name, i);
                check_str(unknown[i], NCHLT_CASES[c].unknown[i], what);
            }
        }

        ca_nchlt_free(p);
    }

    for (size_t c = 0; c < COUNT(PREDICT_CASES); c++) {
        ca_nchlt_phonemizer *p = make_phonemizer();
        if (!p) return;

        const char *got[32];
        size_t n = ca_nchlt_predict_word(p, PREDICT_CASES[c].word, got, 32);

        char what[160];
        snprintf(what, sizeof what, "predictWord(%s) count", PREDICT_CASES[c].word);
        checks++;
        if (n != PREDICT_CASES[c].phone_count) {
            printf("  FAIL: %s — got %zu, want %zu\n", what, n, PREDICT_CASES[c].phone_count);
            failures++;
        } else {
            for (size_t i = 0; i < n; i++) {
                snprintf(what, sizeof what, "predictWord(%s) phone %zu",
                         PREDICT_CASES[c].word, i);
                check_str(got[i], PREDICT_CASES[c].phones[i], what);
            }
        }
        ca_nchlt_free(p);
    }

    /* Both paths can pronounce this word. The dictionary must win, and the rule
     * counter must show it did — the counter is the only evidence of which path
     * ran, and a port that always predicted would still return sensible phones. */
    {
        ca_nchlt_phonemizer *p = make_phonemizer();
        if (p) {
            const char *got[32];
            ca_nchlt_phonemize(p, "sawubona", got, 32);
            check(ca_nchlt_last_rule_predicted_words(p) == 0,
                  "a catalogued word must not be predicted");
            ca_nchlt_free(p);
        }
    }
}

int main(void)
{
    printf("=== voice text-module parity ===\n");
    test_sentence_splitter();
    test_splits_non_latin_scripts();
    test_does_not_split_decimal_or_domain();
    test_language_spans();
    test_geez();
    test_tone_shaper();
    test_nchlt();
    printf("%d checks, %d failures\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
