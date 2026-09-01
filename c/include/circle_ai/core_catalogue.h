#ifndef CIRCLE_AI_CORE_CATALOGUE_H
#define CIRCLE_AI_CORE_CATALOGUE_H

/*
 * core_catalogue.h - CircleAI.Core (C11): what a model is, where it lives,
 * where the RAM figure came from, what the device can tell us, the audit log,
 * and the quantisation codec.
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

/* ── the catalogue ────────────────────────────────────────────────────────── */

/* What a model DOES. Kept separate from its size or its backend, because those
 * change with the build and this does not. */
typedef enum {
    CA_MODEL_MODALITY_CHAT = 0,
    CA_MODEL_MODALITY_ASR,
    CA_MODEL_MODALITY_TTS,
    CA_MODEL_MODALITY_VAD,
    CA_MODEL_MODALITY_WAKE_WORD,
    CA_MODEL_MODALITY_VISION,
    CA_MODEL_MODALITY_MUSIC,
    CA_MODEL_MODALITY_VIDEO,
    CA_MODEL_MODALITY_CODING,
    CA_MODEL_MODALITY_PHONEMIZER
} ca_model_modality_t;

const char *ca_model_modality_name(ca_model_modality_t modality);

/* Where a model's bytes come from.
 *
 * HUGGING_FACE_BUCKET is a separate member rather than a URL detail: it is a
 * bucket we hold no token for, and a 401 from a bucket is not the same problem
 * as a 404 from a repo. Treating them alike sends somebody looking for a file
 * that is there. */
typedef enum {
    CA_MODEL_SOURCE_MODELSCOPE = 0,
    CA_MODEL_SOURCE_HUGGING_FACE = 1,
    CA_MODEL_SOURCE_HUGGING_FACE_BUCKET = 2,
    CA_MODEL_SOURCE_GITHUB_RELEASE = 3
} ca_model_source_t;

/* What a download is doing right now - not all of it is transfer.
 *
 * A 433 MB bundle spends real time hashing and, on a bad link, retrying.
 * Without a phase those look identical to a stalled download, and the person
 * watching concludes the app has hung. */
typedef enum {
    CA_DOWNLOAD_PHASE_DOWNLOADING = 0,
    CA_DOWNLOAD_PHASE_RESUMING,
    CA_DOWNLOAD_PHASE_RETRYING,
    CA_DOWNLOAD_PHASE_VERIFYING,
    CA_DOWNLOAD_PHASE_CACHED,
    CA_DOWNLOAD_PHASE_COMPLETE
} ca_download_phase_t;

const char *ca_download_phase_name(ca_download_phase_t phase);

/* ── where models live ────────────────────────────────────────────────────── */

/*
 * THE MODEL DIRECTORY WAS DECIDED IN FOUR PLACES AND THEY DISAGREED ON A PHONE.
 * Three loaders used the application-data folder and the mobile head used the
 * app's own data directory; on Android the first is a SUBDIRECTORY of the
 * second. Nothing failed - both existed, both were writable, both looked right
 * in a log. What happened instead is that a 523 MB chat model was downloaded
 * twice onto a phone with 890 MB of app data, and it was found by looking at
 * the disk.
 *
 * Deliberately not a cache directory: a system is free to evict a cache under
 * pressure, and a half-evicted 400 MB bundle fails its hash on the next launch
 * with no explanation.
 *
 * Returns borrowed static storage.
 */
const char *ca_model_paths_root(void);
const char *ca_model_paths_default(void);

/* The directory to use, created if absent. Blank means the DEFAULT, not the
 * working directory: a relative path here puts a 400 MB download wherever the
 * process happened to be started from. Caller frees. */
char *ca_model_paths_resolve(const char *requested);

/* ── where the RAM figure came from ───────────────────────────────────────── */

/* Real device memory, supplied by a platform head that can read it.
 *
 * Two numbers on purpose: total is the device CLASS, available is what is free
 * now. Collapsing them makes a busy 8 GB phone look like a 2 GB one. A negative
 * value means "not supplied". */
typedef struct {
    int64_t ram_available_bytes;
    int64_t storage_free_bytes;
    int64_t ram_total_bytes;
} ca_platform_memory_t;

ca_platform_memory_t ca_platform_memory_unknown(void);

typedef enum {
    /* A caller stated it outright (tests, hosts that already know). */
    CA_RAM_MEASUREMENT_EXPLICIT = 0,
    /* Read from the device by a platform head. */
    CA_RAM_MEASUREMENT_PLATFORM_MEASURED,
    /* Nobody supplied one, so it was inferred. On mobile that is a guess. */
    CA_RAM_MEASUREMENT_HEURISTIC
} ca_ram_measurement_t;

