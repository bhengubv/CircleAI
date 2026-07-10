#ifndef CIRCLE_AI_VISION_CLOUD_H
#define CIRCLE_AI_VISION_CLOUD_H

/*
 * vision_cloud.h — CircleAI.Vision.Cloud IImageGenerator (C11 port).
 *
 * Ports the image-generation contract surface 1:1:
 *   Records   : ImageGenerationRequest(Prompt, NegativePrompt?, Size=1024,
 *               Count=1, Style?); ImageArtifact(GeneratorId, Prompt, MimeType,
 *               Url?, Bytes?, GeneratedAtUtc).
 *   Generator : IImageGenerator (GeneratorId / DisplayLabel / IsConfigured /
 *               StatusMessage / GenerateAsync) — an injectable vtable seam.
 *               Ships NullImageGenerator + ImageGeneratorFallbackChain.
 *
 * The concrete OpenAI/Stability generators are external HttpClient dependencies
 * (out of scope for the hermetic C port, exactly as speech_cloud excluded the
 * HTTP STT/TTS recognizers). They plug in behind the IImageGenerator vtable. A
 * deterministic in-memory fake generator ships so the fallback chain (skip
 * unconfigured, first non-empty wins) is fully exercisable.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with a
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * ImageGenerationRequest(Prompt, NegativePrompt?, Size=1024, Count=1, Style?)
 * =========================================================================== */

typedef struct {
    const char *prompt;           /* borrowed for the call */
    const char *negative_prompt;  /* borrowed, or NULL */
    int         size;             /* default 1024 */
    int         count;            /* default 1 */
    const char *style;            /* borrowed, or NULL */
} ca_image_generation_request_t;

/* Initialise with the record defaults (Size=1024, Count=1, others NULL). */
void ca_image_generation_request_init(ca_image_generation_request_t *req,
                                      const char *prompt);

/* ===========================================================================
 * ImageArtifact(GeneratorId, Prompt, MimeType, Url?, Bytes?, GeneratedAtUtc)
 *
 * Either Url OR Bytes, never both (matches the C# contract). has_url flag
 * disambiguates a present-but-empty url from an absent one; likewise bytes uses
 * a NULL pointer / 0 length for absent.
 * =========================================================================== */

typedef struct {
    char    *generator_id;       /* owned, non-null */
    char    *prompt;             /* owned, non-null */
    char    *mime_type;          /* owned, non-null */
    char    *url;                /* owned, or NULL */
    uint8_t *bytes;              /* owned, or NULL */
    size_t   byte_count;
    int64_t  generated_at_utc_ms;
} ca_image_artifact_t;

void ca_image_artifact_free(ca_image_artifact_t *a);
void ca_image_artifact_free_array(ca_image_artifact_t *arr, size_t count);
/* Deep-copy src into *dst (freshly owned). 0 / -1. */
int  ca_image_artifact_copy(ca_image_artifact_t *dst, const ca_image_artifact_t *src);

/* ===========================================================================
 * IImageGenerator seam
 *
 * generate: fill a fresh owned array (*out_count). Fail-soft: NULL + 0 when not
 * configured or when the backend produced nothing. NULL + *out_count SIZE_MAX on
 * a hard error. GeneratorId / DisplayLabel / StatusMessage return borrowed
 * strings owned by the generator (stable for its lifetime).
 * =========================================================================== */

typedef struct {
    void *self;
    const char *(*generator_id)(void *self);      /* non-null */
    const char *(*display_label)(void *self);     /* non-null */
    bool        (*is_configured)(void *self);
    const char *(*status_message)(void *self);    /* non-null */
    ca_image_artifact_t *(*generate)(void *self,
                                     const ca_image_generation_request_t *req,
                                     size_t *out_count);
    void        (*destroy)(void *self);           /* may be NULL */
} ca_image_generator_t;

/* Dispatchers. */
const char          *ca_image_generator_id(const ca_image_generator_t *g);
const char          *ca_image_generator_display_label(const ca_image_generator_t *g);
bool                 ca_image_generator_is_configured(const ca_image_generator_t *g);
const char          *ca_image_generator_status_message(const ca_image_generator_t *g);
ca_image_artifact_t *ca_image_generator_generate(const ca_image_generator_t *g,
                                                 const ca_image_generation_request_t *req,
                                                 size_t *out_count);

/* ===========================================================================
 * NullImageGenerator — GeneratorId "null", DisplayLabel "No image generator",
 * IsConfigured false, StatusMessage the "…Configure OpenAI:ApiKey or
 * Stability:ApiKey…" line; always returns no images.
 * =========================================================================== */

ca_image_generator_t ca_null_image_generator(void);

/* ===========================================================================
 * Deterministic fake generator (for tests + a local default in a chain)
 *
 * GeneratorId/DisplayLabel are the supplied id/label. IsConfigured is the
 * supplied flag. When configured, GenerateAsync returns Math.Clamp(Count,1,4)
 * url-bearing artifacts ("mem://<id>/<prompt>/<i>", mime "image/png"). When not
 * configured, returns no images (empty). generated_at_utc_ms is the supplied
 * fixed clock so tests are deterministic.
 * =========================================================================== */

typedef struct ca_fake_image_generator ca_fake_image_generator_t;

ca_fake_image_generator_t *ca_fake_image_generator_create(const char *generator_id,
                                                          const char *display_label,
                                                          bool configured,
                                                          int64_t fixed_clock_ms);
void ca_fake_image_generator_destroy(ca_fake_image_generator_t *g);
/* The seam view (borrowed). The view's destroy is NULL — call
 * ca_fake_image_generator_destroy yourself unless you hand ownership to a chain
 * built with own=true. */
ca_image_generator_t ca_fake_image_generator_as_iface(ca_fake_image_generator_t *g);

/* ===========================================================================
 * ImageGeneratorFallbackChain — walks the chain in order, skipping any whose
 * IsConfigured is false, returning the first non-empty artifact set (or empty).
 *
 * GeneratorId "fallback-chain"; DisplayLabel "Fallback (<n>)"; IsConfigured =
 * any child configured; StatusMessage "Ready · a → b" (configured ids joined
 * with " → ") or "No configured generator in chain.".
 * =========================================================================== */

typedef struct ca_image_generator_fallback_chain ca_image_generator_fallback_chain_t;

/* Build over an ordered array of generator ifaces (copied by value). When
 * own=true the chain calls each iface's destroy on chain destroy. */
ca_image_generator_fallback_chain_t *ca_image_generator_fallback_chain_create(
    const ca_image_generator_t *generators, size_t count, bool own);
void ca_image_generator_fallback_chain_destroy(ca_image_generator_fallback_chain_t *c);
size_t ca_image_generator_fallback_chain_count(const ca_image_generator_fallback_chain_t *c);

/* The chain as an IImageGenerator seam view (borrowed; StatusMessage/DisplayLabel
 * strings are cached on the chain and refreshed each call). */
ca_image_generator_t ca_image_generator_fallback_chain_as_iface(
    ca_image_generator_fallback_chain_t *c);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VISION_CLOUD_H */
