#ifndef CIRCLE_AI_CAST_H
#define CIRCLE_AI_CAST_H

/*
 * cast.h - CircleAI.Cast (C11).
 *
 * Putting something the assistant made onto the television in the room.
 *
 * DE-GOOGLED BY DESIGN: no Google Cast, no Chromecast SDK. The only backend is
 * open UPnP/DLNA, which every television in this market already speaks and
 * which needs nobody's account.
 *
 * THE RENDERER PULLS. Nothing is ever pushed to it — a caller hands the
 * television a URL and the television fetches it. That single fact is why
 * casting a local file needs an HTTP server running on THIS device, and it is
 * the thing most people implementing DLNA get wrong first.
 *
 * OFFLINE AND LAN-ONLY. Nothing in this module reaches the internet.
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

/* The only protocol. An enum with one member on purpose: it is the place a
 * second one would go, and its absence is the de-Googling decision made
 * visible rather than assumed. */
typedef enum {
    CA_CAST_PROTOCOL_DLNA = 0
} ca_cast_protocol_t;

typedef struct {
    char *value;
} ca_cast_target_id_t;

void ca_cast_target_id_free(ca_cast_target_id_t *id);

typedef enum {
    CA_CAST_CONTENT_IMAGE = 0,
    CA_CAST_CONTENT_AUDIO,
    CA_CAST_CONTENT_VIDEO,
    CA_CAST_CONTENT_SLIDESHOW
} ca_cast_content_kind_t;

typedef enum {
    CA_CAST_STATE_UNKNOWN = 0,
    CA_CAST_STATE_IDLE,
    CA_CAST_STATE_BUFFERING,
    CA_CAST_STATE_PLAYING,
    CA_CAST_STATE_PAUSED,
    CA_CAST_STATE_STOPPED,
    CA_CAST_STATE_ERROR
} ca_cast_playback_state_t;

const char *ca_cast_playback_state_name(ca_cast_playback_state_t state);

/* Where the media is.
 *
 * A tagged union rather than three types, because the three are handled at
 * exactly one place — the point where a URL has to be produced for the
 * television to fetch. A file and a byte buffer both become a URL served by
 * this device; only `url` is already one. */
typedef enum {
    CA_CAST_SOURCE_URL = 0,
    CA_CAST_SOURCE_FILE,
    CA_CAST_SOURCE_BYTES
} ca_cast_media_source_kind_t;

typedef struct {
    ca_cast_media_source_kind_t kind;
    char *url;          /* CA_CAST_SOURCE_URL */
    char *path;         /* CA_CAST_SOURCE_FILE */
    uint8_t *bytes;     /* CA_CAST_SOURCE_BYTES */
    size_t byte_count;
} ca_cast_media_source_t;

void ca_cast_media_source_free(ca_cast_media_source_t *source);

ca_cast_media_source_t *ca_cast_media_source_from_url(const char *url);
ca_cast_media_source_t *ca_cast_media_source_from_file(const char *path);
ca_cast_media_source_t *ca_cast_media_source_from_bytes(const uint8_t *bytes, size_t count);

typedef struct {
    ca_cast_media_source_t *source;
    char *mime_type;
    ca_cast_content_kind_t kind;
    char *title;
    /* Negative when unknown. Zero is a real answer for a still image. */
    double duration_seconds;
} ca_cast_media_t;

void ca_cast_media_free(ca_cast_media_t *media);

typedef struct {
    ca_cast_playback_state_t state;
    double position_seconds;
    double duration_seconds;
    char *current_uri;
} ca_cast_status_t;

void ca_cast_status_free(ca_cast_status_t *status);

/* Why a cast failed.
 *
 * C has no exceptions, so the C# CastException / CastControlException pair is
 * this enum. CONTROL is separated from the rest because it means the television
 * answered and refused, which is a different problem from never reaching it. */
typedef enum {
    CA_CAST_OK = 0,
    CA_CAST_ERR_GENERAL,
    CA_CAST_ERR_CONTROL,
    CA_CAST_ERR_NO_MEDIA_HOST,
    CA_CAST_ERR_NOT_FOUND,
    CA_CAST_ERR_TRANSPORT
} ca_cast_error_t;

const char *ca_cast_error_message(ca_cast_error_t error);

/* ── SSDP discovery ───────────────────────────────────────────────────────── */

typedef struct {
    char *location;     /* where the device description lives */
    char *usn;          /* unique service name */
    char *server;
    char *search_target;
} ca_ssdp_response_t;

void ca_ssdp_response_free(ca_ssdp_response_t *response);

/* Parses one M-SEARCH reply. Header names are matched case-INSENSITIVELY:
 * televisions disagree about capitalisation and a case-sensitive parser finds
 * some of them and not others, which reads as a flaky network. */
bool ca_ssdp_parse_response(const char *text, ca_ssdp_response_t *out_response);

/* Builds the M-SEARCH datagram for a search target. */
char *ca_ssdp_build_search(const char *search_target, int mx_seconds);

typedef struct ca_ssdp_client ca_ssdp_client_t;

ca_ssdp_client_t *ca_ssdp_client_new(void);
void ca_ssdp_client_free(ca_ssdp_client_t *client);

