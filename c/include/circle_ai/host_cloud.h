#ifndef CIRCLE_AI_HOST_CLOUD_H
#define CIRCLE_AI_HOST_CLOUD_H

/*
 * host_cloud.h — CircleAI.Hosting.CloudFallback (C11 port).
 *
 * Ports (from src/CircleAI.Hosting.CloudFallback):
 *   IChatGenerator seam (Generate + Stream) — the generic generator contract
 *   IConfigurableChatGenerator (IsConfigured / EngineLabel / StatusMessage)
 *   CloudFallbackChain          — start-of-call ordering; first *ready*
 *                                 generator serves; a "[... not configured ...]"
 *                                 fail-soft first frame is filtered and the
 *                                 chain moves on
 *   BackupBrainOrchestrator     — between-turn failover: degrade a brain after
 *                                 N consecutive failures, cool-down, half-open
 *                                 retry; BrainHealth + BrainStatus + policy
 *
 * The real vendor generators (OpenAI/Groq/…) are injected impls behind the
 * generator seam. A deterministic local fake (ca_fake_chat_generator) is
 * provided for tests: configurable IsConfigured, a canned reply, and a
 * scripted failure count so the failover/cool-down logic is fully exercisable.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup owning fields.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "inference_rt.h"   /* ca_chat_msg_t */
#include "inference.h"      /* ca_generation_options_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * IChatGenerator / IConfigurableChatGenerator seam
 * =========================================================================== */

/* Streaming: on_chunk(user, chunk) per frame; chunk borrowed for the call.
 * Return false to stop early (cancellation). */
typedef bool (*ca_gen_chunk_fn)(void *user, const char *chunk);

/*
 * A generator vtable. `generate` returns a malloc'd full reply (or NULL). To
 * signal a hard failure (throw), return NULL from generate / -1 from stream.
 * is_configured/engine_label/status_message are optional (the C#
 * IConfigurableChatGenerator subset). When is_configured is NULL the generator
 * is presumed always ready (matches IsReady's default-true).
 */
typedef struct {
    char *(*generate)(void *self, const ca_chat_msg_t *messages, size_t count,
                      const ca_generation_options_t *opts);
    /* stream: drive on_chunk per frame; return frames yielded, or -1 on
     * failure. When NULL, the chain/orchestrator falls back to generate(). */
    long  (*stream)(void *self, const ca_chat_msg_t *messages, size_t count,
                    const ca_generation_options_t *opts,
                    ca_gen_chunk_fn on_chunk, void *chunk_user);
    bool        (*is_configured)(void *self);        /* may be NULL => true */
    const char *(*engine_label)(void *self);         /* may be NULL */
    const char *(*status_message)(void *self);       /* may be NULL */
    void        (*destroy)(void *self);              /* may be NULL */
    void        *self;
} ca_chat_gen_iface_t;

/* Dispatchers. */
char       *ca_chat_gen_generate(ca_chat_gen_iface_t *g, const ca_chat_msg_t *messages,
                                 size_t count, const ca_generation_options_t *opts);
long        ca_chat_gen_stream(ca_chat_gen_iface_t *g, const ca_chat_msg_t *messages,
                               size_t count, const ca_generation_options_t *opts,
                               ca_gen_chunk_fn on_chunk, void *chunk_user);
bool        ca_chat_gen_is_configured(ca_chat_gen_iface_t *g);
const char *ca_chat_gen_engine_label(ca_chat_gen_iface_t *g);
const char *ca_chat_gen_status_message(ca_chat_gen_iface_t *g);

/* ===========================================================================
 * Deterministic local fake generator (for tests + local-default in a chain)
 * ===========================================================================
 *
 * generate: returns "<label>: <last-user-content>" (or a fail-soft
 * "[<label> not configured]" first frame when configured==false — matching the
 * OpenAiCompatible fail-soft frame the chain filters).
 * Optionally scripts `fail_times` hard failures (NULL/-1) before succeeding,
 * to drive BackupBrainOrchestrator degrade/cool-down.
 */
typedef struct ca_fake_chat_generator ca_fake_chat_generator_t;

ca_fake_chat_generator_t *ca_fake_chat_generator_create(const char *engine_label,
                                                        bool configured);
