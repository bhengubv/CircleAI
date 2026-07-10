// telephony_carriers_test.go
//
// Verifies the three real carrier bindings (CircleAI.Telephony.Twilio/.Telnyx/
// .Plivo) over the injected FakeCarrierTransport — no real calls. Each test
// drives the carrier's REST flow and asserts the wire details (method, URL,
// auth header, body) and the parsed results, matching the C# adapters.

package circleai_test

import (
	"context"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Twilio
// ---------------------------------------------------------------------------

func TestTwilioCarrier_ConfiguredAndAuth(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	c, err := circleai.NewTwilioCarrier(tr, circleai.TwilioOptions{AccountSid: "AC123", AuthToken: "tok"}, telephonyClock())
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if c.CarrierID() != "twilio" || !c.IsConfigured() {
		t.Fatal("carrier id/configured wrong")
	}

	unconfigured, _ := circleai.NewTwilioCarrier(tr, circleai.TwilioOptions{}, nil)
	if unconfigured.IsConfigured() {
		t.Error("empty creds should be unconfigured")
	}
	// Unconfigured provision/dial error.
	if _, err := unconfigured.ProvisionNumber(context.Background(), "ZA", ""); err == nil {
		t.Error("unconfigured provision should error")
	}
}

func TestTwilioCarrier_ProvisionNumber(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	// 1) availability search, 2) reserve.
	tr.EnqueueJSON(200, `{"available_phone_numbers":[{"phone_number":"+27215550100","price":"1.15"}]}`)
	tr.EnqueueJSON(201, `{"sid":"PN1"}`)
	c, _ := circleai.NewTwilioCarrier(tr, circleai.TwilioOptions{AccountSid: "AC1", AuthToken: "tok"}, telephonyClock())

	pn, err := c.ProvisionNumber(ctx, "ZA", "21")
	if err != nil {
		t.Fatalf("provision: %v", err)
	}
	if pn.PhoneNumber != "+27215550100" || pn.CarrierID != "twilio" || pn.MonthlyRecurringCost.String() != "1.15" {
		t.Errorf("provisioned = %+v", pn)
	}

	reqs := tr.Requests()
	if len(reqs) != 2 {
		t.Fatalf("expected 2 requests, got %d", len(reqs))
	}
	// First request: GET the AvailablePhoneNumbers with AreaCode + Basic auth.
	if reqs[0].Method != "GET" ||
		!strings.Contains(reqs[0].URL, "/2010-04-01/Accounts/AC1/AvailablePhoneNumbers/ZA/Local.json") ||
		!strings.Contains(reqs[0].URL, "AreaCode=21") {
		t.Errorf("availability request wrong: %s %s", reqs[0].Method, reqs[0].URL)
	}
	if !strings.HasPrefix(reqs[0].Headers["Authorization"], "Basic ") {
		t.Errorf("missing Basic auth: %q", reqs[0].Headers["Authorization"])
	}
	// Second: POST reserve with PhoneNumber form field.
	if reqs[1].Method != "POST" || !strings.Contains(string(reqs[1].Body), "PhoneNumber=") {
		t.Errorf("reserve request wrong: %s %s", reqs[1].Method, reqs[1].Body)
	}
}

func TestTwilioCarrier_ProvisionNoNumbers(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"available_phone_numbers":[]}`)
	c, _ := circleai.NewTwilioCarrier(tr, circleai.TwilioOptions{AccountSid: "AC1", AuthToken: "tok"}, telephonyClock())
	if _, err := c.ProvisionNumber(ctx, "ZA", ""); err == nil || !strings.Contains(err.Error(), "no available numbers") {
		t.Errorf("expected no-available-numbers error, got %v", err)
	}
}

func TestTwilioCarrier_DialAndTransferHangup(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(201, `{"sid":"CA9"}`) // dial
	tr.EnqueueStatus(200)                // redirect (transfer)
	tr.EnqueueStatus(200)                // end (hangup)
	c, _ := circleai.NewTwilioCarrier(tr, circleai.TwilioOptions{AccountSid: "AC1", AuthToken: "tok"}, telephonyClock())

	sess, err := c.Dial(ctx, "+27000000001", "+27000000002", mustURL(t, "wss://host/stream"), &circleai.OutboundDialOptions{DetectAnsweringMachine: true, RingTimeoutSeconds: 20})
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	if sess.Info().CallID != "CA9" || sess.Info().MediaFormat != circleai.CallMediaFormatMulaw8000 {
		t.Errorf("dial info = %+v", sess.Info())
	}
	dialReq, _ := tr.LastRequest()
	body := string(dialReq.Body)
	// TwiML stream + Connect, machine detection, timeout in the form body.
	if !strings.Contains(body, "Twiml=") || !strings.Contains(body, "MachineDetection=Enable") || !strings.Contains(body, "Timeout=20") {
		t.Errorf("dial body missing fields: %s", body)
	}

	// Cold transfer -> redirect call; status Transferred.
	if err := sess.Transfer(ctx, "+15551230000", circleai.TransferModeCold, ""); err != nil {
		t.Fatalf("transfer: %v", err)
	}
	if sess.Status() != circleai.CallStatusTransferred {
		t.Errorf("status after transfer = %v", sess.Status())
	}
	transferReq, _ := tr.LastRequest()
	if !strings.Contains(string(transferReq.Body), "Twiml=") || !strings.Contains(string(transferReq.Body), "Dial") {
		t.Errorf("transfer TwiML missing Dial: %s", transferReq.Body)
	}

	// Hang up -> POST Status=completed.
	if err := sess.HangUp(ctx); err != nil {
		t.Fatalf("hangup: %v", err)
	}
	hangReq, _ := tr.LastRequest()
	if !strings.Contains(string(hangReq.Body), "Status=completed") {
		t.Errorf("hangup body = %s", hangReq.Body)
	}
}

func TestTwilioCarrier_ListNumbersFailSoft(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(500) // non-2xx -> empty
	c, _ := circleai.NewTwilioCarrier(tr, circleai.TwilioOptions{AccountSid: "AC1", AuthToken: "tok"}, telephonyClock())
	nums, err := c.ListNumbers(ctx)
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(nums) != 0 {
		t.Errorf("non-2xx list should be empty, got %+v", nums)
	}
}

// ---------------------------------------------------------------------------
// Telnyx
// ---------------------------------------------------------------------------

func TestTelnyxCarrier_ProvisionNumber(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"data":[{"phone_number":"+15551110000","cost_information":{"monthly_cost":"2.50"}}]}`)
	tr.EnqueueJSON(201, `{"data":{"id":"order1"}}`)
	c, _ := circleai.NewTelnyxCarrier(tr, circleai.TelnyxOptions{ApiKey: "KEY"}, telephonyClock())
	if c.CarrierID() != "telnyx" || !c.IsConfigured() {
		t.Fatal("telnyx id/configured wrong")
	}

	pn, err := c.ProvisionNumber(ctx, "US", "")
	if err != nil {
		t.Fatalf("provision: %v", err)
	}
	if pn.PhoneNumber != "+15551110000" || pn.MonthlyRecurringCost.String() != "2.5" {
		t.Errorf("provisioned = %+v", pn)
	}
	reqs := tr.Requests()
	// Bearer auth + /v2 search.
	if !strings.HasPrefix(reqs[0].Headers["Authorization"], "Bearer ") ||
		!strings.Contains(reqs[0].URL, "/v2/available_phone_numbers") {
		t.Errorf("search request wrong: %s %q", reqs[0].URL, reqs[0].Headers["Authorization"])
	}
	// Order body carries the phone number.
	if !strings.Contains(string(reqs[1].Body), `"phone_number":"+15551110000"`) {
		t.Errorf("order body = %s", reqs[1].Body)
	}
}

