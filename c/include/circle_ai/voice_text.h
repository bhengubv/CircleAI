#ifndef CIRCLE_AI_VOICE_TEXT_H
#define CIRCLE_AI_VOICE_TEXT_H

/*
 * voice_text.h - CircleAI.Voice (C11): text into pieces, audio into and out of
 * files, and everything about choosing a wake phrase.
 *
 * voice_frontend.h next door is the signal half - filterbanks, VAD, the
 * confirmers. This is the half that deals in TEXT and in FILES: the
 * sentencepiece vocabulary a keyword spotter matches against, the WAV reader,
 * the tone shaper that runs over synthesised speech before it becomes PCM, and
 * the machinery that decides whether "hey" is a wake phrase somebody can
 * actually live with.
 *
 * WHY SO MUCH OF THIS IS ABOUT CHOOSING THE PHRASE. A wake word is the only
 * part of an assistant that runs constantly, and a bad one fails in the two
 * worst ways at once: it misses when you want it and fires when you do not.
 * Neither is fixable later by tuning - a one-syllable phrase has too little
 * signal, and a phrase made of common words is in every third sentence. So the
 * phrase book judges the phrase BEFORE anybody lives with it.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- sentencepiece -------------------------------------------------------- */

/* How a piece is used, mirroring sentencepiece's own enum. The values are the
 * on-disk ones: a vocabulary file names them by number. */
typedef enum {
    CA_SENTENCE_PIECE_NORMAL = 1,
    CA_SENTENCE_PIECE_UNKNOWN = 2,
    CA_SENTENCE_PIECE_CONTROL = 3,
    CA_SENTENCE_PIECE_USER_DEFINED = 4,
    CA_SENTENCE_PIECE_BYTE = 6,
    CA_SENTENCE_PIECE_UNUSED = 5
} ca_sentence_piece_kind_t;

const char *ca_sentence_piece_kind_name(ca_sentence_piece_kind_t kind);

typedef struct {
    char *piece;
    float score;
    ca_sentence_piece_kind_t kind;
    int id;
} ca_sentence_piece_t;

void ca_sentence_piece_free(ca_sentence_piece_t *piece);

/*
 * THE WORD-BOUNDARY MARKER IS U+2581, NOT AN UNDERSCORE.
 *
 * It looks like one in a terminal and it is not one. A tokenizer that
 * substitutes "_" produces pieces that are absent from every real vocabulary,
 * so every word falls back to bytes, and the only symptom is a spotter that
 * quietly never matches anything.
 *
 * Returned as the UTF-8 bytes, borrowed.
 */
const char *ca_sentence_piece_word_marker(void);

typedef struct ca_sentence_piece_tokenizer ca_sentence_piece_tokenizer_t;

/* Takes ownership of neither; copies what it needs. */
ca_sentence_piece_tokenizer_t *ca_sentence_piece_tokenizer_new(
    const ca_sentence_piece_t *pieces, size_t count);

void ca_sentence_piece_tokenizer_free(ca_sentence_piece_tokenizer_t *tokenizer);

size_t ca_sentence_piece_tokenizer_count(const ca_sentence_piece_tokenizer_t *tokenizer);
const ca_sentence_piece_t *ca_sentence_piece_tokenizer_at(
    const ca_sentence_piece_tokenizer_t *tokenizer, int id);

/* sentencepiece's normalisation: spaces become the marker AND one is prefixed.
 * The prefix is not optional - without it the first word of a sentence
 * tokenises differently from the same word anywhere else. Caller frees. */
char *ca_sentence_piece_normalise(const char *text);

/* Best-scoring segmentation. Returns a heap array of *out_count piece ids. */
int *ca_sentence_piece_tokenizer_encode(const ca_sentence_piece_tokenizer_t *tokenizer,
                                        const char *text, size_t *out_count);

/* Whether every piece of the text is in the vocabulary. The question a phrase
 * book asks before promising a keyword will ever be matched. */
bool ca_sentence_piece_tokenizer_covers(const ca_sentence_piece_tokenizer_t *tokenizer,
                                        const char *text);

typedef struct ca_sentence_piece_unigram ca_sentence_piece_unigram_t;

/*
 * The unigram language model: Viterbi over every segmentation, choosing the one
 * with the best summed log-probability.
 *
 * Not greedy longest-match. Greedy is faster and gets ordinary words right, but
 * it splits exactly the words that matter here - names, loanwords, anything the
 * vocabulary only half covers - and it splits them differently depending on
 * what preceded them.
 */
