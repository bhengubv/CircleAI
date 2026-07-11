// pipelines.go
//
// Ports CircleAI.Pipelines (Contracts.cs + InMemoryPipelines.cs +
// NullImplementations.cs): data-pipeline source/sink/executor contracts and a
// tiny in-memory SELECT-only database query tool.
//
//	PipelineRecord / PipelineRun / DatabaseQueryResult (records) -> structs
//	IPipelineSource / IPipelineSink / IPipelineExecutor          -> interfaces
//	IDatabaseQueryTool                                          -> DatabaseQueryTool
//	InMemoryPipelineSource / Sink / Executor                     -> in-memory impls
//	InMemoryDatabaseQueryTool                                    -> in-memory impl
//	NullPipeline* / NullDatabaseQueryTool                        -> null impls
//
// C# IReadOnlyDictionary<string, object?> maps to Go map[string]any. The C#
// IAsyncEnumerable<PipelineRecord> ReadAsync maps to a receive-only <-chan
// PipelineRecord driven by the shared unboundedChannel[T] primitive (which
// mirrors System.Threading.Channels.Channel.CreateUnbounded semantics: writes
// never block, competing consumers, completion drains-then-closes).

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// PipelineRecord is one record flowing through a pipeline. Ports the
// PipelineRecord record. Values is a string-keyed map of arbitrary values.
type PipelineRecord struct {
	Stream string
	Values map[string]any
}

// PipelineRun is a completed (or in-flight) pipeline run. Ports the PipelineRun
// record. EndUTC is the zero Time when the run has not ended (C# nullable
// DateTimeOffset); FailureReason is empty on success (C# nullable string).
type PipelineRun struct {
	RunID         string
	PipelineID    string
	StartUTC      time.Time
	EndUTC        time.Time
	RowsProcessed int64
	FailureReason string
}

// PipelineSource reads records from a named stream. Ports IPipelineSource.
type PipelineSource interface {
	BackendID() string
	// Read returns a receive-only channel yielding records for stream until the
	// stream is completed-and-drained or ctx is cancelled, then closes.
	Read(ctx context.Context, stream string) <-chan PipelineRecord
}

// PipelineSink writes records to a destination. Ports IPipelineSink.
type PipelineSink interface {
	BackendID() string
	Write(ctx context.Context, record PipelineRecord) error
	Flush(ctx context.Context) error
}

// PipelineExecutor runs registered pipelines and tracks runs. Ports
// IPipelineExecutor.
type PipelineExecutor interface {
	BackendID() string
	Run(ctx context.Context, pipelineID string) (PipelineRun, error)
	// GetRun returns the run for runID and true, or (zero, false) if absent.
	GetRun(ctx context.Context, runID string) (PipelineRun, bool)
}

// DatabaseQueryResult is the result of a query. Ports the DatabaseQueryResult
// record.
type DatabaseQueryResult struct {
	Rows     []map[string]any
	RowCount int
}

// DatabaseQueryTool runs SQL queries. Ports IDatabaseQueryTool. parameters may
// be nil (C# nullable IReadOnlyDictionary default).
type DatabaseQueryTool interface {
	BackendID() string
	Query(ctx context.Context, sql string, parameters map[string]any) (DatabaseQueryResult, error)
}

// ── InMemoryPipelineSource ──────────────────────────────────────────────────

// InMemoryPipelineSource holds per-stream unbounded channels. Ports
// InMemoryPipelineSource. The zero value is not usable — construct with
// NewInMemoryPipelineSource.
type InMemoryPipelineSource struct {
	mu      sync.Mutex
	streams map[string]*unboundedChannel[PipelineRecord]
}

// NewInMemoryPipelineSource constructs an empty source.
func NewInMemoryPipelineSource() *InMemoryPipelineSource {
	return &InMemoryPipelineSource{streams: make(map[string]*unboundedChannel[PipelineRecord])}
}

func (s *InMemoryPipelineSource) channel(stream string) *unboundedChannel[PipelineRecord] {
	s.mu.Lock()
	defer s.mu.Unlock()
	ch, ok := s.streams[stream]
	if !ok {
		ch = newUnboundedChannel[PipelineRecord]()
		s.streams[stream] = ch
	}
	return ch
}

