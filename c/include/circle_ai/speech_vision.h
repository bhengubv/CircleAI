#ifndef CIRCLE_AI_SPEECH_VISION_H
#define CIRCLE_AI_SPEECH_VISION_H

/*
 * speech_vision.h - CircleAI.Speech, CircleAI.Speech.Cloud, CircleAI.Vision
 * and CircleAI.Charts (C11).
 *
 * Cleaning up audio before anything tries to understand it, the cloud speech
 * services that exist for when the device cannot, what a camera can be asked,
 * and drawing a chart.
 *
 * THE AUDIO CHAIN RUNS IN ONE ORDER AND IT IS NOT ARBITRARY: echo cancellation,
 * then noise reduction, then voice activity, then recognition. Cancelling echo
 * after denoising means the canceller is looking for a reference signal that
 * the denoiser has already altered, and it stops converging. This is the single
 * most common way a voice stack is wired wrong, and it presents as "the
 * assistant hears itself".
 *
 * FACES AND PLATES ARE THE TWO MOST DANGEROUS THINGS IN THIS FILE. Both are
 * seams with no default implementation, both return NULL rather than a guess,
 * and neither stores anything. A face embedder that cached would be a database
 * of who was in the room.
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

/* -- audio format --------------------------------------------------------- */

/*
 * Between sample rates, channel counts and bit depths.
 *
 * DOWN-MIXING TO MONO AVERAGES, IT DOES NOT TAKE THE LEFT CHANNEL. Taking one
 * channel loses half the energy on material that is genuinely stereo, and on a
 * phone whose two microphones are beamformed it can select the one pointing
 * away from the speaker.
 *
 * Resampling here is linear and says so. Adequate for feature extraction, not
 * for playback - anything reaching a speaker wants a real filter, and calling
 * this one "good enough" for that is how a voice acquires a metallic edge
 * nobody can locate.
 */
typedef struct {
    int sample_rate_hz;
    int channels;
    int bits_per_sample;
} ca_audio_format_t;

/* Caller frees; *out_len is bytes. */
uint8_t *ca_audio_format_converter_convert(const uint8_t *pcm, size_t len,
                                           ca_audio_format_t from,
                                           ca_audio_format_t to,
                                           size_t *out_len);

float *ca_audio_format_converter_to_float_mono(const uint8_t *pcm, size_t len,
                                               ca_audio_format_t from,
                                               size_t *out_count);

/* -- echo cancellation ---------------------------------------------------- */

typedef struct ca_echo_canceller_model_runner {
    void *state;
    /* Takes the microphone signal AND the reference - what the device is
     * playing. Without the reference there is nothing to cancel, and a
     * canceller wired with only the microphone is a very expensive pass-through
     * that appears to work in a quiet room. */
    bool (*process)(void *state, const float *microphone, const float *reference,
                    size_t count, float *out);
    void (*free_fn)(void *state);
} ca_echo_canceller_model_runner_t;

void ca_echo_canceller_model_runner_free(ca_echo_canceller_model_runner_t *runner);

typedef struct ca_echo_canceller ca_echo_canceller_t;

/*
 * The WebRTC AEC3 shape: an adaptive filter plus a residual suppressor.
 *
 * `delay_ms` is the loudspeaker-to-microphone latency the device actually has,
 * and getting it wrong is the difference between working and not. The filter
 * only searches a window around it; a phone whose real delay is 120 ms and
 * whose configured delay is 20 ms never finds the echo at all.
 */
ca_echo_canceller_t *ca_web_rtc_echo_canceller_new(int sample_rate_hz, int delay_ms);
void ca_echo_canceller_free(ca_echo_canceller_t *canceller);

bool ca_echo_canceller_process(ca_echo_canceller_t *canceller, const float *microphone,
                               const float *reference, size_t count, float *out);

/* -- noise reduction ------------------------------------------------------ */

typedef struct ca_noise_reducer_model_runner {
    void *state;
    bool (*process)(void *state, const float *input, size_t count, float *out);
    void (*free_fn)(void *state);
} ca_noise_reducer_model_runner_t;

void ca_noise_reducer_model_runner_free(ca_noise_reducer_model_runner_t *runner);

typedef struct ca_noise_reducer ca_noise_reducer_t;

void ca_noise_reducer_free(ca_noise_reducer_t *reducer);