ca_sentence_piece_unigram_t *ca_sentence_piece_unigram_load(const char *vocab_json_path,
                                                            const char *scores_json_path);

void ca_sentence_piece_unigram_free(ca_sentence_piece_unigram_t *unigram);

size_t ca_sentence_piece_unigram_count(const ca_sentence_piece_unigram_t *unigram);

/* The penalty for falling back to raw bytes. Finite and large rather than
 * infinite: byte fallback must be the last resort and must still be REACHABLE,
 * or a single unknown character makes the whole string untokenisable. */
double ca_sentence_piece_unigram_byte_fallback_cost(const ca_sentence_piece_unigram_t *unigram);

int *ca_sentence_piece_unigram_encode(const ca_sentence_piece_unigram_t *unigram,
                                      const char *text, size_t *out_count);

/*
 * One Piper voice's configuration.
 *
 * THE PAD RULE lives here and it has cost more time than anything else in this
 * module: a blank pad token means the MODEL's blank, not the literal "_". Piper
 * pads with id 3 and MMS with id 0, and getting it wrong produces audio that is
 * silent or a burst of noise — never an error, and never anything a log
 * mentions.
 */
typedef struct {
    char *voice_id;
    char *model_path;
    char *config_path;
    int sample_rate_hz;
    /* Negative means the model did not declare one, which is not the same as
     * zero — zero is MMS's actual pad id. */
    int pad_id;
    char *language;
    char **phoneme_ids;
    size_t phoneme_count;
    double length_scale;
    double noise_scale;
} ca_piper_voice_config_t;

void ca_piper_voice_config_free(ca_piper_voice_config_t *config);

/* Reads the .onnx.json beside the model. NULL when the file is absent or does
 * not declare a phoneme map — a voice that cannot be phonemised is a voice that
 * will produce noise, and failing here is the only place it can be said. */
ca_piper_voice_config_t *ca_piper_voice_config_load(const char *config_path);

/* One completed turn: what was heard, what was said, and how long it took.
 * The C# carries this as VoiceExchangeEventArgs; C has no EventArgs, so it is
 * the payload of the callback. */
typedef struct {
    char *heard;
    char *said;
    char *language;
    int64_t started_unix_ms;
    int64_t duration_ms;
    /* Whether the person interrupted. Recorded because a turn that was cut off
     * and one that completed are different events, and a transcript that treats
     * them alike reads as though the assistant finished. */
    bool interrupted;
} ca_voice_exchange_t;

void ca_voice_exchange_free(ca_voice_exchange_t *exchange);

/* -- splitting text into what gets spoken --------------------------------- */

typedef struct {
    char *text;
    /* Which language this run is in, when the splitter could tell. NULL means
     * it could not, which is not the same as the document's language. */
    char *language;
    size_t start_offset;
    size_t length;
} ca_speech_segment_t;

void ca_speech_segment_free(ca_speech_segment_t *segment);

/*
 * Splits into sentences for synthesis. Returns a heap array of *out_count.
 *
 * Different from the telephony chunker, which is streaming and optimises for
 * time-to-first-audio. This one sees the whole text and optimises for PROSODY:
 * a synthesiser handed a sentence in two halves puts a full stop in the middle
 * of it, and no amount of joining the audio afterwards takes that back.
 */
ca_speech_segment_t *ca_sentence_splitter_split(const char *text, size_t *out_count);

/* -- respelling and tone -------------------------------------------------- */

typedef struct ca_respelling_tts_engine ca_respelling_tts_engine_t;

/* Wraps an engine so a word goes through the respellers before synthesis.
 * Composed rather than built in, because whether respelling helps depends on
 * the voice: a model trained on the same accent needs none of it. */
ca_respelling_tts_engine_t *ca_respelling_tts_engine_new(void *inner_engine);
void ca_respelling_tts_engine_free(ca_respelling_tts_engine_t *engine);

typedef struct ca_phrased_tts_engine ca_phrased_tts_engine_t;

/* Splits into phrases and synthesises each, so a long passage does not drift.
 * Long-form synthesis loses pitch and pace over tens of seconds; phrase-sized
 * chunks re-anchor it without an audible seam. */
ca_phrased_tts_engine_t *ca_phrased_tts_engine_new(void *inner_engine,
                                                   int max_phrase_chars);

void ca_phrased_tts_engine_free(ca_phrased_tts_engine_t *engine);

/* X-SAMPA in, IPA out. Caller frees.
 *
 * Needed because lexicons in this space are published in X-SAMPA - it is ASCII
 * and survives a spreadsheet - while every model consumes IPA. */