/* Collects replies for `timeout_ms`. Returns a heap array of `*out_count`. */
ca_ssdp_response_t *ca_ssdp_client_search(ca_ssdp_client_t *client,
                                          const char *search_target,
                                          int timeout_ms,
                                          size_t *out_count);

/* ── device description ───────────────────────────────────────────────────── */

typedef struct {
    char *friendly_name;
    char *manufacturer;
    char *model_name;
    char *udn;
    /* Absolute. The description document gives a RELATIVE control URL and the
     * base is the document's own address; resolving it late is how a caller
     * ends up POSTing to its own host. */
    char *control_url;
    char *service_type;
} ca_renderer_description_t;

void ca_renderer_description_free(ca_renderer_description_t *description);

typedef struct {
    char *friendly_name;
    char *manufacturer;
    char *model_name;
    char *udn;
    ca_renderer_description_t *services;
    size_t service_count;
} ca_device_description_t;

void ca_device_description_free(ca_device_description_t *description);

/* Parses the XML at `location`. `base_url` resolves relative control URLs. */
ca_device_description_t *ca_device_description_parse(const char *xml, const char *base_url);

/* ── UPnP control ─────────────────────────────────────────────────────────── */

/* Builds the DIDL-Lite metadata a renderer wants alongside a URI.
 *
 * Not optional in practice: a television handed a URI with no metadata will
 * often play it and show nothing, or refuse outright. Caller frees. */
char *ca_didl_lite_build(const char *title, const char *mime_type,
                         ca_cast_content_kind_t kind, const char *uri);

typedef struct ca_upnp_control_point ca_upnp_control_point_t;

/* `soap` performs one SOAP POST and returns the body, or NULL. It is a function
 * pointer because the transport is the host's — this module does not own an
 * HTTP client. */
ca_upnp_control_point_t *ca_upnp_control_point_new(
    char *(*soap)(void *state, const char *control_url, const char *action,
                  const char *body),
    void *state);

void ca_upnp_control_point_free(ca_upnp_control_point_t *point);

/* SOAPACTION must be QUOTED. An unquoted action is rejected by most renderers
 * with a 500 and no explanation, and it is the single most common reason a
 * first DLNA implementation does not work. */
ca_cast_error_t ca_upnp_set_av_transport_uri(ca_upnp_control_point_t *point,
                                             const char *control_url,
                                             const char *uri,
                                             const char *didl_metadata);

ca_cast_error_t ca_upnp_play(ca_upnp_control_point_t *point, const char *control_url);
ca_cast_error_t ca_upnp_pause(ca_upnp_control_point_t *point, const char *control_url);
ca_cast_error_t ca_upnp_stop(ca_upnp_control_point_t *point, const char *control_url);
ca_cast_error_t ca_upnp_seek(ca_upnp_control_point_t *point, const char *control_url,
                             double position_seconds);

ca_cast_error_t ca_upnp_get_position_info(ca_upnp_control_point_t *point,
                                          const char *control_url,
                                          ca_cast_status_t *out_status);

/* ── targets and sessions ─────────────────────────────────────────────────── */

typedef struct {
    ca_cast_target_id_t id;
    char *friendly_name;
    char *control_url;
    ca_cast_protocol_t protocol;
} ca_dlna_cast_target_t;

void ca_dlna_cast_target_free(ca_dlna_cast_target_t *target);

typedef struct ca_dlna_cast_discovery ca_dlna_cast_discovery_t;

ca_dlna_cast_discovery_t *ca_dlna_cast_discovery_new(ca_ssdp_client_t *ssdp);
void ca_dlna_cast_discovery_free(ca_dlna_cast_discovery_t *discovery);

/* Finds renderers on the LAN. Returns a heap array of `*out_count`. */
ca_dlna_cast_target_t *ca_dlna_cast_discovery_find(ca_dlna_cast_discovery_t *discovery,
                                                   int timeout_ms,
                                                   size_t *out_count);

typedef struct ca_dlna_cast_session ca_dlna_cast_session_t;

/* Opens a session against one target. `media_host_base` is the address THIS
 * device serves local files from — NULL means only already-addressable URLs can
 * be cast, and a file or byte source then fails with
 * CA_CAST_ERR_NO_MEDIA_HOST rather than silently doing nothing. */
ca_dlna_cast_session_t *ca_dlna_cast_session_open(const ca_dlna_cast_target_t *target,
                                                  ca_upnp_control_point_t *control,
                                                  const char *media_host_base);

void ca_dlna_cast_session_close(ca_dlna_cast_session_t *session);

ca_cast_error_t ca_dlna_cast_session_load(ca_dlna_cast_session_t *session,
                                          const ca_cast_media_t *media);

ca_cast_error_t ca_dlna_cast_session_play(ca_dlna_cast_session_t *session);
ca_cast_error_t ca_dlna_cast_session_pause(ca_dlna_cast_session_t *session);
ca_cast_error_t ca_dlna_cast_session_stop(ca_dlna_cast_session_t *session);
ca_cast_error_t ca_dlna_cast_session_seek(ca_dlna_cast_session_t *session,
                                          double position_seconds);

ca_cast_error_t ca_dlna_cast_session_status(ca_dlna_cast_session_t *session,
                                            ca_cast_status_t *out_status);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CAST_H */