/*
 * Spectral subtraction: estimate the noise floor during silence and subtract it.
 *
 * No model, so it runs anywhere. `over_subtraction` above 1.0 removes more
 * noise and introduces musical noise - isolated bins surviving in otherwise
 * silent frames, which sound like faint bells and which a recogniser reads as
 * speech. 1.0 to 1.5 is the usable range; above that it is trading one problem
 * for a worse one.
 */
ca_noise_reducer_t *ca_spectral_subtraction_noise_reducer_new(int sample_rate_hz,
                                                              double over_subtraction);

/* A learned reducer. Much better and needs a model; the seam is here so a host
 * that has one can supply it, and the spectral version stays the default. */
ca_noise_reducer_t *ca_deep_filter_net_noise_reducer_new(
    ca_noise_reducer_model_runner_t *runner);

bool ca_noise_reducer_process(ca_noise_reducer_t *reducer, const float *input,
                              size_t count, float *out);

/* -- voice activity ------------------------------------------------------- */

typedef struct ca_voice_activity_detector ca_voice_activity_detector_t;

void ca_voice_activity_detector_free(ca_voice_activity_detector_t *detector);

/*
 * The Silero VAD shape: a small recurrent model over 32 ms frames.
 *
 * STATEFUL ACROSS FRAMES, and that is why it beats an energy threshold - it
 * carries context, so a breath between words does not end the utterance and a
 * door closing does not begin one. Resetting it between frames turns it back
 * into an expensive energy gate.
 */
ca_voice_activity_detector_t *ca_silero_voice_activity_detector_new(
    int sample_rate_hz, double threshold, void *model_runner);

/* 0..1 for one frame. */
double ca_voice_activity_detector_probability(ca_voice_activity_detector_t *detector,
                                              const float *frame, size_t count);

void ca_voice_activity_detector_reset(ca_voice_activity_detector_t *detector);

/* -- end of turn ---------------------------------------------------------- */

typedef struct ca_end_of_turn_detector ca_end_of_turn_detector_t;

void ca_end_of_turn_detector_free(ca_end_of_turn_detector_t *detector);

/*
 * When somebody has finished speaking - which is NOT the same as when they
 * stopped making noise.
 *
 * Silence plus punctuation plus a syntactic check, because silence alone gets
 * it wrong in both directions: a pause for thought is read as the end of a
 * turn, and a trailing "so..." is read as more to come. The rule-based version
 * is deliberately conservative - interrupting somebody is worse than a slightly
 * late reply.
 */
ca_end_of_turn_detector_t *ca_rule_based_end_of_turn_detector_new(
    int64_t min_silence_ms, int64_t max_wait_ms);

bool ca_end_of_turn_detector_observe(ca_end_of_turn_detector_t *detector,
                                     const char *partial_transcript,
                                     int64_t silence_ms);

/* -- optical character recognition ---------------------------------------- */

typedef struct {
    char *text;
    double confidence;
    /* Pixel bounding box in the source image. */
    int x, y, width, height;
} ca_ocr_region_t;

void ca_ocr_region_free(ca_ocr_region_t *region);

typedef struct ca_optical_character_recognizer {
    void *state;
    ca_ocr_region_t *(*recognize)(void *state, const uint8_t *image, size_t len,
                                  const char *mime_type, size_t *out_count);
    void (*free_fn)(void *state);
} ca_optical_character_recognizer_t;

void ca_optical_character_recognizer_free(ca_optical_character_recognizer_t *recognizer);

/* Reads nothing. The default. */
ca_optical_character_recognizer_t *ca_null_optical_character_recognizer_new(void);

/* -- cloud speech --------------------------------------------------------- */

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    char *language;
} ca_open_ai_voice_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *language;
    /* Speaker labels. Off by default: diarisation on a recording of several
     * people is a decision about other people's voices, and it should be one
     * somebody made rather than a default. */
    bool diarise;
} ca_assembly_ai_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *voice_id;
    double speed;
} ca_play_ht_options_t;

typedef struct ca_speech_recognizer {
    void *state;
    const char *(*provider_id)(void *state);
    bool (*is_configured)(void *state);
    /* Caller frees. */
    char *(*transcribe)(void *state, const uint8_t *pcm, size_t len,
                        int sample_rate_hz, char **out_error);
    void (*free_fn)(void *state);
} ca_speech_recognizer_t;

void ca_speech_recognizer_free(ca_speech_recognizer_t *recognizer);

