// workflows_test.go
//
// Verifies the CircleAI.Workflows port (workflows.go): the in-memory definition
// store / runner (known vs unknown definition) / checkpoint state, the null impls,
// and the PacaConversationRuntime state machine (queue -> run -> finished, and
// stop -> stopped).

package circleai_test

import (
	"context"
	"errors"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestWorkflows_DefinitionStoreRunnerState(t *testing.T) {
	defs := circleai.NewInMemoryWorkflowDefinitionStore()
	_ = defs.Upsert(context.Background(), circleai.WorkflowDefinition{DefinitionID: "wf1", Name: "Flow", Version: "1"})
	runner := circleai.NewInMemoryWorkflowRunner(defs)

	run, err := runner.Start(context.Background(), "wf1", map[string]any{"a": 1})
	if err != nil || run.Phase != circleai.WorkflowPhaseRunning {
		t.Fatalf("start known = %+v err=%v", run, err)
	}
	if got, ok := runner.Get(context.Background(), run.RunID); !ok || got.RunID != run.RunID {
		t.Fatalf("get run failed: %+v ok=%v", got, ok)
	}
	// Unknown definition -> Failed run.
	bad, _ := runner.Start(context.Background(), "ghost", nil)
	if bad.Phase != circleai.WorkflowPhaseFailed || bad.FailureReason == "" {
		t.Fatalf("unknown definition run = %+v", bad)
	}
	// Cancel flips to Failed.
	_ = runner.Cancel(context.Background(), run.RunID)
	if got, _ := runner.Get(context.Background(), run.RunID); got.Phase != circleai.WorkflowPhaseFailed {
		t.Fatalf("cancelled run = %+v", got)
	}

	state := circleai.NewInMemoryWorkflowState()
	_ = state.Checkpoint(context.Background(), circleai.CheckpointPayload{RunID: "r", StepID: "s", StateBlob: []byte{9}})
	if cp, ok := state.Load(context.Background(), "r", "s"); !ok || len(cp.StateBlob) != 1 {
		t.Fatalf("checkpoint load = %+v ok=%v", cp, ok)
	}
}

func TestWorkflows_NullImpls(t *testing.T) {
	run, _ := circleai.NullWorkflowRunnerInstance.Start(context.Background(), "x", nil)
	if run.Phase != circleai.WorkflowPhaseFailed || run.FailureReason != "NullWorkflowRunner" {
		t.Fatalf("null runner = %+v", run)
	}
	if _, ok := circleai.NullWorkflowStateInstance.Load(context.Background(), "r", "s"); ok {
		t.Fatalf("null state must have no checkpoint")
	}
}

// fakeConvExecutor emits a step, then either finishes, errors, or blocks until
// cancelled (to exercise the Stopped path).
type fakeConvExecutor struct {
	err        error
	blockUntil bool
}

func (f fakeConvExecutor) Run(ctx context.Context, c circleai.AgentConversation, p circleai.ConversationPermissions, onStep func(circleai.ConversationStep)) error {
	onStep(circleai.ConversationStep{ConversationID: c.ID, Order: 1, Speaker: "agent", ContentJSON: "{}"})
	if f.blockUntil {
		<-ctx.Done()
		return ctx.Err()
	}
	return f.err
}

func TestWorkflows_ConversationFinishes(t *testing.T) {
	rt := circleai.NewPacaConversationRuntime(fakeConvExecutor{}, nil)
	if _, err := rt.Queue("c1", "p", "agent1", "hi", "human1"); err != nil {
		t.Fatalf("queue: %v", err)
	}
	if _, err := rt.Queue("c1", "p", "a", "x", ""); err == nil {
		t.Fatalf("duplicate queue must error")
	}
	if err := rt.Start(context.Background(), "c1", circleai.ConversationPermissions{}); err != nil {
		t.Fatalf("start: %v", err)
	}
	got, _ := rt.Get("c1")
	if got.State != circleai.ConversationFinished || got.ResultJSON != "{}" {
		t.Fatalf("finished conversation = %+v", got)
	}
	if steps := rt.Steps("c1"); len(steps) != 1 {
		t.Fatalf("steps = %d, want 1", len(steps))
	}
}

func TestWorkflows_ConversationFails(t *testing.T) {
	rt := circleai.NewPacaConversationRuntime(fakeConvExecutor{err: errors.New("kaboom")}, nil)
	_, _ = rt.Queue("c2", "p", "a", "x", "")
	_ = rt.Start(context.Background(), "c2", circleai.ConversationPermissions{})
	got, _ := rt.Get("c2")
	if got.State != circleai.ConversationFailed || got.FailureReason != "kaboom" {
		t.Fatalf("failed conversation = %+v", got)
	}
	// Starting a non-queued conversation errors.
	if err := rt.Start(context.Background(), "c2", circleai.ConversationPermissions{}); err == nil {
		t.Fatalf("re-start of finished conversation must error")
	}
}

func TestWorkflows_ConversationStopped(t *testing.T) {
	rt := circleai.NewPacaConversationRuntime(fakeConvExecutor{blockUntil: true}, nil)
	_, _ = rt.Queue("c3", "p", "a", "x", "")
	done := make(chan struct{})
	go func() {
		_ = rt.Start(context.Background(), "c3", circleai.ConversationPermissions{})
		close(done)
	}()
	// Wait until the conversation is Running, then stop it.
	for {
		if c, ok := rt.Get("c3"); ok && c.State == circleai.ConversationRunning {
			break
		}
	}
	rt.Stop("c3")
	<-done
	got, _ := rt.Get("c3")
	if got.State != circleai.ConversationStopped {
		t.Fatalf("stopped conversation = %+v", got)
	}
}
