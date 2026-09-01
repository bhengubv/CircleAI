#ifndef CIRCLE_AI_MEMORY_ATOMS_H
#define CIRCLE_AI_MEMORY_ATOMS_H

/*
 * memory_atoms.h - CircleAI.Memory (C11): what gets remembered, and what
 * quietly stops being offered.
 *
 * An atom is ONE fact, of ONE kind, from ONE source. The whole store is built
 * on that shape because anything larger cannot be forgotten selectively: a
 * paragraph containing a ruling and a preference either stays whole or goes
 * whole, and neither is right.
 *
 * FORGETTING IS THE FEATURE, not the failure. A store that keeps everything
 * becomes a filing cabinet - technically complete, useless to search, and
 * confidently offering a finished project's decisions in the middle of today's
 * work. What is below the threshold is NOT deleted: it is still in the log,
 * still there by id, still findable by anybody who goes looking. It is just no
 * longer volunteered.
 *
 * THE LOG IS APPEND-ONLY AND IS THE TRUTH. Every store above it is an index
 * that can be rebuilt. This is what makes it safe to change how recall ranks
 * things without the risk of losing what was written down.
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

/* -- what a memory is ----------------------------------------------------- */

typedef enum {
    /*
     * Something that came up, what was chosen, and how it turned out.
     *
     * THE FIRST KIND WORTH HAVING, and the only one that needs no judgement to
     * write down. Every other kind asks a classification question at the moment
     * of capture - is this a ruling or a preference? - and that question is
     * exactly what gets answered wrong by whoever is closest to the mistake.
     *
     * The failures are worth as much as the fixes. "Tried adb push, it wrote
     * nothing" saves the next attempt as surely as knowing what did work.
     */
    CA_ATOM_KIND_DECISION = 0,
    /* A decision that was made. Never decays; surfaces first. */
    CA_ATOM_KIND_RULING,
    /* Something true about the world. Re-checked before it is relied on. */
    CA_ATOM_KIND_FACT,
    /* How somebody likes things done. Applied by default, easy to override. */
    CA_ATOM_KIND_PREFERENCE,
    /* How to work with this person. NEVER quoted back at them - it shapes tone
     * and how much to ask, which is not the same as being repeated. */
    CA_ATOM_KIND_RELATIONSHIP
} ca_atom_kind_t;

const char *ca_atom_kind_name(ca_atom_kind_t kind);

typedef enum {
    /* Decided, but nobody has found out yet whether it worked. */
    CA_DECISION_OUTCOME_OPEN = 0,
    /* It worked. This is the road to take again. */
    CA_DECISION_OUTCOME_RESOLVED,
    /* It did not. Worth as much as a fix, and often sooner. */
    CA_DECISION_OUTCOME_FAILED
} ca_decision_outcome_t;

const char *ca_decision_outcome_name(ca_decision_outcome_t outcome);

typedef struct {
    char *id;
    ca_atom_kind_t kind;
    char *text;
    /* Where it came from. An atom with no source cannot be re-checked, and an
     * unverifiable fact ages into a confident wrong answer. */
    char *source;
    int64_t created_unix;
    int64_t last_recalled_unix;
    /* Days. See ca_forgetting_initial_stability_days. */
    double stability_days;
    int recall_count;
    int correction_count;
    ca_decision_outcome_t outcome;   /* meaningful for DECISION */
    char **tags;
    size_t tag_count;
} ca_memory_atom_t;

void ca_memory_atom_free(ca_memory_atom_t *atom);

/* -- deciding what is worth writing down ---------------------------------- */

typedef struct {
    char *text;
    ca_atom_kind_t kind;
    double confidence;
    char *source;
    char *rationale;
} ca_atom_candidate_t;

void ca_atom_candidate_free(ca_atom_candidate_t *candidate);

/* Above this a candidate is recorded without asking. Below it, it waits.
 *
 * 0.80 rather than a majority: the cost of a wrong atom is not one bad row, it
 * is a wrong answer offered confidently for months, and unlike a missing atom
 * nothing ever prompts anybody to look for it. */
double ca_atom_candidate_record_above(void);

