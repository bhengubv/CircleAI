/*
 * companion_brain.c — CircleAI companion-brain (C11 port).
 *
 * Belief attribution + revision, the background memory encoder (drain-on-close,
 * no threads), and the concrete companion session. Ported from the C# reference
 * and mirroring the Swift/Rust/Go/TS ports 1:1. Pure C11 + libc, links -lm.
 */

#include "circle_ai/companion_brain.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <time.h>

/* ===========================================================================
 * Local helpers (kept file-local; memory_brain.c has its own copies)
 * =========================================================================== */

static char *cb_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static int64_t cb_now_ms(void) { return (int64_t)time(NULL) * 1000; }

static bool cb_is_blank(const char *s) {
    if (!s) return true;
    for (; *s; ++s) if (!isspace((unsigned char)*s)) return false;
    return true;
}

static void cb_lower_inplace(char *s) {
    for (; s && *s; ++s) *s = (char)tolower((unsigned char)*s);
}

static bool cb_eq_ci(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

/* Case-insensitive substring search (ASCII). Returns true if needle in haystack. */
static bool cb_contains_ci(const char *haystack, const char *needle) {
    if (!haystack || !needle) return false;
    size_t hn = strlen(haystack), nn = strlen(needle);
    if (nn == 0) return true;
    if (nn > hn) return false;
    for (size_t i = 0; i + nn <= hn; ++i) {
        size_t j = 0;
        while (j < nn && tolower((unsigned char)haystack[i + j]) == tolower((unsigned char)needle[j])) ++j;
        if (j == nn) return true;
    }
    return false;
}

static bool cb_in_set(char c, const char *set) { return strchr(set, c) != NULL; }

/* ===========================================================================
 * Belief structs
 * =========================================================================== */

void ca_personal_belief_free(ca_personal_belief_t *b) {
    if (!b) return;
    free(b->subject);
    free(b->predicate);
    free(b->object);
    free(b->source);
    memset(b, 0, sizeof(*b));
}

void ca_personal_belief_free_array(ca_personal_belief_t *beliefs, size_t count) {
    if (!beliefs) return;
    for (size_t i = 0; i < count; ++i) ca_personal_belief_free(&beliefs[i]);
    free(beliefs);
}

static void cb_belief_copy(ca_personal_belief_t *dst, const ca_personal_belief_t *src) {
    dst->attribution = src->attribution;
    dst->subject = cb_dup(src->subject);
    dst->predicate = cb_dup(src->predicate);
    dst->object = cb_dup(src->object);
    dst->confidence = src->confidence;
    dst->source = cb_dup(src->source);
    dst->recorded_at_ms = src->recorded_at_ms;
}

/* ===========================================================================
 * Heuristic belief extractor
 * =========================================================================== */

/* Separator set for the belief extractor: NO apostrophe (mirrors the reference,
 * so "i'm" stays one token). */
static const char *CB_SEP = " \t\n\r.,?!;:\"()";

static const char *const CB_RELATIONS[] = {
    "mother","father","mom","mum","dad","sister","brother","wife","husband","son","daughter",
    "aunt","uncle","grandmother","grandfather","granny","grandpa","gran","nan","friend",
    "colleague","boss","neighbour","neighbor","cousin","partner","girlfriend","boyfriend",
};
static const size_t CB_RELATIONS_N = sizeof(CB_RELATIONS) / sizeof(CB_RELATIONS[0]);

static const char *const CB_POSSESSIVE[] = { "my","her","his","their","our" };
static const size_t CB_POSSESSIVE_N = sizeof(CB_POSSESSIVE) / sizeof(CB_POSSESSIVE[0]);

static const char *const CB_STOP[] = {
    "the","a","an","is","are","was","were","be","been","am","to","of","in","on","at","and","or",
    "but","with","has","have","had","that","this","it","as","for","really","very","just","now",
};
static const size_t CB_STOP_N = sizeof(CB_STOP) / sizeof(CB_STOP[0]);

static bool cb_in_list(const char *w, const char *const *list, size_t n) {
    for (size_t i = 0; i < n; ++i) if (strcmp(w, list[i]) == 0) return true;
    return false;
}

/* Tokenise lowercased text on CB_SEP into an owned array (no dedup, keeps order). */
static void cb_tokenise(const char *text, char ***out, size_t *out_count) {
    char **toks = NULL; size_t count = 0, cap = 0;
    char *buf = cb_dup(text ? text : "");
    if (!buf) { *out = NULL; *out_count = 0; return; }
    cb_lower_inplace(buf);
    size_t len = strlen(buf), i = 0;
    while (i < len) {
        while (i < len && cb_in_set(buf[i], CB_SEP)) ++i;
        size_t start = i;
        while (i < len && !cb_in_set(buf[i], CB_SEP)) ++i;
        if (i > start) {
            size_t wlen = i - start;
            char *w = (char *)malloc(wlen + 1);
            if (!w) continue;
            memcpy(w, buf + start, wlen);
            w[wlen] = '\0';
            if (count == cap) {
                size_t nc = cap ? cap * 2 : 8;
                char **n = (char **)realloc(toks, nc * sizeof(char *));
                if (!n) { free(w); break; }
                toks = n; cap = nc;
            }
            toks[count++] = w;
        }
    }
    free(buf);
    *out = toks;
    *out_count = count;
}

ca_personal_belief_t *ca_belief_extract(const char *text, const char *source,
                                        size_t *out_count) {
    if (out_count) *out_count = 0;
    if (cb_is_blank(text)) return NULL;

    char **tokens = NULL; size_t tn = 0;
    cb_tokenise(text, &tokens, &tn);
    if (tn == 0) { free(tokens); return NULL; }

    ca_attribution_t attribution;
    const char *subject;
    /* skip flags for token indices consumed as subject / possessive. */
    unsigned char *skip = (unsigned char *)calloc(tn, 1);
    if (!skip) { for (size_t i = 0; i < tn; ++i) free(tokens[i]); free(tokens); return NULL; }

    if (tn >= 2 && cb_in_list(tokens[0], CB_POSSESSIVE, CB_POSSESSIVE_N) &&
        cb_in_list(tokens[1], CB_RELATIONS, CB_RELATIONS_N)) {
        /* "my mother ..." → someone else */
        attribution = CA_ATTRIBUTION_OTHER;
        subject = tokens[1];
        skip[0] = 1; skip[1] = 1;
    } else if (cb_in_list(tokens[0], CB_RELATIONS, CB_RELATIONS_N)) {
        attribution = CA_ATTRIBUTION_OTHER;
        subject = tokens[0];
        skip[0] = 1;
    } else if (strcmp(tokens[0], "i") == 0 || strcmp(tokens[0], "i'm") == 0 ||
               strcmp(tokens[0], "im") == 0 || strcmp(tokens[0], "me") == 0 ||
               strcmp(tokens[0], "my") == 0) {
        /* "I ..." or "my <non-relation> ..." → the user */
        attribution = CA_ATTRIBUTION_SELF;
        subject = "user";
        skip[0] = 1;
    } else {
        attribution = CA_ATTRIBUTION_WORLD;
        subject = tokens[0];
    }

    /* Object tokens: not skipped, >=3 chars, not stop, not a relation. Joined. */
    size_t obj_cap = 1; /* for NUL */
    for (size_t i = 0; i < tn; ++i) {
        if (skip[i]) continue;
        if (strlen(tokens[i]) < 3) continue;
        if (cb_in_list(tokens[i], CB_STOP, CB_STOP_N)) continue;
        if (cb_in_list(tokens[i], CB_RELATIONS, CB_RELATIONS_N)) continue;
        obj_cap += strlen(tokens[i]) + 1;
    }
    char *obj = (char *)malloc(obj_cap);
    if (!obj) { free(skip); for (size_t i = 0; i < tn; ++i) free(tokens[i]); free(tokens); return NULL; }
    obj[0] = '\0';
    size_t w = 0;
    bool first = true;
    for (size_t i = 0; i < tn; ++i) {
        if (skip[i]) continue;
        if (strlen(tokens[i]) < 3) continue;
        if (cb_in_list(tokens[i], CB_STOP, CB_STOP_N)) continue;
        if (cb_in_list(tokens[i], CB_RELATIONS, CB_RELATIONS_N)) continue;
        if (!first) obj[w++] = ' ';
        size_t l = strlen(tokens[i]);
        memcpy(obj + w, tokens[i], l);
        w += l;
        first = false;
    }
    obj[w] = '\0';

    ca_personal_belief_t *result = NULL;
    if (!cb_is_blank(obj)) {
        result = (ca_personal_belief_t *)calloc(1, sizeof(*result));
        if (result) {
            result->attribution = attribution;
            result->subject = cb_dup(subject);
            result->predicate = cb_dup("isAbout");
            result->object = cb_dup(obj);
            result->confidence = 0.6;
            result->source = cb_dup(source);
            result->recorded_at_ms = cb_now_ms();
            if (out_count) *out_count = 1;
        }
    }

    free(obj);
    free(skip);
    for (size_t i = 0; i < tn; ++i) free(tokens[i]);
    free(tokens);
    return result;
}

ca_personal_belief_t *ca_belief_extractor_heuristic_adapter(void *user, const char *text,
                                                            const char *source,
                                                            size_t *out_count) {
    (void)user;
    return ca_belief_extract(text, source, out_count);
}

/* ===========================================================================
 * SelfBeliefStore
 * =========================================================================== */

struct ca_self_belief_store {
    ca_personal_belief_t *self_facts;
    size_t                self_count, self_cap;
    ca_personal_belief_t *audit;       /* other/world */
    size_t                audit_count, audit_cap;
};

ca_self_belief_store_t *ca_self_belief_store_create(void) {
    return (ca_self_belief_store_t *)calloc(1, sizeof(struct ca_self_belief_store));
}

void ca_self_belief_store_destroy(ca_self_belief_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->self_count; ++i) ca_personal_belief_free(&store->self_facts[i]);
    free(store->self_facts);
    for (size_t i = 0; i < store->audit_count; ++i) ca_personal_belief_free(&store->audit[i]);
    free(store->audit);
    free(store);
}