typedef struct ca_speech_synthesizer {
    void *state;
    const char *(*provider_id)(void *state);
    bool (*is_configured)(void *state);
    uint8_t *(*synthesize)(void *state, const char *text, const char *voice,
                           size_t *out_len, char **out_error);
    void (*free_fn)(void *state);
} ca_speech_synthesizer_t;

void ca_speech_synthesizer_free(ca_speech_synthesizer_t *synthesizer);

/* All four take the key from the HOST at construction. None reads an
 * environment variable, and one with no key is absent rather than broken - the
 * same rule as the chat fallbacks, because the failure it prevents is the same:
 * audio of somebody's voice leaving the device because a variable was set. */
ca_speech_recognizer_t *ca_open_ai_speech_recognizer_new(
    const ca_open_ai_voice_options_t *options, void *http);

ca_speech_recognizer_t *ca_assembly_ai_speech_recognizer_new(
    const ca_assembly_ai_options_t *options, void *http);

ca_speech_synthesizer_t *ca_open_ai_speech_synthesizer_new(
    const ca_open_ai_voice_options_t *options, void *http);

ca_speech_synthesizer_t *ca_eleven_labs_speech_synthesizer_new(
    const char *api_key, const char *voice_id, void *http);

ca_speech_synthesizer_t *ca_play_ht_speech_synthesizer_new(
    const ca_play_ht_options_t *options, void *http);

/* -- vision --------------------------------------------------------------- */

typedef struct ca_computer_vision_runtime {
    void *state;
    const char *(*backend_id)(void *state);
    bool (*is_available)(void *state);
    /* Runs a model over an image and returns raw output. Caller frees. */
    float *(*infer)(void *state, const char *model_id, const uint8_t *image,
                    size_t len, size_t *out_count);
    void (*free_fn)(void *state);
} ca_computer_vision_runtime_t;

void ca_computer_vision_runtime_free(ca_computer_vision_runtime_t *runtime);

/* Reports unavailable and infers nothing. The default. */
ca_computer_vision_runtime_t *ca_null_computer_vision_runtime_new(void);

typedef struct {
    char *model_path;
    double confidence_threshold;
    double nms_threshold;
    int input_size;
    int num_threads;
} ca_onnx_face_detector_options_t;

void ca_onnx_face_detector_options_free(ca_onnx_face_detector_options_t *options);

ca_onnx_face_detector_options_t ca_onnx_face_detector_options_default(void);

typedef struct {
    int x, y, width, height;
    double confidence;
    /* Five landmarks - eyes, nose, mouth corners - used to align the crop
     * before embedding. Skipping alignment costs more accuracy than any other
     * single step in the chain. */
    int landmarks[10];
} ca_face_box_t;

typedef struct ca_onnx_face_detector ca_onnx_face_detector_t;

/*
 * Finds faces. Does not identify them and holds nothing between calls.
 *
 * Detection and identification are separated on purpose: knowing a face is
 * present is what an autofocus or a framing feature needs, and it must not
 * require the thing that could tell you whose face it is.
 */
ca_onnx_face_detector_t *ca_onnx_face_detector_new(
    const ca_onnx_face_detector_options_t *options,
    ca_computer_vision_runtime_t *runtime);

void ca_onnx_face_detector_free(ca_onnx_face_detector_t *detector);

ca_face_box_t *ca_onnx_face_detector_detect(ca_onnx_face_detector_t *detector,
                                            const uint8_t *image, size_t len,
                                            size_t *out_count);

typedef struct {
    char *model_path;
    int embedding_dims;
    int input_size;
    int num_threads;
} ca_onnx_face_embedder_options_t;

void ca_onnx_face_embedder_options_free(ca_onnx_face_embedder_options_t *options);

typedef struct ca_onnx_face_embedder ca_onnx_face_embedder_t;

/*
 * Turns an aligned face crop into a vector.
 *
 * STORES NOTHING. The vector goes back to the caller and this holds no copy,
 * no cache and no index. An embedder that kept what it computed would be a
 * record of who was in front of the camera, accumulating with no one having
 * decided that it should.
 */
ca_onnx_face_embedder_t *ca_onnx_face_embedder_new(
    const ca_onnx_face_embedder_options_t *options,
    ca_computer_vision_runtime_t *runtime);

void ca_onnx_face_embedder_free(ca_onnx_face_embedder_t *embedder);