/*
 * The platform hook.
 *
 * A PROBE THAT GUESSED WAS INDISTINGUISHABLE FROM ONE THAT MEASURED, and every
 * verdict downstream was stated with full confidence about a number that is the
 * managed heap limit - a few hundred megabytes inside an Android sandbox. The
 * device reads as a wearable, every model comes back as not fitting, and
 * nothing anywhere says the input was invented.
 */
void ca_device_memory_set_probe(ca_platform_memory_t (*probe)(void *state), void *state);

/* The hook is asked ONLY when the caller did not state a figure: a test that
 * passes an explicit number must not have it overwritten by whatever hardware
 * happens to be running the test. Pass a negative value for "not stated". */
int64_t ca_device_memory_resolve(int64_t stated_ram_bytes,
                                 ca_ram_measurement_t *out_source);

/*
 * A plain-language warning when the figure is a guess that looks wrong, or NULL.
 *
 * Deliberately NARROW: the heuristic is fine on desktop and server where it
 * returns GB-scale numbers, and warning there is noise nobody reads. It fires
 * only on the actual signature of the bug - an inferred figure too small for
 * any real device. Caller frees.
 */
char *ca_device_memory_warning(int64_t ram_available_bytes, ca_ram_measurement_t source);

/* ── the device context ───────────────────────────────────────────────────── */

/*
 * What the device can say about itself.
 *
 * Every optional field is a pointer or a negative sentinel, never zero. A zero
 * battery level and an unknown battery level are different facts, and reporting
 * 0% tells the assistant the phone is about to die.
 */
typedef struct ca_device_context {
    void *state;
    const char *(*active_app_id)(void *state);
    const char *(*locale)(void *state);
    const char *(*time_zone_id)(void *state);
    /* Negative when unknown. */
    double (*battery_level)(void *state);
    int (*is_charging)(void *state);          /* -1 unknown, 0 no, 1 yes */
    const char *(*network_type)(void *state); /* NULL when unknown */
    double (*cpu_usage_percent)(void *state);
    int64_t (*available_memory_bytes)(void *state);
    const char *(*thermal_state)(void *state);
    int64_t (*storage_free_bytes)(void *state);
    int64_t (*last_active_unix)(void *state);
    void (*record_interaction)(void *state);
    void (*free_fn)(void *state);
} ca_device_context_t;

void ca_device_context_free(ca_device_context_t *context);

/* Everything it cannot honestly answer is unknown. Locale and time zone come
 * from the C library; nothing else is guessed. */
ca_device_context_t *ca_default_device_context_new(const char *active_app_id);

/* ── diagnostics ──────────────────────────────────────────────────────────── */

/* The instrument names, EXACTLY as the C# has them. A dashboard is built on
 * these strings, so renaming one silently splits a metric in two. */
extern const char *const CA_DIAGNOSTICS_OPERATIONS_TOTAL;
extern const char *const CA_DIAGNOSTICS_OPERATION_DURATION_MS;
extern const char *const CA_DIAGNOSTICS_ANOMALY_SIGNALS_TOTAL;
extern const char *const CA_DIAGNOSTICS_INFERENCE_REQUESTS_TOTAL;

/* How an operation ended. A CLOSED vocabulary: "failed", "error" and "err" in
 * three components make a chart nobody can read. */
extern const char *const CA_OUTCOME_SUCCESS;
extern const char *const CA_OUTCOME_CANCELLED;
extern const char *const CA_OUTCOME_UNAVAILABLE;
extern const char *const CA_OUTCOME_RATE_LIMITED;
extern const char *const CA_OUTCOME_INVALID;
extern const char *const CA_OUTCOME_ERROR;

/* Where a measurement goes. NULL by default: nothing is recorded and nothing is
 * allocated until a host says where to put it. */
typedef struct {
    void *state;
    void (*count)(void *state, const char *name, int64_t amount,
                  const char *component, const char *operation, const char *outcome);
    void (*record)(void *state, const char *name, double milliseconds,
                   const char *component, const char *operation, const char *outcome);
} ca_metric_sink_t;

void ca_diagnostics_set_sink(const ca_metric_sink_t *sink);

typedef struct ca_operation ca_operation_t;

ca_operation_t *ca_diagnostics_start_operation(const char *component, const char *operation);

/* Records the duration and the outcome. Idempotent - a caller that finishes in
 * both a success path and a cleanup path must not double-count. A span that
 * ended on free() instead would report every ABANDONED operation as a success,
 * which is exactly backwards. */
