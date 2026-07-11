#ifndef CIRCLE_AI_DOMAIN_H
#define CIRCLE_AI_DOMAIN_H

/*
 * domain.h — CircleAI.Domain (C11 port of Contracts.cs + InMemoryDomain.cs +
 * NullImplementations.cs). The domain-specialist plug points.
 *
 *   Food      : Ingredient(Name, Canonical?, Quantity?);
 *               IFoodEmbeddings -> InMemoryFoodEmbeddings. RegisterEmbedding
 *               (name, vec); RegisterSubstitute(name, alt); Embed(i) -> the
 *               registered vector or a deterministic hash-based 8-dim vector;
 *               Substitutes(i, topK=5) -> registered alternatives (Name-keyed,
 *               OrdinalIgnoreCase), take topK. BackendId "in-memory".
 *   Finance   : FinanceSnippet(Text, Source, float Score);
 *               IFinanceRetrieval -> InMemoryFinanceRetrieval. Add(s); Retrieve
 *               (query, topK=5) where Text Contains query (OrdinalIgnoreCase),
 *               order by Score desc, take topK. BackendId "in-memory".
 *               FinanceFinding(Subject, Summary, Citations[]);
 *               IFinancialAgent -> MultiPassFinancialAgent over a retrieval seam.
 *               BackendId "multi-pass".
 *   Slides    : SlideOutline(Title, Body, Bullets?);
 *               GeneratedPresentation(Slides[], Theme, Format);
 *               IPresentationGenerator -> TemplatePresentationGenerator. Generate
 *               (topic, targetSlideCount=10, theme?) — title + N-2 parts +
 *               conclusion, Format "markdown". BackendId "template".
 *   Jobs      : JobApplicationDraft(ResumeText, CoverLetterText, KeyMatches[]);
 *               IJobSearchPipeline -> TemplateJobSearchPipeline. DraftApplication
 *               (role, profile) — keyword intersection. BackendId "template".
 *   Memory    : MemoryItem(Id, Text, Metadata?); MemoryHit(Item, float Score);
 *               IMemPalaceStore -> InMemoryMemPalaceStore. Upsert(item) (Id
 *               required); Recall(query, topK=5) score 1/(1+firstIndex) via
 *               OrdinalIgnoreCase IndexOf, keep score>0, order desc, take topK.
 *               IHippoRagStore -> InMemoryHippoRagStore. Index(item); MultiHop
 *               Recall(query, topK=5) two-hop over the base store. Both "in-memory".
 *   Swarm     : SwarmPeer(PeerId, Capability, float Health);
 *               ISwarmCoordinator -> InMemorySwarmCoordinator. Register(p);
 *               ListPeers(); ChooseDelegate(capability) — highest Health among
 *               matching Capability (OrdinalIgnoreCase), capability required.
 *               BackendId "in-memory".
 *   LoRA      : LoRATrainingSummary(AdapterId, StepsTrained, float FinalLoss);
 *               IPersonalLoRA -> InMemoryPersonalLoRA. Train(adapterId, samples)
 *               — simulated loss curve, records state; Load/Unload track a loaded
 *               set (Load requires a trained adapter). BackendId "in-memory".
 *   Null variants for every contract.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc (+ libm for the LoRA loss curve).
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* key/value pair (MemoryItem.Metadata). */
typedef struct { char *key; char *value; } ca_domain_kv_t;

/* ═══ Food ═══════════════════════════════════════════════════════════════ */

/* Ingredient(Name, Canonical?, Quantity?). */
typedef struct {
    char *name;      /* owned, non-null */
    char *canonical; /* owned, or NULL */
    char *quantity;  /* owned, or NULL */
} ca_ingredient_t;

void ca_ingredient_free(ca_ingredient_t *i);
void ca_ingredient_free_array(ca_ingredient_t *arr, size_t count);

typedef struct ca_food_embeddings ca_food_embeddings_t;

ca_food_embeddings_t *ca_food_embeddings_create(void); /* NULL on OOM */
void ca_food_embeddings_destroy(ca_food_embeddings_t *f);
const char *ca_food_embeddings_backend_id(const ca_food_embeddings_t *f);

