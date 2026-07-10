// telephony_telnyx.go
//
// Ports CircleAI.Telephony.Telnyx:
//   TelnyxOptions      -> TelnyxOptions
//   TelnyxCarrier      -> TelnyxCarrier (ITelephonyCarrier over an injected CarrierHTTP)
//   TelnyxCallSession  -> constructed via the shared carrierCallSession + telnyxSessionOps
//
// The C# carrier speaks Telnyx v2 with Bearer auth and JSON bodies: the
// available_phone_numbers search + number_orders purchase for provisioning, the
// PATCH call_control_applications + PATCH phone_numbers for the inbound webhook,
// the /v2/calls dial (connection_id / to / from / stream_url / stream_track /
// timeout_secs [+ answering_machine_detection]), the /v2/phone_numbers?page[size]
// list, and the calls/{id}/actions/hangup + /transfer control actions. The JSON
// body strings are byte-reproduced (field order preserved) so the wire matches.
// HttpClient -> CarrierHTTP is the only substitution.

package circleai

import (
	"context"
	"errors"
	"net/url"
	"time"
)

// TelnyxOptions holds Telnyx v2 credentials + Call Control app id. Ports
// TelnyxOptions. Empty ApiKey => fail-soft.
type TelnyxOptions struct {
	// BaseAddress — Telnyx v2 base. Default https://api.telnyx.com.
	BaseAddress string
	// ApiKey — Telnyx v2 API key (Bearer).
	ApiKey string
	// CallControlConnectionId — Call Control Application id used as the outbound
	// connection and inbound webhook owner. Required to dial / configure inbound.
	CallControlConnectionId string
}

const telnyxDefaultBase = "https://api.telnyx.com"

// TelnyxCarrier is an ITelephonyCarrier backed by Telnyx v2 over an injected
// CarrierHTTP. Ports TelnyxCarrier. Fail-soft when the ApiKey is missing.
type TelnyxCarrier struct {
	http    CarrierHTTP
	options TelnyxOptions
	now     func() time.Time
	authHdr string
	base    string
}

// NewTelnyxCarrier constructs the carrier over an injected transport. http is
// required; now defaults to time.Now.
func NewTelnyxCarrier(http CarrierHTTP, options TelnyxOptions, now func() time.Time) (*TelnyxCarrier, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if now == nil {
		now = time.Now
	}
	base := options.BaseAddress
	if base == "" {
		base = telnyxDefaultBase
	}
	c := &TelnyxCarrier{http: http, options: options, now: now, base: base}
	if c.IsConfigured() {
		c.authHdr = "Bearer " + options.ApiKey
	}
	return c, nil
}

// CarrierID is "telnyx".
func (c *TelnyxCarrier) CarrierID() string { return "telnyx" }

// IsConfigured is true when ApiKey is non-blank.
func (c *TelnyxCarrier) IsConfigured() bool { return stringsTrimSpaceNonEmpty(c.options.ApiKey) }

func (c *TelnyxCarrier) headers(contentType string) map[string]string {
	h := map[string]string{}
	if c.authHdr != "" {
		h["Authorization"] = c.authHdr
	}
	if contentType != "" {
		h["Content-Type"] = contentType
	}
	return h
}