static void cb_push_belief(ca_personal_belief_t **arr, size_t *count, size_t *cap,
                           const ca_personal_belief_t *b) {
    if (*count == *cap) {
        size_t nc = *cap ? *cap * 2 : 8;
        ca_personal_belief_t *n = (ca_personal_belief_t *)realloc(*arr, nc * sizeof(*n));
        if (!n) return;
        *arr = n; *cap = nc;
    }
    cb_belief_copy(&(*arr)[*count], b);
    (*count)++;
}

void ca_self_belief_store_record(ca_self_belief_store_t *store,
                                 const ca_personal_belief_t *belief) {
    if (!store || !belief) return;
    if (belief->attribution != CA_ATTRIBUTION_SELF) {
        cb_push_belief(&store->audit, &store->audit_count, &store->audit_cap, belief);
        return;
    }
    /* Supersede an existing self-belief on the same (subject, predicate). */
    size_t w = 0;
    for (size_t i = 0; i < store->self_count; ++i) {
        if (cb_eq_ci(store->self_facts[i].subject, belief->subject) &&
            cb_eq_ci(store->self_facts[i].predicate, belief->predicate)) {
            ca_personal_belief_free(&store->self_facts[i]);
        } else {
            if (w != i) store->self_facts[w] = store->self_facts[i];
            w++;
        }
    }
    store->self_count = w;
    cb_push_belief(&store->self_facts, &store->self_count, &store->self_cap, belief);
}

