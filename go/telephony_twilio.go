// telephony_twilio.go
//
// Ports CircleAI.Telephony.Twilio:
//   TwilioOptions       -> TwilioOptions
//   TwilioCarrier       -> TwilioCarrier (ITelephonyCarrier over an injected CarrierHTTP)
//   TwilioCallSession   -> constructed via the shared carrierCallSession + twilioSessionOps
//
// The C# carrier speaks the Twilio REST API (https://api.twilio.com/2010-04-01/
// Accounts/{Sid}/...) with HTTP Basic auth (AccountSid:AuthToken). Wire details
// are reproduced exactly: the AvailablePhoneNumbers search + IncomingPhoneNumbers
// reserve for provisioning, the VoiceUrl/VoiceMethod form for the inbound
// webhook, the inline <Connect><Stream/> TwiML + Calls.json form for dial, the
// PageSize=100 list, and the Calls/{Sid}.json redirect/complete for
// transfer/hang-up. The only substitution is HttpClient -> CarrierHTTP so no real
// call is placed.

package circleai

import (
	"context"
	"encoding/base64"
	"errors"
	"net/url"
	"time"
)

// TwilioOptions holds Twilio REST credentials + endpoint. Ports TwilioOptions.
// An empty AccountSid/AuthToken means fail-soft (IsConfigured=false).
type TwilioOptions struct {
	// BaseAddress — Twilio REST base. Default https://api.twilio.com.
	BaseAddress string
	// AccountSid — Twilio Account SID (starts with "AC...").
	AccountSid string
	// AuthToken — Twilio Auth Token.
	AuthToken string
}

// twilioDefaultBase is the C# default BaseAddress.
const twilioDefaultBase = "https://api.twilio.com"

// TwilioCarrier is an ITelephonyCarrier backed by Twilio's REST API over an
// injected CarrierHTTP. Ports TwilioCarrier. Fail-soft when credentials missing.
type TwilioCarrier struct {
	http    CarrierHTTP
	options TwilioOptions
	now     func() time.Time
	authHdr string // precomputed "Basic <base64>" when configured
	base    string
}

// NewTwilioCarrier constructs the carrier over an injected transport. http and a
// non-nil options are required (the C# constructor throws on null http/options).
// now defaults to time.Now.
func NewTwilioCarrier(http CarrierHTTP, options TwilioOptions, now func() time.Time) (*TwilioCarrier, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if now == nil {
		now = time.Now
	}
	base := options.BaseAddress
	if base == "" {
		base = twilioDefaultBase
	}
	c := &TwilioCarrier{http: http, options: options, now: now, base: base}
	if c.IsConfigured() {
		creds := base64.StdEncoding.EncodeToString([]byte(options.AccountSid + ":" + options.AuthToken))
		c.authHdr = "Basic " + creds
	}
	return c, nil
}

// CarrierID is "twilio".
func (c *TwilioCarrier) CarrierID() string { return "twilio" }

// IsConfigured is true when AccountSid and AuthToken are both non-blank.
func (c *TwilioCarrier) IsConfigured() bool {
	return stringsTrimSpaceNonEmpty(c.options.AccountSid) && stringsTrimSpaceNonEmpty(c.options.AuthToken)
}

// headers returns the request headers (auth when configured), optionally with a
// content type.
func (c *TwilioCarrier) headers(contentType string) map[string]string {
	h := map[string]string{}
	if c.authHdr != "" {
		h["Authorization"] = c.authHdr
	}
	if contentType != "" {
		h["Content-Type"] = contentType
	}
	return h
}

