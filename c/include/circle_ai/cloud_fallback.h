#ifndef CIRCLE_AI_CLOUD_FALLBACK_H
#define CIRCLE_AI_CLOUD_FALLBACK_H

/*
 * cloud_fallback.h - CircleAI.Hosting.CloudFallback and CircleAI.Realtime.Cloud.
 *
 * The providers this can fall back to when the device cannot answer, and the
 * realtime voice services it can speak to instead of running the loop locally.
 *
 * WHY THIS IS A FALLBACK AND NOT A BACKEND. On-device is the product. These
 * exist for the cases where the honest answer is that the phone cannot do it -
 * a model too large for the hardware, a language with no local voice, a device
 * with 1 GB of RAM. Every one of them sends what somebody said to a company,
 * and the shape here makes that visible rather than routine.
 *
 * NOTHING HERE IS ENABLED BY DEFAULT AND NONE OF IT HOLDS A KEY. Options carry
 * a key the HOST supplies at construction; no provider reads an environment
 * variable, no provider caches a credential, and a provider with no key
 * configured is absent rather than broken. A fallback that turns itself on
 * because a variable happened to be set is a device that started sending
 * conversations to a third party without anybody choosing.
 *
 * MOST OF THESE ARE OPENAI-COMPATIBLE, and that is why the base exists: seven
 * providers, one wire format, one place where a streaming-response parse bug
 * gets fixed. Anthropic and Gemini are not, and have their own.
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

/* -- shared shape --------------------------------------------------------- */

/*
 * What a provider needs. One struct per provider rather than one shared, so
 * that a default endpoint or a required header belongs to the provider it is
 * true for - and so adding a provider cannot quietly change another's defaults.
 *
 * `api_key` is BORROWED and never copied into long-lived storage. The host owns
 * the credential's lifetime, which is what lets it be zeroed on the way out.
 */
typedef struct {
    const char *api_key;
    char *endpoint;      /* NULL uses the provider's own */
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
} ca_open_ai_chat_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
    /* Anthropic sends a dated API version header, and it is not optional - a
     * request without one is rejected outright. */
    char *api_version;
} ca_anthropic_chat_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
} ca_gemini_chat_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
} ca_groq_chat_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
} ca_cerebras_chat_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
} ca_deep_seek_chat_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    double temperature;
    int max_tokens;
    int timeout_seconds;
} ca_together_chat_options_t;

/* -- the generators ------------------------------------------------------- */

typedef struct ca_configurable_chat_generator {
    void *state;
    const char *(*provider_id)(void *state);
    /* Whether it is usable at all - a key was supplied and the model is named.
     * Checked BEFORE a chain reaches for it, so a missing key is a provider
     * that is skipped rather than a request that fails. */
    bool (*is_configured)(void *state);
    /* Caller frees. NULL on failure, with *out_error set. */
    char *(*generate)(void *state, const char *prompt, char **out_error);
    /* Streaming. `on_token` is called per chunk; returning false stops. */
    bool (*stream)(void *state, const char *prompt,
                   bool (*on_token)(void *token_state, const char *token),
                   void *token_state, char **out_error);
    void (*free_fn)(void *state);
} ca_configurable_chat_generator_t;

void ca_configurable_chat_generator_free(ca_configurable_chat_generator_t *generator);

/*
 * The shared implementation for providers that speak OpenAI's wire format.
 *
 * `post` and `post_stream` are the host's HTTP; this module owns no client and
 * opens no socket. That is what keeps the transport - and therefore the proxy,
 * the certificate pinning and the timeout policy - the host's decision.
 */
ca_configurable_chat_generator_t *ca_open_ai_compatible_chat_generator_base_new(
    const char *provider_id, const char *default_endpoint,
    const ca_open_ai_chat_options_t *options,
    char *(*post)(void *state, const char *url, const char *body,
                  const char **headers, size_t header_count, char **out_error),
    void *state);

ca_configurable_chat_generator_t *ca_open_ai_chat_generator_new(
    const ca_open_ai_chat_options_t *options, void *http);

ca_configurable_chat_generator_t *ca_groq_chat_generator_new(
    const ca_groq_chat_options_t *options, void *http);

ca_configurable_chat_generator_t *ca_cerebras_chat_generator_new(
    const ca_cerebras_chat_options_t *options, void *http);

