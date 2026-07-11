#ifndef CIRCLE_AI_DEVTOOLS_H
#define CIRCLE_AI_DEVTOOLS_H

/*
 * devtools.h — CircleAI.DevTools (C11 port of Contracts.cs + InMemoryDevTools.cs
 * + NullImplementations.cs). The IDE / agent-shell replacement surface.
 *
 * The C# real impls read/write files; here the filesystem is the injected
 * boundary and every tool operates over an in-memory buffer store (the host
 * loads path -> text buffers). The algorithms — cursor-partial completion, word-
 * boundary rename, remove-line, append, extract-constant, the built-in agent
 * executor — port faithfully.
 *
 *   Records : FileEdit(Path, RangeStart, RangeEnd, Replacement);
 *             InlineSuggestion(Text, float Confidence);
 *             AgentTurn(TurnId, UserPrompt, Response, Edits[]);
 *             PatchPlan(Goal, Steps[], ProposedEdits[]);
 *             RefactorRequest(Description, TargetPaths[]).
 *   Editor  : ICodeEditor -> BufferCodeEditor. PutBuffer(path, text); Read(path)
 *               -> text copy (false when absent); Apply(edits) groups by path,
 *               orders by RangeStart desc, splices each range; Save no-op.
 *               BackendId "buffer".
 *   Suggest : IInlineSuggester -> TokenContextInlineSuggester. Suggest(path, line,
 *               col, contextBefore) predicts the partial token at the cursor from
 *               the file's own identifier vocabulary (editor buffer for path, else
 *               contextBefore). BackendId "token-context".
 *   Shell   : IAgentShell -> InMemoryAgentShell. RunTurn(prompt) via a built-in
 *               executor ("read "/"write "/"?"/default), TurnId "turn-N", keeps
 *               history; History(limit=50) newest-limit in chronological order.
 *               BackendId "in-memory".
 *   Planner : IPatchPlanner -> PatternMatchPatchPlanner over an editor. Plan(goal)
 *               parses "rename X to Y [in F]" / "remove line N from F" /
 *               "append TEXT to F"; Apply(plan) applies its edits. BackendId
 *               "pattern-match".
 *   Refactor: IRefactorTool -> RegexRefactorTool over an editor. Propose(request)
 *               handles "rename X to Y" + "extract constant from \"LIT\" as NAME".
 *               BackendId "regex".
 *   Null variants return ""/null/empty.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* FileEdit(Path, RangeStart, RangeEnd, Replacement). */
typedef struct {
    char *path;        /* owned, non-null */
    int   range_start;
    int   range_end;
    char *replacement; /* owned, non-null */
} ca_file_edit_t;

void ca_file_edit_free(ca_file_edit_t *e);
void ca_file_edit_free_array(ca_file_edit_t *arr, size_t count);

/* InlineSuggestion(Text, Confidence). */
typedef struct {
    char *text; /* owned, non-null */
    float confidence;
} ca_inline_suggestion_t;

void ca_inline_suggestion_free(ca_inline_suggestion_t *s);

/* AgentTurn(TurnId, UserPrompt, Response, Edits[]). */
typedef struct {
    char           *turn_id;     /* owned, non-null */
    char           *user_prompt; /* owned, non-null */
    char           *response;    /* owned, non-null */
    ca_file_edit_t *edits;       /* owned; NULL when edit_count == 0 */
    size_t          edit_count;
} ca_agent_turn_t;

void ca_agent_turn_free(ca_agent_turn_t *t);
void ca_agent_turn_free_array(ca_agent_turn_t *arr, size_t count);

/* PatchPlan(Goal, Steps[], ProposedEdits[]). */
typedef struct {
    char           *goal;  /* owned, non-null */
    char          **steps; /* owned; NULL when step_count == 0 */
    size_t          step_count;
    ca_file_edit_t *proposed_edits; /* owned; NULL when edit_count == 0 */
    size_t          edit_count;
} ca_patch_plan_t;

void ca_patch_plan_free(ca_patch_plan_t *p);

/* ── ICodeEditor -> BufferCodeEditor ────────────────────────────────────── */

