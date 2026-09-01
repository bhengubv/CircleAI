#ifndef CIRCLE_AI_VOICE_FRONTEND_H
#define CIRCLE_AI_VOICE_FRONTEND_H

/*
 * voice_frontend.h - CircleAI.Voice, the parts that are pure arithmetic (C11).
 *
 * Everything here runs with no model loaded and no native library linked: the
 * filterbank, the wake-word policy, the phonemizers that are lookup or rules,
 * the respellers, and the VAD. What needs onnxruntime, whisper.cpp, espeak-ng
 * or Open JTalk is excluded (see PARITY-EXCLUSIONS.md) - the DECISIONS cross,
 * the bindings do not.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false / SIZE_MAX. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── the filterbank ───────────────────────────────────────────────────────── */

/*
 * 80-dimensional log-mel features, bit-compatible with Kaldi.
 *
 * WHY NOT THE MEL WE ALREADY HAVE. The speaker-identity path computes a
 * perfectly good generic mel: Hamming window, plain hop, no pre-emphasis, no DC
 * removal. Feeding that to a Kaldi-trained model produces features of the right
 * SHAPE and the wrong NUMBERS - the model loads, runs, burns battery and never
 * fires, and nothing errors. That failure looks exactly like "the wake word is
 * not very good".
 *
 * Five details decide it, each a silent killer alone:
 *
 *   high_freq_hz = -400   NEGATIVE means nyquist + this, so the top of the mel
 *                         range is 7600 Hz and not 8000.
 *   snip_edges = false    Frames are CENTRED, the first starts at -120, and
 *                         out-of-range samples are MIRRORED, not zero-padded.
 *   no x 32768            Samples arrive at [-1, 1] and are used as they are.
 *   povey window          (0.5 - 0.5cos)^0.85, not Hamming, not Hann.
 *   DC then pre-emphasis  Per frame, in that order, THEN the window.
 */
typedef struct {
    int sample_rate_hz;
    int num_mel_bins;
    float low_freq_hz;
    float high_freq_hz;
    float frame_length_ms;
    float frame_shift_ms;
    float preemphasis_coefficient;
    bool remove_dc_offset;
    bool snip_edges;
    bool scale_to_int16;
} ca_kaldi_fbank_options_t;

ca_kaldi_fbank_options_t ca_kaldi_fbank_options_default(void);

int ca_kaldi_fbank_frame_length(const ca_kaldi_fbank_options_t *options);
int ca_kaldi_fbank_frame_shift(const ca_kaldi_fbank_options_t *options);
int ca_kaldi_fbank_padded_window(const ca_kaldi_fbank_options_t *options);

/* A positive value is a frequency; a NEGATIVE one is an offset down from
 * nyquist. -400 at 16 kHz means 7600 Hz, not -400 Hz. */
float ca_kaldi_fbank_resolved_high_freq(const ca_kaldi_fbank_options_t *options);

typedef struct ca_kaldi_fbank ca_kaldi_fbank_t;

ca_kaldi_fbank_t *ca_kaldi_fbank_new(const ca_kaldi_fbank_options_t *options);
void ca_kaldi_fbank_free(ca_kaldi_fbank_t *fbank);

void ca_kaldi_fbank_accept_waveform(ca_kaldi_fbank_t *fbank,
                                    const float *samples, size_t count);

/* Ends the utterance, which is when the mirrored tail frames become available.
 * Mid-stream they are deliberately withheld: a frame computed from a mirror
 * that later turns out to have real audio behind it is a DIFFERENT frame, and a
 * streaming detector cannot take it back. */
void ca_kaldi_fbank_flush(ca_kaldi_fbank_t *fbank);

void ca_kaldi_fbank_reset(ca_kaldi_fbank_t *fbank);

size_t ca_kaldi_fbank_frames_ready(const ca_kaldi_fbank_t *fbank);
size_t ca_kaldi_fbank_dimension(const ca_kaldi_fbank_t *fbank);

/* Writes `dimension` floats into `out`. False when the index is not ready. */
bool ca_kaldi_fbank_frame(const ca_kaldi_fbank_t *fbank, size_t index, float *out);