/* RegisterEmbedding(name, vec, len) — keyed by name (OrdinalIgnoreCase). 0/-1. */
int ca_food_embeddings_register_embedding(ca_food_embeddings_t *f, const char *name,
                                          const float *vec, size_t len);
/* RegisterSubstitute(name, alt) — appends under name. 0 / -1. */
int ca_food_embeddings_register_substitute(ca_food_embeddings_t *f, const char *name,
                                           const ca_ingredient_t *alt);
/* Embed(i) -> fresh float vector (*out_len), or NULL on OOM. When no embedding is
 * registered for i.Name, a deterministic 8-dim hash vector is returned. */
float *ca_food_embeddings_embed(const ca_food_embeddings_t *f,
                                const ca_ingredient_t *ingredient, size_t *out_len);
/* Substitutes(i, topK) -> fresh Ingredient array, take topK. NULL + 0 empty;
 * NULL + SIZE_MAX on error (top_k > 0). */
ca_ingredient_t *ca_food_embeddings_substitutes(const ca_food_embeddings_t *f,
                                                const ca_ingredient_t *ingredient,
                                                int top_k, size_t *out_count);

const char *ca_domain_null_food_embeddings_backend_id(void); /* "null" */

/* ═══ Finance ════════════════════════════════════════════════════════════ */

/* FinanceSnippet(Text, Source, Score). */
typedef struct {
    char *text;   /* owned, non-null */
    char *source; /* owned, non-null */
    float score;
} ca_finance_snippet_t;

void ca_finance_snippet_free(ca_finance_snippet_t *s);
void ca_finance_snippet_free_array(ca_finance_snippet_t *arr, size_t count);

typedef struct ca_finance_retrieval ca_finance_retrieval_t;

ca_finance_retrieval_t *ca_finance_retrieval_create(void); /* NULL on OOM */
void ca_finance_retrieval_destroy(ca_finance_retrieval_t *r);
const char *ca_finance_retrieval_backend_id(const ca_finance_retrieval_t *r);

/* Add(snippet). 0 / -1. */
int ca_finance_retrieval_add(ca_finance_retrieval_t *r, const ca_finance_snippet_t *s);
/* Retrieve(query, topK) where Text Contains query, order by Score desc, take
 * topK. NULL + 0 empty; NULL + SIZE_MAX on error (query non-null, top_k > 0). */
ca_finance_snippet_t *ca_finance_retrieval_retrieve(const ca_finance_retrieval_t *r,
                                                    const char *query, int top_k,
                                                    size_t *out_count);

const char *ca_domain_null_finance_retrieval_backend_id(void); /* "null" */

/* FinanceFinding(Subject, Summary, Citations[]). */
typedef struct {
    char  *subject;    /* owned, non-null */
    char  *summary;    /* owned, non-null */
    char **citations;  /* owned; NULL when citation_count == 0 */
    size_t citation_count;
} ca_finance_finding_t;

void ca_finance_finding_free(ca_finance_finding_t *f);
void ca_finance_finding_free_array(ca_finance_finding_t *arr, size_t count);

/* MultiPassFinancialAgent.Research(question) over a retrieval store. Decomposes
 * the question, groups snippets by source, summarises each cluster. NULL + 0
 * empty; NULL + SIZE_MAX on error (question non-null). BackendId "multi-pass". */
ca_finance_finding_t *ca_financial_agent_research(const ca_finance_retrieval_t *retr,
                                                  const char *question,
                                                  size_t *out_count);
const char *ca_financial_agent_backend_id(void); /* "multi-pass" */
const char *ca_domain_null_financial_agent_backend_id(void); /* "null" */

/* ═══ Presentations ══════════════════════════════════════════════════════ */

/* SlideOutline(Title, Body, Bullets?). */
typedef struct {
    char  *title;   /* owned, non-null */
    char  *body;    /* owned, non-null */
    char **bullets; /* owned; NULL when bullet_count == 0 */
    size_t bullet_count;
} ca_slide_outline_t;