static ca_personal_belief_t *cb_copy_belief_array(const ca_personal_belief_t *src,
                                                  size_t count, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (count == 0) return NULL;
    ca_personal_belief_t *arr = (ca_personal_belief_t *)calloc(count, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < count; ++i) cb_belief_copy(&arr[i], &src[i]);
    if (out_count) *out_count = count;
    return arr;
}

ca_personal_belief_t *ca_self_belief_store_self_facts(const ca_self_belief_store_t *store,
                                                      size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store) return NULL;
    return cb_copy_belief_array(store->self_facts, store->self_count, out_count);
}

ca_personal_belief_t *ca_self_belief_store_non_self(const ca_self_belief_store_t *store,
                                                    size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store) return NULL;
    return cb_copy_belief_array(store->audit, store->audit_count, out_count);
}

size_t ca_self_belief_store_retract(ca_self_belief_store_t *store, const char *object_substr) {
    if (!store || cb_is_blank(object_substr)) return 0;
    /* Trim the needle (leading/trailing ws) to mirror the reference. */
    const char *start = object_substr;
    while (*start && isspace((unsigned char)*start)) ++start;
    const char *end = object_substr + strlen(object_substr);
    while (end > start && isspace((unsigned char)*(end - 1))) --end;
    size_t nlen = (size_t)(end - start);
    char *needle = (char *)malloc(nlen + 1);
    if (!needle) return 0;
    memcpy(needle, start, nlen);
    needle[nlen] = '\0';

    size_t before = store->self_count;
    size_t w = 0;
    for (size_t i = 0; i < store->self_count; ++i) {
        if (cb_contains_ci(store->self_facts[i].object, needle)) {
            ca_personal_belief_free(&store->self_facts[i]);
        } else {
            if (w != i) store->self_facts[w] = store->self_facts[i];
            w++;
        }
    }
    store->self_count = w;
    free(needle);
    return before - w;
}

