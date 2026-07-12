#ifndef CIRCLE_AI_PACA_H
#define CIRCLE_AI_PACA_H

/*
 * paca.h — CircleAI.Workflows PACA surface (C11 port).
 *
 * Ports the paca (PacaBoards / PacaAuth / PacaProjects / PacaAgents / PacaDocs /
 * PacaPlugins / PacaMcp / PacaRealtime / PacaSkills / PacaDeploy) subsystem from
 * src/CircleAI.Workflows/. The durable-workflow contracts (Contracts.cs /
 * NullImplementations.cs) already ship as workflows.h; this header carries the
 * missing PACA project-management runtime.
 *
 *   Auth      : HmacJwtAuthenticator — HMAC-SHA256 access+refresh JWTs (self-
 *               contained SHA-256/HMAC, base64url, fixed-time compare) +
 *               PacaApiKeyAuthenticator — SHA-256-hashed API keys in a store.
 *   Projects  : InMemoryPacaStore — projects + auto-numbered tasks (PREFIX-N),
 *               soft deletes, per-project scoping.
 *   Boards    : PacaBoard — status columns (position-ordered), sprints
 *               (Planning/Active/Completed), per-task board metadata, named
 *               views, paginated column reads, sprint bucketing.
 *   Agents    : ProjectMember + AgentProfile + AgentTemplates (5 presets) +
 *               InMemoryPacaMemberStore.
 *   Docs      : PacaDocService — doc tree (folders+documents), version
 *               snapshots, activity feed, task links, @mention extraction.
 *   Plugins   : PacaPluginRegistry — manifest validation (reverse-DNS + SemVer),
 *               install/upgrade(semver-gated)/uninstall/enable, semver compare.
 *   Mcp       : PacaMcpServer — tool registry + per-agent enabled-tool gate +
 *               synchronous invoke + built-in core tool descriptors.
 *   Realtime  : PacaRealtimeHub — permission-gated rooms + broadcaster seam +
 *               query-invalidation key mapping.
 *   Skills    : PacaSkillLibrary (11 built-ins) + frontmatter stripper.
 *   Deploy    : PacaDeployer — docker compose + .env generator (self-contained
 *               random secret) + plugin install/uninstall bash scripts.
 *
 * Conventions: ca_ prefix, _t types, opaque handles where a class owns mutable
 * state, strdup-owning fields with matching *_free, deep-copy getters, errors
 * via NULL / -1 / SIZE_MAX. The async / broadcaster / plugin-runtime / MCP
 * handler boundaries are ca_ fn-ptr vtable seams. Timestamps are Unix ms UTC,
 * supplied by an injectable clock seam (default = 0 for hermetic determinism —
 * the host passes a real clock).
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── injectable clock seam ──────────────────────────────────────────────────
 * Func<DateTimeOffset> in C#. Returns Unix ms UTC. When NULL is passed to a
 * factory, a monotonic-zero clock is used (deterministic). */
typedef int64_t (*ca_paca_clock_fn)(void *ctx);

/* ===========================================================================
 * Auth — HmacJwtAuthenticator (HMAC-SHA256 access + refresh JWTs)
 * =========================================================================== */

/* JwtPair(AccessToken, RefreshToken, AccessExpiresAtUtc, RefreshExpiresAtUtc). */
typedef struct {
    char   *access_token;             /* owned */
    char   *refresh_token;            /* owned */
    int64_t access_expires_at_ms;
    int64_t refresh_expires_at_ms;
} ca_paca_jwt_pair_t;

void ca_paca_jwt_pair_free(ca_paca_jwt_pair_t *p);

/* One (key,value) claim. */
typedef struct {
    char *key;    /* owned */
    char *value;  /* owned */
} ca_paca_claim_t;

/* JwtPayload(Subject, Claims, ExpiresAtUtc). */
typedef struct {
    char            *subject;         /* owned */
    ca_paca_claim_t *claims;          /* owned array (may be NULL/empty) */
    size_t           claim_count;
    int64_t          expires_at_ms;
} ca_paca_jwt_payload_t;

void ca_paca_jwt_payload_free(ca_paca_jwt_payload_t *p);
/* Lookup a claim by key (Ordinal). Borrowed value or NULL. */
const char *ca_paca_jwt_payload_claim(const ca_paca_jwt_payload_t *p, const char *key);

typedef struct ca_paca_jwt_auth ca_paca_jwt_auth_t;

/* HmacJwtAuthenticator(signingSecret, accessLifetimeMs, refreshLifetimeMs,
 * clock). signing_secret must be >= 16 chars (NULL / too-short -> NULL). Pass 0
 * for a lifetime to accept the C# defaults (access 15 min, refresh 7 days). */