/* GeneratedPresentation(Slides[], Theme, Format). */
typedef struct {
    ca_slide_outline_t *slides; /* owned; NULL when slide_count == 0 */
    size_t              slide_count;
    char               *theme;  /* owned, non-null */
    char               *format; /* owned, non-null */
} ca_generated_presentation_t;

void ca_generated_presentation_free(ca_generated_presentation_t *p);

/* Generate(topic, targetSlideCount, theme?) -> fresh presentation into *out,
 * true; false on bad args (topic required, targetSlideCount > 0). theme defaults
 * to "default". BackendId "template". */
bool ca_presentation_generate(const char *topic, int target_slide_count,
                              const char *theme, ca_generated_presentation_t *out);
const char *ca_presentation_generator_backend_id(void); /* "template" */
/* Null: empty slides, theme (or "default"), format "json". */
bool ca_domain_null_presentation_generate(const char *topic, int target_slide_count,
                                          const char *theme,
                                          ca_generated_presentation_t *out);
const char *ca_domain_null_presentation_generator_backend_id(void); /* "null" */

/* ═══ Job search ═════════════════════════════════════════════════════════ */

/* JobApplicationDraft(ResumeText, CoverLetterText, KeyMatches[]). */
typedef struct {
    char  *resume_text;      /* owned, non-null */
    char  *cover_letter_text;/* owned, non-null */
    char **key_matches;      /* owned; NULL when key_match_count == 0 */
    size_t key_match_count;
} ca_job_application_draft_t;

void ca_job_application_draft_free(ca_job_application_draft_t *d);

/* DraftApplication(role, profile) -> fresh draft into *out, true; false on bad
 * args (role/profile non-null). BackendId "template". */
bool ca_job_search_draft(const char *role_description,
                         const char *candidate_profile_text,
                         ca_job_application_draft_t *out);
const char *ca_job_search_pipeline_backend_id(void); /* "template" */
/* Null: empty resume/cover/matches. */
bool ca_domain_null_job_search_draft(const char *role, const char *profile,
                                     ca_job_application_draft_t *out);
const char *ca_domain_null_job_search_pipeline_backend_id(void); /* "null" */

/* ═══ Memory upgrades ════════════════════════════════════════════════════ */

/* MemoryItem(Id, Text, Metadata?). */
typedef struct {
    char           *id;   /* owned, non-null */
    char           *text; /* owned, non-null */
    ca_domain_kv_t *metadata; /* owned; NULL when metadata_count == 0 */
    size_t          metadata_count;
} ca_domain_memory_item_t;

void ca_domain_memory_item_free(ca_domain_memory_item_t *i);

/* MemoryHit(Item, Score). */
typedef struct {
    ca_domain_memory_item_t item; /* owned */
    float                   score;
} ca_domain_memory_hit_t;

void ca_domain_memory_hit_free(ca_domain_memory_hit_t *h);
void ca_domain_memory_hit_free_array(ca_domain_memory_hit_t *arr, size_t count);

typedef struct ca_mempalace_store ca_mempalace_store_t;

ca_mempalace_store_t *ca_mempalace_store_create(void); /* NULL on OOM */
void ca_mempalace_store_destroy(ca_mempalace_store_t *s);
const char *ca_mempalace_store_backend_id(const ca_mempalace_store_t *s);

/* Upsert(item) — keyed by Id (Id required). 0 / -1. */
int ca_mempalace_store_upsert(ca_mempalace_store_t *s,
                              const ca_domain_memory_item_t *item);
/* Recall(query, topK) — score 1/(1+firstIndex) via OrdinalIgnoreCase IndexOf,
 * keep score>0, order desc, take topK. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_domain_memory_hit_t *ca_mempalace_store_recall(const ca_mempalace_store_t *s,
                                                  const char *query, int top_k,
                                                  size_t *out_count);

const char *ca_domain_null_mempalace_store_backend_id(void); /* "null" */

typedef struct ca_hipporag_store ca_hipporag_store_t;