void ca_string_array_free(char **arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i]);
    free(arr);
}

char **ca_self_belief_store_provenance(const ca_self_belief_store_t *store, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->self_count == 0) return NULL;
    char **out = (char **)calloc(store->self_count, sizeof(char *));
    if (!out) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < store->self_count; ++i) {
        const char *s = store->self_facts[i].source;
        if (!s) continue;
        bool seen = false;
        for (size_t j = 0; j < n; ++j) { if (strcmp(out[j], s) == 0) { seen = true; break; } }
        if (!seen) out[n++] = cb_dup(s);
    }
    if (n == 0) { free(out); return NULL; }
    if (out_count) *out_count = n;
    return out;
}

/* ===========================================================================
 * Companion memory encoder — drain-on-close (no threads)
 * =========================================================================== */

typedef struct {
    char *user_text;
    char *assistant_text;
    char *episode_id;
} cb_encode_job_t;

struct ca_memory_encoder {
    ca_kg_extractor_fn      extractor_fn;
    void                   *extractor_user;
    ca_knowledge_graph_t   *graph;         /* borrowed */
    ca_belief_extractor_fn  belief_fn;
    void                   *belief_user;
    ca_self_belief_store_t *beliefs;       /* borrowed, or NULL */
    size_t                  capacity;

    cb_encode_job_t        *queue;
    size_t                  queue_count;
    bool                    closed;
    char                   *last_error;    /* owned, or NULL */
};

ca_memory_encoder_t *ca_memory_encoder_create(ca_kg_extractor_fn extractor_fn,
                                              void *extractor_user,
                                              ca_knowledge_graph_t *graph,
                                              ca_belief_extractor_fn belief_fn,
                                              void *belief_user,
                                              ca_self_belief_store_t *beliefs,
                                              size_t capacity) {
    if (!graph || !extractor_fn) return NULL;
    ca_memory_encoder_t *enc = (ca_memory_encoder_t *)calloc(1, sizeof(*enc));
    if (!enc) return NULL;
    enc->extractor_fn = extractor_fn;
    enc->extractor_user = extractor_user;
    enc->graph = graph;
    enc->belief_fn = belief_fn;
    enc->belief_user = belief_user;
    enc->beliefs = beliefs;
    enc->capacity = capacity ? capacity : 256;
    enc->queue = (cb_encode_job_t *)calloc(enc->capacity, sizeof(cb_encode_job_t));
    if (!enc->queue) { free(enc); return NULL; }
    return enc;
}

static void cb_job_free(cb_encode_job_t *job) {
    free(job->user_text);
    free(job->assistant_text);
    free(job->episode_id);
    memset(job, 0, sizeof(*job));
}

void ca_memory_encoder_destroy(ca_memory_encoder_t *enc) {
    if (!enc) return;
    for (size_t i = 0; i < enc->queue_count; ++i) cb_job_free(&enc->queue[i]);
    free(enc->queue);
    free(enc->last_error);
    free(enc);
}

void ca_memory_encoder_enqueue(ca_memory_encoder_t *enc, const char *user_text,
                               const char *assistant_text, const char *episode_id) {
    if (!enc) return;
    if (cb_is_blank(episode_id)) return;        /* blank id ignored */
    if (enc->closed) return;                    /* no work after close */
    if (enc->queue_count >= enc->capacity) return; /* DropWrite: never block */
    cb_encode_job_t *job = &enc->queue[enc->queue_count];
    job->user_text = cb_dup(user_text ? user_text : "");
    job->assistant_text = cb_dup(assistant_text ? assistant_text : "");
    job->episode_id = cb_dup(episode_id);
    enc->queue_count++;
}