// ProvisionNumber ports ProvisionNumberAsync: search AvailablePhoneNumbers, take
// the first, reserve it via IncomingPhoneNumbers, return its metadata.
func (c *TwilioCarrier) ProvisionNumber(_ context.Context, countryCode, areaCode string) (ProvisionedNumber, error) {
	if err := c.ensureConfigured(); err != nil {
		return ProvisionedNumber{}, err
	}
	path := "/2010-04-01/Accounts/" + c.options.AccountSid + "/AvailablePhoneNumbers/" + countryCode + "/Local.json"
	if stringsTrimSpaceNonEmpty(areaCode) {
		path += "?AreaCode=" + escapeDataString(areaCode) + "&Limit=1"
	} else {
		path += "?Limit=1"
	}

	availResp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: c.headers("")})
	if err != nil {
		return ProvisionedNumber{}, err
	}
	if !carrierHTTPStatusOK(availResp.StatusCode) {
		return ProvisionedNumber{}, statusError("Twilio AvailablePhoneNumbers", availResp.StatusCode)
	}
	root, err := parseJSONObject(availResp.Body)
	if err != nil {
		return ProvisionedNumber{}, err
	}
	arr, _ := tjArray(root, "available_phone_numbers")
	if len(arr) == 0 {
		return ProvisionedNumber{}, errors.New("Twilio has no available numbers in country='" + countryCode + "', areaCode='" + areaCode + "'.")
	}
	first, _ := arr[0].(map[string]interface{})
	phoneNumber, _ := tjString(first, "phone_number")

	// Reserve on the account.
	reservePath := "/2010-04-01/Accounts/" + c.options.AccountSid + "/IncomingPhoneNumbers.json"
	form := formEncode(map[string]string{"PhoneNumber": phoneNumber})
	reserveResp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, reservePath),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(form),
	})
	if err != nil {
		return ProvisionedNumber{}, err
	}
	if !carrierHTTPStatusOK(reserveResp.StatusCode) {
		return ProvisionedNumber{}, statusError("Twilio IncomingPhoneNumbers reserve", reserveResp.StatusCode)
	}

	cost := ZeroDecimal
	if d, ok := tjDecimal(first, "price"); ok {
		cost = d
	}
	return ProvisionedNumber{
		PhoneNumber:          phoneNumber,
		CarrierID:            c.CarrierID(),
		ProvisionedAtUTC:     c.now().UTC(),
		MonthlyRecurringCost: cost,
	}, nil
}

// ConfigureInboundWebhook ports ConfigureInboundWebhookAsync: find the number's
// SID, then POST VoiceUrl/VoiceMethod to update it.
func (c *TwilioCarrier) ConfigureInboundWebhook(_ context.Context, phoneNumber string, inboundWebhook *url.URL) error {
	if err := c.ensureConfigured(); err != nil {
		return err
	}
	if inboundWebhook == nil {
		return errors.New("inboundWebhook is required")
	}
	listPath := "/2010-04-01/Accounts/" + c.options.AccountSid + "/IncomingPhoneNumbers.json?PhoneNumber=" + escapeDataString(phoneNumber)
	listResp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, listPath), Headers: c.headers("")})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(listResp.StatusCode) {
		return statusError("Twilio IncomingPhoneNumbers list", listResp.StatusCode)
	}
	root, err := parseJSONObject(listResp.Body)
	if err != nil {
		return err
	}
	arr, _ := tjArray(root, "incoming_phone_numbers")
	if len(arr) == 0 {
		return errors.New("Phone number '" + phoneNumber + "' is not owned on this Twilio account.")
	}
	entry, _ := arr[0].(map[string]interface{})
	sid, _ := tjString(entry, "sid")

	configPath := "/2010-04-01/Accounts/" + c.options.AccountSid + "/IncomingPhoneNumbers/" + sid + ".json"
	form := formEncode(map[string]string{
		"VoiceUrl":    inboundWebhook.String(),
		"VoiceMethod": "POST",
	})
	updateResp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, configPath),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(form),
	})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(updateResp.StatusCode) {
		return statusError("Twilio IncomingPhoneNumbers update", updateResp.StatusCode)
	}
	return nil
}

// Dial ports DialAsync: POST inline <Connect><Stream/> TwiML + From/To/Timeout
// (+ MachineDetection) to Calls.json, then wrap the returned CallSid in a session
// rooted on a PendingMediaStream (Mulaw8000).
func (c *TwilioCarrier) Dial(_ context.Context, fromNumber, toNumber string, streamURL *url.URL, options *OutboundDialOptions) (ICallSession, error) {
	if err := c.ensureConfigured(); err != nil {
		return nil, err
	}
	if streamURL == nil {
		return nil, errors.New("streamURL is required")
	}
	o := effectiveDialOptions(options)

	twiml := "<Response><Connect><Stream url='" + htmlEncode(streamURL.String()) + "'/></Connect></Response>"
	from := fromNumber
	if o.CallerIDOverride != "" {
		from = o.CallerIDOverride
	}
	fields := map[string]string{
		"From":    from,
		"To":      toNumber,
		"Twiml":   twiml,
		"Timeout": itoaSmall(o.RingTimeoutSeconds),
	}
	if o.DetectAnsweringMachine {
		fields["MachineDetection"] = "Enable"
	}
	callsPath := "/2010-04-01/Accounts/" + c.options.AccountSid + "/Calls.json"
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, callsPath),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(formEncode(fields)),
	})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Twilio Calls", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	callSid, _ := tjString(root, "sid")

	pending := NewPendingMediaStream(CallInfo{
		CallID:       callSid,
		Direction:    CallDirectionOutbound,
		From:         fromNumber,
		To:           toNumber,
		CarrierID:    c.CarrierID(),
		MediaFormat:  CallMediaFormatMulaw8000,
		StartedAtUTC: c.now().UTC(),
	})
	return newCarrierCallSession(pending, &twilioSessionOps{carrier: c}, warmTransferConfig{carrier: c}), nil
}