char *ca_xsampa_to_ipa(const char *xsampa);

/*
 * Two RBJ biquads in series over the float waveform, before it becomes PCM: a
 * low shelf that lifts the bottom and a peaking dip that takes out the harsh
 * band.
 *
 * The defaults are measured, not chosen by ear on one machine: +4 dB shelf from
 * 320 Hz, -4 dB dip at 3200 Hz with Q 0.8. Warmer, with no cost to
 * intelligibility - which is the constraint, since a warmer voice nobody can
 * make out is a worse voice.
 *
 * Two multiply-accumulates per sample, against a vocoder that just spent
 * seconds producing the waveform. The cost does not register.
 */
typedef struct {
    double low_shelf_hz;   /* 320 */
    double low_shelf_db;   /* +4.0 */
    double presence_hz;    /* 3200 */
    double presence_db;    /* -4.0, negative cuts */
    double presence_q;     /* 0.8, lower is wider */
} ca_tone_shaper_t;

/* The measured setting. */
ca_tone_shaper_t ca_tone_shaper_warm(void);

/* Filters in place. */
void ca_tone_shaper_apply(const ca_tone_shaper_t *shaper, float *waveform,
                          size_t count, int sample_rate_hz);

/* One direct-form-I biquad in place. Exposed because the shaper is two of them
 * and a caller building a different chain should not reimplement it. */
void ca_biquad_apply(float *waveform, size_t count,
                     double b0, double b1, double b2, double a1, double a2);

/* -- learning how one person says a word ---------------------------------- */

typedef enum {
    /* Still listening. Nothing has changed how the word is spoken. */
    CA_LEARNING_LISTENING = 0,
    /* Five hearings agreed; the new spelling is in use and awaiting its check. */
    CA_LEARNING_ADOPTED,
    /* The check passed. This is how the word is said for this person. */
    CA_LEARNING_CONFIRMED
} ca_learning_state_t;

const char *ca_learning_state_name(ca_learning_state_t state);

typedef struct {
    char *word;
    /* NULL while still listening. */
    char *spelling;
    ca_learning_state_t state;
    /* Each candidate and how many hearings agreed on it. Kept after adoption:
     * a word can be re-learned when somebody's pronunciation shifts, and
     * throwing the tallies away makes that restart from nothing. */
    char **candidates;
    int *candidate_counts;
    size_t candidate_count;
} ca_learned_word_t;

void ca_learned_word_free(ca_learned_word_t *word);

typedef struct ca_personal_respellings ca_personal_respellings_t;

/*
 * Learns how this person says borrowed words, from ordinary use - nothing to
 * set up and nothing to correct by hand.
 *
 * FIVE AGREEING HEARINGS before a spelling is adopted. One is a mis-hearing;
 * five in agreement is a habit. Adopting on the first would make the assistant
 * mispronounce a word confidently on the strength of one bad frame, and the
 * person would have no idea why it changed.
 */
ca_personal_respellings_t *ca_personal_respellings_new(void);
void ca_personal_respellings_free(ca_personal_respellings_t *respellings);

void ca_personal_respellings_hear(ca_personal_respellings_t *respellings,
                                  const char *word, const char *heard_spelling);

/* Borrowed; NULL when nothing has been learned for that word. */
const ca_learned_word_t *ca_personal_respellings_lookup(
    const ca_personal_respellings_t *respellings, const char *word);

/* Marks the adopted spelling as having survived its check. */
bool ca_personal_respellings_confirm(ca_personal_respellings_t *respellings,
                                     const char *word);

/* -- wake -------------------------------------------------------------- */

typedef enum {
    /* Three-graph streaming transducer; keywords are text, so a phrase can be
     * changed without training anything. */
    CA_WAKE_ENGINE_ZIPFORMER_TRANSDUCER = 0,
    /* Single-graph classifier; one trained phrase and no other. */
    CA_WAKE_ENGINE_SINGLE_GRAPH_CLASSIFIER
} ca_wake_engine_t;

const char *ca_wake_engine_name(ca_wake_engine_t engine);

/* Which engine a bundle on disk actually is, from what is in it. Detected
 * rather than configured: a bundle and a setting that disagree fail at the
 * first utterance, with a shape error nobody can read. */
ca_wake_engine_t ca_wake_word_factory_engine_for(const char *bundle_directory);