typedef struct ca_code_editor ca_code_editor_t;

ca_code_editor_t *ca_code_editor_create(void); /* NULL on OOM */
void ca_code_editor_destroy(ca_code_editor_t *e);
const char *ca_code_editor_backend_id(const ca_code_editor_t *e); /* "buffer" */

/* PutBuffer(path, text) — keyed by path (replace). 0 / -1 on bad args / OOM. */
int ca_code_editor_put_buffer(ca_code_editor_t *e, const char *path,
                              const char *text);
/* Read(path) -> fresh copy of the buffer, or NULL when absent / bad args. */
char *ca_code_editor_read(const ca_code_editor_t *e, const char *path);
/* Apply(edits) — groups by path, orders RangeStart desc, splices. 0 / -1 on bad
 * args, an out-of-range edit, or a missing buffer. */
int ca_code_editor_apply(ca_code_editor_t *e, const ca_file_edit_t *edits,
                         size_t edit_count);

const char *ca_dt_null_code_editor_backend_id(void); /* "null" */

/* ── IInlineSuggester -> TokenContextInlineSuggester ────────────────────── */

/* Suggest(path, line, col, contextBefore) -> fresh suggestion into *out, true;
 * false when there is no suggestion (partial < 2 chars, no candidate) or on bad
 * args (path required, contextBefore non-null). editor may be NULL. BackendId
 * "token-context". */
bool ca_inline_suggest(const ca_code_editor_t *editor, const char *path, int line,
                       int column, const char *context_before,
                       ca_inline_suggestion_t *out);
const char *ca_inline_suggester_backend_id(void); /* "token-context" */
const char *ca_dt_null_inline_suggester_backend_id(void); /* "null" */

/* ── IAgentShell -> InMemoryAgentShell ──────────────────────────────────── */

typedef struct ca_agent_shell ca_agent_shell_t;

ca_agent_shell_t *ca_agent_shell_create(void); /* NULL on OOM */
void ca_agent_shell_destroy(ca_agent_shell_t *s);
const char *ca_agent_shell_backend_id(const ca_agent_shell_t *s); /* "in-memory" */

/* RunTurn(prompt) -> fresh turn into *out, true; false on bad args (prompt
 * non-null). Records the turn in history with TurnId "turn-N". */
bool ca_agent_shell_run_turn(ca_agent_shell_t *s, const char *user_prompt,
                             ca_agent_turn_t *out);
/* History(limit) newest-limit in chronological order. NULL + 0 empty; NULL +
 * SIZE_MAX on error (limit > 0). */
ca_agent_turn_t *ca_agent_shell_history(const ca_agent_shell_t *s, int limit,
                                        size_t *out_count);

const char *ca_dt_null_agent_shell_backend_id(void); /* "null" */

/* ── IPatchPlanner -> PatternMatchPatchPlanner ──────────────────────────── */

/* Plan(goal) over the editor's buffers -> fresh plan into *out, true; false on
 * a blank goal / bad args. BackendId "pattern-match". */
bool ca_patch_plan(const ca_code_editor_t *editor, const char *goal,
                   ca_patch_plan_t *out);
/* Apply(plan) — applies the plan's edits via the editor. 0 / -1. */
int ca_patch_plan_apply(ca_code_editor_t *editor, const ca_patch_plan_t *plan);
const char *ca_patch_planner_backend_id(void); /* "pattern-match" */
const char *ca_dt_null_patch_planner_backend_id(void); /* "null" */

/* ── IRefactorTool -> RegexRefactorTool ─────────────────────────────────── */

/* Propose(description, targetPaths) over the editor's buffers -> fresh edit
 * array. NULL + 0 empty; NULL + SIZE_MAX on error (description / targetPaths
 * non-null). BackendId "regex". */
ca_file_edit_t *ca_refactor_propose(const ca_code_editor_t *editor,
                                    const char *description,
                                    const char *const *target_paths,
                                    size_t target_count, size_t *out_count);
const char *ca_refactor_tool_backend_id(void); /* "regex" */
const char *ca_dt_null_refactor_tool_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DEVTOOLS_H */
