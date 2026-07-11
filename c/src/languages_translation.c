/*
 * languages_translation.c — CircleAI.Languages.Translation (C11 port).
 *
 * Ports TranslationTypes.cs + ITranslationEngine.cs + ILiveTranslator.cs +
 * LlmTranslationEngine.cs. Drives the on-device LLM via the ca_local_chat_generator_t
 * seam (inference_rt.h). Async methods complete synchronously (house rule).
 *
 * Pure C11 + libc. Linear arrays (no hashtable), no pthreads.
 */

#include "circle_ai/languages_translation.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── shared helpers (copied from media.c house style) ───────────────────── */

static char *tr_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *tr_strdup_empty(const char *s) { return tr_strdup(s ? s : ""); }

/* Trim leading/trailing ASCII whitespace into a fresh allocation (string.Trim()).
 * Returns NULL only on OOM. An all-whitespace/empty input yields "". */
static char *tr_trim(const char *s) {
    if (!s) return tr_strdup("");
    const char *a = s;
    while (*a && isspace((unsigned char)*a)) a++;
    const char *b = s + strlen(s);
    while (b > a && isspace((unsigned char)b[-1])) b--;
    size_t n = (size_t)(b - a);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, a, n);
    out[n] = '\0';
    return out;
}

/* Append src to *buf (grown as needed). *len is the live length, *cap the
 * allocation. Returns false on OOM (leaving *buf intact for the caller to free). */
static bool tr_append(char **buf, size_t *len, size_t *cap, const char *src) {
    if (!src) return true;
    size_t sl = strlen(src);
    if (*len + sl + 1 > *cap) {
        size_t nc = *cap ? *cap : 64;
        while (*len + sl + 1 > nc) nc *= 2;
        char *n = (char *)realloc(*buf, nc);
        if (!n) return false;
        *buf = n;
        *cap = nc;
    }
    memcpy(*buf + *len, src, sl);
    *len += sl;
    (*buf)[*len] = '\0';
    return true;
}

/* ===========================================================================
 * TranslationMode
 * =========================================================================== */

const char *ca_translation_mode_name(ca_translation_mode_t mode) {
    switch (mode) {
        case CA_TRANSLATION_MODE_STANDARD:       return "Standard";
        case CA_TRANSLATION_MODE_CONVERSATIONAL: return "Conversational";
        case CA_TRANSLATION_MODE_DOCUMENT:       return "Document";
        case CA_TRANSLATION_MODE_TECHNICAL:      return "Technical";
        case CA_TRANSLATION_MODE_LEGAL:          return "Legal";
        case CA_TRANSLATION_MODE_MEDICAL:        return "Medical";
        default:                                 return "Standard";
    }
}

/* ===========================================================================
 * TranslationRequest / TranslationResult / ConversationTurn
 * =========================================================================== */

void ca_translation_request_free(ca_translation_request_t *r) {
    if (!r) return;
    free(r->text);
    free(r->source_bcp_tag);
    free(r->target_bcp_tag);
    free(r->context_hint);
    r->text = r->source_bcp_tag = r->target_bcp_tag = r->context_hint = NULL;
}

void ca_translation_result_free(ca_translation_result_t *r) {
    if (!r) return;
    free(r->original_text);
    free(r->translated_text);
    free(r->source_bcp_tag);
    free(r->target_bcp_tag);
    r->original_text = r->translated_text = NULL;
    r->source_bcp_tag = r->target_bcp_tag = NULL;
}

void ca_conversation_turn_free(ca_conversation_turn_t *t) {
    if (!t) return;
    free(t->speaker_bcp_tag);
    free(t->original_text);
    free(t->translated_text);   /* NULL-safe */
    t->speaker_bcp_tag = t->original_text = t->translated_text = NULL;
}
void ca_conversation_turn_free_array(ca_conversation_turn_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_conversation_turn_free(&arr[i]);
    free(arr);
}

/* Deep-copy src into dst (dst assumed uninitialised). translated_text stays NULL
 * when the source is NULL (preserves the C# null). false on OOM. */
static bool turn_copy(ca_conversation_turn_t *dst,
                      const ca_conversation_turn_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->speaker_bcp_tag = tr_strdup_empty(src->speaker_bcp_tag);
    dst->original_text   = tr_strdup_empty(src->original_text);
    dst->translated_text = src->translated_text ? tr_strdup(src->translated_text)
                                                : NULL;
    dst->timestamp_ms    = src->timestamp_ms;
    if (!dst->speaker_bcp_tag || !dst->original_text ||
        (src->translated_text && !dst->translated_text)) {
        ca_conversation_turn_free(dst);
        return false;
    }
    return true;
}

/* ===========================================================================
 * LlmTranslationEngine
 * =========================================================================== */

struct ca_translation_engine {
    ca_local_chat_generator_t *generator;   /* borrowed, not owned */
};

