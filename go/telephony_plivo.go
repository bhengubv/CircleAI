// telephony_plivo.go
//
// Ports CircleAI.Telephony.Plivo:
//   PlivoOptions       -> PlivoOptions
//   PlivoCarrier       -> PlivoCarrier (ITelephonyCarrier over an injected CarrierHTTP)
//   PlivoCallSession   -> constructed via the shared carrierCallSession + plivoSessionOps
//
// The C# carrier speaks Plivo v1 with Basic auth (AuthId:AuthToken) and form
// bodies under /v1/Account/{AuthId}/: the PhoneNumber search + buy for
// provisioning, the Number/{n}/ answer_url update for the inbound webhook, the
// AnswerUrlBase-with-?stream= composition + Call/ dial, the Number/?limit=100
// list, the DELETE Call/{uuid}/ hangup, and the aleg_url replay transfer. The
// AnswerUrl query composition and the data:application/xml transfer payload are
// reproduced exactly. HttpClient -> CarrierHTTP is the only substitution.

package circleai

import (
	"context"
	"encoding/base64"
	"errors"
	"net/url"
	"strings"
	"time"
)

// PlivoOptions holds Plivo v1 credentials + AnswerUrl base. Ports PlivoOptions.
// Empty AuthId/AuthToken => fail-soft.
type PlivoOptions struct {
	// BaseAddress — Plivo v1 base. Default https://api.plivo.com.
	BaseAddress string
	// AuthId — Plivo Auth ID.
	AuthId string
	// AuthToken — Plivo Auth Token.
	AuthToken string
	// AnswerUrlBase — HTTPS URL the host serves that, given ?stream=<wss url>,
	// returns Plivo XML with the matching <Stream/> verb. Required to dial.
	AnswerUrlBase string
}

const plivoDefaultBase = "https://api.plivo.com"

// PlivoCarrier is an ITelephonyCarrier backed by Plivo v1 over an injected
// CarrierHTTP. Ports PlivoCarrier. Fail-soft when credentials missing.
type PlivoCarrier struct {
	http    CarrierHTTP
	options PlivoOptions
	now     func() time.Time
	authHdr string
	base    string
}

// NewPlivoCarrier constructs the carrier over an injected transport. http is
// required; now defaults to time.Now.
func NewPlivoCarrier(http CarrierHTTP, options PlivoOptions, now func() time.Time) (*PlivoCarrier, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if now == nil {
		now = time.Now
	}
	base := options.BaseAddress
	if base == "" {
		base = plivoDefaultBase
	}
	c := &PlivoCarrier{http: http, options: options, now: now, base: base}
	if c.IsConfigured() {
		creds := base64.StdEncoding.EncodeToString([]byte(options.AuthId + ":" + options.AuthToken))
		c.authHdr = "Basic " + creds
	}
	return c, nil
}

// CarrierID is "plivo".
func (c *PlivoCarrier) CarrierID() string { return "plivo" }

// IsConfigured is true when AuthId and AuthToken are both non-blank.
func (c *PlivoCarrier) IsConfigured() bool {
	return stringsTrimSpaceNonEmpty(c.options.AuthId) && stringsTrimSpaceNonEmpty(c.options.AuthToken)
}

func (c *PlivoCarrier) headers(contentType string) map[string]string {
	h := map[string]string{}
	if c.authHdr != "" {
		h["Authorization"] = c.authHdr
	}
	if contentType != "" {
		h["Content-Type"] = contentType
	}
	return h
}

