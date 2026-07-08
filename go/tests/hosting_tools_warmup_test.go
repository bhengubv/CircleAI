// hosting_tools_warmup_test.go
//
// Verifies CircleAI.Hosting.Tools + CircleAI.Hosting.Warmup + GenerativeUI ports:
//   InMemoryToolCatalog (upsert/get/remove/list/search/listByProvider + import)
//   HistogramRequestPredictor + PredictiveWarmupController
//   JsonRenderParser (fixture-driven) + catalog prompt description

package circleai_test

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── InMemoryToolCatalog ─────────────────────────────────────────────────────

func TestInMemoryToolCatalog_CRUD(t *testing.T) {
	ctx := context.Background()
	cat := circleai.NewInMemoryToolCatalog()
	if cat.Count() != 0 {
		t.Fatal("new catalog should be empty")
	}

	gmail := circleai.ToolDescriptor{Name: "gmail.send", Description: "Send an email", Provider: "gmail", Tags: []string{"communication", "oauth"}}
	github := circleai.ToolDescriptor{Name: "github.pr", Description: "Open a pull request", Provider: "github", Tags: []string{"code"}}

	if err := cat.Upsert(ctx, gmail); err != nil {
		t.Fatalf("upsert: %v", err)
	}
	_ = cat.Upsert(ctx, github)
	if cat.Count() != 2 {
		t.Fatalf("count = %d, want 2", cat.Count())
	}

	got, _ := cat.Get(ctx, "GMAIL.SEND") // case-insensitive
	if got == nil || got.Provider != "gmail" {
		t.Fatalf("get case-insensitive failed: %+v", got)
	}

	list := cat.List()
	if len(list) != 2 || list[0].Name != "github.pr" { // ordered by name
		t.Fatalf("list order wrong: %+v", list)
	}

	byProv := cat.ListByProvider("GitHub")
	if len(byProv) != 1 || byProv[0].Name != "github.pr" {
		t.Fatalf("listByProvider = %+v", byProv)
	}

	removed, _ := cat.Remove(ctx, "gmail.send")
	if !removed || cat.Count() != 1 {
		t.Fatalf("remove failed: removed=%v count=%d", removed, cat.Count())
	}
	removedAgain, _ := cat.Remove(ctx, "gmail.send")
	if removedAgain {
		t.Error("removing missing tool should report false")
	}
}

func TestInMemoryToolCatalog_Search(t *testing.T) {
	ctx := context.Background()
	cat := circleai.NewInMemoryToolCatalog()
	_ = cat.Upsert(ctx, circleai.ToolDescriptor{Name: "email.send", Description: "Send email via SMTP", Provider: "mail", Tags: []string{"email"}})
	_ = cat.Upsert(ctx, circleai.ToolDescriptor{Name: "calendar.create", Description: "Create a calendar event", Provider: "cal", Tags: []string{"schedule"}})

	// "email" hits name(+5) + desc(+2) + tag(+3) on the first tool only.
	hits := cat.Search("email", 10)
	if len(hits) != 1 || hits[0].Name != "email.send" {
		t.Fatalf("search 'email' = %+v", hits)
	}

	// Blank query / non-positive topK return nil.
	if cat.Search("  ", 10) != nil {
		t.Error("blank query should return nil")
	}
	if cat.Search("email", 0) != nil {
		t.Error("topK<=0 should return nil")
	}
}

// fakeProvider is an IToolProvider that returns a fixed tool list.
type fakeProvider struct {
	id    string
	tools []circleai.ToolDescriptor
}

func (p fakeProvider) ProviderID() string { return p.id }
func (p fakeProvider) Discover(context.Context) ([]circleai.ToolDescriptor, error) {
	return p.tools, nil
}
func (p fakeProvider) IsAvailable(context.Context) (bool, error) { return true, nil }