// BackendID returns "in-memory".
func (s *InMemoryPipelineSource) BackendID() string { return "in-memory" }

// Push enqueues a record onto a stream. Ports Push. Panics if stream is blank
// (mirrors the C# ArgumentException).
func (s *InMemoryPipelineSource) Push(stream string, record PipelineRecord) {
	if strings.TrimSpace(stream) == "" {
		panic("stream required")
	}
	s.channel(stream).Write(record)
}

// Complete marks a stream completed so readers drain-then-finish. Ports
// Complete (no-op when the stream was never created).
func (s *InMemoryPipelineSource) Complete(stream string) {
	s.mu.Lock()
	ch, ok := s.streams[stream]
	s.mu.Unlock()
	if ok {
		ch.Complete()
	}
}

// Read returns a channel yielding stream records until completed-and-drained or
// ctx cancellation. Ports ReadAsync. Panics if stream is blank.
func (s *InMemoryPipelineSource) Read(ctx context.Context, stream string) <-chan PipelineRecord {
	if strings.TrimSpace(stream) == "" {
		panic("stream required")
	}
	return s.channel(stream).ReadAll(ctx)
}

// ── InMemoryPipelineSink ────────────────────────────────────────────────────

// InMemoryPipelineSink accumulates records in insertion order. Ports
// InMemoryPipelineSink. The zero value is ready to use.
type InMemoryPipelineSink struct {
	mu      sync.Mutex
	records []PipelineRecord
}

// BackendID returns "in-memory".
func (s *InMemoryPipelineSink) BackendID() string { return "in-memory" }

// Write appends a record. Ports WriteAsync.
func (s *InMemoryPipelineSink) Write(ctx context.Context, record PipelineRecord) error {
	s.mu.Lock()
	s.records = append(s.records, record)
	s.mu.Unlock()
	return nil
}

// Flush is a no-op. Ports FlushAsync.
func (s *InMemoryPipelineSink) Flush(ctx context.Context) error { return nil }

// Records returns a snapshot of the accumulated records. Ports the Records
// property.
func (s *InMemoryPipelineSink) Records() []PipelineRecord {
	s.mu.Lock()
	out := make([]PipelineRecord, len(s.records))
	copy(out, s.records)
	s.mu.Unlock()
	return out
}

// ── InMemoryPipelineExecutor ────────────────────────────────────────────────

// InMemoryPipelineExecutor runs registered pipeline funcs and tracks runs.
// Ports InMemoryPipelineExecutor. A registered runner returns (rowsProcessed,
// error); a returned error is captured as the run's FailureReason (matching the
// C# try/catch that records ex.Message). Construct with
// NewInMemoryPipelineExecutor.
type InMemoryPipelineExecutor struct {
	mu        sync.Mutex
	pipelines map[string]func(ctx context.Context) (int64, error)
	runs      map[string]PipelineRun
	runSeq    int64
}

// NewInMemoryPipelineExecutor constructs an empty executor.
func NewInMemoryPipelineExecutor() *InMemoryPipelineExecutor {
	return &InMemoryPipelineExecutor{
		pipelines: make(map[string]func(ctx context.Context) (int64, error)),
		runs:      make(map[string]PipelineRun),
	}
}

// BackendID returns "in-memory".
func (e *InMemoryPipelineExecutor) BackendID() string { return "in-memory" }

// Register stores (or replaces by pipelineID) a runner. Ports Register. Panics
// if pipelineID is blank or runner is nil.
func (e *InMemoryPipelineExecutor) Register(pipelineID string, runner func(ctx context.Context) (int64, error)) {
	if strings.TrimSpace(pipelineID) == "" {
		panic("pipelineId required")
	}
	if runner == nil {
		panic("runner must not be nil")
	}
	e.mu.Lock()
	e.pipelines[pipelineID] = runner
	e.mu.Unlock()
}

