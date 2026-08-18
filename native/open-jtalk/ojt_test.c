/*
 * ojt_test — run the phonemiser on the device it has to work on.
 *
 * A desktop build proves the code compiles. It does not prove bionic maps a
 * 100 MB sys.dic, that mmap succeeds under the app's memory limits, or that
 * the readings are right on the hardware. Push this next to the dictionary and
 * run it over adb.
 *
 *   usage: ojt_test <dic_dir> [sentence ...]
 */
#include <stdio.h>
#include <string.h>

extern void *openjtalk_g2p_open(const char *dic_dir);
extern void  openjtalk_g2p_close(void *handle);
extern int   openjtalk_g2p(void *handle, const char *text, char *out, int out_len);

/* The reference sentence from the ReazonSpeech/parakeet-ja fixtures, so the
 * phonemes can be checked against a reading a Japanese speaker published —
 * not against one we invented. 聞き取れて is the interesting part: the old
 * lexicon had no entry for bare れ and dropped it. */
static const char *DEFAULT[] = {
    "これはテスト文ですこの機械が日本語をちゃんと聞き取れているかどうかを計ります",
    "日本語ちゃんと聞き取れてますか",
    "こんにちは。フランスの首都はパリです。",
    "1234円です",
    NULL
};

int main(int argc, char **argv)
{
    if (argc < 2) { fprintf(stderr, "usage: ojt_test <dic_dir> [sentence ...]\n"); return 2; }

    void *h = openjtalk_g2p_open(argv[1]);
    if (h == NULL) { fprintf(stderr, "FAIL: could not open dictionary at %s\n", argv[1]); return 1; }
    printf("dictionary opened: %s\n\n", argv[1]);

    char out[16384];
    int failures = 0;

    if (argc > 2) {
        for (int i = 2; i < argc; i++) {
            int n = openjtalk_g2p(h, argv[i], out, sizeof(out));
            printf("in  : %s\nout : %s\nph  : %d\n\n", argv[i], n > 0 ? out : "(none)", n);
            if (n <= 0) failures++;
        }
    } else {
        for (int i = 0; DEFAULT[i] != NULL; i++) {
            int n = openjtalk_g2p(h, DEFAULT[i], out, sizeof(out));
            printf("in  : %s\nout : %s\nph  : %d\n\n", DEFAULT[i], n > 0 ? out : "(none)", n);
            if (n <= 0) failures++;
        }
    }

    openjtalk_g2p_close(h);
    printf(failures ? "FAILURES: %d\n" : "all sentences phonemised\n", failures);
    return failures ? 1 : 0;
}