ca_hipporag_store_t *ca_hipporag_store_create(void); /* NULL on OOM */
void ca_hipporag_store_destroy(ca_hipporag_store_t *s);
const char *ca_hipporag_store_backend_id(const ca_hipporag_store_t *s);

/* Index(item) — same as MemPalace Upsert. 0 / -1. */
int ca_hipporag_store_index(ca_hipporag_store_t *s,
                            const ca_domain_memory_item_t *item);
/* MultiHopRecall(query, topK) — first hop, then expand with the top hit's text,
 * union by Id, order desc, take topK. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_domain_memory_hit_t *ca_hipporag_store_multihop_recall(const ca_hipporag_store_t *s,
                                                          const char *query, int top_k,
                                                          size_t *out_count);

const char *ca_domain_null_hipporag_store_backend_id(void); /* "null" */

/* ═══ Swarm ══════════════════════════════════════════════════════════════ */

/* SwarmPeer(PeerId, Capability, Health). */
typedef struct {
    char *peer_id;    /* owned, non-null */
    char *capability; /* owned, non-null */
    float health;
} ca_swarm_peer_t;

void ca_swarm_peer_free(ca_swarm_peer_t *p);
void ca_swarm_peer_free_array(ca_swarm_peer_t *arr, size_t count);

typedef struct ca_swarm_coordinator ca_swarm_coordinator_t;

ca_swarm_coordinator_t *ca_swarm_coordinator_create(void); /* NULL on OOM */
void ca_swarm_coordinator_destroy(ca_swarm_coordinator_t *c);
const char *ca_swarm_coordinator_backend_id(const ca_swarm_coordinator_t *c);

/* Register(peer) — keyed by PeerId (replace). 0 / -1. */
int ca_swarm_coordinator_register(ca_swarm_coordinator_t *c,
                                  const ca_swarm_peer_t *peer);
/* ListPeers() insertion order. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_swarm_peer_t *ca_swarm_coordinator_list_peers(const ca_swarm_coordinator_t *c,
                                                 size_t *out_count);
/* ChooseDelegate(capability) -> fresh PeerId (highest Health among matching
 * Capability, OrdinalIgnoreCase), or NULL when none / on bad args (capability
 * required — returns NULL). */
char *ca_swarm_coordinator_choose_delegate(const ca_swarm_coordinator_t *c,
                                           const char *capability);

const char *ca_domain_null_swarm_coordinator_backend_id(void); /* "null" */

/* ═══ Personal LoRA ══════════════════════════════════════════════════════ */

/* LoRATrainingSummary(AdapterId, StepsTrained, FinalLoss). */
typedef struct {
    char *adapter_id;   /* owned, non-null */
    int   steps_trained;
    float final_loss;
} ca_lora_training_summary_t;

void ca_lora_training_summary_free(ca_lora_training_summary_t *s);

typedef struct ca_personal_lora ca_personal_lora_t;

ca_personal_lora_t *ca_personal_lora_create(void); /* NULL on OOM */
void ca_personal_lora_destroy(ca_personal_lora_t *l);
const char *ca_personal_lora_backend_id(const ca_personal_lora_t *l);

/* Train(adapterId, samples[]) -> fresh summary into *out, true; false on bad args
 * (adapterId required, at least one sample). Records adapter state. */
bool ca_personal_lora_train(ca_personal_lora_t *l, const char *adapter_id,
                            const char *const *samples, size_t sample_count,
                            ca_lora_training_summary_t *out);
/* LoadAdapter(adapterId) — 0 on success; -1 on bad args or an untrained adapter. */
int ca_personal_lora_load(ca_personal_lora_t *l, const char *adapter_id);
/* UnloadAdapter(adapterId) — 0; -1 on bad args. */
int ca_personal_lora_unload(ca_personal_lora_t *l, const char *adapter_id);
/* IsLoaded(adapterId). */
bool ca_personal_lora_is_loaded(const ca_personal_lora_t *l, const char *adapter_id);

const char *ca_domain_null_personal_lora_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DOMAIN_H */
