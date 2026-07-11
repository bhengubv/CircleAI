#ifndef CIRCLE_AI_LANGUAGES_TRANSLATION_H
#define CIRCLE_AI_LANGUAGES_TRANSLATION_H

/*
 * languages_translation.h — CircleAI.Languages.Translation (C11 port).
 *
 * Ports the on-device translation vertical 1:1:
 *
 *   TranslationTypes.cs :
 *     Enum   : TranslationMode { Standard, Conversational, Document, Technical,
 *              Legal, Medical } -> ca_translation_mode_t 0..5.
 *     Record : TranslationRequest(Text, SourceBcpTag, TargetBcpTag,
 *              Mode=Standard, ContextHint?) -> ca_translation_request_t.
 *     Record : TranslationResult(OriginalText, TranslatedText, SourceBcpTag,
 *              TargetBcpTag, float Confidence, DateTimeOffset TranslatedAt)
 *              -> ca_translation_result_t (TranslatedAt as int64 Unix ms UTC).
 *     Record : ConversationTurn(SpeakerBcpTag, OriginalText, TranslatedText?,
 *              DateTimeOffset Timestamp) -> ca_conversation_turn_t.
 *   ITranslationEngine.cs / ILiveTranslator.cs / LlmTranslationEngine.cs :
 *     LlmTranslationEngine(IChatGenerator) — TranslateAsync / StreamTranslateAsync
 *     / IsLanguagePairSupportedAsync / StreamConversationAsync. The IChatGenerator
 *     dependency is the C seam ca_local_chat_generator_t (inference_rt.h): the C#
 *     GenerateAsync(ChatMessage[]) maps to ca_local_chat_generator_generate over
 *     ca_chat_msg_t[]; StreamAsync maps to ca_local_chat_generator_stream_fragments.
 *
 * Async -> sync adaptations (house rule: "async methods complete synchronously"):
 *   - TranslateAsync            -> ca_translation_engine_translate (blocking).
 *   - StreamTranslateAsync      -> ca_translation_engine_stream_translate delegates
 *     to the generator's own StreamFragmentsAsync seam, forwarding each fragment to
 *     a caller callback (ca_chat_stream_fn). Faithful: the C# method simply relays
 *     the generator's token stream.
 *   - StreamConversationAsync   -> ca_translation_engine_translate_conversation maps
 *     an input turn array to an output turn array (each deep-copied with its
 *     TranslatedText filled). Faithful: the C# generator consumed one input turn and
 *     yielded one output turn with no cross-turn state.
 *   - IsLanguagePairSupportedAsync -> ca_translation_engine_is_language_pair_supported
 *     (always true).
 *   IsLanguagePairSupportedAsync always returns true, mirroring the C#.
 *
 * The engine borrows (does not own) its ca_local_chat_generator_t; the caller
 * keeps it alive for the engine's lifetime.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free / *_free_array, deep-copy getters, errors via NULL / count
 * SIZE_MAX. DateTimeOffset carried as int64 Unix ms UTC (passed in — the clock is
 * explicit; see field docs). ContextHint / TranslatedText may be NULL (the C#
 * string?). Linear arrays, no hashtable, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "inference_rt.h"   /* ca_local_chat_generator_t, ca_chat_msg_t,
                             * ca_chat_stream_fn, ca_generation_options_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * TranslationMode
 * =========================================================================== */

typedef enum {
    CA_TRANSLATION_MODE_STANDARD       = 0,
    CA_TRANSLATION_MODE_CONVERSATIONAL = 1,
    CA_TRANSLATION_MODE_DOCUMENT       = 2,
    CA_TRANSLATION_MODE_TECHNICAL      = 3,
    CA_TRANSLATION_MODE_LEGAL          = 4,
    CA_TRANSLATION_MODE_MEDICAL        = 5
} ca_translation_mode_t;

/* The enum member name ("Standard".."Medical") used verbatim in BuildPrompt.
 * Returns a static string; "Standard" for an out-of-range value. */
const char *ca_translation_mode_name(ca_translation_mode_t mode);

/* ===========================================================================
 * TranslationRequest / TranslationResult / ConversationTurn
 * =========================================================================== */

/* TranslationRequest(Text, SourceBcpTag, TargetBcpTag, Mode=Standard,
 * ContextHint?). context_hint may be NULL (the C# null default). */
typedef struct {
    char                 *text;            /* owned, non-null */
    char                 *source_bcp_tag;  /* owned, non-null */
    char                 *target_bcp_tag;  /* owned, non-null */
    ca_translation_mode_t mode;
    char                 *context_hint;    /* owned; NULL ok */
} ca_translation_request_t;

void ca_translation_request_free(ca_translation_request_t *r);

/* TranslationResult(OriginalText, TranslatedText, SourceBcpTag, TargetBcpTag,
 * float Confidence, DateTimeOffset TranslatedAt). translated_at_ms carries the
 * DateTimeOffset as Unix ms UTC. */
