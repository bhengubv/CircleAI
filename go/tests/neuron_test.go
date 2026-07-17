// neuron_test.go — the Go Neuron port.
//
// Mirrors the C# CircleAI.Tests Neuron suite: the concierge decision table +
// gate, the two-slot admission gate + eviction, the router-gated slot selection
// inside AIService (specialist hot-load, generalist floor), and the NeuronNode
// facade.

package circleai_test

import (
	"context"
	"errors"
	"path/filepath"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── test doubles ─────────────────────────────────────────────────────────────

// neuronFakeGen is an IChatGenerator + SessionPersistence returning a fixed reply
// and recording Close.
type neuronFakeGen struct {
	reply  string
	closed bool
}

func (g *neuronFakeGen) Generate(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (string, error) {
	return g.reply, nil
}

func (g *neuronFakeGen) Stream(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string, 1)
	errc := make(chan error, 1)
	out <- g.reply
	close(out)
	close(errc)
	return out, errc
}

func (g *neuronFakeGen) Close() error                        { g.closed = true; return nil }
func (g *neuronFakeGen) SaveSession(string) (bool, error)    { return true, nil }
func (g *neuronFakeGen) LoadSession(string) (bool, error)    { return true, nil }

type neuronFixedRouter struct{ d circleai.RouteDecision }

func (r neuronFixedRouter) Route(circleai.RouteContext) circleai.RouteDecision { return r.d }

type neuronFakeSelector struct {
	sel   circleai.ModelSelection
	calls int
}

func (s *neuronFakeSelector) BestFit(circleai.DeviceProbe, circleai.ChatCapability) (circleai.ModelSelection, error) {
	s.calls++
	return s.sel, nil
}

func (s *neuronFakeSelector) AllCandidates(circleai.DeviceProbe) []circleai.ModelSelection {
	return []circleai.ModelSelection{s.sel}
}

func neuronSel(id string, bytes int64) circleai.ModelSelection {
	return circleai.ModelSelection{ModelID: id, EstimatedBytes: bytes, Tier: circleai.DeviceTierDesktop}
}

// ── concierge router + gate ──────────────────────────────────────────────────

func TestNeuronRouter_PlainGeneralist(t *testing.T) {
	d := circleai.NewHeuristicNeuronRouter(nil, 0).Route(circleai.RouteContext{Query: "what's the weather today?"})
	if d.Organ != circleai.OrganGeneralist || d.Capability != circleai.CapDefault {
		t.Fatalf("want generalist/default, got %v/%v", d.Organ, d.Capability)
	}
}

func TestNeuronRouter_Vision(t *testing.T) {
	d := circleai.NewHeuristicNeuronRouter(nil, 0).Route(circleai.RouteContext{Query: "what is this?", HasImage: true})
	if d.Organ != circleai.OrganSpecialist || d.Capability != circleai.CapVision {
		t.Fatalf("want vision specialist, got %v/%v", d.Organ, d.Capability)
	}
}

func TestNeuronRouter_Reasoning(t *testing.T) {
	d := circleai.NewHeuristicNeuronRouter(nil, 0).Route(circleai.RouteContext{Query: "please debug this stack trace"})
	if d.Organ != circleai.OrganSpecialist || d.Capability != circleai.CapReasoning {
		t.Fatalf("want reasoning specialist, got %v/%v", d.Organ, d.Capability)
	}
}

func TestNeuronRouter_LongContext(t *testing.T) {
	d := circleai.NewHeuristicNeuronRouter(nil, 50).Route(circleai.RouteContext{Query: strings.Repeat("x", 60)})
	if d.Organ != circleai.OrganSpecialist || d.Capability != circleai.CapLongContext {
		t.Fatalf("want long-context specialist, got %v/%v", d.Organ, d.Capability)
	}
}

func TestNeuronRouter_GateVeto(t *testing.T) {
	gate := circleai.NewNeuronGate(func(string) bool { return false })
	d := circleai.NewHeuristicNeuronRouter(gate, 0).Route(circleai.RouteContext{Query: "solve this equation"})
	if d.Organ != circleai.OrganGeneralist {
		t.Fatalf("gate should veto specialist, got %v", d.Organ)
	}
}

// ── resident slot manager ────────────────────────────────────────────────────

func TestSlotManager_AdmitsWithinBudget(t *testing.T) {
	m := circleai.NewResidentSlotManager(1000, func() int64 { return 1_000_000 })
	g := &neuronFakeGen{reply: "S"}
	a := m.EnsureSpecialist(neuronSel("spec", 5000), func(string) (circleai.IChatGenerator, error) { return g, nil })
	if a.Outcome != circleai.SlotAdmitted || a.Generator != g || m.ResidentSpecialistModelID() != "spec" {
		t.Fatalf("admit failed: %+v (resident=%q)", a, m.ResidentSpecialistModelID())
	}
}

func TestSlotManager_DeniesOverBudget(t *testing.T) {
	m := circleai.NewResidentSlotManager(900_000, func() int64 { return 1_000_000 })
	a := m.EnsureSpecialist(neuronSel("spec", 500_000), func(string) (circleai.IChatGenerator, error) { return &neuronFakeGen{}, nil })
	if a.Outcome != circleai.SlotInsufficientRAM || a.Generator != nil || m.ResidentSpecialistModelID() != "" {
		t.Fatalf("deny failed: %+v", a)
	}
}

func TestSlotManager_AlreadyResident(t *testing.T) {
	m := circleai.NewResidentSlotManager(0, func() int64 { return 1_000_000 })
	builds := 0
	build := func(string) (circleai.IChatGenerator, error) { builds++; return &neuronFakeGen{}, nil }
	m.EnsureSpecialist(neuronSel("spec", 1), build)
	a := m.EnsureSpecialist(neuronSel("spec", 1), build)
	if a.Outcome != circleai.SlotAlreadyResident || builds != 1 {
		t.Fatalf("already-resident failed: %+v builds=%d", a, builds)
	}
}

func TestSlotManager_SwapEvicts(t *testing.T) {
	m := circleai.NewResidentSlotManager(0, func() int64 { return 1_000_000 })
	a := &neuronFakeGen{reply: "A"}
	b := &neuronFakeGen{reply: "B"}
	m.EnsureSpecialist(neuronSel("A", 1), func(string) (circleai.IChatGenerator, error) { return a, nil })
	m.EnsureSpecialist(neuronSel("B", 1), func(string) (circleai.IChatGenerator, error) { return b, nil })
	if !a.closed || b.closed || m.ResidentSpecialistModelID() != "B" {
		t.Fatalf("swap failed: aClosed=%v bClosed=%v resident=%q", a.closed, b.closed, m.ResidentSpecialistModelID())
	}
}

func TestSlotManager_BuildFailure(t *testing.T) {
	m := circleai.NewResidentSlotManager(0, func() int64 { return 1_000_000 })
	a := m.EnsureSpecialist(neuronSel("spec", 1), func(string) (circleai.IChatGenerator, error) { return nil, errors.New("boom") })
	if a.Outcome != circleai.SlotBuildFailed || m.ResidentSpecialistModelID() != "" {
		t.Fatalf("build-failure failed: %+v", a)
	}
}

func TestSlotManager_Evict(t *testing.T) {
	m := circleai.NewResidentSlotManager(0, func() int64 { return 1_000_000 })
	g := &neuronFakeGen{}
	m.EnsureSpecialist(neuronSel("spec", 1), func(string) (circleai.IChatGenerator, error) { return g, nil })
	m.EvictSpecialist()
	if !g.closed || m.ResidentSpecialistModelID() != "" {
		t.Fatalf("evict failed: closed=%v resident=%q", g.closed, m.ResidentSpecialistModelID())
	}
}

// ── AIService two-slot residency ─────────────────────────────────────────────

func TestNeuronAIService_RouterNilUsesGeneralist(t *testing.T) {
	ctx := context.Background()
	gen := &neuronFakeGen{reply: "GEN"}
	svc := circleai.NewAIService(func() (circleai.IChatGenerator, error) { return gen, nil }, circleai.WithWarmOnStart(false))
	_ = svc.Start(ctx)
	got, _ := svc.Ask(ctx, "solve this equation") // reasoning cue, but no router
	if got != "GEN" {
		t.Fatalf("want GEN, got %q", got)
	}
}

func TestNeuronAIService_HotLoadsSpecialist(t *testing.T) {
	ctx := context.Background()
	gen := &neuronFakeGen{reply: "GEN"}
	spec := &neuronFakeGen{reply: "SPEC"}
	selr := &neuronFakeSelector{sel: neuronSel("spec-model", 1024)}
	svc := circleai.NewAIService(
		func() (circleai.IChatGenerator, error) { return gen, nil },
		circleai.WithWarmOnStart(false),
		circleai.WithNeuronRouter(neuronFixedRouter{circleai.SpecialistDecision(circleai.CapReasoning, "t")}),
		circleai.WithNeuronSpecialist(selr, func(string) (circleai.IChatGenerator, error) { return spec, nil }, "gen-model"),
	)
	_ = svc.Start(ctx)
	got, _ := svc.Ask(ctx, "anything")
	if got != "SPEC" {
		t.Fatalf("want SPEC, got %q", got)
	}
}

func TestNeuronAIService_BestFitEqualsGeneralist(t *testing.T) {
	ctx := context.Background()
	gen := &neuronFakeGen{reply: "GEN"}
	spec := &neuronFakeGen{reply: "SPEC"}
	selr := &neuronFakeSelector{sel: neuronSel("gen-model", 1024)}
	svc := circleai.NewAIService(
		func() (circleai.IChatGenerator, error) { return gen, nil },
		circleai.WithWarmOnStart(false),
		circleai.WithNeuronRouter(neuronFixedRouter{circleai.SpecialistDecision(circleai.CapReasoning, "t")}),
		circleai.WithNeuronSpecialist(selr, func(string) (circleai.IChatGenerator, error) { return spec, nil }, "gen-model"),
	)
	_ = svc.Start(ctx)
	got, _ := svc.Ask(ctx, "anything")
	if got != "GEN" {
		t.Fatalf("want GEN (best-fit==generalist), got %q", got)
	}
}

func TestNeuronAIService_SessionRoundTrip(t *testing.T) {
	ctx := context.Background()
	svc := circleai.NewAIService(func() (circleai.IChatGenerator, error) { return &neuronFakeGen{reply: "GEN"}, nil }, circleai.WithWarmOnStart(false))
	_ = svc.Start(ctx)
	p := filepath.Join(t.TempDir(), "active.session")
	if ok, _ := svc.SaveSession(ctx, p); !ok {
		t.Fatalf("save should succeed")
	}
	if ok, _ := svc.LoadSession(ctx, p); !ok {
		t.Fatalf("load should succeed")
	}
}

// ── NeuronNode facade + NullChatRuntime ──────────────────────────────────────

func TestNeuronNode_StreamAndStatus(t *testing.T) {
	ctx := context.Background()
	gen := &neuronFakeGen{reply: "hello"}
	svc := circleai.NewAIService(
		func() (circleai.IChatGenerator, error) { return gen, nil },
		circleai.WithWarmOnStart(false),
		circleai.WithGeneralistModelID("qwen-x"),
	)
	node := circleai.NewNeuronNode(svc, "", "")

	if node.ID() != "circleai-neuron" {
		t.Fatalf("id: %q", node.ID())
	}
	if node.IsReady() || node.StatusMessage() != "loading model…" {
		t.Fatalf("pre-start status: ready=%v msg=%q", node.IsReady(), node.StatusMessage())
	}
	_ = svc.Start(ctx)
	if !node.IsReady() || node.StatusMessage() != "ready" {
		t.Fatalf("post-start status: ready=%v msg=%q", node.IsReady(), node.StatusMessage())
	}
	if !strings.Contains(node.EngineLabel(), "qwen-x") {
		t.Fatalf("engine label: %q", node.EngineLabel())
	}

	out, errc := node.Stream(ctx, []circleai.ChatTurn{{Role: "user", Content: "hi"}})
	var sb strings.Builder
	for c := range out {
		sb.WriteString(c)
	}
	if err := <-errc; err != nil {
		t.Fatalf("stream err: %v", err)
	}
	if sb.String() != "hello" {
		t.Fatalf("want hello, got %q", sb.String())
	}

	p := filepath.Join(t.TempDir(), "active.session")
	if ok, _ := node.SaveSession(ctx, p); !ok {
		t.Fatalf("node save")
	}
	if ok, _ := node.LoadSession(ctx, p); !ok {
		t.Fatalf("node load")
	}
	if node.SessionSnapshotPath() == "" {
		t.Fatalf("snapshot path empty")
	}
}

func TestNeuronNode_NullRuntime(t *testing.T) {
	ctx := context.Background()
	var null circleai.NullChatRuntime
	if null.IsReady() {
		t.Fatalf("null must not be ready")
	}
	out, _ := null.Stream(ctx, []circleai.ChatTurn{{Role: "user", Content: "hi"}})
	var sb strings.Builder
	for c := range out {
		sb.WriteString(c)
	}
	if !strings.Contains(sb.String(), "No chat engine") {
		t.Fatalf("null status: %q", sb.String())
	}
}