static void cb_capture_error(ca_memory_encoder_t *enc, const char *msg) {
    if (!enc->last_error && msg) enc->last_error = cb_dup(msg);
}

static void cb_encode_one(ca_memory_encoder_t *enc, cb_encode_job_t *job) {
    /* Give the memory node a readable name so recall hands back the exchange. */
    if (!ca_knowledge_graph_upsert_node(enc->graph, job->episode_id, "memory",
                                        job->user_text, NULL, NULL, 0)) {
        cb_capture_error(enc, "upsert_node failed");
        return;
    }

    size_t tn = 0;
    ca_knowledge_triple_t *triples = enc->extractor_fn(enc->extractor_user, job->user_text,
                                                       job->assistant_text, job->episode_id, &tn);
    if (tn == SIZE_MAX) {
        cb_capture_error(enc, "boom"); /* extractor error sentinel */
        return;
    }
    for (size_t i = 0; i < tn; ++i) {
        if (!ca_knowledge_graph_add_triple(enc->graph, triples[i].subject, triples[i].predicate,
                                           triples[i].object, triples[i].source,
                                           triples[i].confidence)) {
            cb_capture_error(enc, "add_triple failed");
            ca_knowledge_triple_free_array(triples, tn);
            return;
        }
    }
    ca_knowledge_triple_free_array(triples, tn);

    /* Attributed beliefs — a third party's fact never becomes the user's. */
    if (enc->belief_fn && enc->beliefs) {
        size_t bn = 0;
        ca_personal_belief_t *bs = enc->belief_fn(enc->belief_user, job->user_text,
                                                  job->episode_id, &bn);
        if (bn == SIZE_MAX) { cb_capture_error(enc, "belief extract failed"); return; }
        for (size_t i = 0; i < bn; ++i) ca_self_belief_store_record(enc->beliefs, &bs[i]);
        ca_personal_belief_free_array(bs, bn);
    }
}

void ca_memory_encoder_close(ca_memory_encoder_t *enc) {
    if (!enc || enc->closed) return;
    enc->closed = true;
    for (size_t i = 0; i < enc->queue_count; ++i) {
        cb_encode_one(enc, &enc->queue[i]);
        cb_job_free(&enc->queue[i]);
    }
    enc->queue_count = 0;
}

const char *ca_memory_encoder_last_error(const ca_memory_encoder_t *enc) {
    return enc ? enc->last_error : NULL;
}

/* ===========================================================================
 * Companion session
 * =========================================================================== */

typedef struct { char *role; char *content; } cb_turn_t;

struct ca_companion_session {
    ca_generate_fn          generator;
    void                   *generator_user;
    ca_episodic_store_t    *episodic;   /* borrowed */
    ca_fused_recall_t      *recall;     /* borrowed */
    ca_memory_encoder_t    *encoder;    /* borrowed, or NULL */
    ca_self_belief_store_t *beliefs;    /* borrowed, or NULL */

    char *session_id, *identity_id, *interface_kind;
    char *persona_hints, *affect_summary, *app_context;
    int   recall_top_k;

    cb_turn_t *history;
    size_t     history_count, history_cap;

    char     **snippets;    /* recalled on the last turn (owned) */
    size_t     snippet_count;
};

ca_companion_session_t *ca_companion_session_create(ca_generate_fn generator,
                                                    void *generator_user,
                                                    ca_episodic_store_t *episodic,
                                                    ca_fused_recall_t *recall,
                                                    ca_memory_encoder_t *encoder,
                                                    ca_self_belief_store_t *beliefs,
                                                    const ca_companion_session_options_t *opts) {
    if (!generator || !episodic || !recall || !opts) return NULL;
    ca_companion_session_t *s = (ca_companion_session_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->generator = generator;
    s->generator_user = generator_user;
    s->episodic = episodic;
    s->recall = recall;
    s->encoder = encoder;
    s->beliefs = beliefs;
    s->session_id = cb_dup(opts->session_id);
    s->identity_id = cb_dup(opts->identity_id);
    s->interface_kind = cb_dup(opts->interface_kind);
    s->persona_hints = cb_dup(opts->persona_hints);
    s->affect_summary = cb_dup(opts->affect_summary);
    s->app_context = cb_dup(opts->app_context);
    s->recall_top_k = opts->recall_top_k > 0 ? opts->recall_top_k : 5;
    return s;
}

static void cb_free_snippets(ca_companion_session_t *s) {
    if (s->snippets) {
        for (size_t i = 0; i < s->snippet_count; ++i) free(s->snippets[i]);
        free(s->snippets);
    }
    s->snippets = NULL;
    s->snippet_count = 0;
}

void ca_companion_session_destroy(ca_companion_session_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->history_count; ++i) { free(s->history[i].role); free(s->history[i].content); }
    free(s->history);
    cb_free_snippets(s);
    free(s->session_id);
    free(s->identity_id);
    free(s->interface_kind);
    free(s->persona_hints);
    free(s->affect_summary);
    free(s->app_context);
    free(s);
}