typedef struct {
    char   *original_text;    /* owned, non-null */
    char   *translated_text;  /* owned, non-null */
    char   *source_bcp_tag;   /* owned, non-null */
    char   *target_bcp_tag;   /* owned, non-null */
    float   confidence;
    int64_t translated_at_ms;
} ca_translation_result_t;

void ca_translation_result_free(ca_translation_result_t *r);

/* ConversationTurn(SpeakerBcpTag, OriginalText, TranslatedText?, DateTimeOffset
 * Timestamp). translated_text may be NULL (the C# null before translation).
 * timestamp_ms is the DateTimeOffset as Unix ms UTC. */
typedef struct {
    char   *speaker_bcp_tag;  /* owned, non-null */
    char   *original_text;    /* owned, non-null */
    char   *translated_text;  /* owned; NULL ok */
    int64_t timestamp_ms;
} ca_conversation_turn_t;

void ca_conversation_turn_free(ca_conversation_turn_t *t);
void ca_conversation_turn_free_array(ca_conversation_turn_t *arr, size_t count);

/* ===========================================================================
 * LlmTranslationEngine
 * ===========================================================================
 *
 * ca_translation_engine_t wraps a *borrowed* ca_local_chat_generator_t (the C#
 * IChatGenerator dependency). The caller owns the generator and must keep it
 * alive for the engine's lifetime.
 */

typedef struct ca_translation_engine ca_translation_engine_t;

/* LlmTranslationEngine(IChatGenerator generator). generator must be non-NULL
 * (mirrors the C# ArgumentNullException). Returns NULL on NULL generator / OOM.
 * The generator is borrowed, not owned. */
ca_translation_engine_t *ca_translation_engine_create(
    ca_local_chat_generator_t *generator);
void ca_translation_engine_destroy(ca_translation_engine_t *engine);

/* Build the exact translation prompt (exposed for tests; mirrors BuildPrompt):
 *   "Translate the following text from " + SourceBcpTag + " to " + TargetBcpTag +
 *   ". Mode: " + <ModeName> + ". Preserve meaning and cultural context, not just
 *   literal words. " + (ContextHint!=null ? "Context: " + ContextHint + ". " : "")
 *   + "Return only the translation with no explanation.\n\n" + Text
 * Returns a freshly-allocated string (caller frees) or NULL on bad args / OOM. */
char *ca_translation_build_prompt(const ca_translation_request_t *request);

/* TranslateAsync(request): sends one "user" message (BuildPrompt(request)) to the
 * generator, then fills *out with TranslationResult(request.Text, translated.Trim(),
 * request.Source, request.Target, 0.9f, now_ms). translated is trimmed of leading /
 * trailing ASCII whitespace. now_ms is the DateTimeOffset.UtcNow stamp (explicit
 * clock — pass the current Unix ms UTC). Returns true on success, false on bad
 * args / generator failure / OOM (with *out zeroed). */
bool ca_translation_engine_translate(ca_translation_engine_t *engine,
                                     const ca_translation_request_t *request,
                                     int64_t now_ms,
                                     ca_translation_result_t *out);

/* StreamTranslateAsync(request): delegates to the generator's StreamFragmentsAsync
 * seam, forwarding each fragment to on_fragment (fragment.text is valid only for
 * the call). opts may be NULL. Returns false on bad args. */
bool ca_translation_engine_stream_translate(ca_translation_engine_t *engine,
                                            const ca_translation_request_t *request,
                                            const ca_generation_options_t *opts,
                                            ca_chat_stream_fn on_fragment,
                                            void *user);

/* IsLanguagePairSupportedAsync(src, tgt) -> always true (the on-device LLM handles
 * any pair it was trained on). Returns false only when engine is NULL. src / tgt
 * are accepted for signature parity and otherwise unused. */
bool ca_translation_engine_is_language_pair_supported(
    const ca_translation_engine_t *engine,
    const char *source_bcp_tag, const char *target_bcp_tag);

/* StreamConversationAsync(inputStream, partyA, partyB): for each input turn,
 * targetTag = (turn.SpeakerBcpTag == partyA ? partyB : partyA) (Ordinal compare),
 * translate OriginalText (Conversational mode), and emit the turn deep-copied with
 * TranslatedText filled. now_ms stamps each internal TranslateAsync (explicit
 * clock). Returns a freshly-allocated array of *out_count turns (caller frees with
 * ca_conversation_turn_free_array). NULL + *out_count 0 when n == 0; NULL +
 * SIZE_MAX on bad args / generator failure / OOM. */
ca_conversation_turn_t *ca_translation_engine_translate_conversation(
    ca_translation_engine_t *engine,
    const ca_conversation_turn_t *in, size_t n,
    const char *party_a_bcp_tag, const char *party_b_bcp_tag,
    int64_t now_ms, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_LANGUAGES_TRANSLATION_H */