// ListNumbers ports ListNumbersAsync: GET IncomingPhoneNumbers?PageSize=100,
// fail-soft to empty on non-2xx.
func (c *TwilioCarrier) ListNumbers(_ context.Context) ([]ProvisionedNumber, error) {
	if !c.IsConfigured() {
		return []ProvisionedNumber{}, nil
	}
	path := "/2010-04-01/Accounts/" + c.options.AccountSid + "/IncomingPhoneNumbers.json?PageSize=100"
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: c.headers("")})
	if err != nil {
		return []ProvisionedNumber{}, nil
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return []ProvisionedNumber{}, nil
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return []ProvisionedNumber{}, nil
	}
	arr, ok := tjArray(root, "incoming_phone_numbers")
	if !ok {
		return []ProvisionedNumber{}, nil
	}
	list := make([]ProvisionedNumber, 0, len(arr))
	for _, item := range arr {
		obj, _ := item.(map[string]interface{})
		pn, _ := tjString(obj, "phone_number")
		list = append(list, ProvisionedNumber{
			PhoneNumber:          pn,
			CarrierID:            c.CarrierID(),
			ProvisionedAtUTC:     c.now().UTC(),
			MonthlyRecurringCost: ZeroDecimal,
		})
	}
	return list, nil
}

// redirectCall ports RedirectCallAsync: POST fresh TwiML to Calls/{Sid}.json.
// Fail-soft (logs in C#; here it returns the error to the caller which is the
// transfer path — matching that RedirectCall is awaited but its non-2xx only
// warns, the Go transfer treats a transport error as a failure and a non-2xx as
// a warning-swallow to preserve "best-effort reaches a human").
func (c *TwilioCarrier) redirectCall(_ context.Context, callSid, twiml string) error {
	if err := c.ensureConfigured(); err != nil {
		return err
	}
	path := "/2010-04-01/Accounts/" + c.options.AccountSid + "/Calls/" + callSid + ".json"
	_, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, path),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(formEncode(map[string]string{"Twiml": twiml})),
	})
	// C# only logs a warning on non-2xx; a transport error propagates.
	return err
}

// endCall ports EndCallAsync: POST Status=completed to Calls/{Sid}.json.
// Fail-soft: returns nil when unconfigured, swallows non-2xx (C# logs a warning),
// surfaces only transport errors.
func (c *TwilioCarrier) endCall(_ context.Context, callSid string) error {
	if !c.IsConfigured() {
		return nil
	}
	path := "/2010-04-01/Accounts/" + c.options.AccountSid + "/Calls/" + callSid + ".json"
	_, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, path),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(formEncode(map[string]string{"Status": "completed"})),
	})
	return err
}

func (c *TwilioCarrier) ensureConfigured() error {
	if !c.IsConfigured() {
		return errors.New("Twilio carrier is not configured. Set TwilioOptions.AccountSid and AuthToken before calling REST operations.")
	}
	return nil
}

// twilioSessionOps satisfies carrierSessionOps for Twilio: cold transfer redirects
// to <Dial> TwiML, hang-up completes the call. Ports the TwilioCallSession
// divergent bodies.
type twilioSessionOps struct {
	carrier *TwilioCarrier
}

// endCall terminates the call via the Twilio REST API.
func (o *twilioSessionOps) endCall(ctx context.Context, callID string) error {
	return o.carrier.endCall(ctx, callID)
}

// coldTransfer redirects the call to <Dial>{target}</Dial> TwiML (target HTML-encoded).
func (o *twilioSessionOps) coldTransfer(ctx context.Context, callID, targetNumber string) error {
	twiml := "<Response><Dial>" + htmlEncode(targetNumber) + "</Dial></Response>"
	return o.carrier.redirectCall(ctx, callID, twiml)
}

var (
	_ ITelephonyCarrier = (*TwilioCarrier)(nil)
	_ carrierSessionOps = (*twilioSessionOps)(nil)
)