// ProvisionNumber ports ProvisionNumberAsync: search availability, take the
// first, place a number order, return its metadata.
func (c *TelnyxCarrier) ProvisionNumber(_ context.Context, countryCode, areaCode string) (ProvisionedNumber, error) {
	if err := c.ensureConfigured(); err != nil {
		return ProvisionedNumber{}, err
	}
	searchPath := "/v2/available_phone_numbers?filter[country_code]=" + countryCode + "&filter[limit]=1"
	if stringsTrimSpaceNonEmpty(areaCode) {
		searchPath += "&filter[national_destination_code]=" + escapeDataString(areaCode)
	}
	searchResp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, searchPath), Headers: c.headers("")})
	if err != nil {
		return ProvisionedNumber{}, err
	}
	if !carrierHTTPStatusOK(searchResp.StatusCode) {
		return ProvisionedNumber{}, statusError("Telnyx available_phone_numbers", searchResp.StatusCode)
	}
	root, err := parseJSONObject(searchResp.Body)
	if err != nil {
		return ProvisionedNumber{}, err
	}
	arr, _ := tjArray(root, "data")
	if len(arr) == 0 {
		return ProvisionedNumber{}, errors.New("Telnyx has no available numbers in country='" + countryCode + "', areaCode='" + areaCode + "'.")
	}
	first, _ := arr[0].(map[string]interface{})
	phoneNumber, _ := tjString(first, "phone_number")

	// Place a Number Order — body byte-reproduced from the C# raw string.
	orderBody := `{"phone_numbers":[{"phone_number":"` + phoneNumber + `"}]}`
	orderResp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "/v2/number_orders"),
		Headers: c.headers("application/json"),
		Body:    []byte(orderBody),
	})
	if err != nil {
		return ProvisionedNumber{}, err
	}
	if !carrierHTTPStatusOK(orderResp.StatusCode) {
		return ProvisionedNumber{}, statusError("Telnyx number_orders", orderResp.StatusCode)
	}

	cost := ZeroDecimal
	if d, ok := telnyxMonthlyCost(first); ok {
		cost = d
	}
	return ProvisionedNumber{
		PhoneNumber:          phoneNumber,
		CarrierID:            c.CarrierID(),
		ProvisionedAtUTC:     c.now().UTC(),
		MonthlyRecurringCost: cost,
	}, nil
}

// ConfigureInboundWebhook ports ConfigureInboundWebhookAsync: PATCH the Call
// Control Application webhook URL, then PATCH the number's connection assignment
// (assignment non-2xx only warns).
func (c *TelnyxCarrier) ConfigureInboundWebhook(_ context.Context, phoneNumber string, inboundWebhook *url.URL) error {
	if err := c.ensureConfigured(); err != nil {
		return err
	}
	if !stringsTrimSpaceNonEmpty(c.options.CallControlConnectionId) {
		return errors.New("Telnyx ConfigureInboundWebhook requires CallControlConnectionId on TelnyxOptions.")
	}
	if inboundWebhook == nil {
		return errors.New("inboundWebhook is required")
	}
	appPath := "/v2/call_control_applications/" + c.options.CallControlConnectionId
	appBody := `{"webhook_event_url":"` + inboundWebhook.String() + `"}`
	appResp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "PATCH",
		URL:     joinBaseAndPath(c.base, appPath),
		Headers: c.headers("application/json"),
		Body:    []byte(appBody),
	})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(appResp.StatusCode) {
		return statusError("Telnyx call_control_applications", appResp.StatusCode)
	}

	assignPath := "/v2/phone_numbers/" + escapeDataString(phoneNumber)
	assignBody := `{"connection_id":"` + c.options.CallControlConnectionId + `"}`
	// A non-2xx here only warns in C# (may already be assigned); a transport
	// error still propagates.
	_, err = c.http.Do(&CarrierHTTPRequest{
		Method:  "PATCH",
		URL:     joinBaseAndPath(c.base, assignPath),
		Headers: c.headers("application/json"),
		Body:    []byte(assignBody),
	})
	return err
}