void ca_operation_finish(ca_operation_t *operation, const char *outcome);
bool ca_operation_is_finished(const ca_operation_t *operation);
double ca_operation_elapsed_ms(const ca_operation_t *operation);
void ca_operation_free(ca_operation_t *operation);

/* ── the audit log ────────────────────────────────────────────────────────── */

typedef struct {
    char *entry_id;
    int64_t at_unix;
    char *actor;
    char *action;
    char *subject;
    char *outcome;
    /* Free-form, and REDACTED by the writer, not the reader. A log that stores
     * the sensitive value and hides it at display time is a log that leaks the
     * moment somebody opens the file. */
    char *detail_json;
} ca_audit_entry_t;

void ca_audit_entry_free(ca_audit_entry_t *entry);

typedef struct {
    char *actor;
    char *action;
    int64_t from_unix;
    int64_t to_unix;
    size_t limit;
} ca_audit_query_t;

void ca_audit_query_free(ca_audit_query_t *query);

typedef struct ca_audit_log {
    void *state;
    bool (*append)(void *state, const ca_audit_entry_t *entry);
    ca_audit_entry_t *(*query)(void *state, const ca_audit_query_t *query,
                               size_t *out_count);
    void (*free_fn)(void *state);
} ca_audit_log_t;

void ca_audit_log_free(ca_audit_log_t *log);

/* Writes each entry through a line callback. APPEND-ONLY: an audit log with a
 * delete is not an audit log. */
ca_audit_log_t *ca_logger_audit_log_new(void (*write_line)(void *state, const char *line),
                                        void *state);

/* Convenience over whichever log is installed. */
void ca_auditing_record(ca_audit_log_t *log, const char *actor, const char *action,
                        const char *subject, const char *outcome, const char *detail_json);

/* ── quantisation ─────────────────────────────────────────────────────────── */

/* Writes sub-byte values into a byte stream, MSB-first within each byte.
 *
 * The bit order is part of the format: a reader that unpacks LSB-first gets
 * plausible numbers out of the same bytes, which is a corruption nothing
 * detects. */
typedef struct ca_bit_packer ca_bit_packer_t;

ca_bit_packer_t *ca_bit_packer_new(size_t capacity_bytes);
void ca_bit_packer_free(ca_bit_packer_t *packer);

bool ca_bit_packer_write(ca_bit_packer_t *packer, uint32_t value, int bits);
const uint8_t *ca_bit_packer_bytes(const ca_bit_packer_t *packer, size_t *out_len);

bool ca_bit_unpack(const uint8_t *bytes, size_t len, size_t bit_offset, int bits,
                   uint32_t *out_value);

/*
 * A Lloyd-Max codebook over a beta distribution.
 *
 * Beta rather than Gaussian because weight distributions after normalisation
 * are bounded and skewed, and a Gaussian codebook spends half its levels on
 * values that never occur.
 *
 * NOTE: dim=4 takes about a second to build here (a sqrt singularity in the
 * integral makes the quadrature work hard). That is not a hang, and changing
 * the integrator changes the codec's output - so it is left alone.
 */
typedef struct ca_beta_codebook ca_beta_codebook_t;

ca_beta_codebook_t *ca_beta_lloyd_max_codebook_new(int bits, double alpha, double beta);
void ca_beta_codebook_free(ca_beta_codebook_t *codebook);

size_t ca_beta_codebook_level_count(const ca_beta_codebook_t *codebook);
double ca_beta_codebook_level(const ca_beta_codebook_t *codebook, size_t index);
size_t ca_beta_codebook_quantise(const ca_beta_codebook_t *codebook, double value);

typedef struct {
    uint8_t *bytes;
    size_t byte_count;
    int bits_per_value;
    size_t value_count;
    double scale;
    double offset;
} ca_turbo_quant_payload_t;

void ca_turbo_quant_payload_free(ca_turbo_quant_payload_t *payload);

ca_turbo_quant_payload_t *ca_turbo_quant_encode(const float *values, size_t count,
                                                int bits_per_value);

/* Writes `value_count` floats into `out`. */
bool ca_turbo_quant_decode(const ca_turbo_quant_payload_t *payload, float *out);

typedef struct {
    ca_turbo_quant_payload_t payload;
    int layer;
    int head;
    size_t sequence_position;
} ca_shard_compressed_frame_t;

void ca_shard_compressed_frame_free(ca_shard_compressed_frame_t *frame);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CORE_CATALOGUE_H */