typedef struct ca_atom_extractor {
    void *state;
    /* Heap array of *out_count. Most turns yield NOTHING, and an extractor that
     * always finds something fills the store with the ordinary. */
    ca_atom_candidate_t *(*extract)(void *state, const char *text, size_t *out_count);
    void (*free_fn)(void *state);
} ca_atom_extractor_t;

void ca_atom_extractor_free(ca_atom_extractor_t *extractor);

/* The cues that make a sentence worth a second look: a correction, a
 * preference stated outright, an outcome reported. Separated from the extractor
 * so the cheap pass can run everywhere and the expensive one only after it. */
typedef struct ca_cue_extractor ca_cue_extractor_t;

ca_cue_extractor_t *ca_cue_extractor_new(void);
void ca_cue_extractor_free(ca_cue_extractor_t *extractor);

char **ca_cue_extractor_cues(ca_cue_extractor_t *extractor, const char *text,
                             size_t *out_count);

typedef struct {
    int examined;
    int recorded;
    int held;
    int merged;
    char *note;
} ca_learn_report_t;

void ca_learn_report_free(ca_learn_report_t *report);

typedef struct ca_atom_learner ca_atom_learner_t;

/* Turns a conversation into atoms, reporting what it did rather than doing it
 * silently. The report is the accountability: a learner nobody can audit is a
 * component that edits what an assistant believes with no record. */
ca_atom_learner_t *ca_atom_learner_new(ca_atom_extractor_t *extractor);
void ca_atom_learner_free(ca_atom_learner_t *learner);

bool ca_atom_learner_learn(ca_atom_learner_t *learner, const char *text,
                           ca_learn_report_t *out_report);

/* -- the log -------------------------------------------------------------- */

typedef struct {
    int64_t sequence;
    int64_t at_unix;
    char *operation;   /* "append", "recall", "correct", "supersede" */
    char *atom_id;
    char *payload_json;
} ca_atom_record_t;

void ca_atom_record_free(ca_atom_record_t *record);

typedef struct ca_atom_log ca_atom_log_t;

/*
 * Append-only, and the only thing here that is authoritative.
 *
 * There is no delete. Superseding writes a new record that points at the old
 * one, so the history of what was believed - and when it changed - survives.
 * A store that edits in place cannot answer "why did it think that", which is
 * the question every memory bug turns out to be.
 */
ca_atom_log_t *ca_atom_log_open(const char *path);
void ca_atom_log_close(ca_atom_log_t *log);

bool ca_atom_log_append(ca_atom_log_t *log, const ca_atom_record_t *record);

ca_atom_record_t *ca_atom_log_read_from(ca_atom_log_t *log, int64_t from_sequence,
                                        size_t *out_count);

int64_t ca_atom_log_sequence(const ca_atom_log_t *log);

/* -- the store ------------------------------------------------------------ */

typedef struct ca_atom_store {
    void *state;
    bool (*put)(void *state, const ca_memory_atom_t *atom);
    /* Borrowed; NULL when absent. */
    const ca_memory_atom_t *(*get)(void *state, const char *atom_id);
    ca_memory_atom_t *(*search)(void *state, const char *query, size_t top_k,
                                size_t *out_count);
    size_t (*count)(void *state);
    void (*free_fn)(void *state);
} ca_atom_store_t;

void ca_atom_store_free(ca_atom_store_t *store);

ca_atom_store_t *ca_atom_store_new(void);
ca_atom_store_t *ca_sqlite_atom_store_open(const char *path);

/* -- forgetting ----------------------------------------------------------- */

/*
 * Initial stability, in days.
 *
 * A quarter untouched and still there; most of a year untouched and gone. A
 * finished project's decisions crowding today's recall is how a store becomes a
 * filing cabinet.
 *
 * THE FIRST ATTEMPT WAS FOURTEEN DAYS, reasoned from how fast a single human
 * exposure decays, and it was wrong by a factor of six. What it missed is that
 * THE VALUE OF A MEMORY IS INVERSELY RELATED TO HOW OFTEN THE SITUATION COMES
 * UP: what happens daily gets learned anyway, and what happens twice a year is
 * exactly what nobody remembers and exactly what is worth writing down. At
 * fourteen days, the thing written down in January had gone quiet by March.
 */
double ca_forgetting_initial_stability_days(void);