// ProvisionNumber ports ProvisionNumberAsync: GET PhoneNumber/ search, take the
// first objects[].number, POST PhoneNumber/{n}/ to buy, return metadata.
func (c *PlivoCarrier) ProvisionNumber(_ context.Context, countryCode, areaCode string) (ProvisionedNumber, error) {
	if err := c.ensureConfigured(); err != nil {
		return ProvisionedNumber{}, err
	}
	path := "/v1/Account/" + c.options.AuthId + "/PhoneNumber/?country_iso=" + countryCode + "&limit=1"
	if stringsTrimSpaceNonEmpty(areaCode) {
		path += "&pattern=" + escapeDataString(areaCode)
	}
	searchResp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: c.headers("")})
	if err != nil {
		return ProvisionedNumber{}, err
	}
	if !carrierHTTPStatusOK(searchResp.StatusCode) {
		return ProvisionedNumber{}, statusError("Plivo PhoneNumber search", searchResp.StatusCode)
	}
	root, err := parseJSONObject(searchResp.Body)
	if err != nil {
		return ProvisionedNumber{}, err
	}
	arr, _ := tjArray(root, "objects")
	if len(arr) == 0 {
		return ProvisionedNumber{}, errors.New("Plivo has no available numbers in country='" + countryCode + "', areaCode='" + areaCode + "'.")
	}
	first, _ := arr[0].(map[string]interface{})
	phoneNumber, _ := tjString(first, "number")

	buyPath := "/v1/Account/" + c.options.AuthId + "/PhoneNumber/" + phoneNumber + "/"
	buyForm := formEncode(map[string]string{"app_id": ""})
	buyResp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, buyPath),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(buyForm),
	})
	if err != nil {
		return ProvisionedNumber{}, err
	}
	if !carrierHTTPStatusOK(buyResp.StatusCode) {
		return ProvisionedNumber{}, statusError("Plivo PhoneNumber buy", buyResp.StatusCode)
	}

	cost := ZeroDecimal
	if d, ok := tjDecimal(first, "monthly_rental_rate"); ok {
		cost = d
	}
	return ProvisionedNumber{
		PhoneNumber:          phoneNumber,
		CarrierID:            c.CarrierID(),
		ProvisionedAtUTC:     c.now().UTC(),
		MonthlyRecurringCost: cost,
	}, nil
}

// ConfigureInboundWebhook ports ConfigureInboundWebhookAsync: POST answer_url /
// answer_method to Number/{n}/.
func (c *PlivoCarrier) ConfigureInboundWebhook(_ context.Context, phoneNumber string, inboundWebhook *url.URL) error {
	if err := c.ensureConfigured(); err != nil {
		return err
	}
	if inboundWebhook == nil {
		return errors.New("inboundWebhook is required")
	}
	path := "/v1/Account/" + c.options.AuthId + "/Number/" + phoneNumber + "/"
	form := formEncode(map[string]string{
		"answer_url":    inboundWebhook.String(),
		"answer_method": "POST",
	})
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, path),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(form),
	})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("Plivo Number update", resp.StatusCode)
	}
	return nil
}

// Dial ports DialAsync: compose the answer URL with ?stream=<encoded wss>, POST
// from/to/answer_url/answer_method/ring_timeout (+machine_detection) to Call/,
// wrap request_uuid in a session on a PendingMediaStream (Mulaw8000).
func (c *PlivoCarrier) Dial(_ context.Context, fromNumber, toNumber string, streamURL *url.URL, options *OutboundDialOptions) (ICallSession, error) {
	if err := c.ensureConfigured(); err != nil {
		return nil, err
	}
	if !stringsTrimSpaceNonEmpty(c.options.AnswerUrlBase) {
		return nil, errors.New("Plivo DialAsync requires PlivoOptions.AnswerUrlBase. The host must serve XML containing a <Stream/> verb pointing to the streamUrl.")
	}
	if streamURL == nil {
		return nil, errors.New("streamURL is required")
	}
	o := effectiveDialOptions(options)

	answerURL, err := composePlivoAnswerURL(c.options.AnswerUrlBase, streamURL.String())
	if err != nil {
		return nil, err
	}
	from := fromNumber
	if o.CallerIDOverride != "" {
		from = o.CallerIDOverride
	}
	fields := map[string]string{
		"from":          from,
		"to":            toNumber,
		"answer_url":    answerURL,
		"answer_method": "POST",
		"ring_timeout":  itoaSmall(o.RingTimeoutSeconds),
	}
	if o.DetectAnsweringMachine {
		fields["machine_detection"] = "true"
	}
	path := "/v1/Account/" + c.options.AuthId + "/Call/"
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, path),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(formEncode(fields)),
	})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Plivo Call", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	requestUUID, _ := tjString(root, "request_uuid")

	pending := NewPendingMediaStream(CallInfo{
		CallID:       requestUUID,
		Direction:    CallDirectionOutbound,
		From:         fromNumber,
		To:           toNumber,
		CarrierID:    c.CarrierID(),
		MediaFormat:  CallMediaFormatMulaw8000,
		StartedAtUTC: c.now().UTC(),
	})
	return newCarrierCallSession(pending, &plivoSessionOps{carrier: c}, warmTransferConfig{carrier: c}), nil
}

