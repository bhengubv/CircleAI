#ifndef CIRCLE_AI_TELEPHONY_TELNYX_H
#define CIRCLE_AI_TELEPHONY_TELNYX_H

/*
 * telephony_telnyx.h — CircleAI.Telephony.Telnyx (C11 port).
 *
 * Ports the Telnyx v2 ITelephonyCarrier binding (TelnyxCarrier.cs +
 * TelnyxCallSession.cs + TelnyxOptions.cs). Bearer-token auth, the /v2 namespace,
 * JSON request bodies, and the Call Control action surface. NO real network —
 * every request goes through the injected ca_tel_http_t transport.
 *
 *   CarrierId "telnyx"; IsConfigured := ApiKey non-blank.
 *   ProvisionNumber : GET /v2/available_phone_numbers?filter[country_code]=&
 *                     filter[limit]=1[&filter[national_destination_code]=] ->
 *                     data[0].phone_number, then POST /v2/number_orders
 *                     {"phone_numbers":[{"phone_number":".."}]}. Cost from
 *                     data[0].cost_information.monthly_cost.
 *   ConfigureInbound: requires CallControlConnectionId. PATCH
 *                     /v2/call_control_applications/{id} {"webhook_event_url":..},
 *                     then PATCH /v2/phone_numbers/{number} {"connection_id":..}.
 *   Dial            : requires CallControlConnectionId. POST /v2/calls
 *                     {connection_id,to,from,stream_url,stream_track:"both_tracks",
 *                     timeout_secs[,answering_machine_detection:"detect"]} ->
 *                     data.call_control_id; session over PendingMediaStream
 *                     (Pcm16000, Outbound).
 *   ListNumbers     : GET /v2/phone_numbers?page[size]=100 (empty on non-2xx).
 *   EndCall         : POST /v2/calls/{id}/actions/hangup {}.
 *   Transfer        : POST /v2/calls/{id}/actions/transfer {"to":".."}.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#include "telephony.h"

#ifdef __cplusplus
extern "C" {
#endif

/* TelnyxOptions. base_address defaults to "https://api.telnyx.com" when NULL.
 * api_key may be NULL (fail-soft). call_control_connection_id is required to dial
 * / configure inbound (operations fail without it, mirroring the C#). */
typedef struct {
    const char *base_address;               /* borrowed; NULL -> default */
    const char *api_key;                    /* borrowed or NULL */
    const char *call_control_connection_id; /* borrowed or NULL */
} ca_tel_telnyx_options_t;

/* Create a Telnyx carrier over the injected HTTP transport + options. NULL on
 * OOM. Destroy with ca_tel_carrier_destroy. */
ca_tel_carrier_t *ca_tel_telnyx_create(ca_tel_http_t http,
                                       const ca_tel_telnyx_options_t *options);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TELEPHONY_TELNYX_H */
