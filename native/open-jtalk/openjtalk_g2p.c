/*
 * openjtalk_g2p — Japanese grapheme-to-phoneme for CircleAI, on the device.
 *
 * WHY THIS EXISTS. Japanese cannot be phonemised from a lookup table: 聞き取れて
 * is read by segmenting the sentence, identifying 聞く as a verb, and applying
 * its conjugation — morphology, not character mapping. Our voice was running on
 * a 51-token phoneme table with a Chinese-dominant word list, which covered only
 * 50/86 hiragana and 25/90 katakana as standalone characters. Bare kana are
 * exactly where Japanese conjugation ends, so real sentences lost characters
 * silently and measured CER 0.42 against human speech.
 *
 * Open JTalk (Nagoya Institute of Technology) is the analyser the entire
 * Japanese TTS ecosystem is built on, VOICEVOX included, and it is modified BSD
 * rather than GPL — so unlike espeak-ng it links straight into the app instead
 * of needing a second package to stay licence-clean.
 *
 * WHAT IT RETURNS. Open JTalk's output is HTS full-context labels, one per
 * phoneme, shaped like
 *
 *     xx^xx-sil+k=o/A:xx+xx+xx/B:xx-xx_xx/C:xx_xx+xx/...
 *          ^^^ ^                          ^
 *          prev current  next
 *
 * The current phoneme is the field between '-' and '+'. Everything after '/' is
 * prosodic context an HTS acoustic model would use; a VITS model driven by a
 * phoneme sequence does not, so it is dropped here. Accent information is
 * therefore NOT preserved — see the note on accent below before assuming this
 * is enough for a pitch-accent-aware voice.
 *
 * THREADING. Mecab holds parse state, so one instance is not safe to share
 * across concurrent calls. The handle is opaque and per-caller; the C# side
 * serialises on it rather than this file locking, because the caller already
 * owns a lock around synthesis and a second one here would only add a way to
 * deadlock.
 */

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* THESE INCLUDES ARE ORDER-DEPENDENT — DO NOT SORT THEM.
 *
 * Open JTalk's headers do not include their own dependencies: mecab2njd.h
 * declares mecab2njd(NJD *, ...) without including njd.h, and njd2jpcommon.h
 * names both JPCommon and NJD without including either. Alphabetical order
 * fails to compile with "unknown type name 'NJD'". The type-defining headers
 * (mecab.h, njd.h, jpcommon.h) therefore come first, and the ones that consume
 * those types follow — the same order upstream's own bin/open_jtalk.c uses. */
#include "mecab.h"
#include "njd.h"
#include "jpcommon.h"

#include "text2mecab.h"
#include "mecab2njd.h"
#include "njd2jpcommon.h"
#include "njd_set_accent_phrase.h"
#include "njd_set_accent_type.h"
#include "njd_set_digit.h"
#include "njd_set_long_vowel.h"
#include "njd_set_pronunciation.h"
#include "njd_set_unvoiced_vowel.h"

#if defined(_WIN32) && !defined(__MINGW32__)
#  define OJT_EXPORT __declspec(dllexport)
#else
#  define OJT_EXPORT __attribute__((visibility("default")))
#endif

/* text2mecab writes an escaped copy of the input. open_jtalk's own driver uses
 * a fixed 1024-byte buffer and truncates; a spoken answer can exceed that, so
 * the limit here is generous and enforced rather than assumed. */
#define OJT_MAX_INPUT 8192

typedef struct {
    Mecab    mecab;
    NJD      njd;
    JPCommon jpcommon;
    int      ready;
} ojt_handle;

/*
 * Open a phonemiser over a compiled Open JTalk dictionary directory (the one
 * holding sys.dic, matrix.bin, char.bin, unk.dic). Returns NULL on failure so
 * the managed side can fall back rather than crash.
 */
OJT_EXPORT void *openjtalk_g2p_open(const char *dic_dir)
{
    if (dic_dir == NULL || *dic_dir == '\0') return NULL;

    ojt_handle *h = (ojt_handle *)calloc(1, sizeof(ojt_handle));
    if (h == NULL) return NULL;

    if (Mecab_initialize(&h->mecab) != TRUE) { free(h); return NULL; }

    /* Mecab_load takes a mutable char* in this vintage of the API. The string
     * is not modified, but the cast is needed to compile without a warning. */
    if (Mecab_load(&h->mecab, (char *)dic_dir) != TRUE) {
        Mecab_clear(&h->mecab);
        free(h);
        return NULL;
    }

    NJD_initialize(&h->njd);
    JPCommon_initialize(&h->jpcommon);
    h->ready = 1;
    return h;
}

OJT_EXPORT void openjtalk_g2p_close(void *handle)
{
    ojt_handle *h = (ojt_handle *)handle;
    if (h == NULL) return;
    if (h->ready) {
        JPCommon_clear(&h->jpcommon);
        NJD_clear(&h->njd);
        Mecab_clear(&h->mecab);
    }
    free(h);
}

/*
 * Phonemise UTF-8 `text` into `out` as space-separated phonemes.
 *
 * Returns the number of phonemes written, 0 when nothing was produced, or -1 on
 * a bad argument or a buffer too small to hold the result. A truncated phoneme
 * string would be spoken as a different sentence, so this refuses rather than
 * writing a partial one.
 */
