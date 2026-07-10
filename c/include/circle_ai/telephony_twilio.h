#ifndef CIRCLE_AI_TELEPHONY_TWILIO_H
#define CIRCLE_AI_TELEPHONY_TWILIO_H

/*
 * telephony_twilio.h — CircleAI.Telephony.Twilio (C11 port).
 *
 * Ports the Twilio ITelephonyCarrier binding (TwilioCarrier.cs +
 * TwilioCallSession.cs + TwilioOptions.cs). The REST surface talks to
 * https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/... with HTTP Basic
 * auth (AccountSid:AuthToken). NO real network — every request is issued through
 * the injected ca_tel_http_t transport, so the exact path + form/TwiML body + auth
 * header the C# adapter would produce are all exercised deterministically.
 *
 *   CarrierId "twilio"; IsConfigured := AccountSid && AuthToken both non-blank.
 *   ProvisionNumber : GET AvailablePhoneNumbers/{cc}/Local.json[?AreaCode=&Limit=1]
 *                     -> first.phone_number, then POST IncomingPhoneNumbers.json
 *                     (PhoneNumber=...). Cost from first.price.
 *   ConfigureInbound: GET IncomingPhoneNumbers.json?PhoneNumber= -> sid, then
 *                     POST IncomingPhoneNumbers/{sid}.json (VoiceUrl,VoiceMethod).
 *   Dial            : POST Calls.json (From,To,Twiml=<Connect><Stream/>,Timeout
 *                     [,MachineDetection]) -> sid; session over PendingMediaStream
 *                     (Mulaw8000, Outbound).
 *   ListNumbers     : GET IncomingPhoneNumbers.json?PageSize=100 (empty on non-2xx).
 *   EndCall         : POST Calls/{sid}.json (Status=completed).
 *   Transfer (cold) : POST Calls/{sid}.json (Twiml=<Dial>target</Dial>).
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#include "telephony.h"

#ifdef __cplusplus
extern "C" {
#endif

/* TwilioOptions. base_address defaults to "https://api.twilio.com" when NULL.
 * account_sid / auth_token may be NULL (fail-soft: IsConfigured=false). */
typedef struct {
    const char *base_address;   /* borrowed; NULL -> default */
    const char *account_sid;    /* borrowed or NULL */
    const char *auth_token;     /* borrowed or NULL */
} ca_tel_twilio_options_t;

/* Create a Twilio carrier over the injected HTTP transport + options. Copies the
 * option strings it needs. NULL on OOM. The returned handle owns its binding
 * state; destroy with ca_tel_carrier_destroy. */
ca_tel_carrier_t *ca_tel_twilio_create(ca_tel_http_t http,
                                       const ca_tel_twilio_options_t *options);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TELEPHONY_TWILIO_H */