ca_paca_jwt_auth_t *ca_paca_jwt_auth_create(const char *signing_secret,
                                            int64_t access_lifetime_ms,
                                            int64_t refresh_lifetime_ms,
                                            ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_jwt_auth_destroy(ca_paca_jwt_auth_t *a);

/* Issue access + refresh tokens. subject must be non-blank. claims may be NULL.
 * Fills *out (owned; free with ca_paca_jwt_pair_free). 0 / -1. */
int ca_paca_jwt_auth_issue(ca_paca_jwt_auth_t *a, const char *subject,
                           const ca_paca_claim_t *claims, size_t claim_count,
                           ca_paca_jwt_pair_t *out);

/* Verify a token of the given type ("access"/"refresh"). Fills *out (owned) and
 * returns 0 on a valid, unexpired, correctly-typed token; -1 otherwise. */
int ca_paca_jwt_auth_verify(ca_paca_jwt_auth_t *a, const char *token,
                            const char *expected_type, ca_paca_jwt_payload_t *out);

/* ===========================================================================
 * Auth — PacaApiKeyAuthenticator (SHA-256-hashed API keys)
 * =========================================================================== */

/* PacaApiKeyRecord(KeyId, Label, HashedSecret, CreatedAtUtc, RevokedAtUtc). */
typedef struct {
    char   *key_id;        /* owned */
    char   *label;         /* owned */
    char   *hashed_secret; /* owned */
    int64_t created_at_ms;
    int64_t revoked_at_ms; /* -1 == null (live) */
} ca_paca_api_key_record_t;

void ca_paca_api_key_record_free(ca_paca_api_key_record_t *r);

typedef struct ca_paca_api_key_auth ca_paca_api_key_auth_t;

ca_paca_api_key_auth_t *ca_paca_api_key_auth_create(ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_api_key_auth_destroy(ca_paca_api_key_auth_t *a);

/* Issue a fresh key. label must be non-blank. Fills *out_record (owned) and
 * *out_raw_secret (owned; the raw secret returned ONCE). 0 / -1. */
int ca_paca_api_key_auth_issue(ca_paca_api_key_auth_t *a, const char *label,
                               ca_paca_api_key_record_t *out_record,
                               char **out_raw_secret);

/* Verify a presented (key_id, secret). Fills *out (owned) + returns 0 when the
 * key exists, is live, and the hash matches (fixed-time); -1 otherwise. */
int ca_paca_api_key_auth_verify(ca_paca_api_key_auth_t *a, const char *key_id,
                                const char *presented_secret,
                                ca_paca_api_key_record_t *out);

/* Revoke a key (idempotent). */
void ca_paca_api_key_auth_revoke(ca_paca_api_key_auth_t *a, const char *key_id);

/* ===========================================================================
 * Projects — InMemoryPacaStore (projects + auto-numbered tasks)
 * =========================================================================== */

/* PacaProject(Id, Name, Prefix, SettingsJson, CreatedAtUtc, DeletedAtUtc). */
typedef struct {
    char   *id;            /* owned */
    char   *name;          /* owned */
    char   *prefix;        /* owned */
    char   *settings_json; /* owned */
    int64_t created_at_ms;
    int64_t deleted_at_ms; /* -1 == null (live) */
} ca_paca_project_t;

void ca_paca_project_free(ca_paca_project_t *p);
void ca_paca_project_free_array(ca_paca_project_t *arr, size_t count);

/* PacaTask(ProjectId, Number, Title, DescriptionJson, Status, CreatedAtUtc,
 * DeletedAtUtc). */
typedef struct {
    char   *project_id;       /* owned */
    int     number;
    char   *title;            /* owned */
    char   *description_json; /* owned */
    char   *status;           /* owned */
    int64_t created_at_ms;
    int64_t deleted_at_ms;    /* -1 == null (live) */
} ca_paca_task_t;

void ca_paca_task_free(ca_paca_task_t *t);
void ca_paca_task_free_array(ca_paca_task_t *arr, size_t count);
/* Reference() -> "<prefix>-<Number>". Owned; caller frees. */
char *ca_paca_task_reference(const ca_paca_task_t *t, const char *prefix);

typedef struct ca_paca_store ca_paca_store_t;

ca_paca_store_t *ca_paca_store_create(ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_store_destroy(ca_paca_store_t *s);

/* CreateProject. id/name/prefix must be non-blank. settings_json NULL -> "{}".
 * Fills *out (owned) + returns 0; -1 on validation failure OR duplicate id. */
int ca_paca_store_create_project(ca_paca_store_t *s, const char *id,
                                 const char *name, const char *prefix,
                                 const char *settings_json, ca_paca_project_t *out);

/* GetProject (live only). Fills *out (owned) + 0; -1 if missing / soft-deleted. */
int ca_paca_store_get_project(ca_paca_store_t *s, const char *id, ca_paca_project_t *out);

/* DeleteProject (soft, idempotent). */
void ca_paca_store_delete_project(ca_paca_store_t *s, const char *id);

/* UpdateProjectSettings. new_settings_json NULL -> "{}". Fills *out + 0; -1 if
 * the project is missing. */
int ca_paca_store_update_project_settings(ca_paca_store_t *s, const char *project_id,
                                          const char *new_settings_json,
                                          ca_paca_project_t *out);

/* AddTask (auto-numbered). status NULL -> "todo", description NULL -> "{}".
 * Fills *out + 0; -1 if the project is missing. */
int ca_paca_store_add_task(ca_paca_store_t *s, const char *project_id,
                           const char *title, const char *description_json,
                           const char *status, ca_paca_task_t *out);

/* ListTasks (live, ordered by Number). Owned array; free with
 * ca_paca_task_free_array. NULL + *out_count SIZE_MAX on error; empty is a
 * non-NULL/NULL array with count 0. */
ca_paca_task_t *ca_paca_store_list_tasks(ca_paca_store_t *s, const char *project_id,
                                         size_t *out_count);

/* GetTaskByReference ("PREFIX-3"). Fills *out + 0; -1 if not found. */
int ca_paca_store_get_task_by_reference(ca_paca_store_t *s, const char *project_id,
                                        const char *reference, ca_paca_task_t *out);

/* UpdateTask in place (matched by ProjectId + Number). No-op if absent. */
void ca_paca_store_update_task(ca_paca_store_t *s, const ca_paca_task_t *updated);

/* DeleteTask (soft). */
void ca_paca_store_delete_task(ca_paca_store_t *s, const char *project_id, int number);

/* ===========================================================================
 * Boards — PacaBoard (columns + sprints + metadata + views)
 * =========================================================================== */

typedef enum { CA_PACA_SPRINT_PLANNING, CA_PACA_SPRINT_ACTIVE, CA_PACA_SPRINT_COMPLETED }
    ca_paca_sprint_state_t;

/* StatusColumn(Name, Category, Position, Collapsed). */
typedef struct {
    char *name;      /* owned */
    char *category;  /* owned */
    int   position;
    bool  collapsed;
} ca_paca_status_column_t;

void ca_paca_status_column_free(ca_paca_status_column_t *c);
void ca_paca_status_column_free_array(ca_paca_status_column_t *arr, size_t count);

/* PacaSprint(Id, ProjectId, Name, Goal, StartDate, EndDate, State). */
typedef struct {
    char                  *id;         /* owned */
    char                  *project_id; /* owned */
    char                  *name;       /* owned */
    char                  *goal;       /* owned */
    int64_t                start_ms;
    int64_t                end_ms;
    ca_paca_sprint_state_t state;
} ca_paca_sprint_t;

void ca_paca_sprint_free(ca_paca_sprint_t *s);

/* TaskBoardMetadata(ProjectId, Number, StoryPoints, Importance, AssigneeMemberId,
 * ReporterMemberId, ParentTaskNumber, SprintId, Tags, CustomFields,
 * PositionInColumn). */
typedef struct {
    char            *project_id;         /* owned */
    int              number;
    int              story_points;
    int              importance;         /* 0..5 */
    char            *assignee_member_id; /* owned, NULL == null */
    char            *reporter_member_id; /* owned, NULL == null */
    int              parent_task_number; /* -1 == null */
    char            *sprint_id;          /* owned, NULL == null */
    char           **tags;               /* owned array of owned strings */
    size_t           tag_count;
    ca_paca_claim_t *custom_fields;      /* owned (key,value) array */
    size_t           custom_field_count;
    int              position_in_column;
} ca_paca_task_metadata_t;

void ca_paca_task_metadata_free(ca_paca_task_metadata_t *m);

/* BoardView(Name, FilterTagsCsv, FilterAssignee, SortBy, SortDescending,
 * VisibleColumns, VisibleFields). */
typedef struct {
    char   *name;            /* owned */
    char   *filter_tags_csv;  /* owned, NULL == null */
    char   *filter_assignee;  /* owned, NULL == null */
    char   *sort_by;          /* owned, NULL == null */
    bool    sort_descending;
    char  **visible_columns;  /* owned array of owned strings */
    size_t  visible_column_count;
    char  **visible_fields;   /* owned array of owned strings */
    size_t  visible_field_count;
} ca_paca_board_view_t;

void ca_paca_board_view_free(ca_paca_board_view_t *v);
void ca_paca_board_view_free_array(ca_paca_board_view_t *arr, size_t count);

typedef struct ca_paca_board ca_paca_board_t;

/* PacaBoard(tasks). Borrows the store (must outlive the board). Seeds the six
 * default columns (todo/in_progress/in_review/done/cancelled/blocked). */
ca_paca_board_t *ca_paca_board_create(ca_paca_store_t *tasks,
                                      ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_board_destroy(ca_paca_board_t *b);

/* Columns (ordered by Position). Owned array; free with
 * ca_paca_status_column_free_array. */
ca_paca_status_column_t *ca_paca_board_columns(ca_paca_board_t *b, size_t *out_count);

/* AddColumn (upsert by Name). Copies col. 0 / -1. */
int ca_paca_board_add_column(ca_paca_board_t *b, const ca_paca_status_column_t *col);

/* CollapseColumn (no-op if absent). */
void ca_paca_board_collapse_column(ca_paca_board_t *b, const char *name, bool collapsed);

/* MoveTask across status columns, updating in-column position. 0 / -1 (task not
 * found OR unknown status). */
int ca_paca_board_move_task(ca_paca_board_t *b, const char *project_id, int number,
                            const char *new_status, int new_position);

/* SetTaskMetadata (copies). 0 / -1. */
int ca_paca_board_set_task_metadata(ca_paca_board_t *b, const ca_paca_task_metadata_t *m);

/* GetTaskMetadata. Fills *out (owned) + 0; -1 if none set. */
int ca_paca_board_get_task_metadata(ca_paca_board_t *b, const char *project_id,
                                    int number, ca_paca_task_metadata_t *out);

/* TasksInColumn (paginated; ordered by PositionInColumn). Owned array. */
ca_paca_task_t *ca_paca_board_tasks_in_column(ca_paca_board_t *b, const char *project_id,
                                              const char *status, int skip, int take,
                                              size_t *out_count);

/* TasksInSprint. Owned array. */
ca_paca_task_t *ca_paca_board_tasks_in_sprint(ca_paca_board_t *b, const char *sprint_id,
                                              size_t *out_count);

/* CreateSprint (Planning). Fills *out (owned) + 0; -1 on OOM. */
int ca_paca_board_create_sprint(ca_paca_board_t *b, const char *id, const char *project_id,
                                const char *name, const char *goal,
                                int64_t start_ms, int64_t end_ms, ca_paca_sprint_t *out);

/* GetSprint. Fills *out + 0; -1 if missing. */
int ca_paca_board_get_sprint(ca_paca_board_t *b, const char *id, ca_paca_sprint_t *out);
/* StartSprint -> Active; CompleteSprint -> Completed. Fills *out + 0; -1 if
 * missing. */
int ca_paca_board_start_sprint(ca_paca_board_t *b, const char *id, ca_paca_sprint_t *out);
int ca_paca_board_complete_sprint(ca_paca_board_t *b, const char *id, ca_paca_sprint_t *out);

/* SaveView (upsert by Name, copies). 0 / -1. */
int ca_paca_board_save_view(ca_paca_board_t *b, const ca_paca_board_view_t *v);
/* GetView. Fills *out + 0; -1 if missing. */
int ca_paca_board_get_view(ca_paca_board_t *b, const char *name, ca_paca_board_view_t *out);
/* ListViews (ordered by Name). Owned array. */
ca_paca_board_view_t *ca_paca_board_list_views(ca_paca_board_t *b, size_t *out_count);

/* ===========================================================================
 * Agents — ProjectMember + AgentProfile + AgentTemplates + member store
 * =========================================================================== */

typedef enum { CA_PACA_MEMBER_HUMAN, CA_PACA_MEMBER_AGENT } ca_paca_member_kind_t;

/* ProjectMember(Id, ProjectId, Kind, DisplayName, Handle, Role, AvatarUrl,
 * CreatedAtUtc, DeletedAtUtc). */
typedef struct {
    char                 *id;           /* owned */
    char                 *project_id;   /* owned */
    ca_paca_member_kind_t kind;
    char                 *display_name; /* owned */
    char                 *handle;       /* owned */
    char                 *role;         /* owned */
    char                 *avatar_url;   /* owned, NULL == null */
    int64_t               created_at_ms;
    int64_t               deleted_at_ms;/* -1 == null (live) */
} ca_paca_member_t;

void ca_paca_member_free(ca_paca_member_t *m);
void ca_paca_member_free_array(ca_paca_member_t *arr, size_t count);

/* AgentProfile — flattened (LlmConfig + SystemPrompts + Capabilities + Limits +
 * GitIdentity + Triggers). NULL string fields model C# null. */
typedef struct {
    char   *member_id;                  /* owned */
    /* AgentLlmConfig */
    char   *llm_provider;               /* owned */
    char   *llm_model;                  /* owned */
    char   *llm_api_key;                /* owned, NULL == null */
    char   *llm_base_address;           /* owned, NULL == null (Uri) */
    /* AgentSystemPrompts */
    char   *task_prompt;                /* owned, NULL == null */
    char   *doc_prompt;                 /* owned, NULL == null */
    char   *chat_prompt;                /* owned, NULL == null */
    /* AgentCapabilities */
    bool    can_clone_repos;
    bool    can_create_prs;
    bool    can_write_files;
    bool    can_call_external_tools;
    /* AgentLimits */
    int     max_iterations;
    int64_t timeout_ms;
    /* AgentGitIdentity */
    char   *git_name;                   /* owned */
    char   *git_email;                  /* owned */
    /* AgentTriggers */
    char   *trigger_task_created;       /* owned, NULL == null */
    char   *trigger_chat_mention;       /* owned, NULL == null */
    char   *trigger_doc_edit;           /* owned, NULL == null */
    char   *trigger_direct_mention;     /* owned, NULL == null */
} ca_paca_agent_profile_t;

void ca_paca_agent_profile_free(ca_paca_agent_profile_t *p);
/* Deep copy (dst assumed uninitialised). Returns dst or NULL on OOM. */
ca_paca_agent_profile_t *ca_paca_agent_profile_copy(ca_paca_agent_profile_t *dst,
                                                    const ca_paca_agent_profile_t *src);

/* AgentTemplates — the five presets. Fill *out (owned; free with
 * ca_paca_agent_profile_free). 0 / -1. api_key/base_address may be NULL. */
int ca_paca_agent_template_development(const char *member_id, const char *api_key,
                                       const char *base_address, ca_paca_agent_profile_t *out);
int ca_paca_agent_template_product_manager(const char *member_id, const char *api_key,
                                           ca_paca_agent_profile_t *out);
int ca_paca_agent_template_designer(const char *member_id, const char *api_key,
                                    ca_paca_agent_profile_t *out);
int ca_paca_agent_template_qa(const char *member_id, const char *api_key,
                              ca_paca_agent_profile_t *out);
int ca_paca_agent_template_code_reviewer(const char *member_id, const char *api_key,
                                         ca_paca_agent_profile_t *out);
/* PresetNames — { "development", "pm", "design", "qa", "review" }. Borrowed. */
const char *const *ca_paca_agent_preset_names(size_t *out_count);

typedef struct ca_paca_member_store ca_paca_member_store_t;

ca_paca_member_store_t *ca_paca_member_store_create(ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_member_store_destroy(ca_paca_member_store_t *s);

/* AddHuman. role NULL -> "developer". Fills *out (owned) + 0; -1 on validation
 * failure OR duplicate id. */
int ca_paca_member_store_add_human(ca_paca_member_store_t *s, const char *id,
                                   const char *project_id, const char *display_name,
                                   const char *handle, const char *role,
                                   const char *avatar, ca_paca_member_t *out);

/* AddAgent (role forced to "agent"; stores a copy of profile with MemberId=id).
 * Fills *out (owned) + 0; -1 on failure. */
int ca_paca_member_store_add_agent(ca_paca_member_store_t *s, const char *id,
                                   const char *project_id, const char *display_name,
                                   const char *handle,
                                   const ca_paca_agent_profile_t *profile,
                                   const char *avatar, ca_paca_member_t *out);

/* GetMember (live only). Fills *out + 0; -1 if missing / deleted. */
int ca_paca_member_store_get_member(ca_paca_member_store_t *s, const char *id,
                                    ca_paca_member_t *out);

/* GetAgentProfile. Fills *out (owned) + 0; -1 if none. */
int ca_paca_member_store_get_agent_profile(ca_paca_member_store_t *s, const char *member_id,
                                           ca_paca_agent_profile_t *out);

/* ListMembers (live; ordered by DisplayName). kind_filter: -1 for all, else a
 * ca_paca_member_kind_t. Owned array. */
ca_paca_member_t *ca_paca_member_store_list_members(ca_paca_member_store_t *s,
                                                    const char *project_id,
                                                    int kind_filter, size_t *out_count);

/* RemoveMember (soft). */
void ca_paca_member_store_remove_member(ca_paca_member_store_t *s, const char *id);

/* UpdateAgentProfile (member must exist and be an agent). Fills *out + 0; -1. */
int ca_paca_member_store_update_agent_profile(ca_paca_member_store_t *s,
                                              const char *member_id,
                                              const ca_paca_agent_profile_t *updated,
                                              ca_paca_agent_profile_t *out);

/* ===========================================================================
 * Docs — PacaDocService (doc tree + versions + activity + links + mentions)
 * =========================================================================== */

/* DocNode(Id, ProjectId, ParentId, IsFolder, Title, ContentJson, CreatedAtUtc,
 * DeletedAtUtc). */
typedef struct {
    char   *id;           /* owned */
    char   *project_id;   /* owned */
    char   *parent_id;    /* owned, NULL == null */
    bool    is_folder;
    char   *title;        /* owned */
    char   *content_json; /* owned */
    int64_t created_at_ms;
    int64_t deleted_at_ms;/* -1 == null (live) */
} ca_paca_doc_node_t;

void ca_paca_doc_node_free(ca_paca_doc_node_t *n);
void ca_paca_doc_node_free_array(ca_paca_doc_node_t *arr, size_t count);

/* DocVersion(VersionId, DocId, ContentJson, SavedAtUtc, AuthorMemberId). */
typedef struct {
    char   *version_id;      /* owned */
    char   *doc_id;          /* owned */
    char   *content_json;    /* owned */
    int64_t saved_at_ms;
    char   *author_member_id;/* owned */
} ca_paca_doc_version_t;

void ca_paca_doc_version_free(ca_paca_doc_version_t *v);
void ca_paca_doc_version_free_array(ca_paca_doc_version_t *arr, size_t count);

/* DocActivity(ActivityId, DocId, AuthorMemberId, Action, Detail, At). */
typedef struct {
    char   *activity_id;     /* owned */
    char   *doc_id;          /* owned */
    char   *author_member_id;/* owned */
    char   *action;          /* owned */
    char   *detail;          /* owned, NULL == null */
    int64_t at_ms;
} ca_paca_doc_activity_t;

void ca_paca_doc_activity_free(ca_paca_doc_activity_t *a);
void ca_paca_doc_activity_free_array(ca_paca_doc_activity_t *arr, size_t count);

/* DocLink(LinkId, DocId, SectionAnchor, ProjectId, TaskNumber). */
typedef struct {
    char *link_id;        /* owned */
    char *doc_id;         /* owned */
    char *section_anchor; /* owned */
    char *project_id;     /* owned */
    int   task_number;
} ca_paca_doc_link_t;

void ca_paca_doc_link_free(ca_paca_doc_link_t *l);
void ca_paca_doc_link_free_array(ca_paca_doc_link_t *arr, size_t count);

typedef struct ca_paca_doc_service ca_paca_doc_service_t;

ca_paca_doc_service_t *ca_paca_doc_service_create(ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_doc_service_destroy(ca_paca_doc_service_t *s);

/* CreateFolder. Fills *out + 0; -1 on validation / duplicate. */
int ca_paca_doc_service_create_folder(ca_paca_doc_service_t *s, const char *id,
                                      const char *project_id, const char *parent_id,
                                      const char *title, ca_paca_doc_node_t *out);

/* CreateDocument. content_json NULL -> "{}". Fills *out + 0; -1. */
int ca_paca_doc_service_create_document(ca_paca_doc_service_t *s, const char *id,
                                        const char *project_id, const char *parent_id,
                                        const char *title, const char *content_json,
                                        const char *author_member_id, ca_paca_doc_node_t *out);

/* Get (live only). Fills *out + 0; -1. */
int ca_paca_doc_service_get(ca_paca_doc_service_t *s, const char *id, ca_paca_doc_node_t *out);

/* ListChildren (live; ordered by Title). parent_id NULL matches root. Owned. */
ca_paca_doc_node_t *ca_paca_doc_service_list_children(ca_paca_doc_service_t *s,
                                                      const char *project_id,
                                                      const char *parent_id,
                                                      size_t *out_count);

/* Edit: writes a version snapshot (of the PRIOR content) + an activity entry,
 * returns the mentioned handles extracted from the NEW content. Owned array of
 * owned strings (deduped, case-insensitive). NULL + *out_count SIZE_MAX on
 * error (doc missing / is a folder / deleted). */
char **ca_paca_doc_service_edit(ca_paca_doc_service_t *s, const char *id,
                                const char *new_content_json, const char *author_member_id,
                                bool is_ai_edit, size_t *out_count);

/* Versions. Owned array. */
ca_paca_doc_version_t *ca_paca_doc_service_versions(ca_paca_doc_service_t *s,
                                                    const char *doc_id, size_t *out_count);

/* DiffLines: added = lines in `after` not in `before`; removed = the reverse
 * (set-difference on '\n'-split lines). Both owned arrays of owned strings. */
int ca_paca_doc_service_diff_lines(const char *before, const char *after,
                                   char ***out_added, size_t *out_added_count,
                                   char ***out_removed, size_t *out_removed_count);

/* Activity. Owned array. */
ca_paca_doc_activity_t *ca_paca_doc_service_activity(ca_paca_doc_service_t *s,
                                                     const char *doc_id, size_t *out_count);

/* Link a doc section to a task; appends a "linked" activity. Fills *out + 0; -1. */
int ca_paca_doc_service_link(ca_paca_doc_service_t *s, const char *doc_id,
                             const char *section_anchor, const char *project_id,
                             int task_number, ca_paca_doc_link_t *out);

/* Links. Owned array. */
ca_paca_doc_link_t *ca_paca_doc_service_links(ca_paca_doc_service_t *s,
                                              const char *doc_id, size_t *out_count);

/* Standalone @mention extractor: unique @([a-zA-Z0-9_-]+) handles (without the
 * '@'), deduped case-insensitively, first-seen order. Owned array. */
char **ca_paca_extract_mentions(const char *content, size_t *out_count);

/* Free an owned array of owned strings (used by mentions / diff). */
void ca_paca_string_array_free(char **arr, size_t count);

/* ===========================================================================
 * Plugins — PacaPluginRegistry (manifest validation + semver lifecycle)
 * =========================================================================== */

typedef enum {
    CA_PACA_EXT_SIDEBAR, CA_PACA_EXT_TASK_DETAIL, CA_PACA_EXT_SETTINGS,
    CA_PACA_EXT_CUSTOM_VIEW, CA_PACA_EXT_ROUTE, CA_PACA_EXT_EVENT, CA_PACA_EXT_MCP_TOOL
} ca_paca_ext_point_t;

/* PluginResourceLimits(CallTimeoutMs=5000, MemoryCeilingBytes=64MB). */
typedef struct {
    int     call_timeout_ms;
    int64_t memory_ceiling_bytes;
} ca_paca_plugin_limits_t;

/* PluginManifest. */
typedef struct {
    char                *name;               /* owned, reverse-DNS */
    char                *display_name;       /* owned */
    char                *version;            /* owned, SemVer */
    char                *description;        /* owned */
    char                *artifact_wasm_url;  /* owned, NULL == null */
    char                *frontend_module_url;/* owned, NULL == null */
    ca_paca_ext_point_t *extension_points;   /* owned array */
    size_t               extension_point_count;
    char               **mcp_tools;          /* owned array of owned strings */
    size_t               mcp_tool_count;
    char               **sql_migration_files;/* owned array of owned strings */
    size_t               sql_migration_file_count;
    ca_paca_plugin_limits_t limits;
} ca_paca_plugin_manifest_t;

void ca_paca_plugin_manifest_free(ca_paca_plugin_manifest_t *m);
ca_paca_plugin_manifest_t *ca_paca_plugin_manifest_copy(ca_paca_plugin_manifest_t *dst,
                                                        const ca_paca_plugin_manifest_t *src);

/* InstalledPlugin(Id, Manifest, InstalledFromCatalog, InstalledAtUtc, Enabled). */
typedef struct {
    char                     *id;             /* owned (== manifest.name) */
    ca_paca_plugin_manifest_t manifest;       /* owned */
    char                     *installed_from_catalog; /* owned */
    int64_t                   installed_at_ms;
    bool                      enabled;
} ca_paca_installed_plugin_t;

void ca_paca_installed_plugin_free(ca_paca_installed_plugin_t *p);
void ca_paca_installed_plugin_free_array(ca_paca_installed_plugin_t *arr, size_t count);

/* IPluginRuntimeHost seam (wazero-style). Each returns 0 / -1. */
typedef struct {
    void *self;
    int (*install)(void *self, const ca_paca_installed_plugin_t *plugin);
    int (*uninstall)(void *self, const char *plugin_id, bool drop_artifacts);
    int (*upgrade)(void *self, const ca_paca_installed_plugin_t *from,
                   const ca_paca_installed_plugin_t *to);
} ca_paca_plugin_runtime_host_t;

/* ValidateManifest — reverse-DNS name + parseable SemVer + positive limits.
 * 0 valid / -1 invalid. Static (no registry needed). */
int ca_paca_plugin_validate_manifest(const ca_paca_plugin_manifest_t *m);

/* CompareSemver — <0 / 0 / >0 (prerelease/build metadata stripped, up to four
 * dotted numeric components). */
int ca_paca_plugin_compare_semver(const char *a, const char *b);

typedef struct ca_paca_plugin_registry ca_paca_plugin_registry_t;

/* Borrows `runtime` (must outlive the registry). */
ca_paca_plugin_registry_t *ca_paca_plugin_registry_create(
    const ca_paca_plugin_runtime_host_t *runtime, ca_paca_clock_fn clock, void *clock_ctx);
void ca_paca_plugin_registry_destroy(ca_paca_plugin_registry_t *r);

/* ListInstalled. Owned array. */
ca_paca_installed_plugin_t *ca_paca_plugin_registry_list(ca_paca_plugin_registry_t *r,
                                                         size_t *out_count);
/* Get. Fills *out + 0; -1 if not installed. */
int ca_paca_plugin_registry_get(ca_paca_plugin_registry_t *r, const char *id,
                                ca_paca_installed_plugin_t *out);

/* Install (validates; fails if already installed; invokes runtime.install).
 * Fills *out + 0; -1 on any failure. */
int ca_paca_plugin_registry_install(ca_paca_plugin_registry_t *r,
                                    const ca_paca_plugin_manifest_t *manifest,
                                    const char *catalog, ca_paca_installed_plugin_t *out);

/* Upgrade (only if strictly newer SemVer; invokes runtime.upgrade). 0 / -1. */
int ca_paca_plugin_registry_upgrade(ca_paca_plugin_registry_t *r,
                                    const ca_paca_plugin_manifest_t *new_manifest,
                                    const char *catalog, ca_paca_installed_plugin_t *out);

/* Uninstall (invokes runtime.uninstall when present). No-op if absent. */
void ca_paca_plugin_registry_uninstall(ca_paca_plugin_registry_t *r, const char *id,
                                       bool drop_artifacts);

/* SetEnabled (no-op if absent). */
void ca_paca_plugin_registry_set_enabled(ca_paca_plugin_registry_t *r, const char *id,
                                         bool enabled);

/* ===========================================================================
 * Mcp — PacaMcpServer (tool registry + per-agent gate + invoke)
 * =========================================================================== */

/* PacaMcpTool(Name, Description, InputSchema). */
typedef struct {
    char *name;         /* owned */
    char *description;  /* owned */
    char *input_schema; /* owned */
} ca_paca_mcp_tool_t;

void ca_paca_mcp_tool_free(ca_paca_mcp_tool_t *t);
void ca_paca_mcp_tool_free_array(ca_paca_mcp_tool_t *arr, size_t count);

/* PacaMcpHandler — invoked with arguments JSON; returns a freshly-owned result
 * JSON string via *out_result (0), or -1 to model a thrown exception (the
 * server then wraps its message; supply *out_result with the message on -1 or
 * NULL for a generic error). */
typedef int (*ca_paca_mcp_handler_fn)(void *ctx, const char *arguments_json,
                                      char **out_result);

/* AgentMcpConfig(AgentMemberId, Transports, EnabledTools, ToolSettings). */
typedef enum { CA_PACA_MCP_STDIO, CA_PACA_MCP_SSE, CA_PACA_MCP_HTTP } ca_paca_mcp_transport_t;

typedef struct {
    char                    *agent_member_id;  /* owned */
    ca_paca_mcp_transport_t *transports;        /* owned array */
    size_t                   transport_count;
    char                   **enabled_tools;     /* owned array of owned strings */
    size_t                   enabled_tool_count;
    ca_paca_claim_t         *tool_settings;     /* owned (key,value) array */
    size_t                   tool_setting_count;
} ca_paca_mcp_agent_config_t;

void ca_paca_mcp_agent_config_free(ca_paca_mcp_agent_config_t *c);

typedef struct ca_paca_mcp_server ca_paca_mcp_server_t;

ca_paca_mcp_server_t *ca_paca_mcp_server_create(void);
void ca_paca_mcp_server_destroy(ca_paca_mcp_server_t *s);

/* RegisterTool (upsert by Name, case-insensitive; copies tool). 0 / -1. */
int ca_paca_mcp_server_register_tool(ca_paca_mcp_server_t *s,
                                     const ca_paca_mcp_tool_t *tool,
                                     ca_paca_mcp_handler_fn handler, void *ctx);

/* Tools. Owned array. */
ca_paca_mcp_tool_t *ca_paca_mcp_server_tools(ca_paca_mcp_server_t *s, size_t *out_count);

/* ConfigureAgent (upsert; copies config). 0 / -1. */
int ca_paca_mcp_server_configure_agent(ca_paca_mcp_server_t *s,
                                       const ca_paca_mcp_agent_config_t *config);

/* GetAgentConfig. Fills *out + 0; -1 if none. */
int ca_paca_mcp_server_get_agent_config(ca_paca_mcp_server_t *s, const char *agent_member_id,
                                        ca_paca_mcp_agent_config_t *out);

/* Invoke a tool for an agent (enforces the agent's enabled-tool list when set).
 * Always fills *out_result (owned JSON) and returns 0; unknown/blocked tools and
 * handler exceptions become {"error":{"message":...}} payloads. */
int ca_paca_mcp_server_invoke(ca_paca_mcp_server_t *s, const char *agent_member_id,
                              const char *tool_name, const char *arguments_json,
                              char **out_result);

/* Built-in core tool descriptors (create_task / list_tasks / edit_task /
 * create_doc / link_doc_to_task). Fill *out (owned) + 0. */
int ca_paca_mcp_core_tool_create_task(ca_paca_mcp_tool_t *out);
int ca_paca_mcp_core_tool_list_tasks(ca_paca_mcp_tool_t *out);
int ca_paca_mcp_core_tool_edit_task(ca_paca_mcp_tool_t *out);
int ca_paca_mcp_core_tool_create_doc(ca_paca_mcp_tool_t *out);
int ca_paca_mcp_core_tool_link_doc_to_task(ca_paca_mcp_tool_t *out);

/* ===========================================================================
 * Realtime — PacaRealtimeHub (permission-gated rooms + broadcaster seam)
 * =========================================================================== */

typedef enum {
    CA_PACA_EV_TASK_UPDATED, CA_PACA_EV_QUERY_INVALIDATION, CA_PACA_EV_DOC_CURSOR_MOVE,
    CA_PACA_EV_AGENT_ACTIVITY, CA_PACA_EV_CONVERSATION_STEP
} ca_paca_realtime_event_kind_t;

/* RealtimePacaEvent union (ProjectId + At + kind-specific payload). */
typedef struct {
    ca_paca_realtime_event_kind_t kind;
    char   *project_id;   /* owned */
    int64_t at_ms;
    /* TaskUpdatedEvent */
    int     task_number;
    /* QueryInvalidationEvent */
    char   *query_key;    /* owned, NULL unless QUERY_INVALIDATION */
    /* DocCursorMoveEvent */
    char   *doc_id;       /* owned, NULL unless DOC_CURSOR_MOVE */
    char   *member_id;    /* owned, NULL unless DOC_CURSOR_MOVE */
    int     cursor_offset;
    /* AgentActivityEvent */
    char   *agent_member_id; /* owned, NULL unless AGENT_ACTIVITY */
    char   *action;          /* owned, NULL unless AGENT_ACTIVITY */
    char   *detail_json;     /* owned, NULL unless AGENT_ACTIVITY */
    /* ConversationStepEvent */
    char   *conversation_id; /* owned, NULL unless CONVERSATION_STEP */
} ca_paca_realtime_event_t;

void ca_paca_realtime_event_free(ca_paca_realtime_event_t *e);

/* IRealtimeBroadcaster seam. Receives a BORROWED event. 0 / -1. */
typedef struct {
    void *self;
    int (*broadcast)(void *self, const char *room, const ca_paca_realtime_event_t *ev);
} ca_paca_broadcaster_t;

/* PermissionCheck seam. Return true if `member_id` may join `room`. */
typedef bool (*ca_paca_permission_fn)(void *ctx, const char *member_id, const char *room);

typedef struct ca_paca_realtime_hub ca_paca_realtime_hub_t;

/* Borrows the broadcaster. permission NULL -> always allow. NULL on OOM /
 * NULL broadcaster. */
ca_paca_realtime_hub_t *ca_paca_realtime_hub_create(const ca_paca_broadcaster_t *broadcaster,
                                                    ca_paca_permission_fn permission,
                                                    void *permission_ctx);
void ca_paca_realtime_hub_destroy(ca_paca_realtime_hub_t *h);

/* Join (gated by the permission check). Returns true when allowed. */
bool ca_paca_realtime_hub_join(ca_paca_realtime_hub_t *h, const char *member_id,
                               const char *room);
void ca_paca_realtime_hub_leave(ca_paca_realtime_hub_t *h, const char *member_id,
                                const char *room);
/* Members of a room. Owned array of owned strings. */
char **ca_paca_realtime_hub_members(ca_paca_realtime_hub_t *h, const char *room,
                                    size_t *out_count);

/* Publish to "project:<ProjectId>". 0 / -1. */
int ca_paca_realtime_hub_publish(ca_paca_realtime_hub_t *h, const ca_paca_realtime_event_t *ev);
/* Publish to "doc:<docId>". 0 / -1. */
int ca_paca_realtime_hub_publish_to_doc(ca_paca_realtime_hub_t *h, const char *doc_id,
                                        const ca_paca_realtime_event_t *ev);

/* QueryInvalidation.KeysFor — maps an event to its client query-invalidation
 * keys. Owned array of owned strings (may be empty). */
char **ca_paca_query_invalidation_keys_for(const ca_paca_realtime_event_t *ev,
                                           size_t *out_count);

/* ===========================================================================
 * Skills — PacaSkillLibrary + installer helpers
 * =========================================================================== */

/* PacaSkill(Name, Description, Body). */
typedef struct {
    char *name;        /* owned */
    char *description; /* owned */
    char *body;        /* owned */
} ca_paca_skill_t;

void ca_paca_skill_free(ca_paca_skill_t *s);
void ca_paca_skill_free_array(ca_paca_skill_t *arr, size_t count);

/* ToMarkdown — "---\nname: ..\ndescription: ..\n---\n\n<body>". Owned. */
char *ca_paca_skill_to_markdown(const ca_paca_skill_t *s);

/* PacaSkillLibrary.All — the eleven built-ins (deduped by name, insertion
 * order). Owned array; free with ca_paca_skill_free_array. */
ca_paca_skill_t *ca_paca_skill_library_all(size_t *out_count);
/* Find (case-insensitive). Fills *out (owned) + 0; -1 if not found. */
int ca_paca_skill_library_find(const char *name, ca_paca_skill_t *out);

/* StripFrontmatter — remove a leading "---...---\n" block; then TrimStart. When
 * there is no leading frontmatter, returns the input TrimStart'd. Owned. */
char *ca_paca_skill_strip_frontmatter(const char *markdown);

/* ===========================================================================
 * Deploy — PacaDeployer (compose + .env + plugin script generators)
 * =========================================================================== */

typedef enum { CA_PACA_DEPLOY_DEV, CA_PACA_DEPLOY_PROD, CA_PACA_DEPLOY_E2E }
    ca_paca_deploy_mode_t;

/* PacaDeployOverrides(UseExternalPostgres?, UseExternalS3?, SkipAiAgent). */
typedef struct {
    const char *use_external_postgres; /* NULL == null (bundle it) */
    const char *use_external_s3;       /* NULL == null (bundle MinIO) */
    bool        skip_ai_agent;
} ca_paca_deploy_overrides_t;

/* PacaDeployArtifact(ComposeYaml, EnvFile). */
typedef struct {
    char *compose_yaml; /* owned */
    char *env_file;     /* owned */
} ca_paca_deploy_artifact_t;

void ca_paca_deploy_artifact_free(ca_paca_deploy_artifact_t *a);

/* Build the compose + .env pair. overrides may be NULL (all defaults). Fills
 * *out (owned) + 0; -1 on OOM. The .env carries fresh random secrets. */
int ca_paca_deployer_build(ca_paca_deploy_mode_t mode,
                           const ca_paca_deploy_overrides_t *overrides,
                           ca_paca_deploy_artifact_t *out);

/* BuildInstallPluginScript / BuildUninstallPluginScript. plugin_name must be
 * non-blank. Owned string; NULL on failure. */
char *ca_paca_deployer_build_install_plugin_script(const char *plugin_name);
char *ca_paca_deployer_build_uninstall_plugin_script(const char *plugin_name);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PACA_H */