/* Below this an atom has faded out of what recall OFFERS. Not deleted: still in
 * the log, still there by id, still findable by anybody who goes looking. */
double ca_forgetting_threshold(void);

/* What a retrieval at the edge of fading is worth. A retrieval at
 * retrievability 0 multiplies stability by 1 + this; one at retrievability 1
 * does not move it at all. Two is a doubling at the edge, which puts an atom
 * rescued at the last moment about six weeks further out. */
double ca_forgetting_spacing_gain(void);

/* What a correction is worth. Being told the same thing again is the strongest
 * encoding there is - it carries the weight of having got it wrong. Four
 * corrections put an atom roughly a year out on its own. */
double ca_forgetting_correction_gain(void);

/* How much of a kind's weight decays at all. Rulings and relationships hold
 * hardest (0.40), preferences less (0.20), and a decision's record does not
 * decay by kind at all - what happened, happened. */
double ca_forgetting_kind_decay(ca_atom_kind_t kind);

/* 0..1: how likely this is to be retrievable now. */
double ca_forgetting_retrievability(const ca_memory_atom_t *atom, int64_t now_unix);

/* The new stability after a retrieval or a correction. Pure, so the caller
 * decides whether to write it - recall must be able to run without mutating
 * the store, or reading a memory changes it and no measurement is repeatable. */
double ca_forgetting_reinforce(const ca_memory_atom_t *atom, int64_t now_unix,
                               bool was_correction);

bool ca_forgetting_is_faded(const ca_memory_atom_t *atom, int64_t now_unix);

/* -- wear ----------------------------------------------------------------- */

typedef struct {
    char *atom_id;
    int64_t at_unix;
    /* What was being done when it was reached for. Wear is only meaningful
     * against a situation: an atom recalled constantly in one context and never
     * in another is not "hot", it is specific. */
    char *situation;
} ca_memory_trace_t;

void ca_memory_trace_free(ca_memory_trace_t *trace);

typedef struct ca_memory_wear ca_memory_wear_t;

/* Which paths are actually walked. Used to rank, never to prune - deleting what
 * has not been used yet is how a store forgets the thing somebody needs once a
 * year, which is the exact case it exists for. */
ca_memory_wear_t *ca_memory_wear_new(void);
void ca_memory_wear_free(ca_memory_wear_t *wear);

void ca_memory_wear_record(ca_memory_wear_t *wear, const ca_memory_trace_t *trace);
double ca_memory_wear_score(const ca_memory_wear_t *wear, const char *atom_id,
                            const char *situation);

/* How long a module keeps what it writes. Stated per module rather than
 * globally: a scratchpad and a ledger have no business sharing a policy. */
typedef struct {
    char *module;
    int64_t max_age_seconds;   /* negative for forever */
    size_t max_atoms;          /* 0 for unlimited */
} ca_memory_retention_t;

void ca_memory_retention_free(ca_memory_retention_t *retention);

/* -- recall --------------------------------------------------------------- */

typedef struct {
    char *description;
    char **active_goals;
    size_t goal_count;
    char *app_context;
    char *language;
    int64_t at_unix;
} ca_situation_t;

void ca_situation_free(ca_situation_t *situation);

/*
 * What recall is allowed to spend.
 *
 * Both limits, not one: five atoms of two hundred words each blows a prompt
 * budget as surely as fifty short ones. Defaults 5 atoms and 600 characters.
 */
typedef struct {
    int max_atoms;
    int max_characters;
} ca_recall_budget_t;

ca_recall_budget_t ca_recall_budget_default(void);

typedef struct ca_memory_service ca_memory_service_t;

ca_memory_service_t *ca_memory_service_new(ca_atom_store_t *store,
                                           ca_atom_log_t *log,
                                           ca_memory_wear_t *wear);

void ca_memory_service_free(ca_memory_service_t *service);

/* Heap array of *out_count, best first, within budget. Faded atoms are not
 * offered here; they are still reachable by id. */
ca_memory_atom_t *ca_memory_service_recall(ca_memory_service_t *service,
                                           const ca_situation_t *situation,
                                           const ca_recall_budget_t *budget,
                                           size_t *out_count);

bool ca_memory_service_remember(ca_memory_service_t *service,
                                const ca_atom_candidate_t *candidate);