// Dial ports DialAsync: POST the call-control JSON to /v2/calls, wrap the
// returned data.call_control_id in a session on a PendingMediaStream (Pcm16000).
func (c *TelnyxCarrier) Dial(_ context.Context, fromNumber, toNumber string, streamURL *url.URL, options *OutboundDialOptions) (ICallSession, error) {
	if err := c.ensureConfigured(); err != nil {
		return nil, err
	}
	if !stringsTrimSpaceNonEmpty(c.options.CallControlConnectionId) {
		return nil, errors.New("Telnyx DialAsync requires CallControlConnectionId on TelnyxOptions.")
	}
	if streamURL == nil {
		return nil, errors.New("streamURL is required")
	}
	o := effectiveDialOptions(options)
	from := fromNumber
	if o.CallerIDOverride != "" {
		from = o.CallerIDOverride
	}
	// Body assembled field-by-field in the same order as the C# StringBuilder.
	body := "{" +
		`"connection_id":"` + c.options.CallControlConnectionId + `",` +
		`"to":"` + toNumber + `",` +
		`"from":"` + from + `",` +
		`"stream_url":"` + streamURL.String() + `",` +
		`"stream_track":"both_tracks",` +
		`"timeout_secs":` + itoaSmall(o.RingTimeoutSeconds)
	if o.DetectAnsweringMachine {
		body += `,"answering_machine_detection":"detect"`
	}
	body += "}"

	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "/v2/calls"),
		Headers: c.headers("application/json"),
		Body:    []byte(body),
	})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Telnyx calls", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	data, _ := tjObject(root, "data")
	callControlID, _ := tjString(data, "call_control_id")

	pending := NewPendingMediaStream(CallInfo{
		CallID:       callControlID,
		Direction:    CallDirectionOutbound,
		From:         fromNumber,
		To:           toNumber,
		CarrierID:    c.CarrierID(),
		MediaFormat:  CallMediaFormatPcm16000,
		StartedAtUTC: c.now().UTC(),
	})
	return newCarrierCallSession(pending, &telnyxSessionOps{carrier: c}, warmTransferConfig{carrier: c}), nil
}

// ListNumbers ports ListNumbersAsync: GET /v2/phone_numbers?page[size]=100,
// fail-soft to empty on non-2xx.
func (c *TelnyxCarrier) ListNumbers(_ context.Context) ([]ProvisionedNumber, error) {
	if !c.IsConfigured() {
		return []ProvisionedNumber{}, nil
	}
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, "/v2/phone_numbers?page[size]=100"), Headers: c.headers("")})
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
	arr, ok := tjArray(root, "data")
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

// endCall ports EndCallAsync: POST {} to calls/{id}/actions/hangup. Fail-soft:
// nil when unconfigured, non-2xx only warns, transport errors propagate.
func (c *TelnyxCarrier) endCall(_ context.Context, callControlID string) error {
	if !c.IsConfigured() {
		return nil
	}
	_, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "/v2/calls/"+callControlID+"/actions/hangup"),
		Headers: c.headers("application/json"),
		Body:    []byte("{}"),
	})
	return err
}

// transferCall ports TransferCallAsync: POST {"to":target} to
// calls/{id}/actions/transfer. Non-2xx only warns.
func (c *TelnyxCarrier) transferCall(_ context.Context, callControlID, targetNumber string) error {
	if err := c.ensureConfigured(); err != nil {
		return err
	}
	body := `{"to":"` + targetNumber + `"}`
	_, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "/v2/calls/"+callControlID+"/actions/transfer"),
		Headers: c.headers("application/json"),
		Body:    []byte(body),
	})
	return err
}

func (c *TelnyxCarrier) ensureConfigured() error {
	if !c.IsConfigured() {
		return errors.New("Telnyx carrier is not configured. Set TelnyxOptions.ApiKey before calling REST operations.")
	}
	return nil
}

// telnyxMonthlyCost ports ParseMonthlyCost: cost_information.monthly_cost as a
// number or a string-parsed decimal.
func telnyxMonthlyCost(first map[string]interface{}) (Decimal, bool) {
	cost, ok := tjObject(first, "cost_information")
	if !ok {
		return ZeroDecimal, false
	}
	return tjDecimal(cost, "monthly_cost")
}

// telnyxSessionOps satisfies carrierSessionOps for Telnyx. Ports the
// TelnyxCallSession divergent bodies (transfer action / hangup action).
type telnyxSessionOps struct {
	carrier *TelnyxCarrier
}

func (o *telnyxSessionOps) endCall(ctx context.Context, callID string) error {
	return o.carrier.endCall(ctx, callID)
}

func (o *telnyxSessionOps) coldTransfer(ctx context.Context, callID, targetNumber string) error {
	return o.carrier.transferCall(ctx, callID, targetNumber)
}

var (
	_ ITelephonyCarrier = (*TelnyxCarrier)(nil)
	_ carrierSessionOps = (*telnyxSessionOps)(nil)
)
