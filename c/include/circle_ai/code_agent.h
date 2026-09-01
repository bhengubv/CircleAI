#ifndef CIRCLE_AI_CODE_AGENT_H
#define CIRCLE_AI_CODE_AGENT_H

/*
 * code_agent.h - CircleAI.CodeAgent (C11): a model that edits a codebase.
 *
 * The loop is small - read, edit, run, search, finish - and almost everything
 * here is about BOUNDING it. That is not caution for its own sake: this is the
 * only module in the codebase where a wrong answer writes to disk and runs a
 * program, so the interesting engineering is in what it cannot do.
 *
 * THE COMMAND RUNNER IS ALLOW-LISTED AND THE DEFAULT RUNS NOTHING. Not a
 * deny-list: a deny-list is a claim to have thought of every dangerous command,
 * and it is wrong the first time somebody pipes one into another. A host names
 * the executables it will permit, and anything else is refused with a reason.
 *
 * AN UNPARSEABLE REPLY IS AN ACTION, NOT AN ERROR. It comes back as UNKNOWN
 * with the raw text kept, so the loop can re-prompt. Throwing on a malformed
 * reply turns the single most common thing a model does - answering in prose
 * when asked for JSON - into a crash.
 *
 * THE DEVICE FLOOR IS REAL. A 3B coding model needs about 8 GB of RAM, and on
 * a phone that does not have it the honest answer is that this feature is
 * absent - not that it is slow.
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

/* -- what the model asked for --------------------------------------------- */

typedef enum {
    /* The reply could not be parsed into a known action. Kept as a value so
     * the loop can re-prompt rather than fail. */
    CA_AGENT_ACTION_UNKNOWN = 0,
    CA_AGENT_ACTION_READ_FILE,
    /* A character-range edit. Ranges rather than a diff because a diff that
     * fails to apply leaves the model guessing why; a range either is or is not
     * inside the file. */
    CA_AGENT_ACTION_EDIT_FILE,
    CA_AGENT_ACTION_RUN_COMMAND,
    CA_AGENT_ACTION_SEARCH_CODE,
    CA_AGENT_ACTION_FINISH
} ca_agent_action_kind_t;

const char *ca_agent_action_kind_name(ca_agent_action_kind_t kind);

typedef struct {
    ca_agent_action_kind_t kind;
    char *path;
    int range_start;
    int range_end;
    char *replacement;
    char *command;
    char *query;
    int top_k;          /* 10 by default */
    char *summary;
    /* The source JSON, or the whole reply when it did not parse. Kept for
     * diagnostics and for re-prompting: without it, a loop that goes wrong
     * leaves no evidence of what the model actually said. */
    char *raw;
} ca_agent_action_t;

void ca_agent_action_free(ca_agent_action_t *action);

/*
 * Parses one reply into an action. Never fails - a reply it cannot understand
 * becomes UNKNOWN with `raw` set.
 *
 * Finds the JSON object by BRACE DEPTH rather than by regex, because models
 * routinely wrap the object in prose, in a fenced block, or in both, and a
 * regex that handles two of those three quietly mis-parses the third.
 */
ca_agent_action_t *ca_agent_action_parser_parse(const char *reply);

/* -- running a command ---------------------------------------------------- */

typedef struct {
    char *executable;
    char **arguments;
    size_t argument_count;
    char *working_directory;
    int timeout_ms;     /* 60000 by default */
} ca_command_request_t;

void ca_command_request_free(ca_command_request_t *request);

typedef struct {
    /* Whether it ran at all. FALSE with exit code 0 is the shape of a refusal,
     * and a caller that only checks the exit code would read that as success. */
    bool executed;
    bool timed_out;
    int exit_code;
    char *stdout_text;
    char *stderr_text;
    /* Why it did not run. Populated only when `executed` is false. */
    char *refusal;
} ca_command_result_t;

void ca_command_result_free(ca_command_result_t *result);

bool ca_command_result_success(const ca_command_result_t *result);

typedef struct ca_command_runner {
    void *state;
    ca_command_result_t *(*run)(void *state, const ca_command_request_t *request);
    void (*free_fn)(void *state);
} ca_command_runner_t;

void ca_command_runner_free(ca_command_runner_t *runner);

/* Refuses everything, with a reason. THE DEFAULT: an agent that can run
 * commands because nobody configured a runner is an agent that can run commands
 * by accident. */
ca_command_runner_t *ca_disabled_command_runner_new(void);

/*
 * Runs only what is on the list.
 *
 * Matching is on the RESOLVED executable, not the string the model wrote -
 * otherwise "./git", "git.exe" and a relative path through a symlink are three
 * different things to the check and one thing to the operating system.
 *
 * Output is truncated at `max_output_chars` (64 KB by default). A command that
 * prints a hundred megabytes would otherwise be handed to a model as context
 * and cost more than the entire task.
 */
ca_command_runner_t *ca_process_command_runner_new(const char **allowed_executables,
                                                   size_t count,
                                                   size_t max_output_chars);

/* -- what a coding model needs -------------------------------------------- */

/* Which class of device this is. A floor stated in tiers as well as gigabytes
 * because RAM alone does not capture thermal headroom - a phone with 8 GB
 * throttles where a tablet with 8 GB does not. */