ca_translation_engine_t *ca_translation_engine_create(
    ca_local_chat_generator_t *generator) {
    if (!generator) return NULL;   /* ArgumentNullException(nameof(generator)) */
    ca_translation_engine_t *e =
        (ca_translation_engine_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->generator = generator;
    return e;
}
void ca_translation_engine_destroy(ca_translation_engine_t *engine) {
    /* Borrows the generator — nothing else owned. */
    free(engine);
}

char *ca_translation_build_prompt(const ca_translation_request_t *request) {
    if (!request || !request->source_bcp_tag || !request->target_bcp_tag ||
        !request->text)
        return NULL;

    char  *buf = NULL;
    size_t len = 0, cap = 0;
    bool ok = true;

    ok = ok && tr_append(&buf, &len, &cap, "Translate the following text from ");
    ok = ok && tr_append(&buf, &len, &cap, request->source_bcp_tag);
    ok = ok && tr_append(&buf, &len, &cap, " to ");
    ok = ok && tr_append(&buf, &len, &cap, request->target_bcp_tag);
    ok = ok && tr_append(&buf, &len, &cap, ". Mode: ");
    ok = ok && tr_append(&buf, &len, &cap, ca_translation_mode_name(request->mode));
    ok = ok && tr_append(&buf, &len, &cap,
                         ". Preserve meaning and cultural context, not just "
                         "literal words. ");
    if (request->context_hint) {   /* C# (ContextHint is not null ? ... : "") */
        ok = ok && tr_append(&buf, &len, &cap, "Context: ");
        ok = ok && tr_append(&buf, &len, &cap, request->context_hint);
        ok = ok && tr_append(&buf, &len, &cap, ". ");
    }
    ok = ok && tr_append(&buf, &len, &cap,
                         "Return only the translation with no explanation.\n\n");
    ok = ok && tr_append(&buf, &len, &cap, request->text);

    if (!ok) { free(buf); return NULL; }
    return buf ? buf : tr_strdup("");
}

bool ca_translation_engine_translate(ca_translation_engine_t *engine,
                                     const ca_translation_request_t *request,
                                     int64_t now_ms,
                                     ca_translation_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!engine || !request || !out) return false;
    if (!request->text || !request->source_bcp_tag || !request->target_bcp_tag)
        return false;

    char *prompt = ca_translation_build_prompt(request);
    if (!prompt) return false;

    /* messages = [ new ChatMessage("user", BuildPrompt(request)) ] */
    ca_chat_msg_t msg = {0};
    msg.role    = "user";
    msg.content = prompt;

    char *translated = ca_local_chat_generator_generate(engine->generator,
                                                        &msg, 1, NULL);
    free(prompt);
    if (!translated) return false;

    char *trimmed = tr_trim(translated);   /* translated.Trim() */
    free(translated);
    if (!trimmed) return false;

    out->original_text   = tr_strdup_empty(request->text);
    out->translated_text = trimmed;                       /* owns the trimmed buf */
    out->source_bcp_tag  = tr_strdup_empty(request->source_bcp_tag);
    out->target_bcp_tag  = tr_strdup_empty(request->target_bcp_tag);
    out->confidence      = 0.9f;
    out->translated_at_ms = now_ms;
    if (!out->original_text || !out->source_bcp_tag || !out->target_bcp_tag) {
        ca_translation_result_free(out);
        return false;
    }
    return true;
}

bool ca_translation_engine_stream_translate(ca_translation_engine_t *engine,
                                            const ca_translation_request_t *request,
                                            const ca_generation_options_t *opts,
                                            ca_chat_stream_fn on_fragment,
                                            void *user) {
    if (!engine || !request || !on_fragment) return false;
    if (!request->text || !request->source_bcp_tag || !request->target_bcp_tag)
        return false;

    char *prompt = ca_translation_build_prompt(request);
    if (!prompt) return false;

    ca_chat_msg_t msg = {0};
    msg.role    = "user";
    msg.content = prompt;

    /* await foreach (token in _generator.StreamAsync(messages)) yield token; */
    bool ok = ca_local_chat_generator_stream_fragments(engine->generator,
                                                       &msg, 1, opts,
                                                       on_fragment, user);
    free(prompt);
    return ok;
}

bool ca_translation_engine_is_language_pair_supported(
    const ca_translation_engine_t *engine,
    const char *source_bcp_tag, const char *target_bcp_tag) {
    (void)source_bcp_tag;
    (void)target_bcp_tag;
    if (!engine) return false;
    return true;   /* Task.FromResult(true) */
}

ca_conversation_turn_t *ca_translation_engine_translate_conversation(
    ca_translation_engine_t *engine,
    const ca_conversation_turn_t *in, size_t n,
    const char *party_a_bcp_tag, const char *party_b_bcp_tag,
    int64_t now_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!engine || (n > 0 && !in) || !party_a_bcp_tag || !party_b_bcp_tag) {
        *out_count = (size_t)-1;
        return NULL;
    }
    if (n == 0) { *out_count = 0; return NULL; }

    ca_conversation_turn_t *out =
        (ca_conversation_turn_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }

    for (size_t i = 0; i < n; ++i) {
        const ca_conversation_turn_t *turn = &in[i];

        /* targetTag = turn.SpeakerBcpTag == partyA ? partyB : partyA (Ordinal). */
        const char *speaker = turn->speaker_bcp_tag ? turn->speaker_bcp_tag : "";
        const char *target_tag =
            (strcmp(speaker, party_a_bcp_tag) == 0) ? party_b_bcp_tag
                                                    : party_a_bcp_tag;

        ca_translation_request_t req = {0};
        req.text           = turn->original_text ? turn->original_text : (char *)"";
        req.source_bcp_tag = (char *)speaker;
        req.target_bcp_tag = (char *)target_tag;
        req.mode           = CA_TRANSLATION_MODE_CONVERSATIONAL;
        req.context_hint   = NULL;

        ca_translation_result_t result;
        if (!ca_translation_engine_translate(engine, &req, now_ms, &result)) {
            ca_conversation_turn_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }

        /* yield return turn with { TranslatedText = result.TranslatedText }; */
        if (!turn_copy(&out[i], turn)) {
            ca_translation_result_free(&result);
            ca_conversation_turn_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
        free(out[i].translated_text);   /* drop the copied-through value (if any) */
        out[i].translated_text = tr_strdup_empty(result.translated_text);
        ca_translation_result_free(&result);
        if (!out[i].translated_text) {
            ca_conversation_turn_free_array(out, i + 1);
            *out_count = (size_t)-1;
            return NULL;
        }
    }

    *out_count = n;
    return out;
}