// Run executes the named pipeline, capturing rows and any failure. Ports
// RunAsync. Returns an error only when the pipeline is unknown; a runner error
// is captured as the run's FailureReason (the run is still returned).
func (e *InMemoryPipelineExecutor) Run(ctx context.Context, pipelineID string) (PipelineRun, error) {
	if strings.TrimSpace(pipelineID) == "" {
		return PipelineRun{}, errors.New("pipelineId required")
	}
	e.mu.Lock()
	runner, ok := e.pipelines[pipelineID]
	e.mu.Unlock()
	if !ok {
		return PipelineRun{}, errors.New("Unknown pipeline '" + pipelineID + "'.")
	}
	runID := "run-" + itoa64(atomic.AddInt64(&e.runSeq, 1))
	start := time.Now().UTC()
	var rows int64
	var failure string
	if r, err := runner(ctx); err != nil {
		failure = err.Error()
	} else {
		rows = r
	}
	run := PipelineRun{
		RunID:         runID,
		PipelineID:    pipelineID,
		StartUTC:      start,
		EndUTC:        time.Now().UTC(),
		RowsProcessed: rows,
		FailureReason: failure,
	}
	e.mu.Lock()
	e.runs[runID] = run
	e.mu.Unlock()
	return run, nil
}

// GetRun returns the run for runID. Ports GetRunAsync.
func (e *InMemoryPipelineExecutor) GetRun(ctx context.Context, runID string) (PipelineRun, bool) {
	e.mu.Lock()
	r, ok := e.runs[runID]
	e.mu.Unlock()
	return r, ok
}

// ── InMemoryDatabaseQueryTool ───────────────────────────────────────────────

// InMemoryDatabaseQueryTool is a tiny in-memory database supporting simple
// "SELECT * FROM <table>" queries against registered tables (table names are
// case-insensitive, matching the C# OrdinalIgnoreCase dictionary). Ports
// InMemoryDatabaseQueryTool. Construct with NewInMemoryDatabaseQueryTool.
type InMemoryDatabaseQueryTool struct {
	mu     sync.Mutex
	tables map[string][]map[string]any // key is lower-cased table name
}

// NewInMemoryDatabaseQueryTool constructs an empty query tool.
func NewInMemoryDatabaseQueryTool() *InMemoryDatabaseQueryTool {
	return &InMemoryDatabaseQueryTool{tables: make(map[string][]map[string]any)}
}

// BackendID returns "in-memory".
func (d *InMemoryDatabaseQueryTool) BackendID() string { return "in-memory" }

// Insert appends a row to a table (creating it if needed). Ports Insert. The
// row is copied so later mutation of the caller's map does not leak in. Panics
// if tableName is blank.
func (d *InMemoryDatabaseQueryTool) Insert(tableName string, row map[string]any) {
	if strings.TrimSpace(tableName) == "" {
		panic("tableName required")
	}
	copied := make(map[string]any, len(row))
	for k, v := range row {
		copied[k] = v
	}
	key := strings.ToLower(tableName)
	d.mu.Lock()
	d.tables[key] = append(d.tables[key], copied)
	d.mu.Unlock()
}

// Query executes a SELECT * FROM <table>. Ports QueryAsync. Returns an error
// for a blank SQL, a non-SELECT statement, or a SELECT lacking a FROM clause
// (mirroring the C# ArgumentException / NotSupportedException / InvalidOperationException).
// An unknown table yields an empty result, not an error.
func (d *InMemoryDatabaseQueryTool) Query(ctx context.Context, sql string, parameters map[string]any) (DatabaseQueryResult, error) {
	if strings.TrimSpace(sql) == "" {
		return DatabaseQueryResult{}, errors.New("sql required")
	}
	trimmed := strings.TrimSpace(sql)
	if !hasPrefixFold(trimmed, "SELECT ") {
		return DatabaseQueryResult{}, errors.New("Only SELECT queries are supported by InMemoryDatabaseQueryTool.")
	}
	fromIdx := indexFold(trimmed, "FROM ")
	if fromIdx < 0 {
		return DatabaseQueryResult{}, errors.New("SELECT requires a FROM clause.")
	}
	rest := strings.TrimSpace(trimmed[fromIdx+5:])
	spaceIdx := strings.IndexAny(rest, " ;")
	tableName := rest
	if spaceIdx > 0 {
		tableName = rest[:spaceIdx]
	}
	d.mu.Lock()
	list, ok := d.tables[strings.ToLower(tableName)]
	if !ok {
		d.mu.Unlock()
		return DatabaseQueryResult{Rows: []map[string]any{}, RowCount: 0}, nil
	}
	rows := make([]map[string]any, len(list))
	copy(rows, list)
	d.mu.Unlock()
	return DatabaseQueryResult{Rows: rows, RowCount: len(rows)}, nil
}