typedef enum {
    CA_DEVICE_TIER_WEARABLE = 0,
    CA_DEVICE_TIER_LOW_PHONE,
    CA_DEVICE_TIER_PHONE,
    CA_DEVICE_TIER_TABLET,
    CA_DEVICE_TIER_DESKTOP,
    CA_DEVICE_TIER_SERVER
} ca_device_tier_t;

const char *ca_device_tier_name(ca_device_tier_t tier);

/* What a chat model must be able to do to drive the loop. Flags, because a
 * model missing any ONE of them fails differently and the caller should be able
 * to say which. */
typedef enum {
    CA_CHAT_CAPABILITY_NONE = 0,
    /* Must emit tool-call blocks, or the loop has nothing to act on. */
    CA_CHAT_CAPABILITY_TOOLS = 1 << 0,
    CA_CHAT_CAPABILITY_REASONING = 1 << 1,
    CA_CHAT_CAPABILITY_LONG_CONTEXT = 1 << 2,
    CA_CHAT_CAPABILITY_VISION = 1 << 3
} ca_chat_capability_t;

typedef struct {
    int min_parameters_billion;
    double min_ram_gb;
    double min_free_storage_gb;
    ca_device_tier_t min_device_tier;
    ca_chat_capability_t required_capabilities;
} ca_coding_model_requirements_t;

/*
 * The provisional floor: 3B+, ~8 GB RAM, ~6 GB free, tablet or better, with
 * tools, reasoning and long context.
 *
 * PROVISIONAL AND LABELLED SO. These are reasoned, not measured - the numbers
 * to trust are the ones a bench run produces on the actual device, and a
 * default that pretends otherwise is a threshold nobody ever revisits.
 */
ca_coding_model_requirements_t ca_coding_model_requirements_default(void);

typedef struct {
    char *model_id;
    int parameters_billion;
    double ram_gb;
    double download_gb;
    ca_chat_capability_t capabilities;
    char *note;
} ca_coding_model_descriptor_t;

void ca_coding_model_descriptor_free(ca_coding_model_descriptor_t *descriptor);

typedef struct ca_coding_model_catalog {
    void *state;
    ca_coding_model_descriptor_t *(*list)(void *state, size_t *out_count);
    /* Borrowed; NULL when nothing in the catalogue meets the floor. NULL is the
     * whole answer - returning the closest model and letting it fail on load is
     * how a feature becomes a crash report. */
    const ca_coding_model_descriptor_t *(*best_for)(
        void *state, const ca_coding_model_requirements_t *requirements);
    void (*free_fn)(void *state);
} ca_coding_model_catalog_t;

void ca_coding_model_catalog_free(ca_coding_model_catalog_t *catalog);

ca_coding_model_catalog_t *ca_coding_model_catalog_new(void);
ca_coding_model_catalog_t *ca_empty_coding_model_catalog_new(void);

typedef struct ca_coding_capability_planner {
    void *state;
    /* Whether this device can run a coding agent at all, and what to say if
     * not. The reason is shown to a person, so it names the shortfall - "needs
     * about 8 GB of memory" - rather than a policy identifier. */
    bool (*is_capable)(void *state, char **out_reason);
    void (*free_fn)(void *state);
} ca_coding_capability_planner_t;

void ca_coding_capability_planner_free(ca_coding_capability_planner_t *planner);

ca_coding_capability_planner_t *ca_coding_capability_planner_new(
    ca_coding_model_catalog_t *catalog, int64_t ram_bytes,
    int64_t free_storage_bytes, ca_device_tier_t tier);

/* -- the loop ------------------------------------------------------------- */

typedef struct {
    int index;
    ca_agent_action_t *action;
    /* What came back - file text, command output, search hits. Truncated to
     * what the context budget allows, and the truncation is marked so the model
     * knows it did not see everything. */
    char *observation;
    bool observation_truncated;
    int64_t duration_ms;
} ca_code_agent_step_t;

void ca_code_agent_step_free(ca_code_agent_step_t *step);

typedef struct {
    bool finished;
    char *summary;
    ca_code_agent_step_t *steps;
    size_t step_count;
    /* Set when the loop stopped because it hit the iteration cap rather than
     * because the model said FINISH. The two must never be confused: one is a
     * completed task and the other is an abandoned one. */
    bool exhausted_iterations;
    char *error;
} ca_code_agent_run_result_t;

void ca_code_agent_run_result_free(ca_code_agent_run_result_t *result);

typedef struct ca_code_agent_loop ca_code_agent_loop_t;

/*
 * `max_iterations` is a termination guarantee, not a tuning knob.
 *
 * A model that has lost the thread does not stop - it reads the same file
 * again, edits it back, and reads it once more. Without a cap, that costs money
 * until somebody notices, and on a phone it costs battery until it is flat.
 */
ca_code_agent_loop_t *ca_code_agent_loop_new(ca_command_runner_t *runner,
                                             int max_iterations);

void ca_code_agent_loop_free(ca_code_agent_loop_t *loop);

ca_code_agent_run_result_t *ca_code_agent_loop_run(ca_code_agent_loop_t *loop,
                                                   const char *task,
                                                   const char *working_directory);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CODE_AGENT_H */