func TestTelnyxCarrier_DialRequiresConnection(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	c, _ := circleai.NewTelnyxCarrier(tr, circleai.TelnyxOptions{ApiKey: "KEY"}, telephonyClock())
	// No CallControlConnectionId -> dial errors before any request.
	if _, err := c.Dial(ctx, "+1", "+2", mustURL(t, "wss://h/s"), nil); err == nil {
		t.Error("dial without connection id should error")
	}
}

func TestTelnyxCarrier_DialAndTransfer(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"data":{"call_control_id":"CC1"}}`) // dial
	tr.EnqueueStatus(200)                                     // transfer action
	tr.EnqueueStatus(200)                                     // hangup action
	c, _ := circleai.NewTelnyxCarrier(tr, circleai.TelnyxOptions{ApiKey: "KEY", CallControlConnectionId: "conn1"}, telephonyClock())

	sess, err := c.Dial(ctx, "+27000000001", "+27000000002", mustURL(t, "wss://host/stream"), nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	if sess.Info().CallID != "CC1" || sess.Info().MediaFormat != circleai.CallMediaFormatPcm16000 {
		t.Errorf("dial info = %+v", sess.Info())
	}
	dialReq, _ := tr.LastRequest()
	if !strings.Contains(string(dialReq.Body), `"connection_id":"conn1"`) ||
		!strings.Contains(string(dialReq.Body), `"stream_track":"both_tracks"`) {
		t.Errorf("dial JSON body wrong: %s", dialReq.Body)
	}

	// Cold transfer -> transfer action; status Transferred.
	if err := sess.Transfer(ctx, "+15551230000", circleai.TransferModeCold, ""); err != nil {
		t.Fatalf("transfer: %v", err)
	}
	transferReq, _ := tr.LastRequest()
	if !strings.Contains(transferReq.URL, "/actions/transfer") || !strings.Contains(string(transferReq.Body), `"to":"+15551230000"`) {
		t.Errorf("transfer request wrong: %s %s", transferReq.URL, transferReq.Body)
	}
	if sess.Status() != circleai.CallStatusTransferred {
		t.Errorf("status = %v", sess.Status())
	}

	if err := sess.HangUp(ctx); err != nil {
		t.Fatalf("hangup: %v", err)
	}
	hangReq, _ := tr.LastRequest()
	if !strings.Contains(hangReq.URL, "/actions/hangup") {
		t.Errorf("hangup url = %s", hangReq.URL)
	}
}

// ---------------------------------------------------------------------------
// Plivo
// ---------------------------------------------------------------------------

func TestPlivoCarrier_ProvisionNumber(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"objects":[{"number":"+441110000","monthly_rental_rate":"3.00"}]}`)
	tr.EnqueueJSON(201, `{"status":"fulfilled"}`)
	c, _ := circleai.NewPlivoCarrier(tr, circleai.PlivoOptions{AuthId: "MA1", AuthToken: "tok"}, telephonyClock())
	if c.CarrierID() != "plivo" || !c.IsConfigured() {
		t.Fatal("plivo id/configured wrong")
	}

	pn, err := c.ProvisionNumber(ctx, "GB", "")
	if err != nil {
		t.Fatalf("provision: %v", err)
	}
	if pn.PhoneNumber != "+441110000" || pn.MonthlyRecurringCost.String() != "3" {
		t.Errorf("provisioned = %+v", pn)
	}
	reqs := tr.Requests()
	// Basic auth + /v1/Account/MA1/PhoneNumber search.
	if !strings.HasPrefix(reqs[0].Headers["Authorization"], "Basic ") ||
		!strings.Contains(reqs[0].URL, "/v1/Account/MA1/PhoneNumber/") {
		t.Errorf("search request wrong: %s", reqs[0].URL)
	}
}