ca_configurable_chat_generator_t *ca_deep_seek_chat_generator_new(
    const ca_deep_seek_chat_options_t *options, void *http);

ca_configurable_chat_generator_t *ca_together_chat_generator_new(
    const ca_together_chat_options_t *options, void *http);

/* Not OpenAI-shaped: system prompt is a top-level field rather than a message,
 * and the response content is a list of blocks. */
ca_configurable_chat_generator_t *ca_anthropic_chat_generator_new(
    const ca_anthropic_chat_options_t *options, void *http);

/* Also not: "contents" and "parts" rather than messages, and the key goes in
 * the query string unless a header is used - which is why it is never logged
 * with the URL. */
ca_configurable_chat_generator_t *ca_gemini_chat_generator_new(
    const ca_gemini_chat_options_t *options, void *http);

/* -- realtime voice services ---------------------------------------------- */

typedef struct {
    const char *api_key;
    char *endpoint;
    char *voice;
    char *model;
    int sample_rate_hz;
} ca_open_ai_realtime_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *agent_id;
    int sample_rate_hz;
} ca_eleven_labs_conv_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    int sample_rate_hz;
} ca_gemini_live_options_t;

typedef struct {
    const char *api_key;
    char *region;
    char *voice;
    int sample_rate_hz;
} ca_nova_sonic_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *system_prompt;
    int sample_rate_hz;
} ca_ultravox_options_t;

/*
 * A duplex audio link to a realtime service.
 *
 * Duplex is the whole difference from the chat generators: audio goes up while
 * audio comes down, and the caller can be interrupted mid-sentence. A
 * request/response shape cannot express that, which is why these are a separate
 * seam rather than another generator.
 */
typedef struct ca_realtime_transport {
    void *state;
    bool (*connect)(void *state, char **out_error);
    bool (*send_audio)(void *state, const uint8_t *pcm, size_t len);
    /* Called on the transport's own thread. */
    void (*set_audio_handler)(void *state,
                              void (*on_audio)(void *handler_state,
                                               const uint8_t *pcm, size_t len),
                              void *handler_state);
    /* Barge-in. Not optional and not a nicety: without it the service keeps
     * speaking over somebody who has started talking, which is the single
     * thing that makes a voice assistant feel broken. */
    bool (*interrupt)(void *state);
    void (*close)(void *state);
    void (*free_fn)(void *state);
} ca_realtime_transport_t;

void ca_realtime_transport_free(ca_realtime_transport_t *transport);

typedef struct ca_realtime_transport_factory {
    void *state;
    ca_realtime_transport_t *(*create)(void *state, const char *provider_id);
    void (*free_fn)(void *state);
} ca_realtime_transport_factory_t;

void ca_realtime_transport_factory_free(ca_realtime_transport_factory_t *factory);

/* Creates nothing. The default: a build with no realtime provider configured
 * runs the local loop, which is the intended behaviour rather than a
 * degradation. */
ca_realtime_transport_factory_t *ca_null_realtime_transport_factory_new(void);

/* The WebSocket session all of these ride on. `ws` is the host's socket - one
 * place where the framing, the ping/pong and the reconnect live, rather than
 * five slightly different copies. */
typedef struct ca_realtime_web_socket_session ca_realtime_web_socket_session_t;

ca_realtime_web_socket_session_t *ca_realtime_web_socket_session_new(
    const char *url, const char **headers, size_t header_count, void *ws);

void ca_realtime_web_socket_session_free(ca_realtime_web_socket_session_t *session);

ca_realtime_transport_t *ca_open_ai_realtime_service_new(
    const ca_open_ai_realtime_options_t *options, void *ws);

ca_realtime_transport_t *ca_eleven_labs_conv_service_new(
    const ca_eleven_labs_conv_options_t *options, void *ws);

ca_realtime_transport_t *ca_gemini_live_service_new(
    const ca_gemini_live_options_t *options, void *ws);

ca_realtime_transport_t *ca_nova_sonic_service_new(
    const ca_nova_sonic_options_t *options, void *ws);

ca_realtime_transport_t *ca_ultravox_service_new(
    const ca_ultravox_options_t *options, void *ws);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CLOUD_FALLBACK_H */
