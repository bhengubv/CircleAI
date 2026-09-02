// telephony_tool_calling_test.go
//
// Verifies CircleAI.Telephony/ToolCalling.cs port: local handler dispatch,
// webhook dispatch over an injected HTTPDoer (body shape + status handling),
// unknown-tool + registered-without-either failure results, definition
// enumeration, and case-insensitive tool matching.

package circleai_test

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// telToolDoer is a scripted HTTPDoer that captures the last request body.
type telToolDoer struct {
	resp     *circleai.InboundHTTPResponse
	err      error
	lastURL  string
	lastBody []byte
}

func (d *telToolDoer) Do(req *circleai.OutboundHTTPRequest) (*circleai.InboundHTTPResponse, error) {
	d.lastURL = req.URL
	d.lastBody = req.Body
	if d.err != nil {
		return nil, d.err
	}
	return d.resp, nil
}

func TestToolRegistry_LocalHandler(t *testing.T) {
	ctx := context.Background()
	reg := circleai.NewDefaultTelephonyToolCallRegistry(nil)
	err := reg.RegisterLocal(
		circleai.TelephonyToolDefinition{Name: "get_time", Description: "d", ArgumentsJSONSchema: "{}"},
		func(_ context.Context, args string) (string, error) {
			return `{"time":"noon","echo":` + args + `}`, nil
		})
	if err != nil {
		t.Fatalf("register: %v", err)
	}

	res, _ := reg.Invoke(ctx, circleai.TelephonyToolInvocation{CallID: "c1", ToolName: "get_time", ArgumentsJSON: `{"tz":"utc"}`})
	if !res.Succeeded {
		t.Fatalf("expected success, got error %q", res.Error)
	}
	if res.CallID != "c1" || !strings.Contains(res.ResultJSON, `"time":"noon"`) {
		t.Errorf("result = %+v", res)
	}

	// Case-insensitive match (OrdinalIgnoreCase).
	res2, _ := reg.Invoke(ctx, circleai.TelephonyToolInvocation{CallID: "c2", ToolName: "GET_TIME", ArgumentsJSON: "{}"})
	if !res2.Succeeded {
		t.Errorf("case-insensitive lookup failed: %q", res2.Error)
	}
}

func TestToolRegistry_LocalHandlerError(t *testing.T) {
	ctx := context.Background()
	reg := circleai.NewDefaultTelephonyToolCallRegistry(nil)
	_ = reg.RegisterLocal(circleai.TelephonyToolDefinition{Name: "boom"}, func(_ context.Context, _ string) (string, error) {
		return "", errors.New("kaboom")
	})
	res, _ := reg.Invoke(ctx, circleai.TelephonyToolInvocation{CallID: "c", ToolName: "boom", ArgumentsJSON: "{}"})
	if res.Succeeded || res.Error != "kaboom" || res.ResultJSON != "{}" {
		t.Errorf("expected failed result with kaboom, got %+v", res)
	}
}

func TestToolRegistry_Webhook(t *testing.T) {
	ctx := context.Background()
	doer := &telToolDoer{resp: &circleai.InboundHTTPResponse{StatusCode: 200, Body: []byte(`{"ok":true}`)}}
	reg := circleai.NewDefaultTelephonyToolCallRegistry(doer)
	if err := reg.RegisterWebhook(circleai.TelephonyToolDefinition{Name: "lookup"}, "https://api.example.com/tool"); err != nil {
		t.Fatalf("register webhook: %v", err)
	}

	res, _ := reg.Invoke(ctx, circleai.TelephonyToolInvocation{CallID: "cid", ToolName: "lookup", ArgumentsJSON: `{"q":"x"}`})
	if !res.Succeeded || res.ResultJSON != `{"ok":true}` {
		t.Fatalf("webhook result = %+v", res)
	}
	if doer.lastURL != "https://api.example.com/tool" {
		t.Errorf("posted URL = %q", doer.lastURL)
	}
	// Body shape: {"call_id","tool","arguments":{...}}.
	var body map[string]json.RawMessage
	if err := json.Unmarshal(doer.lastBody, &body); err != nil {
		t.Fatalf("body not JSON: %v (%s)", err, doer.lastBody)
	}
	if string(body["call_id"]) != `"cid"` || string(body["tool"]) != `"lookup"` {
		t.Errorf("body call_id/tool wrong: %s", doer.lastBody)
	}
	if string(body["arguments"]) != `{"q":"x"}` {
		t.Errorf("arguments not embedded as parsed JSON: %s", body["arguments"])
	}
}

func TestToolRegistry_WebhookNon2xx(t *testing.T) {
	ctx := context.Background()
	doer := &telToolDoer{resp: &circleai.InboundHTTPResponse{StatusCode: 500, Body: []byte("boom-detail")}}
	reg := circleai.NewDefaultTelephonyToolCallRegistry(doer)
	_ = reg.RegisterWebhook(circleai.TelephonyToolDefinition{Name: "w"}, "https://x.example/w")
	res, _ := reg.Invoke(ctx, circleai.TelephonyToolInvocation{CallID: "c", ToolName: "w", ArgumentsJSON: "{}"})
	if res.Succeeded || !strings.Contains(res.Error, "Webhook 500") || !strings.Contains(res.Error, "boom-detail") {
		t.Errorf("expected Webhook 500 failure, got %+v", res)
	}
}

func TestToolRegistry_Unknown(t *testing.T) {
	ctx := context.Background()
	reg := circleai.NewDefaultTelephonyToolCallRegistry(nil)
	res, _ := reg.Invoke(ctx, circleai.TelephonyToolInvocation{CallID: "c", ToolName: "nope"})
	if res.Succeeded || !strings.Contains(res.Error, "is not registered") || res.ResultJSON != "{}" {
		t.Errorf("expected not-registered failure, got %+v", res)
	}
}

func TestToolRegistry_Definitions(t *testing.T) {
	reg := circleai.NewDefaultTelephonyToolCallRegistry(nil)
	_ = reg.RegisterLocal(circleai.TelephonyToolDefinition{Name: "a"}, func(context.Context, string) (string, error) { return "{}", nil })
	_ = reg.RegisterLocal(circleai.TelephonyToolDefinition{Name: "b"}, func(context.Context, string) (string, error) { return "{}", nil })
	defs := reg.Definitions()
	if len(defs) != 2 {
		t.Fatalf("definitions count = %d, want 2", len(defs))
	}

	// Validation errors.
	if err := reg.RegisterLocal(circleai.TelephonyToolDefinition{Name: "  "}, func(context.Context, string) (string, error) { return "{}", nil }); err == nil {
		t.Error("blank name should error")
	}
	if err := reg.RegisterWebhook(circleai.TelephonyToolDefinition{Name: "x"}, "not-a-url"); err == nil {
		t.Error("relative webhook should error")
	}
}
