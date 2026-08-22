/*
 * test_voice_parity.c — asserts the C voice port against the same answers the
 * C# reference generates.
 *
 * THE EXPECTED VALUES ARE TRANSCRIBED FROM THE FIXTURES, not parsed from them.
 * The C port has no JSON reader and this test will not vendor one; the other
 * eight ports read fixtures/voice_xsampa_to_ipa.json and
 * fixtures/voice_sentencepiece_unigram.json directly, which is stronger. That
 * makes this the ONE port where the fixture can drift without a test failing,
 * so if you change a fixture, change these literals in the same commit. Every
 * value below is copied verbatim from those two files.
 *
 * The cases are adversarial on purpose: the SentencePiece vocabulary is built
 * so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases carry a
 * multi-character token, the script-g that is U+0261 rather than ASCII 'g',
 * and a phone that cannot map and must be REPORTED rather than dropped.
 */

#include "circle_ai/voice_xsampa.h"

#include <stdio.h>
#include <string.h>

static int failures = 0;
static int checks = 0;

static void check(int cond, const char *what)
{
    checks++;
    if (!cond) { printf("  FAIL: %s\n", what); failures++; }
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

static void check_ids(const int *actual, size_t actual_count,
                      const int *expected, size_t expected_count, const char *what)
{
    checks++;
    if (actual_count != expected_count) {
        printf("  FAIL: %s — got %zu ids, want %zu\n", what, actual_count, expected_count);
        failures++;
        return;
    }
    for (size_t i = 0; i < expected_count; i++) {
        if (actual[i] != expected[i]) {
            printf("  FAIL: %s — id %zu is %d, want %d\n", what, i, actual[i], expected[i]);
            failures++;
            return;
        }
    }
}

/* ---- X-SAMPA -> IPA, from fixtures/voice_xsampa_to_ipa.json -------------- */

static void test_xsampa(void)
{
    printf("X-SAMPA to IPA\n");

    {   /* hond — the one deliberate approximation (h\ is ɦ, voice has only h) */
        const char *in[] = {"h\\", "O", "n", "t"};
        const char *want[] = {"h", "ɔ", "n", "t"};
        circle_voice_conversion c = circle_voice_xsampa_to_ipa(in, 4);
        check_strv(c.ipa, c.ipa_count, want, 4, "h\\ O n t -> ipa");
        check(c.unmapped_count == 0, "h\\ O n t -> nothing unmapped");
        check(circle_voice_xsampa_can_say_all(in, 4) == 1, "h\\ O n t -> canSayAll");
        circle_voice_conversion_free(&c);
    }

    {   /* A:r must match WHOLE — never A + : + r. 'e' has no Afrikaans phone. */
        const char *in[] = {"A:r", "b", "e", "i"};
        const char *want[] = {"ɑ", "ː", "r", "b", "i"};
        const char *want_unmapped[] = {"e"};
        circle_voice_conversion c = circle_voice_xsampa_to_ipa(in, 4);
        check_strv(c.ipa, c.ipa_count, want, 5, "A:r b e i -> ipa");
        check_strv(c.unmapped, c.unmapped_count, want_unmapped, 1, "A:r b e i -> unmapped");
        check(circle_voice_xsampa_can_say_all(in, 4) == 0, "A:r b e i -> !canSayAll");
        circle_voice_conversion_free(&c);
    }

    {   /* g is U+0261 script g, NOT ASCII 'g' — invisible in a diff. */
        const char *in[] = {"g", "u", "d"};
        const char *want[] = {"ɡ", "u", "d"};
        circle_voice_conversion c = circle_voice_xsampa_to_ipa(in, 3);
        check_strv(c.ipa, c.ipa_count, want, 3, "g u d -> ipa");
        check(c.ipa_count > 0 && strcmp(c.ipa[0], "g") != 0,
              "g maps to U+0261, not ASCII g (which the voice would drop)");
        circle_voice_conversion_free(&c);
    }

    {   /* Diphthongs: one token in, two code points out. */
        const char *in[] = {"9y", "@i", "@u"};
        const char *want[] = {"œ", "y", "ə", "i", "ə", "u"};
        circle_voice_conversion c = circle_voice_xsampa_to_ipa(in, 3);
        check_strv(c.ipa, c.ipa_count, want, 6, "9y @i @u -> ipa");
        circle_voice_conversion_free(&c);
    }

    {   /* An unmappable phone is REPORTED, and the rest still convert. */
        const char *in[] = {"a", "ZZZ", "b"};
        const char *want[] = {"a", "b"};
        const char *want_unmapped[] = {"ZZZ"};
        circle_voice_conversion c = circle_voice_xsampa_to_ipa(in, 3);
        check_strv(c.ipa, c.ipa_count, want, 2, "a ZZZ b -> ipa");
        check_strv(c.unmapped, c.unmapped_count, want_unmapped, 1, "a ZZZ b -> unmapped");
        check(circle_voice_xsampa_can_say_all(in, 3) == 0, "a ZZZ b -> !canSayAll");
        circle_voice_conversion_free(&c);
    }

    check(circle_voice_xsampa_known_phone_count() == 38,
          "the phone table has 38 entries, as the reference does");
}

/* ---- SentencePiece, from fixtures/voice_sentencepiece_unigram.json ------- */

/*
 * Verbatim from the fixture. "▁hello" (id 7) scores WORSE than "▁hell" + "o",
 * which is what makes greedy and Viterbi disagree.
 */
static const circle_voice_sp_entry VOCAB[] = {
    {"<unk>", 0, 0.0f},   {"<s>", 1, 0.0f},     {"</s>", 2, 0.0f},   {"<pad>", 3, 0.0f},
    {"▁", 4, -6.0f},      {"▁he", 5, -4.0f},    {"▁hell", 6, -2.0f}, {"▁hello", 7, -9.0f},
    {"h", 8, -7.0f},      {"e", 9, -5.0f},      {"l", 10, -5.0f},    {"o", 11, -1.0f},
    {"w", 12, -7.0f},     {"r", 13, -7.0f},     {"d", 14, -6.0f},    {"▁world", 15, -1.5f},
    {"▁wor", 16, -5.0f},  {"ld", 17, -3.0f},    {"lo", 18, -4.0f},   {"ll", 19, -4.0f},
    {"<0xC3>", 20, -20.0f}, {"<0xA9>", 21, -20.0f},
};
static const size_t VOCAB_COUNT = sizeof(VOCAB) / sizeof(VOCAB[0]);

static void test_sentencepiece(void)
{
    printf("SentencePiece unigram\n");
    circle_voice_sp *sp = circle_voice_sp_new(VOCAB, VOCAB_COUNT);
    check(sp != NULL, "tokeniser constructed");
    if (!sp) return;

    int ids[64];

    {   /* THE case: Viterbi gives hell+o+world; greedy would give hello+world. */
        const int want[] = {6, 11, 15};
        const int greedy[] = {7, 15};
        size_t n = circle_voice_sp_encode(sp, "hello world", ids, 64);
        check_ids(ids, n, want, 3, "\"hello world\" -> Viterbi ids");
        check(!(n == 2 && ids[0] == greedy[0] && ids[1] == greedy[1]),
              "not the greedy answer — the port is doing Viterbi");
    }

    {   const int want[] = {6, 11};
        size_t n = circle_voice_sp_encode(sp, "hello", ids, 64);
        check_ids(ids, n, want, 2, "\"hello\" -> ids"); }

    {   const int want[] = {15};
        size_t n = circle_voice_sp_encode(sp, "world", ids, 64);
        check_ids(ids, n, want, 1, "\"world\" -> ids"); }

    {   const int want[] = {6};
        size_t n = circle_voice_sp_encode(sp, "hell", ids, 64);
        check_ids(ids, n, want, 1, "\"hell\" -> ids"); }

    {   size_t n = circle_voice_sp_encode(sp, "", ids, 64);
        check(n == 0, "empty text encodes to nothing"); }

    {   /* é is UTF-8 C3 A9. Emitting A9 C3 does not crash — both are real
         * pieces with real ids — the model just says a different character. */
        const int want[] = {4, 8, 20, 21};
        size_t n = circle_voice_sp_encode(sp, "hé", ids, 64);
        check_ids(ids, n, want, 4, "\"he\\u00e9\" -> byte fallback in UTF-8 order");
        check(n == 4 && ids[2] == 20 && ids[3] == 21,
              "byte fallback emits C3 then A9, not A9 then C3"); }

    {   /* Capacity 0 must still report the length, so callers can size buffers. */
        size_t n = circle_voice_sp_encode(sp, "hello world", NULL, 0);
        check(n == 3, "encode with capacity 0 reports the required length"); }

    circle_voice_sp_free(sp);
}

int main(void)
{
    printf("voice parity (C)\n");
    test_xsampa();
    test_sentencepiece();
    printf("%d checks, %d failures\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