func TestImportToolsFrom(t *testing.T) {
	ctx := context.Background()
	cat := circleai.NewInMemoryToolCatalog()
	prov := fakeProvider{id: "local", tools: []circleai.ToolDescriptor{
		{Name: "a", Provider: "local"},
		{Name: "b", Provider: "local"},
	}}
	n, err := circleai.ImportToolsFrom(ctx, cat, prov)
	if err != nil {
		t.Fatalf("import: %v", err)
	}
	if n != 2 || cat.Count() != 2 {
		t.Fatalf("imported %d, count %d, want 2/2", n, cat.Count())
	}
}

// ── HistogramRequestPredictor ───────────────────────────────────────────────

func TestHistogramRequestPredictor_ColdStart(t *testing.T) {
	p := circleai.NewHistogramRequestPredictor(7)
	f := p.Predict(time.Now().UTC(), time.Minute)
	if f.Confidence != 0 || f.ProbabilityOfArrival != 0 {
		t.Errorf("cold start should be zero, got %+v", f)
	}
	if p.ObservedArrivals() != 0 {
		t.Error("no arrivals recorded yet")
	}
}

func TestHistogramRequestPredictor_LearnsSpike(t *testing.T) {
	p := circleai.NewHistogramRequestPredictor(7)
	slot := time.Date(2026, 7, 8, 9, 0, 0, 0, time.UTC)
	for i := 0; i < 40; i++ {
		p.RecordArrival(slot)
	}
	if p.ObservedArrivals() != 40 {
		t.Fatalf("observed = %d, want 40", p.ObservedArrivals())
	}
	f := p.Predict(slot, time.Minute)
	if f.ProbabilityOfArrival <= 0 {
		t.Errorf("probability should be positive at a learned slot, got %v", f.ProbabilityOfArrival)
	}
	if f.Confidence <= 0 {
		t.Errorf("confidence should rise with samples, got %v", f.Confidence)
	}
	// A quiet slot 12h away should forecast ~nothing.
	quiet := slot.Add(12 * time.Hour)
	fq := p.Predict(quiet, time.Minute)
	if fq.ProbabilityOfArrival >= f.ProbabilityOfArrival {
		t.Error("quiet slot should forecast lower probability than the busy slot")
	}
}

// ── PredictiveWarmupController ───────────────────────────────────────────────

func TestPredictiveWarmupController_TickFires(t *testing.T) {
	ctx := context.Background()
	butler := &fakeButler{}
	pred := circleai.NewHistogramRequestPredictor(7)

	fixedNow := time.Date(2026, 7, 8, 9, 0, 0, 0, time.UTC)
	// Train the current slot heavily so prob*confidence >= threshold.
	for i := 0; i < 60; i++ {
		pred.RecordArrival(fixedNow)
	}

	opts := circleai.DefaultPredictiveWarmupOptions()
	opts.Enabled = true
	opts.WarmupThreshold = 0.3
	ctrl := circleai.NewPredictiveWarmupController(butler, pred, opts, func() time.Time { return fixedNow })

	fired, err := ctrl.Tick(ctx)
	if err != nil {
		t.Fatalf("tick: %v", err)
	}
	if !fired {
		t.Fatal("expected warmup to fire on a trained slot")
	}
	if !butler.IsReady() {
		t.Error("Prewarm should have started the butler")
	}

	// Second immediate tick is throttled by MinTimeBetweenWarmups.
	fired2, _ := ctrl.Tick(ctx)
	if fired2 {
		t.Error("second immediate tick should be throttled")
	}
}

func TestPredictiveWarmupController_Disabled(t *testing.T) {
	pred := circleai.NewHistogramRequestPredictor(7)
	opts := circleai.DefaultPredictiveWarmupOptions() // Enabled=false
	ctrl := circleai.NewPredictiveWarmupController(&fakeButler{}, pred, opts, nil)
	ctrl.Start(context.Background()) // must be a no-op
	ctrl.Stop()
}

// ── JsonRenderParser ─────────────────────────────────────────────────────────

