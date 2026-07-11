// microagents_test.go
//
// Verifies the CircleAI.MicroAgents port (microagents.go): FuncMicroAgent,
// host register/list/invoke (found + not-found), capability search + query
// search, invocation log ordering, and null impl.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func newTestAgent(id, desc string, caps []string, out string) circleai.MicroAgent {
	return circleai.NewFuncMicroAgent(id, desc, caps, func(ctx context.Context, input string) (circleai.MicroAgentResponse, error) {
		return circleai.MicroAgentResponse{AgentID: id, Output: out + ":" + input}, nil
	})
}

func TestMicroAgents_HostRegisterListInvoke(t *testing.T) {
	h := circleai.NewInMemoryMicroAgentHost()
	if h.BackendID() != "in-memory" {
		t.Fatalf("backend id")
	}
	h.Register(newTestAgent("a1", "does a", []string{"code"}, "A"))
	h.Register(newTestAgent("a2", "does b", []string{"ops"}, "B"))
	if got := h.List(); len(got) != 2 {
		t.Fatalf("list = %d agents, want 2", len(got))
	}
	resp, ok, err := h.Invoke(context.Background(), "a1", "x")
	if err != nil || !ok || resp.Output != "A:x" {
		t.Fatalf("invoke a1 = %+v ok=%v err=%v", resp, ok, err)
	}
	if _, ok, _ := h.Invoke(context.Background(), "ghost", "x"); ok {
		t.Fatalf("unknown agent should not be found")
	}
}

func TestMicroAgents_ByCapabilityOrdered(t *testing.T) {
	descs := []circleai.MicroAgentDescriptor{
		{AgentID: "z", Capabilities: []string{"Code"}},
		{AgentID: "a", Capabilities: []string{"code"}},
		{AgentID: "m", Capabilities: []string{"ops"}},
	}
	got := circleai.MicroAgentByCapability(descs, "code")
	if len(got) != 2 || got[0].AgentID != "a" || got[1].AgentID != "z" {
		t.Fatalf("by-capability (case-insensitive, AgentId order) = %+v", got)
	}
}

func TestMicroAgents_SearchTopK(t *testing.T) {
	descs := []circleai.MicroAgentDescriptor{
		{AgentID: "alpha", Description: "the first"},
		{AgentID: "beta", Description: "alpha adjacent", Capabilities: []string{"x"}},
		{AgentID: "gamma", Description: "unrelated"},
	}
	got := circleai.MicroAgentSearch(descs, "alpha", 10)
	if len(got) != 2 {
		t.Fatalf("search 'alpha' matched %d, want 2 (id + description)", len(got))
	}
	if capped := circleai.MicroAgentSearch(descs, "a", 1); len(capped) != 1 {
		t.Fatalf("topK=1 must cap to 1, got %d", len(capped))
	}
}

func TestMicroAgents_InvocationLogOrder(t *testing.T) {
	var log circleai.MicroAgentInvocationLog
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	log.Append(circleai.MicroAgentInvocation{AgentID: "a", Input: "1", AtUTC: base})
	log.Append(circleai.MicroAgentInvocation{AgentID: "a", Input: "3", AtUTC: base.Add(2 * time.Hour)})
	log.Append(circleai.MicroAgentInvocation{AgentID: "a", Input: "2", AtUTC: base.Add(time.Hour)})
	log.Append(circleai.MicroAgentInvocation{AgentID: "b", Input: "x", AtUTC: base})
	if log.TotalInvocations() != 4 {
		t.Fatalf("total = %d, want 4", log.TotalInvocations())
	}
	got := log.ForAgent("a", 10)
	if len(got) != 3 || got[0].Input != "3" || got[2].Input != "1" {
		t.Fatalf("for-agent desc order = %+v", got)
	}
}

func TestMicroAgents_NullAgent(t *testing.T) {
	resp, _ := circleai.NullMicroAgent{}.Invoke(context.Background(), "x")
	if resp.AgentID != "null" || resp.Output != "" {
		t.Fatalf("null agent resp = %+v", resp)
	}
}
