// audit_log.go
//
// Ports CircleAI.Core.Auditing.ICircleAIAuditLog + CircleAIAuditEntry +
// CircleAIAuditQuery (ICircleAIAuditLog.cs), CircleAI.Core.Auditing.LoggerAuditLog
// (LoggerAuditLog.cs), CircleAI.Core.Auditing.NoopAuditLog (NoopAuditLog.cs), and
// the ambient accessor CircleAI.Core.Auditing.CircleAIAuditing (CircleAIAuditing.cs).
//
// The audit surface records every state-changing SDK operation. Record MUST
// NOT fail the caller — implementations fail open. C# QueryAsync yields an
// IAsyncEnumerable; the Go port returns a slice (the stream carries only
// entries, no incremental semantics to preserve).

package circleai

import (
	"context"
	"fmt"
	"sync"
	"time"
)

// CircleAIAuditEntry is one immutable audit entry. Ports CircleAIAuditEntry.
type CircleAIAuditEntry struct {
	At               time.Time // UTC timestamp of the action (required)
	Component        string    // canonical component name (required)
	Operation        string    // logical operation name (required)
	Outcome          string    // outcome, one of the Outcomes constants (required)
	TenantID         string    // tenant id when multi-tenant; empty otherwise
	UhidIdentityID   string    // UHID when user-scoped; empty otherwise
	CorrelationID    string    // optional correlation id (session/request)
	DurationMs       float64   // operation duration in milliseconds
	ErrorType        string    // CLR/Go error type when Outcome != success
	ErrorCode        string    // implementation-supplied error code
	PayloadSha256Hex string    // hash of any sensitive payload; never the payload
}

// CircleAIAuditQuery filters QueryAsync. Ports CircleAIAuditQuery. A zero value
// has MaxItems == 0; use NewCircleAIAuditQuery for the C# default of 1000.
type CircleAIAuditQuery struct {
	FromUTC        *time.Time // inclusive lower bound on At
	ToUTC          *time.Time // inclusive upper bound on At
	Component      string     // restrict to a single component
	TenantID       string     // restrict to a single tenant
	UhidIdentityID string     // restrict to a single UHID
	Outcome        string     // restrict to a single outcome
	MaxItems       int        // max entries to return
}

// NewCircleAIAuditQuery returns a query with the C# default MaxItems (1000).
func NewCircleAIAuditQuery() CircleAIAuditQuery {
	return CircleAIAuditQuery{MaxItems: 1000}
}

// ICircleAIAuditLog is the audit sink contract. Ports ICircleAIAuditLog.
type ICircleAIAuditLog interface {
	// Record appends an entry. MUST NOT return an error that would abort the
	// caller — implementations fail open. The error return exists only for
	// implementations that choose to surface a non-fatal note.
	Record(ctx context.Context, entry CircleAIAuditEntry) error

	// Query returns historical entries matching the filter.
	Query(ctx context.Context, query CircleAIAuditQuery) ([]CircleAIAuditEntry, error)
}

// NoopAuditLog silently discards every entry and returns no query results.
// Ports CircleAI.Core.Auditing.NoopAuditLog.
type NoopAuditLog struct{}

// NoopAuditLogInstance is the shared singleton. Mirrors NoopAuditLog.Instance.
var NoopAuditLogInstance = NoopAuditLog{}

// Record discards the entry.
func (NoopAuditLog) Record(context.Context, CircleAIAuditEntry) error { return nil }

// Query returns an empty result.
func (NoopAuditLog) Query(context.Context, CircleAIAuditQuery) ([]CircleAIAuditEntry, error) {
	return nil, nil
}

// AuditLogSink receives a formatted structured audit line. This is the
// injection point that replaces the C# ILogger in LoggerAuditLog.
type AuditLogSink func(message string)

// LoggerAuditLog writes structured entries to an injected sink at
// information level. Query always returns empty — reading back from a log
// pipeline is not possible at the SDK layer. Ports LoggerAuditLog.
type LoggerAuditLog struct {
	sink AuditLogSink
}

// NewLoggerAuditLog builds a logger-backed audit log over the given sink
// (required). Mirrors the C# ctor's null guard.
func NewLoggerAuditLog(sink AuditLogSink) (*LoggerAuditLog, error) {
	if sink == nil {
		return nil, fmt.Errorf("sink is required")
	}
	return &LoggerAuditLog{sink: sink}, nil
}

// Record formats the entry as a structured line and hands it to the sink. The
// template mirrors the C# LogInformation call, with "-" for empty fields.
func (l *LoggerAuditLog) Record(_ context.Context, entry CircleAIAuditEntry) error {
	l.sink(fmt.Sprintf(
		"CircleAI audit %s.%s %s tenant=%s uhid=%s corr=%s duration_ms=%g error=%s(%s) payload_sha256=%s at=%s",
		entry.Component, entry.Operation, entry.Outcome,
		orDash(entry.TenantID), orDash(entry.UhidIdentityID), orDash(entry.CorrelationID),
		entry.DurationMs, orDash(entry.ErrorType), orDash(entry.ErrorCode),
		orDash(entry.PayloadSha256Hex), entry.At.UTC().Format(time.RFC3339Nano)))
	return nil
}

// Query always returns empty.
func (l *LoggerAuditLog) Query(context.Context, CircleAIAuditQuery) ([]CircleAIAuditEntry, error) {
	return nil, nil
}

func orDash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

// ── CircleAIAuditing ambient accessor ─────────────────────────────────────

// circleAIAuditing is the process-wide ambient audit sink. Ports the static
// CircleAI.Core.Auditing.CircleAIAuditing. Guarded for concurrent Set/Get.
var circleAIAuditing = struct {
	mu      sync.RWMutex
	current ICircleAIAuditLog
}{current: NoopAuditLogInstance}

// AuditingDefault returns the current ambient audit sink (default NoopAuditLog).
// Ports CircleAIAuditing.Default.
func AuditingDefault() ICircleAIAuditLog {
	circleAIAuditing.mu.RLock()
	defer circleAIAuditing.mu.RUnlock()
	return circleAIAuditing.current
}

// SetAuditingDefault replaces the ambient audit sink (required, non-nil).
// Ports CircleAIAuditing.SetDefault.
func SetAuditingDefault(audit ICircleAIAuditLog) error {
	if audit == nil {
		return fmt.Errorf("audit sink is required")
	}
	circleAIAuditing.mu.Lock()
	defer circleAIAuditing.mu.Unlock()
	circleAIAuditing.current = audit
	return nil
}

// ResetAuditingToNoop restores the ambient sink to NoopAuditLog. Test helper.
// Ports CircleAIAuditing.ResetToNoop.
func ResetAuditingToNoop() {
	circleAIAuditing.mu.Lock()
	defer circleAIAuditing.mu.Unlock()
	circleAIAuditing.current = NoopAuditLogInstance
}

var (
	_ ICircleAIAuditLog = NoopAuditLog{}
	_ ICircleAIAuditLog = (*LoggerAuditLog)(nil)
)
