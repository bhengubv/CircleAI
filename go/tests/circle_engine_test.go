// circle_engine_test.go
//
// Verifies the Core facade + ambient contexts:
//   - CircleEngine module bag (Register/Get/Has by runtime type) + ModelLoader
//     accessor + EmbeddingService slot — CircleEngine.
//   - NullTenantContext throws-on-read; SingleTenantContext returns a fixed id —
//     ICircleAITenantContext / NullTenantContext / SingleTenantContext.
//   - NoopAuditLog drops; LoggerAuditLog formats a structured line; the ambient
//     CircleAIAuditing default swaps + resets — ICircleAIAuditLog / LoggerAuditLog
//     / NoopAuditLog / CircleAIAuditing.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// noopLoader is a minimal IModelLoader for engine construction.
type noopLoader struct{}

func (noopLoader) DownloadModel(context.Context, string, func(float32)) (string, error) {
	return "", nil
}
func (noopLoader) GetModelPath(string) (string, error)         { return "", nil }
func (noopLoader) ModelExists(string) bool                     { return false }
func (noopLoader) CheckForCriticalUpdate(context.Context) bool { return false }
func (noopLoader) Close() error                                { return nil }

// fakeModule is a registrable ICircleModule.
type fakeModule struct{ name string }

func (m *fakeModule) ModuleName() string                                 { return m.name }
func (m *fakeModule) Init(context.Context, *circleai.CircleEngine) error { return nil }
func (m *fakeModule) IsModelLoaded() bool                                { return true }
func (m *fakeModule) Close() error                                       { return nil }

func TestCircleEngine_ModuleBag(t *testing.T) {
	loader := noopLoader{}
	engine, err := circleai.NewCircleEngine(loader)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if engine.ModelLoader() == nil {
		t.Error("ModelLoader should be set")
	}

	mod := &fakeModule{name: "fake"}
	if engine.HasModule((*fakeModule)(nil)) {
		t.Error("should not have module before registration")
	}
	if _, err := engine.RegisterModule(mod); err != nil {
		t.Fatalf("register: %v", err)
	}
	if !engine.HasModule((*fakeModule)(nil)) {
		t.Error("HasModule should be true after registration")
	}
	got := engine.GetModule((*fakeModule)(nil))
	if got == nil {
		t.Fatal("GetModule returned nil")
	}
	if got.(*fakeModule).name != "fake" {
		t.Errorf("wrong module returned: %+v", got)
	}
	// Unregistered type → nil.
	if engine.GetModule((*noopLoader)(nil)) != nil {
		t.Error("unregistered type should return nil")
	}
}

func TestCircleEngine_RequiresLoader(t *testing.T) {
	if _, err := circleai.NewCircleEngine(nil); err == nil {
		t.Fatal("nil loader should error")
	}
}

func TestCircleEngine_EmbeddingServiceSlot(t *testing.T) {
	engine, _ := circleai.NewCircleEngine(noopLoader{})
	if engine.EmbeddingService != nil {
		t.Error("EmbeddingService should default nil")
	}
	engine.EmbeddingService = "svc"
	if engine.EmbeddingService != "svc" {
		t.Error("EmbeddingService setter/getter broken")
	}
}

// ── Tenant context ────────────────────────────────────────────────────────

func TestNullTenantContext_ThrowsOnRead(t *testing.T) {
	var ctx circleai.ICircleAITenantContext = circleai.NullTenantContextInstance
	if ctx.HasTenant() {
		t.Error("null context should have no tenant")
	}
	if _, err := ctx.CurrentTenantID(); err == nil {
		t.Fatal("null context CurrentTenantID must error")
	}
}

func TestSingleTenantContext_ReturnsFixedId(t *testing.T) {
	ctx, err := circleai.NewSingleTenantContext("acme")
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if !ctx.HasTenant() {
		t.Error("single context should have a tenant")
	}
	id, err := ctx.CurrentTenantID()
	if err != nil || id != "acme" {
		t.Errorf("CurrentTenantID: got (%q,%v)", id, err)
	}
	if _, err := circleai.NewSingleTenantContext("  "); err == nil {
		t.Error("blank tenant id should error")
	}
}

// ── Audit log ─────────────────────────────────────────────────────────────

func TestNoopAuditLog_DropsAndQueriesEmpty(t *testing.T) {
	var log circleai.ICircleAIAuditLog = circleai.NoopAuditLogInstance
	if err := log.Record(context.Background(), circleai.CircleAIAuditEntry{Component: "X"}); err != nil {
		t.Errorf("Record should not error: %v", err)
	}
	entries, err := log.Query(context.Background(), circleai.NewCircleAIAuditQuery())
	if err != nil || len(entries) != 0 {
		t.Errorf("Query should return empty: %d entries, err=%v", len(entries), err)
	}
}

func TestLoggerAuditLog_FormatsStructuredLine(t *testing.T) {
	var captured string
	log, err := circleai.NewLoggerAuditLog(func(msg string) { captured = msg })
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	entry := circleai.CircleAIAuditEntry{
		At:         time.Date(2026, 7, 8, 12, 0, 0, 0, time.UTC),
		Component:  "JsonPersonaProvider",
		Operation:  "GetAsync",
		Outcome:    "success",
		TenantID:   "t1",
		DurationMs: 12.5,
	}
	if err := log.Record(context.Background(), entry); err != nil {
		t.Fatalf("record: %v", err)
	}
	for _, want := range []string{"JsonPersonaProvider", "GetAsync", "success", "tenant=t1", "duration_ms=12.5"} {
		if !strings.Contains(captured, want) {
			t.Errorf("audit line missing %q: %s", want, captured)
		}
	}
	// Empty optional fields render as "-".
	if !strings.Contains(captured, "uhid=-") {
		t.Errorf("empty uhid should render as '-': %s", captured)
	}
}

func TestLoggerAuditLog_RequiresSink(t *testing.T) {
	if _, err := circleai.NewLoggerAuditLog(nil); err == nil {
		t.Fatal("nil sink should error")
	}
}

func TestCircleAIAuditing_AmbientDefault(t *testing.T) {
	// Default is Noop.
	circleai.ResetAuditingToNoop()
	if circleai.AuditingDefault() == nil {
		t.Fatal("default sink should never be nil")
	}
	var seen int
	custom, _ := circleai.NewLoggerAuditLog(func(string) { seen++ })
	if err := circleai.SetAuditingDefault(custom); err != nil {
		t.Fatalf("set default: %v", err)
	}
	_ = circleai.AuditingDefault().Record(context.Background(), circleai.CircleAIAuditEntry{Component: "C", Operation: "O", Outcome: "success"})
	if seen != 1 {
		t.Errorf("ambient default should route to custom sink, seen=%d", seen)
	}
	if err := circleai.SetAuditingDefault(nil); err == nil {
		t.Error("nil default should error")
	}
	// Restore.
	circleai.ResetAuditingToNoop()
}