bool ca_onnx_face_embedder_embed(ca_onnx_face_embedder_t *embedder,
                                 const uint8_t *image, size_t len,
                                 const ca_face_box_t *box, float *out_vector);

typedef struct {
    char *detector_model_path;
    char *recognizer_model_path;
    double confidence_threshold;
    /* Which plate format to expect. Layouts differ enough per country that a
     * general recogniser is worse than a specific one everywhere. */
    char *region;
} ca_onnx_plate_recognizer_options_t;

void ca_onnx_plate_recognizer_options_free(ca_onnx_plate_recognizer_options_t *options);

typedef struct ca_onnx_plate_recognizer ca_onnx_plate_recognizer_t;

/*
 * Reads a number plate.
 *
 * NULL rather than a low-confidence guess, always. A wrong plate is not a
 * degraded result - it names a different vehicle, and everything downstream
 * treats it as fact.
 */
ca_onnx_plate_recognizer_t *ca_onnx_plate_recognizer_new(
    const ca_onnx_plate_recognizer_options_t *options,
    ca_computer_vision_runtime_t *runtime);

void ca_onnx_plate_recognizer_free(ca_onnx_plate_recognizer_t *recognizer);

char *ca_onnx_plate_recognizer_read(ca_onnx_plate_recognizer_t *recognizer,
                                    const uint8_t *image, size_t len,
                                    double *out_confidence);

/* -- charts --------------------------------------------------------------- */

typedef enum {
    CA_CHART_TYPE_LINE = 0,
    CA_CHART_TYPE_BAR,
    CA_CHART_TYPE_STACKED_BAR,
    CA_CHART_TYPE_AREA,
    CA_CHART_TYPE_SCATTER,
    CA_CHART_TYPE_PIE
} ca_chart_type_t;

const char *ca_chart_type_name(ca_chart_type_t type);

typedef struct {
    double x;
    double y;
    char *label;
} ca_chart_data_point_t;

void ca_chart_data_point_free(ca_chart_data_point_t *point);

typedef struct {
    char *name;
    ca_chart_data_point_t *points;
    size_t point_count;
    /* NULL lets the style assign one. A series that picks its own colour makes
     * two charts side by side use the same colour for different things. */
    char *colour;
} ca_chart_series_t;

void ca_chart_series_free(ca_chart_series_t *series);

typedef struct {
    /* The palette, in assignment order. Chosen to stay distinguishable in
     * greyscale and to the most common colour vision deficiencies - a chart
     * that only works for some readers is a chart that is wrong for them. */
    char **series_colours;
    size_t colour_count;
    char *background;
    char *foreground;
    char *grid;
    bool show_legend;
    bool show_grid;
} ca_chart_style_t;

void ca_chart_style_free(ca_chart_style_t *style);

ca_chart_style_t *ca_chart_style_default(void);

/* Font metrics, so a renderer with no font engine can still lay out axis
 * labels without overlapping them. Approximate and honest about it: exact
 * metrics need the font, and the alternative is guessing that every glyph is
 * the same width, which breaks the moment a label is not Latin. */
double ca_chart_fonts_text_width(const char *text, double font_size);
double ca_chart_fonts_line_height(double font_size);

typedef struct {
    char *title;
    ca_chart_type_t type;
    ca_chart_series_t *series;
    size_t series_count;
    char *x_axis_label;
    char *y_axis_label;
    ca_chart_style_t *style;
    int width;
    int height;
} ca_chart_spec_t;

void ca_chart_spec_free(ca_chart_spec_t *spec);

/* Builds a spec from data plus a chart type, choosing axes and ranges. The
 * y-axis includes zero for bar charts and does NOT force it for line charts:
 * a truncated bar chart misrepresents magnitude, and a zero-forced line chart
 * hides the variation it exists to show. */
ca_chart_spec_t *ca_chart_spec_factory_build(ca_chart_type_t type, const char *title,
                                             const ca_chart_series_t *series,
                                             size_t series_count);

typedef struct ca_chart_renderer {
    void *state;
    /* SVG, PNG - whatever the renderer does. Caller frees. */
    uint8_t *(*render)(void *state, const ca_chart_spec_t *spec, size_t *out_len);
    const char *(*mime_type)(void *state);
    void (*free_fn)(void *state);
} ca_chart_renderer_t;

void ca_chart_renderer_free(ca_chart_renderer_t *renderer);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SPEECH_VISION_H */