void ca_kaldi_fbank_consume(ca_kaldi_fbank_t *fbank, size_t frames);

/* Exposed for the tests, because each is a silent killer on its own. */
void ca_kaldi_povey_window(int n, float *out);
float ca_kaldi_mel_scale(float hz);
void ca_kaldi_power_spectrum(const float *frame, int n, float *out);

/* ── voice activity ───────────────────────────────────────────────────────── */

typedef struct {
    uint8_t *audio;
    size_t audio_len;
    bool is_speech;
} ca_vad_segment_t;

void ca_vad_segment_free(ca_vad_segment_t *segment);

typedef struct ca_voice_activity_detector {
    void *state;
    /* Feeds one frame. Returns a completed segment when one ends, else NULL. */
    ca_vad_segment_t *(*accept)(void *state, const uint8_t *pcm, size_t len);
    /* Ends the stream and returns any trailing partial segment. Without this a
     * person who stops talking at the end of a recording loses their last
     * words. */
    ca_vad_segment_t *(*flush)(void *state);
    void (*reset)(void *state);
    void (*free_fn)(void *state);
} ca_voice_activity_detector_t;

void ca_voice_activity_detector_free(ca_voice_activity_detector_t *vad);

/* RMS energy against a threshold, with a silence run to close a segment. Cheap
 * enough to run continuously on a Kirin 710, which is the whole requirement. */
ca_voice_activity_detector_t *ca_energy_vad_detector_new(float energy_threshold,
                                                         int silence_frame_count,
                                                         size_t frame_size_bytes);

/* Every frame is speech. Not "no frame is": a null VAD that reports silence
 * makes the pipeline above it look broken rather than unconfigured. */
ca_voice_activity_detector_t *ca_null_voice_activity_detector_new(void);

/* ── wake word ────────────────────────────────────────────────────────────── */

typedef struct {
    char *wake_word;
    float confidence;
    int64_t at_unix_ms;
} ca_wake_word_detected_t;

void ca_wake_word_detected_free(ca_wake_word_detected_t *event);

typedef struct {
    char *phrase;
    int at_frame;
    double probability;
    int start_frame;
} ca_kws_detection_t;

void ca_kws_detection_free(ca_kws_detection_t *detection);

/* Milliseconds per decoder frame. 40 ms is the model's, not a choice. */
double ca_kws_detection_ms_per_frame(void);
double ca_kws_detection_start_ms(const ca_kws_detection_t *detection);
double ca_kws_detection_end_ms(const ca_kws_detection_t *detection);

typedef struct {
    ca_kws_detection_t detection;
    const float *window;      /* borrowed */
    size_t window_len;
    int keyword_start;
    int keyword_end;
} ca_wake_candidate_t;

/*
 * The second stage.
 *
 * TWO STAGES, BECAUSE ONE CANNOT BE BOTH CHEAP AND CERTAIN. Measured on the
 * P30, stage one heard "Circle" 12 times out of 12 - and produced 21 false
 * accepts over 30 clips of ordinary speech, EVERY one a sentence with the word
 * inside it ("let us circle back"). A threshold cannot fix that: "circle back"
 * scores 0.802, higher than most genuine wakes, so no cut through confidence
 * separates the two populations.
 *
 * What separates them is that a wake word is the START of what you say. So
 * stage two asks one question - was anyone talking just before this? - and that
 * costs no model, no memory and no measurable battery.
 */
typedef struct ca_wake_confirmer {
    void *state;
    bool (*confirm)(void *state, const ca_wake_candidate_t *candidate);
    /* Why it refused, or NULL. "It never fires" and "it fires and is vetoed
     * every time" are completely different problems and look identical from
     * outside without this. */
    const char *(*last_reason)(void *state);
    void (*free_fn)(void *state);
} ca_wake_confirmer_t;

void ca_wake_confirmer_free(ca_wake_confirmer_t *confirmer);

/* Confirms everything. For a host that wants stage one's recall and will accept
 * its false accepts. */
ca_wake_confirmer_t *ca_always_confirm_new(void);