void ca_fake_chat_generator_destroy(ca_fake_chat_generator_t *g);
/* Script the next N generate/stream calls to fail hard. */
void ca_fake_chat_generator_set_fail_times(ca_fake_chat_generator_t *g, int fail_times);
/* Override the canned reply (default: "<label>: <last user>"). */
void ca_fake_chat_generator_set_reply(ca_fake_chat_generator_t *g, const char *reply);
/* The generator seam view (borrowed). NOTE: the view's destroy is NULL — call
 * ca_fake_chat_generator_destroy yourself unless you hand ownership to a chain
 * built with *_own. */
ca_chat_gen_iface_t ca_fake_chat_generator_as_iface(ca_fake_chat_generator_t *g);

/* ===========================================================================
 * CloudFallbackChain
 * =========================================================================== */

typedef struct ca_cloud_fallback_chain ca_cloud_fallback_chain_t;

/* Build a chain over an ordered array of generator ifaces (copied by value; the
 * chain does NOT own the underlying generators unless you pass own=true, in
 * which case it calls each iface's destroy on chain destroy). Order matters. */
ca_cloud_fallback_chain_t *ca_cloud_fallback_chain_create(
    const ca_chat_gen_iface_t *generators, size_t count, bool own);
void ca_cloud_fallback_chain_destroy(ca_cloud_fallback_chain_t *c);
size_t ca_cloud_fallback_chain_count(const ca_cloud_fallback_chain_t *c);

/* GenerateAsync — first ready generator wins; skips unconfigured + throwing.
 * Returns malloc'd text (never NULL — a no-generator case returns the sentinel
 * "[CloudFallbackChain: no configured generator could serve the request]"). */
char *ca_cloud_fallback_chain_generate(ca_cloud_fallback_chain_t *c,
                                       const ca_chat_msg_t *messages, size_t count,
                                       const ca_generation_options_t *opts);

/* StreamAsync — first ready generator whose first frame isn't a fail-soft
 * frame; on any generator faulting before yielding, moves on. Drives on_chunk
 * per real frame; returns frames yielded (>=0). When none serve, yields the
 * sentinel once and returns 1. */
long ca_cloud_fallback_chain_stream(ca_cloud_fallback_chain_t *c,
                                    const ca_chat_msg_t *messages, size_t count,
                                    const ca_generation_options_t *opts,
                                    ca_gen_chunk_fn on_chunk, void *chunk_user);

/* ===========================================================================
 * BackupBrainOrchestrator
 * =========================================================================== */

typedef enum {
    CA_BRAIN_HEALTHY      = 0,
    CA_BRAIN_DEGRADED     = 1,
    CA_BRAIN_COOLING_DOWN = 2
} ca_brain_health_t;

typedef struct {
    char             *label;    /* owned */
    ca_brain_health_t health;
    int               consecutive_failures;
} ca_brain_status_t;

void ca_brain_status_free(ca_brain_status_t *s);
void ca_brain_status_free_array(ca_brain_status_t *arr, size_t count);

typedef struct {
    int     degraded_after_failures;   /* default 2 */
    int64_t cool_down_ms;              /* default 30 s */
    int     max_retries_per_turn;      /* default 3 */
} ca_backup_brain_policy_t;

void ca_backup_brain_policy_init(ca_backup_brain_policy_t *p);

typedef struct ca_backup_brain_orchestrator ca_backup_brain_orchestrator_t;

/* At least one brain required. policy NULL => defaults. The orchestrator uses a
 * host-driven clock: pass the current time to generate/stream (mirrors the
 * injected Func<DateTimeOffset>). Generators are copied by value (own=true makes
 * the orchestrator destroy them). */
ca_backup_brain_orchestrator_t *ca_backup_brain_orchestrator_create(
    const ca_chat_gen_iface_t *brains, size_t count,
    const ca_backup_brain_policy_t *policy, bool own);
void ca_backup_brain_orchestrator_destroy(ca_backup_brain_orchestrator_t *o);

/* GenerateAsync at now_ms. Returns malloc'd text; "[All brains failed.]" when
 * every attempt fails. */
char *ca_backup_brain_orchestrator_generate(ca_backup_brain_orchestrator_t *o,
                                            const ca_chat_msg_t *messages, size_t count,
                                            const ca_generation_options_t *opts,
                                            int64_t now_ms);

/* Statuses snapshot at now_ms. Fresh array (caller frees). */
ca_brain_status_t *ca_backup_brain_orchestrator_statuses(
    ca_backup_brain_orchestrator_t *o, int64_t now_ms, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_CLOUD_H */
