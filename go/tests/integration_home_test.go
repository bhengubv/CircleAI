// integration_home_test.go
//
// Verifies the Home Assistant connector (integration_home.go) over the injected
// FakeCarrierTransport — no real network. Covers api/states listing (domain +
// friendly-name derivation, attribute stringification by JSON kind, blank
// entity_id skip, non-array → empty), CallService POST + validation, and the
// TurnOn/TurnOff convenience wrappers.

package circleai_test

import (
	"context"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mustHA(t *testing.T, tr *circleai.FakeCarrierTransport) *circleai.HomeAssistantConnector {
	t.Helper()
	c, err := circleai.NewHomeAssistantConnector(tr, circleai.HomeAssistantOptions{
		BaseURL: "http://homeassistant.local:8123/", AccessToken: "llt-token",
	})
	if err != nil {
		t.Fatalf("new ha: %v", err)
	}
	return c
}

func TestHA_ListEntities(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `[
		{"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen Light","brightness":128,"supported":true}},
		{"entity_id":"sensor.temp","state":"21.5","attributes":{"unit_of_measurement":"°C"}},
		{"entity_id":"","state":"ignored"}
	]`)
	c := mustHA(t, tr)
	if c.ProviderID() != "home-assistant" || !c.IsConfigured() {
		t.Fatalf("ha id/configured wrong")
	}
	ents, err := c.ListEntities(context.Background())
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(ents) != 2 { // blank entity_id skipped
		t.Fatalf("expected 2 entities, got %d: %+v", len(ents), ents)
	}
	k := ents[0]
	if k.EntityID != "light.kitchen" || k.Domain != "light" || k.State != "on" || k.FriendlyName != "Kitchen Light" {
		t.Fatalf("kitchen entity wrong: %+v", k)
	}
	// Attributes stringified: number->text, bool->"true".
	if k.Attributes["brightness"] != "128" || k.Attributes["supported"] != "true" || k.Attributes["friendly_name"] != "Kitchen Light" {
		t.Fatalf("kitchen attrs wrong: %+v", k.Attributes)
	}
	s := ents[1]
	// No friendly_name -> FriendlyName defaults to entity_id.
	if s.FriendlyName != "sensor.temp" || s.Domain != "sensor" || s.Attributes["unit_of_measurement"] != "°C" {
		t.Fatalf("sensor entity wrong: %+v", s)
	}
	req := tr.Requests()[0]
	if req.Method != "GET" || !strings.HasSuffix(req.URL, "/api/states") || !strings.HasPrefix(req.Headers["Authorization"], "Bearer ") {
		t.Fatalf("states request wrong: %s %s hdr=%v", req.Method, req.URL, req.Headers)
	}
}

func TestHA_ListEntitiesNonArray(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"error":"not a list"}`)
	c := mustHA(t, tr)
	ents, err := c.ListEntities(context.Background())
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(ents) != 0 {
		t.Fatalf("non-array body should yield empty, got %+v", ents)
	}
}

func TestHA_CallService(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(200)
	c := mustHA(t, tr)
	err := c.CallService(context.Background(), "light", "turn_on", map[string]interface{}{"entity_id": "light.kitchen", "brightness": 200})
	if err != nil {
		t.Fatalf("call service: %v", err)
	}
	req, _ := tr.LastRequest()
	if req.Method != "POST" || !strings.HasSuffix(req.URL, "/api/services/light/turn_on") {
		t.Fatalf("service request wrong: %s %s", req.Method, req.URL)
	}
	if !strings.Contains(string(req.Body), `"entity_id":"light.kitchen"`) {
		t.Fatalf("service body wrong: %s", req.Body)
	}
	// Validation.
	if err := c.CallService(context.Background(), "  ", "svc", nil); err == nil {
		t.Fatalf("blank domain should error")
	}
	if err := c.CallService(context.Background(), "light", "", nil); err == nil {
		t.Fatalf("blank service should error")
	}
}

func TestHA_CallServiceNilDataSendsEmptyObject(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(200)
	c := mustHA(t, tr)
	if err := c.CallService(context.Background(), "script", "run", nil); err != nil {
		t.Fatalf("call: %v", err)
	}
	req, _ := tr.LastRequest()
	if strings.TrimSpace(string(req.Body)) != "{}" {
		t.Fatalf("nil data should send {}, got %q", req.Body)
	}
}

func TestHA_TurnOnOff(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(200)
	tr.EnqueueStatus(200)
	c := mustHA(t, tr)
	if err := c.TurnOn(context.Background(), "switch.fan"); err != nil {
		t.Fatalf("turn on: %v", err)
	}
	if err := c.TurnOff(context.Background(), "switch.fan"); err != nil {
		t.Fatalf("turn off: %v", err)
	}
	reqs := tr.Requests()
	if len(reqs) != 2 ||
		!strings.HasSuffix(reqs[0].URL, "/api/services/homeassistant/turn_on") ||
		!strings.HasSuffix(reqs[1].URL, "/api/services/homeassistant/turn_off") {
		t.Fatalf("turn on/off requests wrong: %+v", reqs)
	}
	if !strings.Contains(string(reqs[0].Body), `"entity_id":"switch.fan"`) {
		t.Fatalf("turn_on body wrong: %s", reqs[0].Body)
	}
}

func TestHA_Unconfigured(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	c, _ := circleai.NewHomeAssistantConnector(tr, circleai.HomeAssistantOptions{BaseURL: "http://x/", AccessToken: ""})
	if c.IsConfigured() {
		t.Fatalf("blank token should be unconfigured")
	}
}
