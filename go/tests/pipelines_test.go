// pipelines_test.go
//
// Verifies the CircleAI.Pipelines port (pipelines.go): source push/read/complete,
// sink accumulation, executor run (success + failure capture + unknown), the
// SELECT-only in-memory database query tool, and null impls.

package circleai_test

import (
	"context"
	"errors"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestPipelines_SourceReadDrainsThenCloses(t *testing.T) {
	src := circleai.NewInMemoryPipelineSource()
	src.Push("s", circleai.PipelineRecord{Stream: "s", Values: map[string]any{"n": 1}})
	src.Push("s", circleai.PipelineRecord{Stream: "s", Values: map[string]any{"n": 2}})
	src.Complete("s")
	got := 0
	for rec := range src.Read(context.Background(), "s") {
		if rec.Stream != "s" {
			t.Fatalf("stream = %q", rec.Stream)
		}
		got++
	}
	if got != 2 {
		t.Fatalf("read %d records, want 2", got)
	}
}

func TestPipelines_SinkAccumulates(t *testing.T) {
	sink := &circleai.InMemoryPipelineSink{}
	_ = sink.Write(context.Background(), circleai.PipelineRecord{Stream: "a"})
	_ = sink.Write(context.Background(), circleai.PipelineRecord{Stream: "b"})
	_ = sink.Flush(context.Background())
	if recs := sink.Records(); len(recs) != 2 || recs[0].Stream != "a" || recs[1].Stream != "b" {
		t.Fatalf("records = %+v", recs)
	}
}

func TestPipelines_ExecutorRunSuccessFailureUnknown(t *testing.T) {
	ex := circleai.NewInMemoryPipelineExecutor()
	ex.Register("ok", func(ctx context.Context) (int64, error) { return 7, nil })
	ex.Register("bad", func(ctx context.Context) (int64, error) { return 0, errors.New("boom") })

	run, err := ex.Run(context.Background(), "ok")
	if err != nil || run.RowsProcessed != 7 || run.FailureReason != "" {
		t.Fatalf("ok run = %+v err=%v", run, err)
	}
	got, ok := ex.GetRun(context.Background(), run.RunID)
	if !ok || got.RunID != run.RunID {
		t.Fatalf("get run failed: %+v ok=%v", got, ok)
	}
	badRun, err := ex.Run(context.Background(), "bad")
	if err != nil || badRun.FailureReason != "boom" {
		t.Fatalf("failure should be captured, got %+v err=%v", badRun, err)
	}
	if _, err := ex.Run(context.Background(), "missing"); err == nil {
		t.Fatalf("unknown pipeline must error")
	}
}

func TestPipelines_DatabaseQueryTool(t *testing.T) {
	db := circleai.NewInMemoryDatabaseQueryTool()
	db.Insert("Users", map[string]any{"id": 1, "name": "Ada"})
	db.Insert("users", map[string]any{"id": 2, "name": "Bo"}) // same table, case-insensitive
	res, err := db.Query(context.Background(), "SELECT * FROM users", nil)
	if err != nil || res.RowCount != 2 {
		t.Fatalf("select = %+v err=%v", res, err)
	}
	// Unknown table -> empty, not error.
	empty, err := db.Query(context.Background(), "SELECT * FROM ghosts", nil)
	if err != nil || empty.RowCount != 0 {
		t.Fatalf("unknown table = %+v err=%v", empty, err)
	}
	// Non-SELECT -> error.
	if _, err := db.Query(context.Background(), "DELETE FROM users", nil); err == nil {
		t.Fatalf("non-SELECT must error")
	}
	// Blank -> error.
	if _, err := db.Query(context.Background(), "   ", nil); err == nil {
		t.Fatalf("blank sql must error")
	}
}

func TestPipelines_NullImpls(t *testing.T) {
	run, _ := circleai.NullPipelineExecutorInstance.Run(context.Background(), "x")
	if run.FailureReason != "NullPipelineExecutor" {
		t.Fatalf("null executor run = %+v", run)
	}
	res, _ := circleai.NullDatabaseQueryToolInstance.Query(context.Background(), "SELECT 1", nil)
	if res.RowCount != 0 {
		t.Fatalf("null query result = %+v", res)
	}
	// Null source read channel is closed immediately.
	for range circleai.NullPipelineSourceInstance.Read(context.Background(), "s") {
		t.Fatalf("null source must yield nothing")
	}
}
