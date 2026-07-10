#ifndef CIRCLE_AI_TELEPHONY_PLIVO_H
#define CIRCLE_AI_TELEPHONY_PLIVO_H

/*
 * telephony_plivo.h — CircleAI.Telephony.Plivo (C11 port).
 *
 * Ports the Plivo v1 ITelephonyCarrier binding (PlivoCarrier.cs +
 * PlivoCallSession.cs + PlivoOptions.cs). Basic auth (AuthId:AuthToken), the
 * /v1/Account/{AuthId}/ namespace, form-encoded bodies, and the AnswerUrl-driven
 * Audio Streaming flow. NO real network — every request goes through the injected
 * ca_tel_http_t transport.
 *
 *   CarrierId "plivo"; IsConfigured := AuthId && AuthToken both non-blank.
 *   ProvisionNumber : GET /v1/Account/{id}/PhoneNumber/?country_iso=&limit=1
 *                     [&pattern=area] -> objects[0].number, then POST
 *                     /v1/Account/{id}/PhoneNumber/{number}/ (app_id=""). Cost from
 *                     objects[0].monthly_rental_rate.
 *   ConfigureInbound: POST /v1/Account/{id}/Number/{number}/
 *                     (answer_url, answer_method=POST).
 *   Dial            : requires AnswerUrlBase. Composes answer_url = AnswerUrlBase
 *                     + [?|&]stream=<escaped streamUrl>. POST /v1/Account/{id}/Call/
 *                     (from,to,answer_url,answer_method=POST,ring_timeout
 *                     [,machine_detection=true]) -> request_uuid; session over
 *                     PendingMediaStream (Mulaw8000, Outbound).
 *   ListNumbers     : GET /v1/Account/{id}/Number/?limit=100 (empty on non-2xx).
 *   EndCall         : DELETE /v1/Account/{id}/Call/{uuid}/.
 *   Transfer        : POST /v1/Account/{id}/Call/{uuid}/ (aleg_url=data:xml Dial,
 *                     aleg_method=POST).
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#include "telephony.h"

#ifdef __cplusplus
extern "C" {
#endif

/* PlivoOptions. base_address defaults to "https://api.plivo.com" when NULL.
 * auth_id / auth_token may be NULL (fail-soft). answer_url_base is required to
 * dial (operations fail without it, mirroring the C#). */
typedef struct {
    const char *base_address;    /* borrowed; NULL -> default */
    const char *auth_id;         /* borrowed or NULL */
    const char *auth_token;      /* borrowed or NULL */
    const char *answer_url_base; /* borrowed or NULL */
} ca_tel_plivo_options_t;

/* Create a Plivo carrier over the injected HTTP transport + options. NULL on OOM.
 * Destroy with ca_tel_carrier_destroy. */
ca_tel_carrier_t *ca_tel_plivo_create(ca_tel_http_t http,
                                      const ca_tel_plivo_options_t *options);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TELEPHONY_PLIVO_H */