OJT_EXPORT int openjtalk_g2p(void *handle, const char *text, char *out, int out_len)
{
    ojt_handle *h = (ojt_handle *)handle;
    if (h == NULL || !h->ready || text == NULL || out == NULL || out_len <= 0) return -1;

    out[0] = '\0';
    size_t in_len = strlen(text);
    if (in_len == 0) return 0;
    if (in_len >= OJT_MAX_INPUT) return -1;

    char buff[OJT_MAX_INPUT];
    text2mecab(buff, text);

    if (Mecab_analysis(&h->mecab, buff) != TRUE) return -1;

    mecab2njd(&h->njd, Mecab_get_feature(&h->mecab), Mecab_get_size(&h->mecab));

    /* Order matters and is not obvious: pronunciation before digits (digits are
     * read as words and then need pronouncing), accent phrase before accent
     * type (the type is assigned within a phrase), and long-vowel handling last
     * because it rewrites the vowels the earlier passes produced. This is the
     * sequence open_jtalk's own driver uses; reordering it silently changes the
     * reading. */
    njd_set_pronunciation(&h->njd);
    njd_set_digit(&h->njd);
    njd_set_accent_phrase(&h->njd);
    njd_set_accent_type(&h->njd);
    njd_set_unvoiced_vowel(&h->njd);
    njd_set_long_vowel(&h->njd);

    njd2jpcommon(&h->jpcommon, &h->njd);
    JPCommon_make_label(&h->jpcommon);

    int written = 0, count = 0;
    int n = JPCommon_get_label_size(&h->jpcommon);
    char **labels = JPCommon_get_label_feature(&h->jpcommon);

    for (int i = 0; i < n && labels != NULL; i++) {
        const char *lab = labels[i];
        if (lab == NULL) continue;

        const char *dash = strchr(lab, '-');
        if (dash == NULL) continue;
        const char *plus = strchr(dash + 1, '+');
        if (plus == NULL) continue;

        size_t len = (size_t)(plus - dash - 1);
        if (len == 0) continue;

        /* Utterance-boundary silence is a label, not a sound. A VITS model has
         * its own leading/trailing silence, and passing 'sil' through made the
         * voice speak the padding. Internal pauses ('pau') ARE kept — they are
         * real phrasing. */
        if (len == 3 && strncmp(dash + 1, "sil", 3) == 0) continue;

        int need = (int)len + (count > 0 ? 1 : 0);
        if (written + need + 1 > out_len) {
            out[0] = '\0';
            goto cleanup;              /* refuse rather than truncate */
        }
        if (count > 0) out[written++] = ' ';
        memcpy(out + written, dash + 1, len);
        written += (int)len;
        count++;
    }
    out[written] = '\0';

cleanup:
    /* Both must be refreshed or the next sentence is appended to this one —
     * the symptom is a voice that reads every previous answer again. */
    JPCommon_refresh(&h->jpcommon);
    NJD_refresh(&h->njd);

    return (out[0] == '\0' && count > 0) ? -1 : count;
}

/*
 * Full-context labels, newline-separated — for a future HTS or accent-aware
 * model, and for diagnosing a reading without rebuilding anything. Same refusal
 * contract as openjtalk_g2p.
 */
OJT_EXPORT int openjtalk_labels(void *handle, const char *text, char *out, int out_len)
{
    ojt_handle *h = (ojt_handle *)handle;
    if (h == NULL || !h->ready || text == NULL || out == NULL || out_len <= 0) return -1;

    out[0] = '\0';
    if (strlen(text) == 0) return 0;
    if (strlen(text) >= OJT_MAX_INPUT) return -1;

    char buff[OJT_MAX_INPUT];
    text2mecab(buff, text);
    if (Mecab_analysis(&h->mecab, buff) != TRUE) return -1;

    mecab2njd(&h->njd, Mecab_get_feature(&h->mecab), Mecab_get_size(&h->mecab));
    njd_set_pronunciation(&h->njd);
    njd_set_digit(&h->njd);
    njd_set_accent_phrase(&h->njd);
    njd_set_accent_type(&h->njd);
    njd_set_unvoiced_vowel(&h->njd);
    njd_set_long_vowel(&h->njd);
    njd2jpcommon(&h->jpcommon, &h->njd);
    JPCommon_make_label(&h->jpcommon);

    int written = 0, count = 0;
    int n = JPCommon_get_label_size(&h->jpcommon);
    char **labels = JPCommon_get_label_feature(&h->jpcommon);

    for (int i = 0; i < n && labels != NULL; i++) {
        if (labels[i] == NULL) continue;
        int len = (int)strlen(labels[i]);
        int need = len + (count > 0 ? 1 : 0);
        if (written + need + 1 > out_len) { out[0] = '\0'; break; }
        if (count > 0) out[written++] = '\n';
        memcpy(out + written, labels[i], (size_t)len);
        written += len;
        count++;
    }
    out[written] = '\0';

    JPCommon_refresh(&h->jpcommon);
    NJD_refresh(&h->njd);
    return count;
}
