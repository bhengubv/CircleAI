#ifndef CIRCLE_AI_LLM_EXTRACTOR_H
#define CIRCLE_AI_LLM_EXTRACTOR_H

/*
 * llm_extractor.h — LLM-backed knowledge-graph extraction (C11 port).
 *
 * A turn → (subject, predicate, object) triple extractor that asks an on-device
 * LLM (via the existing ca_generate_fn seam) to emit strict-JSON triples, then
 * parses them defensively. Ported from CircleAI.Companion.LlmKnowledgeGraphExtractor
 * (C#) and mirroring the verified TypeScript reference (memory/llm_extractor.ts)
 * 1:1. In-memory, stateless, no JSON library — a small tolerant scanner.
 *
 * Reuses ca_knowledge_triple_t, ca_knowledge_triple_free_array (memory_brain.h)
 * and ca_chat_message_t + ca_generate_fn (models.h / companion_brain.h).
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>

#include "models.h"          /* ca_chat_message_t, ca_role_t */
#include "memory_brain.h"    /* ca_knowledge_triple_t, ca_knowledge_triple_free_array */
#include "companion_brain.h" /* ca_generate_fn */

#ifdef __cplusplus
extern "C" {
#endif

/* Confidence used when the model omits (or malforms) the "c" field. */
#define CA_LLM_EXTRACTOR_DEFAULT_CONFIDENCE 0.75

/* The verbatim extraction system prompt (copied from the C#/TS reference). A
 * borrowed pointer to a static string. */
const char *ca_llm_extractor_system_prompt(void);

/*
 * Ask the generator to extract triples from a single conversation turn.
 *
 * When both user_text and assistant_text are blank the generator is NOT called
 * and an empty result is returned (count 0, NULL). Otherwise the user message
 * "USER:\n<user>\nASSISTANT:\n<assistant>\n" is built, the verbatim system
 * prompt is sent as a system ca_chat_message_t, and the generator is invoked.
 * A NULL/empty reply, or any malformed reply, degrades to an empty result.
 *
 * generator returns a heap-allocated (malloc'd) reply string that THIS function
 * takes ownership of and frees; returning NULL signals a generator failure and
 * degrades to empty. generator_user is passed through untouched.
 *
 * source_episode_id may be NULL; it is copied onto every returned triple's
 * source field. Returns a fresh triple array the caller frees with
 * ca_knowledge_triple_free_array; *out_count is set to the length (0 → NULL).
 */
ca_knowledge_triple_t *ca_llm_extract_from_turn(
    ca_generate_fn generator, void *generator_user,
    const char *user_text, const char *assistant_text,
    const char *source_episode_id, size_t *out_count);

/*
 * Parse a raw model reply into triples (the ca_llm_extract_from_turn parser,
 * exposed for testing). Finds the first '[' and last ']', hand-parses the JSON
 * array of {"s":..,"p":..,"o":..,"c":..} objects between them, and reads s/p/o
 * (strings) and c (number). c is clamped to [0,1], defaulting to 0.75 when
 * absent/non-numeric. Entries with a blank s/p/o are skipped. Any structural
 * problem yields an empty result (count 0, NULL) rather than an error.
 *
 * source_episode_id may be NULL; copied onto every triple's source. Returns a
 * fresh triple array the caller frees with ca_knowledge_triple_free_array.
 */
ca_knowledge_triple_t *ca_llm_extractor_parse_triples(
    const char *raw, const char *source_episode_id, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_LLM_EXTRACTOR_H */