/* The cheap one: was there speech running up to the phrase? */
ca_wake_confirmer_t *ca_utterance_onset_confirmer_new(double max_lead_in_ms,
                                                      double gap_tolerance_ms,
                                                      double speech_floor);

/* The expensive one: transcribe the window and read the words back. Needs a
 * speech model resident, which is exactly the trade a cheap phone cannot make
 * and an expensive one can. */
ca_wake_confirmer_t *ca_transcript_confirmer_new(
    char *(*transcribe)(void *state, const uint8_t *pcm, size_t len), void *state);

/* Both, in order: the cheap one first so the expensive one is never asked about
 * a wake it would have let through anyway. On the measured corpus that is 27 of
 * 30 clips never reaching the transcriber. */
ca_wake_confirmer_t *ca_either_confirmer_new(ca_wake_confirmer_t *cheap,
                                             ca_wake_confirmer_t *precise);

typedef struct ca_confirmed_keyword_spotter ca_confirmed_keyword_spotter_t;

/* `spot` is stage one; it needs onnxruntime and is supplied by the host. */
ca_confirmed_keyword_spotter_t *ca_confirmed_keyword_spotter_new(
    ca_wake_confirmer_t *confirmer, double history_seconds);

void ca_confirmed_keyword_spotter_free(ca_confirmed_keyword_spotter_t *spotter);

void ca_confirmed_keyword_spotter_accept(ca_confirmed_keyword_spotter_t *spotter,
                                         const float *samples, size_t count);

/* Hands stage one's detection in. Returns whether stage two let it through. */
bool ca_confirmed_keyword_spotter_offer(ca_confirmed_keyword_spotter_t *spotter,
                                        const ca_kws_detection_t *detection);

void ca_confirmed_keyword_spotter_reset(ca_confirmed_keyword_spotter_t *spotter);

/* ── the single-graph classifier ──────────────────────────────────────────── */

typedef enum {
    CA_KWS_INPUT_WAVEFORM = 0,
    CA_KWS_INPUT_LOG_MEL_FILTERBANK
} ca_kws_input_kind_t;

typedef enum {
    CA_SPEAKER_EMBEDDER_INPUT_WAVEFORM = 0,
    CA_SPEAKER_EMBEDDER_INPUT_LOG_MEL_FILTERBANK
} ca_speaker_embedder_input_kind_t;

typedef struct {
    char *model_path;
    float threshold;
    ca_kws_input_kind_t input_kind;
    int sample_rate_hz;
} ca_kws_config_t;

void ca_kws_config_free(ca_kws_config_t *config);

typedef struct {
    char *model_path;
    ca_speaker_embedder_input_kind_t input_kind;
    int sample_rate_hz;
    /* Cosine similarity above which two utterances are the same person. */
    float match_threshold;
} ca_speaker_identity_config_t;

void ca_speaker_identity_config_free(ca_speaker_identity_config_t *config);

typedef struct {
    char *model_path;
    int sample_rate_hz;
    char **labels;
    size_t label_count;
} ca_speech_emotion_config_t;

void ca_speech_emotion_config_free(ca_speech_emotion_config_t *config);

typedef struct ca_kws_wake_word_detector ca_kws_wake_word_detector_t;

/* `score` is the graph and needs onnxruntime; the debounce, the listening state
 * and the confidence clamp are here and testable without it. */
ca_kws_wake_word_detector_t *ca_kws_wake_word_detector_new(
    const ca_kws_config_t *config,
    float (*score)(void *state, const float *window, size_t len), void *state,
    int64_t min_interval_between_fires_ms);

void ca_kws_wake_word_detector_free(ca_kws_wake_word_detector_t *detector);

/* False for this engine: it scores the ONE phrase it was trained on, so the
 * per-person access list the interface documents needs the transducer. */
bool ca_kws_wake_word_detector_supports_per_phrase(const ca_kws_wake_word_detector_t *d);

bool ca_kws_wake_word_detector_offer(ca_kws_wake_word_detector_t *detector,
                                     const float *window, size_t len);

/* ── phonemizers ──────────────────────────────────────────────────────────── */