func TestPlivoCarrier_DialRequiresAnswerUrlBase(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	c, _ := circleai.NewPlivoCarrier(tr, circleai.PlivoOptions{AuthId: "MA1", AuthToken: "tok"}, telephonyClock())
	if _, err := c.Dial(ctx, "+1", "+2", mustURL(t, "wss://h/s"), nil); err == nil {
		t.Error("dial without AnswerUrlBase should error")
	}
}

func TestPlivoCarrier_DialAndTransferHangup(t *testing.T) {
	ctx := context.Background()
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(201, `{"request_uuid":"RU1"}`) // dial
	tr.EnqueueStatus(200)                         // transfer replay
	tr.EnqueueStatus(204)                         // DELETE hangup
	c, _ := circleai.NewPlivoCarrier(tr, circleai.PlivoOptions{AuthId: "MA1", AuthToken: "tok", AnswerUrlBase: "https://host/answer"}, telephonyClock())

	sess, err := c.Dial(ctx, "+27000000001", "+27000000002", mustURL(t, "wss://host/stream"), nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	if sess.Info().CallID != "RU1" || sess.Info().MediaFormat != circleai.CallMediaFormatMulaw8000 {
		t.Errorf("dial info = %+v", sess.Info())
	}
	dialReq, _ := tr.LastRequest()
	body := string(dialReq.Body)
	// answer_url form field carries the ?stream= composition (url-encoded).
	if !strings.Contains(body, "answer_url=") || !strings.Contains(body, "stream") {
		t.Errorf("dial body missing answer_url/stream: %s", body)
	}

	if err := sess.Transfer(ctx, "+15551230000", circleai.TransferModeCold, ""); err != nil {
		t.Fatalf("transfer: %v", err)
	}
	if sess.Status() != circleai.CallStatusTransferred {
		t.Errorf("status = %v", sess.Status())
	}
	transferReq, _ := tr.LastRequest()
	if !strings.Contains(string(transferReq.Body), "aleg_url=") {
		t.Errorf("transfer body missing aleg_url: %s", transferReq.Body)
	}

	if err := sess.HangUp(ctx); err != nil {
		t.Fatalf("hangup: %v", err)
	}
	hangReq, _ := tr.LastRequest()
	if hangReq.Method != "DELETE" || !strings.Contains(hangReq.URL, "/Call/RU1/") {
		t.Errorf("hangup request wrong: %s %s", hangReq.Method, hangReq.URL)
	}
}