/* Append a piece to a growable string buffer. */
typedef struct { char *buf; size_t len, cap; } cb_sb_t;
static void cb_sb_append(cb_sb_t *sb, const char *s) {
    if (!s) return;
    size_t sl = strlen(s);
    if (sb->len + sl + 1 > sb->cap) {
        size_t nc = sb->cap ? sb->cap : 64;
        while (sb->len + sl + 1 > nc) nc *= 2;
        char *n = (char *)realloc(sb->buf, nc);
        if (!n) return;
        sb->buf = n; sb->cap = nc;
    }
    memcpy(sb->buf + sb->len, s, sl + 1);
    sb->len += sl;
}

/* Build the system prompt from persona/affect + user facts + recalled snippets.
 * Returns an owned string. */
static char *cb_build_system_prompt(ca_companion_session_t *s,
                                    char **snippets, size_t snippet_count) {
    cb_sb_t sb = {0};
    bool any = false;

    if (!cb_is_blank(s->persona_hints)) {
        cb_sb_append(&sb, s->persona_hints);
        any = true;
    }
    if (!cb_is_blank(s->affect_summary)) {
        if (any) cb_sb_append(&sb, "\n\n");
        cb_sb_append(&sb, s->affect_summary);
        any = true;
    }

    /* User facts. */
    if (s->beliefs) {
        size_t fc = 0;
        ca_personal_belief_t *facts = ca_self_belief_store_self_facts(s->beliefs, &fc);
        if (fc > 0) {
            if (any) cb_sb_append(&sb, "\n\n");
            cb_sb_append(&sb, "[What you know about the user]");
            for (size_t i = 0; i < fc; ++i) {
                cb_sb_append(&sb, "\n- ");
                cb_sb_append(&sb, facts[i].object ? facts[i].object : "");
            }
            any = true;
        }
        ca_personal_belief_free_array(facts, fc);
    }

    /* Recalled memories. */
    if (snippet_count > 0) {
        if (any) cb_sb_append(&sb, "\n\n");
        cb_sb_append(&sb, "[Relevant memories]");
        for (size_t i = 0; i < snippet_count; ++i) {
            cb_sb_append(&sb, "\n- ");
            cb_sb_append(&sb, snippets[i] ? snippets[i] : "");
        }
        any = true;
    }

    if (!sb.buf) sb.buf = cb_dup("");
    return sb.buf;
}

/* Run a fused recall and return the snippet texts (owned array of owned strings). */
static char **cb_recall_snippets(ca_companion_session_t *s, const char *query,
                                 size_t *out_count) {
    *out_count = 0;
    size_t hc = 0;
    ca_memory_hit_t *hits = ca_fused_recall_recall(s->recall, query, NULL, 0,
                                                   s->recall_top_k, &hc);
    if (hc == SIZE_MAX || hc == 0) { ca_memory_hit_free_array(hits, hc == SIZE_MAX ? 0 : hc); return NULL; }
    char **snips = (char **)calloc(hc, sizeof(char *));
    if (!snips) { ca_memory_hit_free_array(hits, hc); return NULL; }
    for (size_t i = 0; i < hc; ++i) snips[i] = cb_dup(hits[i].item.text ? hits[i].item.text : "");
    ca_memory_hit_free_array(hits, hc);
    *out_count = hc;
    return snips;
}

static void cb_history_push(ca_companion_session_t *s, const char *role, const char *content) {
    if (s->history_count == s->history_cap) {
        size_t nc = s->history_cap ? s->history_cap * 2 : 8;
        cb_turn_t *n = (cb_turn_t *)realloc(s->history, nc * sizeof(cb_turn_t));
        if (!n) return;
        s->history = n; s->history_cap = nc;
    }
    s->history[s->history_count].role = cb_dup(role);
    s->history[s->history_count].content = cb_dup(content);
    s->history_count++;
}