/*
 * Per-device wake tuning that survives a restart.
 *
 * The thresholds were compile-time constants, which is a claim that every
 * phone, room and voice behaves like the ones they were measured on. They do
 * not: the same phrase read 0.42 on one synthetic voice and 0.94 on another.
 * Persisting per device lets a phone that consistently under-scores be nudged
 * ONCE, instead of the default being loosened for everybody - which is how a
 * wake word starts firing on the television.
 *
 * Negative means "not set": use the phrase or engine default.
 */
typedef struct {
    double threshold;
    double max_lead_in_ms;
    int wakes;
} ca_wake_calibration_t;

ca_wake_calibration_t ca_wake_calibration_unset(void);
bool ca_wake_calibration_load(const char *path, ca_wake_calibration_t *out_calibration);
bool ca_wake_calibration_save(const char *path, const ca_wake_calibration_t *calibration);

/* What the device running this can actually do. Both fields decide which engine
 * and which confirmer are viable, and a wrong RAM figure here picks an engine
 * the phone cannot load. */
typedef struct {
    int64_t total_ram_bytes;
    bool transcriber_available;
} ca_wake_host_capabilities_t;

/* The model to use for a language, whether it is that language's own, and a
 * note for the person. `model_name` NULL means no model at all. */
typedef struct {
    char *model_name;
    bool is_native;
    /* Plain language, and EMPTY when native. A note on every choice trains
     * people to ignore notes. */
    char *note;
} ca_wake_language_choice_t;

void ca_wake_language_choice_free(ca_wake_language_choice_t *choice);

bool ca_wake_languages_for(const char *iso_language,
                           ca_wake_language_choice_t *out_choice);

size_t ca_wake_languages_count(void);
const char *ca_wake_languages_at(size_t index);

/* -- judging a wake phrase before somebody lives with it ------------------ */

typedef enum {
    /* Nothing to say against it. */
    CA_WAKE_PHRASE_GOOD = 0,
    /* Usable, with a caveat the owner should hear. */
    CA_WAKE_PHRASE_CAUTION,
    /* Cannot work at all; the reason says why. */
    CA_WAKE_PHRASE_UNUSABLE
} ca_wake_phrase_verdict_t;

const char *ca_wake_phrase_verdict_name(ca_wake_phrase_verdict_t verdict);

typedef struct {
    char *text;
    /* The pieces the spotter will actually match. Out-of-vocabulary pieces are
     * the usual reason a phrase can never fire. */
    char **tokens;
    size_t token_count;
    ca_wake_phrase_verdict_t verdict;
    /* Plain language, shown to the person choosing. Empty when good. */
    char *advice;
    /* Negative for the default. */
    double threshold;
    double boost;
} ca_wake_phrase_t;

void ca_wake_phrase_free(ca_wake_phrase_t *phrase);

typedef struct ca_wake_phrase_book ca_wake_phrase_book_t;

ca_wake_phrase_book_t *ca_wake_phrase_book_new(
    const ca_sentence_piece_tokenizer_t *tokenizer);

void ca_wake_phrase_book_free(ca_wake_phrase_book_t *book);

/*
 * Judges a phrase and says why, in words the person choosing can act on.
 *
 * The three that matter, in order of how often they bite:
 *   - too short. One syllable has too little signal; it will fire on coughs.
 *   - too common. A phrase built from frequent words is inside ordinary
 *     sentences, so it fires while somebody is talking to another person.
 *   - not in the vocabulary. The spotter matches pieces, and a phrase whose
 *     pieces are absent can never match ANYTHING - this is the one that looks
 *     like a broken microphone.
 */
bool ca_wake_phrase_book_judge(ca_wake_phrase_book_t *book, const char *text,
                               ca_wake_phrase_t *out_phrase);

/* Phrases known to work, for somebody who does not want to choose. */
size_t ca_wake_phrase_book_suggested_count(const ca_wake_phrase_book_t *book);
const char *ca_wake_phrase_book_suggested_at(const ca_wake_phrase_book_t *book,
                                             size_t index);

/* -- keyword spotting ----------------------------------------------------- */

typedef struct {
    char *text;
    int *token_ids;
    size_t token_count;
    /* Negative for the spotter's default. */
    double threshold;
    double boost;
} ca_kws_keyword_t;

void ca_kws_keyword_free(ca_kws_keyword_t *keyword);

/* How far a model download has got. Separate from the generic download phase
 * because a wake model is loaded during onboarding, where the person is waiting
 * and the only honest thing to show is a real number. */
typedef struct {
    char *stage;
    int64_t bytes_done;
    int64_t bytes_total;   /* negative when the server did not say */
    double fraction;       /* negative when it cannot be computed */
} ca_kws_progress_t;