typedef struct ca_phonemizer {
    void *state;
    /* Returns a heap array of `*out_count` strings, or NULL. */
    char **(*phonemize)(void *state, const char *text, size_t *out_count);
    void (*free_fn)(void *state);
} ca_phonemizer_t;

void ca_phonemizer_free(ca_phonemizer_t *phonemizer);
void ca_phoneme_list_free(char **phonemes, size_t count);

/* Splits the text into grapheme clusters and hands them back. For a voice whose
 * vocabulary is already the writing system. */
ca_phonemizer_t *ca_passthrough_phonemizer_new(void);

/*
 * Ethiopic to Latin, because the Amharic and Tigrinya voices cannot read it.
 *
 * Those two MMS models ship is_uroman: their vocabularies are 28 and 27 LATIN
 * letters. Measured on the P30, Amharic fed Ethiopic lost 43 distinct
 * characters and produced 3.2 s of noise for a 15 s paragraph - the model has
 * never seen an Ethiopic codepoint.
 *
 * Computed, not tabulated: Unicode lays the syllabary out exactly as the script
 * is taught, eight codepoints per consonant, so consonant = (cp-0x1200)/8 and
 * vowel = (cp-0x1200)%8. Two small tables replace three hundred entries.
 */
bool ca_geez_is_ethiopic(const char *text);
char *ca_geez_romanize(const char *text);

ca_phonemizer_t *ca_geez_phonemizer_new(void);

/*
 * Out of process, because espeak-ng is GPL and linking it would make this GPL
 * too. A pipe is a boundary the licence respects.
 *
 * Text goes in on STDIN and ends with a NEWLINE, and both are load-bearing:
 * argv goes through the ANSI code page on Windows so six scripts came back
 * EMPTY with a zero exit code, and without the newline espeak does not flush
 * the final clause - the last character is dropped or read aloud as its Unicode
 * character NAME.
 */
ca_phonemizer_t *ca_espeak_phonemizer_new(const char *voice, const char *executable);

/* Language-switch markers such as "(en)" are stripped: they are not phonemes,
 * and left in, the letters inside them get mapped and spoken aloud. */
char *ca_espeak_clean_output(const char *raw);

/* A phonemizer that also produces a tone per phoneme. Separate because most
 * languages have no tone channel, and one that does needs the two arrays to
 * stay exactly in step. */
typedef struct ca_tone_source {
    void *state;
    const int64_t *(*last_tones)(void *state, size_t *out_count);
} ca_tone_source_t;

/*
 * Dictionary lookup, for scripts that do not encode sound.
 *
 * Chinese characters carry meaning, not sound, so no character-driven model
 * reads them and no letter-to-sound rule helps. The usual answer is a Python
 * G2P library, which cannot run on the phone - but the sherpa-onnx builds ship
 * the mapping as a plain lexicon.txt, 195,828 entries for Mandarin, and a
 * lookup table is something a Kirin 710 can do.
 */
ca_phonemizer_t *ca_lexicon_phonemizer_load(const char *lexicon_path);
ca_phonemizer_t *ca_lexicon_phonemizer_parse(const char *text);

ca_tone_source_t *ca_lexicon_phonemizer_tones(ca_phonemizer_t *phonemizer);
size_t ca_lexicon_phonemizer_entry_count(const ca_phonemizer_t *phonemizer);

/* Dictionary G2P for the eleven South African languages, from CC-BY data. */
ca_phonemizer_t *ca_nchlt_phonemizer_load(const char *dictionary_path);

/* Greedy longest-match tokenisation against a lexicon. */
typedef struct ca_lexicon_tokeniser ca_lexicon_tokeniser_t;

ca_lexicon_tokeniser_t *ca_lexicon_tokeniser_new(const char *lexicon_text);
void ca_lexicon_tokeniser_free(ca_lexicon_tokeniser_t *tokeniser);
char **ca_lexicon_tokeniser_encode(ca_lexicon_tokeniser_t *tokeniser,
                                   const char *text, size_t *out_count);

/* ── respelling ───────────────────────────────────────────────────────────── */

/* Where a respelling came from. ATTESTED outranks DERIVED, because the language
 * settled the first long before we arrived and the second is our guess. */