type renderFixture struct {
	Cases []struct {
		ID               string                 `json:"id"`
		JSON             string                 `json:"json"`
		Strict           bool                   `json:"strict"`
		ExpectKind       string                 `json:"expectKind"`
		ExpectProps      map[string]interface{} `json:"expectProps"`
		ExpectChildKinds []string               `json:"expectChildKinds"`
	} `json:"cases"`
	ErrorCases []struct {
		ID     string `json:"id"`
		JSON   string `json:"json"`
		Strict bool   `json:"strict"`
	} `json:"errorCases"`
}

func TestJsonRenderParser_Fixtures(t *testing.T) {
	b, err := os.ReadFile(filepath.Join("fixtures", "hosting_render.json"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var f renderFixture
	if err := json.Unmarshal(b, &f); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}
	catalog := circleai.DefaultUiCatalog()

	for _, c := range f.Cases {
		comp, err := circleai.ParseRenderJSON(c.JSON, catalog, c.Strict)
		if err != nil {
			t.Errorf("%s: unexpected error: %v", c.ID, err)
			continue
		}
		if comp.Kind != c.ExpectKind {
			t.Errorf("%s: kind = %q, want %q", c.ID, comp.Kind, c.ExpectKind)
		}
		for k, want := range c.ExpectProps {
			got, ok := comp.Properties[k]
			if !ok {
				t.Errorf("%s: missing property %q", c.ID, k)
				continue
			}
			if !renderValueEqual(got, want) {
				t.Errorf("%s: property %q = %v (%T), want %v (%T)", c.ID, k, got, got, want, want)
			}
		}
		if len(c.ExpectChildKinds) > 0 {
			if len(comp.Children) != len(c.ExpectChildKinds) {
				t.Errorf("%s: %d children, want %d", c.ID, len(comp.Children), len(c.ExpectChildKinds))
				continue
			}
			for i, wantKind := range c.ExpectChildKinds {
				if comp.Children[i].Kind != wantKind {
					t.Errorf("%s: child %d kind = %q, want %q", c.ID, i, comp.Children[i].Kind, wantKind)
				}
			}
		}
	}

	for _, c := range f.ErrorCases {
		if _, err := circleai.ParseRenderJSON(c.JSON, catalog, c.Strict); err == nil {
			t.Errorf("%s: expected error, got none", c.ID)
		}
	}
}

func TestJsonRenderParser_IntVsFloat(t *testing.T) {
	// Integers become int64; fractional numbers become float64 (ToManaged).
	comp, err := circleai.ParseRenderJSON(
		`{"kind":"list","properties":{"ordered":true}}`,
		circleai.DefaultUiCatalog(), true)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if comp.Properties["ordered"] != true {
		t.Errorf("ordered = %v, want true", comp.Properties["ordered"])
	}
}

func TestDescribeUiCatalogForPrompt(t *testing.T) {
	desc := circleai.DescribeUiCatalogForPrompt(circleai.DefaultUiCatalog())
	for _, needle := range []string{"Allowed kinds:", "card", "button", "children: array of components"} {
		if !strings.Contains(desc, needle) {
			t.Errorf("prompt description missing %q", needle)
		}
	}
}

func TestRecordingGenerativeUIRenderer(t *testing.T) {
	r := &circleai.RecordingGenerativeUIRenderer{}
	comp := circleai.UiComponent{Kind: "card", Properties: map[string]interface{}{"title": "T"}}
	_ = r.Render(context.Background(), comp)
	if r.RenderCount != 1 || r.LastRendered == nil || r.LastRendered.Kind != "card" {
		t.Errorf("recorder state wrong: count=%d last=%+v", r.RenderCount, r.LastRendered)
	}
}

func renderValueEqual(got, want interface{}) bool {
	// JSON numbers in `want` (from the fixture) decode to float64; parsed ints
	// are int64. Compare numerically when both are numbers.
	gf, gok := toFloat(got)
	wf, wok := toFloat(want)
	if gok && wok {
		return gf == wf
	}
	return got == want
}

func toFloat(v interface{}) (float64, bool) {
	switch n := v.(type) {
	case int64:
		return float64(n), true
	case float64:
		return n, true
	default:
		return 0, false
	}
}