void ca_kws_progress_free(ca_kws_progress_t *progress);

typedef struct {
    char *bundle_directory;
    /* Negative uses the calibration, then the engine default. */
    double threshold;
    double max_lead_in_ms;
    int num_threads;
    char *provider;
} ca_zipformer_wake_config_t;

void ca_zipformer_wake_config_free(ca_zipformer_wake_config_t *config);

ca_zipformer_wake_config_t ca_zipformer_wake_config_default(const char *bundle_directory);

/* -- Japanese prosody ----------------------------------------------------- */

typedef struct ca_open_jtalk_prosody_tokeniser ca_open_jtalk_prosody_tokeniser_t;

/*
 * Open JTalk's prosody tokens: not phonemes, and not IPA.
 *
 * Japanese is a fourth family here. The others hand a phonemiser's output
 * straight to the model; this one emits accent-phrase markers - ^ $ _ # [ ] -
 * alongside the moras, and the model was trained expecting them. Feeding it
 * bare phonemes produces speech that is intelligible and completely flat, which
 * reads as a broken voice rather than a missing feature.
 *
 * Needs the 103 MB dictionary; without it there is no drop-in substitute.
 */
ca_open_jtalk_prosody_tokeniser_t *ca_open_jtalk_prosody_tokeniser_new(
    const char *dictionary_directory);

void ca_open_jtalk_prosody_tokeniser_free(ca_open_jtalk_prosody_tokeniser_t *tokeniser);

/* Caller frees. */
char *ca_open_jtalk_prosody_tokenise(ca_open_jtalk_prosody_tokeniser_t *tokeniser,
                                     const char *japanese_text);

/* -- audio in and out ----------------------------------------------------- */

/*
 * Reads a WAV as mono float in [-1,1] at 24 kHz, resampling if needed.
 *
 * `max_seconds` is a real guard, not politeness: this is fed by whatever file
 * somebody points at, and a multi-hour recording read whole is an out-of-memory
 * kill on a phone with no message attached to it.
 *
 * Caller frees; *out_count is samples.
 */
float *ca_wav_io_read_mono_24k(const char *path, int max_seconds, size_t *out_count);

/* Float [-1,1] to little-endian signed 16-bit PCM. Caller frees. */
uint8_t *ca_wav_io_to_pcm16(const float *samples, size_t count, size_t *out_len);

/* PCM-16 with a RIFF header. Caller frees. */
uint8_t *ca_wav_io_write(const float *samples, size_t count, int sample_rate_hz,
                         size_t *out_len);

/* Linear resample. Adequate HERE and stated so: the target is a speaker
 * embedding, not playback. Anything reaching a speaker wants a real filter. */
float *ca_wav_io_resample_linear(const float *samples, size_t count,
                                 int from_hz, int to_hz, size_t *out_count);

/* -- playback ------------------------------------------------------------- */

typedef struct ca_audio_player {
    void *state;
    bool (*play)(void *state, const uint8_t *pcm, size_t len, int sample_rate_hz);
    void (*stop)(void *state);
    bool (*is_playing)(void *state);
    void (*free_fn)(void *state);
} ca_audio_player_t;

void ca_audio_player_free(ca_audio_player_t *player);

/* Plays nothing and reports success. The default: a host with no audio output
 * gets a loop that completes rather than one that fails, and a test never opens
 * a device. */
ca_audio_player_t *ca_null_audio_player_new(void);

/* -- tracing a turn ------------------------------------------------------- */

typedef struct ca_voice_trace ca_voice_trace_t;

/*
 * One turn's timeline: when audio arrived, when the wake fired, what the
 * transcriber returned, which voice spoke.
 *
 * It exists because voice failures are not reproducible. By the time somebody
 * says "it did not hear me", the audio is gone; without a trace the only
 * evidence is a description of a sound. The trace is what turns that into a
 * bug.
 *
 * OFF BY DEFAULT and never written anywhere by itself - it holds what somebody
 * said, and a diagnostic that quietly logs speech is a recorder.
 */
ca_voice_trace_t *ca_voice_trace_new(void);
void ca_voice_trace_free(ca_voice_trace_t *trace);

void ca_voice_trace_mark(ca_voice_trace_t *trace, const char *stage,
                         int64_t at_unix_ms, const char *detail);

size_t ca_voice_trace_count(const ca_voice_trace_t *trace);

/* JSON. Caller frees. */
char *ca_voice_trace_to_json(const ca_voice_trace_t *trace);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VOICE_TEXT_H */