typedef enum {
    CA_RESPELLING_NONE = 0,
    CA_RESPELLING_PERSONAL,
    CA_RESPELLING_ATTESTED,
    CA_RESPELLING_DERIVED
} ca_respelling_source_t;

/* Loanwords the language has already settled: esemese, khompiyutha. */
const char *ca_loanword_respell(const char *word, const char *host_language,
                                ca_respelling_source_t *out_source);

bool ca_loanword_is_nguni_or_sotho(const char *tag);

/* Derives a spelling from English IPA using the Nguni CV rule - what a speaker
 * does with an unfamiliar word: hear it, then write it in their own
 * orthography. Caller frees. */
char *ca_nguni_respell_from_ipa(const char *ipa);

/* ── language spans ───────────────────────────────────────────────────────── */

typedef struct {
    char *text;
    /* True for the embedded language (English), false for the surrounding one. */
    bool is_foreign;
} ca_language_span_t;

void ca_language_span_free(ca_language_span_t *span);

/* Splits mixed text into runs. Separators ride along with the run they FOLLOW,
 * so a language change never strands a comma or splits mid-punctuation. */
ca_language_span_t *ca_language_span_split(const char *text, size_t *out_count);

/*
 * Deliberately does NOT flag ordinary lowercase English.
 *
 * That needs a lexicon per language pair, and guessing wrong is worse than not
 * guessing: mispronouncing a native word to "fix" a foreign one insults the
 * speaker in their own language. Only a case compound (CircleAI) or a short
 * all-caps initialism (SMS, GPS) is treated as foreign.
 */
bool ca_language_span_is_foreign_word(const char *word);

/* Rewrites a compound so a synthesiser can say it: "CircleAI" becomes
 * "Circle AI", which is two things the voice already knows. */
char *ca_language_span_to_spoken_form(const char *text);

/* ── TTS ──────────────────────────────────────────────────────────────────── */

typedef struct {
    uint8_t *audio;
    size_t audio_len;
    int sample_rate;
    int channels;
    int bits_per_sample;
} ca_tts_synthesis_result_t;

void ca_tts_synthesis_result_free(ca_tts_synthesis_result_t *result);

/* What the last synthesis could NOT say. A front end that drops a symbol still
 * produces audio, so without this a caller cannot tell a clean render from one
 * that quietly deleted every 'š' in the sentence. Approximations are reported
 * separately from drops: an approximation is a declared substitution and a drop
 * is a hole. */
typedef struct ca_tts_front_end_diagnostics {
    void *state;
    size_t (*last_skipped_count)(void *state);
    const char *const *(*last_skipped_symbols)(void *state, size_t *out_count);
    const char *const *(*last_approximated_symbols)(void *state, size_t *out_count);
} ca_tts_front_end_diagnostics_t;

/* ── the keyword graph ────────────────────────────────────────────────────── */

typedef struct ca_kws_context_state ca_kws_context_state_t;
typedef struct ca_kws_context_graph ca_kws_context_graph_t;

/* A trie of phrases as TEXT, so any number of them can be matched
 * independently - which is what makes a per-person access list possible. */
ca_kws_context_graph_t *ca_kws_context_graph_new(void);
void ca_kws_context_graph_free(ca_kws_context_graph_t *graph);

bool ca_kws_context_graph_add(ca_kws_context_graph_t *graph, const char *phrase,
                              double threshold);

/* Phrases that can never fire because another is a prefix of them. Reported
 * rather than silently dropped: somebody typed that phrase in and deserves to
 * be told it will never work. */
size_t ca_kws_context_graph_shadowed_count(const ca_kws_context_graph_t *graph);
bool ca_kws_context_graph_shadowed_at(const ca_kws_context_graph_t *graph, size_t index,
                                      const char **out_phrase,
                                      const char **out_shadowed_by);

ca_kws_context_state_t *ca_kws_context_state_new(const ca_kws_context_graph_t *graph);
void ca_kws_context_state_free(ca_kws_context_state_t *state);
void ca_kws_context_state_reset(ca_kws_context_state_t *state);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_FRONTEND_H */