char *ca_companion_session_send(ca_companion_session_t *s, const char *message) {
    if (!s) return NULL;
    const char *msg = message ? message : "";

    /* Recall BEFORE persisting this turn — draws on prior memory only. */
    size_t snippet_count = 0;
    char **snippets = cb_recall_snippets(s, msg, &snippet_count);

    /* Build the message list: system, then history, then the user turn. */
    char *system_prompt = cb_build_system_prompt(s, snippets, snippet_count);

    size_t nmsgs = 1 + s->history_count + 1;
    ca_chat_message_t *msgs = (ca_chat_message_t *)calloc(nmsgs, sizeof(ca_chat_message_t));
    if (!msgs) {
        free(system_prompt);
        for (size_t i = 0; i < snippet_count; ++i) free(snippets[i]);
        free(snippets);
        return NULL;
    }
    size_t mi = 0;
    msgs[mi].role = CA_ROLE_SYSTEM; msgs[mi].content = system_prompt; msgs[mi].created_at = 0; mi++;
    for (size_t i = 0; i < s->history_count; ++i) {
        ca_role_t r = CA_ROLE_USER;
        if (s->history[i].role && strcmp(s->history[i].role, "assistant") == 0) r = CA_ROLE_ASSISTANT;
        else if (s->history[i].role && strcmp(s->history[i].role, "system") == 0) r = CA_ROLE_SYSTEM;
        msgs[mi].role = r; msgs[mi].content = s->history[i].content; msgs[mi].created_at = 0; mi++;
    }
    msgs[mi].role = CA_ROLE_USER; msgs[mi].content = msg; msgs[mi].created_at = 0; mi++;

    /* Call the generator (it returns a malloc'd reply we own). */
    char *reply = s->generator(s->generator_user, msgs, nmsgs);
    free(msgs);
    free(system_prompt);

    if (!reply) {
        for (size_t i = 0; i < snippet_count; ++i) free(snippets[i]);
        free(snippets);
        return NULL;
    }

    /* Persist the exchange to episodic memory. */
    char episode_id[64];
    snprintf(episode_id, sizeof(episode_id), "ep-%lld-%zu",
             (long long)cb_now_ms(), s->history_count);
    ca_episodic_entry_t entry;
    memset(&entry, 0, sizeof(entry));
    entry.id = episode_id;
    entry.recorded_at_ms = cb_now_ms();
    entry.user_text = (char *)msg;
    entry.assistant_text = reply;
    entry.app_context = s->app_context;
    entry.embedding = NULL;
    entry.embedding_len = 0;
    ca_episodic_store_add(s->episodic, &entry);

    /* Off the hot path: fill the graph + form beliefs for next time. */
    if (s->encoder) ca_memory_encoder_enqueue(s->encoder, msg, reply, episode_id);

    /* Append to history and update recalled-snippets context. */
    cb_history_push(s, "user", msg);
    cb_history_push(s, "assistant", reply);
    cb_free_snippets(s);
    s->snippets = snippets;
    s->snippet_count = snippet_count;

    return reply; /* caller owns */
}

size_t ca_companion_session_history_count(const ca_companion_session_t *s) {
    return s ? s->history_count : 0;
}

const char *ca_companion_session_history_role(const ca_companion_session_t *s, size_t i) {
    if (!s || i >= s->history_count) return NULL;
    return s->history[i].role;
}

const char *ca_companion_session_history_content(const ca_companion_session_t *s, size_t i) {
    if (!s || i >= s->history_count) return NULL;
    return s->history[i].content;
}

const char *const *ca_companion_session_context_snippets(const ca_companion_session_t *s,
                                                         size_t *out_count) {
    if (out_count) *out_count = s ? s->snippet_count : 0;
    return s ? (const char *const *)s->snippets : NULL;
}

void ca_companion_session_refresh_context(ca_companion_session_t *s) {
    if (!s) return;
    size_t sc = 0;
    char **snips = cb_recall_snippets(s, "", &sc);
    cb_free_snippets(s);
    s->snippets = snips;
    s->snippet_count = sc;
}