bool ca_memory_service_correct(ca_memory_service_t *service, const char *atom_id,
                               const char *corrected_text);

/* -- payloads and hooks --------------------------------------------------- */

typedef struct {
    char *hook;
    char *payload_json;
    int64_t at_unix;
} ca_hook_payload_t;

void ca_hook_payload_free(ca_hook_payload_t *payload);

/*
 * Embeddings, quantised.
 *
 * Vectors are most of a memory store's bytes and almost none of its meaning,
 * so they are the one thing worth compressing hard. The codec is LOSSY and
 * says so: a recall ranked on decompressed vectors will occasionally order two
 * near-identical atoms differently, and that is an acceptable trade nobody
 * should discover by surprise.
 */
/* The codec's format version, written into every payload. A lossy codec with
 * no version is a store that cannot be read after the codec improves - and the
 * vectors are the one part of a memory that cannot be recomputed from the log.
 */
int ca_embedding_payload_codec_version(void);

uint8_t *ca_embedding_payload_encode(const float *vector, size_t dims,
                                     int bits_per_value, size_t *out_len);

bool ca_embedding_payload_decode(const uint8_t *bytes, size_t len, size_t dims,
                                 float *out_vector);

/* -- multimodal ----------------------------------------------------------- */

typedef struct ca_multimodal_captioner {
    void *state;
    /* Caller frees. NULL when it has nothing honest to say - a caption invented
     * for an image nobody can see becomes a remembered fact that was never
     * true. */
    char *(*caption)(void *state, const uint8_t *bytes, size_t len,
                     const char *mime_type);
    void (*free_fn)(void *state);
} ca_multimodal_captioner_t;

void ca_multimodal_captioner_free(ca_multimodal_captioner_t *captioner);

/* Metadata, dimensions, and whatever text is embedded in the file. No model:
 * it describes what can be established, and declines the rest. */
ca_multimodal_captioner_t *ca_heuristic_multimodal_captioner_new(void);

/* -- sync ----------------------------------------------------------------- */

typedef struct {
    int sent;
    int received;
    int conflicts;
    int64_t at_unix;
    char *error;
} ca_sync_report_t;

void ca_sync_report_free(ca_sync_report_t *report);

typedef struct ca_sync_hub ca_sync_hub_t;

/* Everything in one process, for a device syncing between its own components.
 * The default because cross-device sync is the mesh's problem, and a memory
 * store that opens its own connection has made a policy decision that is not
 * its to make. */
ca_sync_hub_t *ca_in_process_sync_hub_new(void);
void ca_sync_hub_free(ca_sync_hub_t *hub);

typedef struct ca_companion_state_channel ca_companion_state_channel_t;

ca_companion_state_channel_t *ca_in_process_companion_state_channel_new(
    ca_sync_hub_t *hub);

void ca_companion_state_channel_free(ca_companion_state_channel_t *channel);

/* Conversations, persona state and LoRA adapters each sync differently, so each
 * gets its own bridge rather than one that switches on a type tag. A
 * conversation merges by append; persona state merges by last-writer; an
 * adapter does not merge at all and the newer one wins whole. */
typedef struct ca_sync_bridge ca_sync_bridge_t;

void ca_sync_bridge_free(ca_sync_bridge_t *bridge);

ca_sync_bridge_t *ca_companion_conversation_sync_bridge_new(
    ca_companion_state_channel_t *channel);

ca_sync_bridge_t *ca_persona_state_sync_bridge_new(
    ca_companion_state_channel_t *channel);

ca_sync_bridge_t *ca_lora_adapter_sync_bridge_new(
    ca_companion_state_channel_t *channel);

bool ca_sync_bridge_run(ca_sync_bridge_t *bridge, ca_sync_report_t *out_report);

/* -- consolidation -------------------------------------------------------- */

typedef struct {
    char *persona_id;
    int64_t at_unix;
    char *delta_json;
    /* What the snapshot was computed FROM, so a persona drift can be traced to
     * the atoms that caused it rather than argued about. */
    char **source_atom_ids;
    size_t source_count;
} ca_persona_delta_snapshot_t;

void ca_persona_delta_snapshot_free(ca_persona_delta_snapshot_t *snapshot);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MEMORY_ATOMS_H */