// ListNumbers ports ListNumbersAsync: GET Number/?limit=100, fail-soft to empty
// on non-2xx.
func (c *PlivoCarrier) ListNumbers(_ context.Context) ([]ProvisionedNumber, error) {
	if !c.IsConfigured() {
		return []ProvisionedNumber{}, nil
	}
	path := "/v1/Account/" + c.options.AuthId + "/Number/?limit=100"
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
	arr, ok := tjArray(root, "objects")
	if !ok {
		return []ProvisionedNumber{}, nil
	}
	list := make([]ProvisionedNumber, 0, len(arr))
	for _, item := range arr {
		obj, _ := item.(map[string]interface{})
		pn, _ := tjString(obj, "number")
		list = append(list, ProvisionedNumber{
			PhoneNumber:          pn,
			CarrierID:            c.CarrierID(),
			ProvisionedAtUTC:     c.now().UTC(),
			MonthlyRecurringCost: ZeroDecimal,
		})
	}
	return list, nil
}

// endCall ports EndCallAsync: DELETE Call/{uuid}/. Fail-soft: nil when
// unconfigured, non-2xx only warns, transport errors propagate.
func (c *PlivoCarrier) endCall(_ context.Context, callUUID string) error {
	if !c.IsConfigured() {
		return nil
	}
	_, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "DELETE",
		URL:     joinBaseAndPath(c.base, "/v1/Account/"+c.options.AuthId+"/Call/"+callUUID+"/"),
		Headers: c.headers(""),
	})
	return err
}

// transferCall ports TransferCallAsync: POST aleg_url (a data:application/xml
// <Dial><Number>) + aleg_method to Call/{uuid}/. Non-2xx only warns.
func (c *PlivoCarrier) transferCall(_ context.Context, callUUID, targetNumber string) error {
	if err := c.ensureConfigured(); err != nil {
		return err
	}
	xml := "<Response><Dial><Number>" + targetNumber + "</Number></Dial></Response>"
	fields := map[string]string{
		"aleg_url":    "data:application/xml," + escapeDataString(xml),
		"aleg_method": "POST",
	}
	_, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "/v1/Account/"+c.options.AuthId+"/Call/"+callUUID+"/"),
		Headers: c.headers("application/x-www-form-urlencoded"),
		Body:    []byte(formEncode(fields)),
	})
	return err
}

func (c *PlivoCarrier) ensureConfigured() error {
	if !c.IsConfigured() {
		return errors.New("Plivo carrier is not configured. Set PlivoOptions.AuthId and AuthToken before calling REST operations.")
	}
	return nil
}

// composePlivoAnswerURL ports the C# UriBuilder composition: append
// "stream=<escaped streamUrl>" to the existing query with '&' when a query is
// already present, else set it as the sole query parameter.
func composePlivoAnswerURL(answerBase, streamURL string) (string, error) {
	u, err := url.Parse(answerBase)
	if err != nil {
		return "", err
	}
	existing := strings.TrimPrefix(u.RawQuery, "?")
	sep := ""
	if existing != "" {
		sep = "&"
	}
	u.RawQuery = existing + sep + "stream=" + escapeDataString(streamURL)
	return u.String(), nil
}

// plivoSessionOps satisfies carrierSessionOps for Plivo. Ports the
// PlivoCallSession divergent bodies (transfer replay / DELETE hangup).
type plivoSessionOps struct {
	carrier *PlivoCarrier
}

func (o *plivoSessionOps) endCall(ctx context.Context, callID string) error {
	return o.carrier.endCall(ctx, callID)
}

func (o *plivoSessionOps) coldTransfer(ctx context.Context, callID, targetNumber string) error {
	return o.carrier.transferCall(ctx, callID, targetNumber)
}

var (
	_ ITelephonyCarrier = (*PlivoCarrier)(nil)
	_ carrierSessionOps = (*plivoSessionOps)(nil)
)