// ── Null implementations ────────────────────────────────────────────────────

// NullPipelineSource is a no-op source that yields nothing. Ports
// NullPipelineSource.
type NullPipelineSource struct{}

// NullPipelineSourceInstance mirrors NullPipelineSource.Instance.
var NullPipelineSourceInstance = NullPipelineSource{}

// BackendID returns "null".
func (NullPipelineSource) BackendID() string { return "null" }

// Read returns an already-closed channel. Ports ReadAsync (yield break).
func (NullPipelineSource) Read(ctx context.Context, stream string) <-chan PipelineRecord {
	ch := make(chan PipelineRecord)
	close(ch)
	return ch
}

// NullPipelineSink is a no-op sink. Ports NullPipelineSink.
type NullPipelineSink struct{}

// NullPipelineSinkInstance mirrors NullPipelineSink.Instance.
var NullPipelineSinkInstance = NullPipelineSink{}

// BackendID returns "null".
func (NullPipelineSink) BackendID() string                            { return "null" }
func (NullPipelineSink) Write(context.Context, PipelineRecord) error { return nil }
func (NullPipelineSink) Flush(context.Context) error                 { return nil }

// NullPipelineExecutor is a no-op executor. Ports NullPipelineExecutor.
type NullPipelineExecutor struct{}

// NullPipelineExecutorInstance mirrors NullPipelineExecutor.Instance.
var NullPipelineExecutorInstance = NullPipelineExecutor{}

// BackendID returns "null".
func (NullPipelineExecutor) BackendID() string { return "null" }

// Run returns a failed run tagged "NullPipelineExecutor". Ports RunAsync (uses
// the empty-GUID run id and DateTimeOffset.MinValue).
func (NullPipelineExecutor) Run(ctx context.Context, pipelineID string) (PipelineRun, error) {
	return PipelineRun{
		RunID:         emptyGUID,
		PipelineID:    pipelineID,
		StartUTC:      time.Time{},
		EndUTC:        time.Time{},
		RowsProcessed: 0,
		FailureReason: "NullPipelineExecutor",
	}, nil
}

// GetRun always returns (zero, false). Ports GetRunAsync (null).
func (NullPipelineExecutor) GetRun(ctx context.Context, runID string) (PipelineRun, bool) {
	return PipelineRun{}, false
}

// NullDatabaseQueryTool is a no-op query tool. Ports NullDatabaseQueryTool.
type NullDatabaseQueryTool struct{}

// NullDatabaseQueryToolInstance mirrors NullDatabaseQueryTool.Instance.
var NullDatabaseQueryToolInstance = NullDatabaseQueryTool{}

// BackendID returns "null".
func (NullDatabaseQueryTool) BackendID() string { return "null" }

// Query always returns an empty result. Ports QueryAsync.
func (NullDatabaseQueryTool) Query(ctx context.Context, sql string, parameters map[string]any) (DatabaseQueryResult, error) {
	return DatabaseQueryResult{Rows: []map[string]any{}, RowCount: 0}, nil
}

// Interface guards.
var (
	_ PipelineSource    = (*InMemoryPipelineSource)(nil)
	_ PipelineSink      = (*InMemoryPipelineSink)(nil)
	_ PipelineExecutor  = (*InMemoryPipelineExecutor)(nil)
	_ DatabaseQueryTool = (*InMemoryDatabaseQueryTool)(nil)
	_ PipelineSource    = NullPipelineSource{}
	_ PipelineSink      = NullPipelineSink{}
	_ PipelineExecutor  = NullPipelineExecutor{}
	_ DatabaseQueryTool = NullDatabaseQueryTool{}
)
