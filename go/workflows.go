// workflows.go
//
// Ports CircleAI.Workflows (Contracts.cs + NullImplementations.cs) and the
// conversation state machine from PacaConversations.cs:
//
//	WorkflowPhase / ConversationState (enums)      -> int consts (declaration ordinals)
//	WorkflowDefinition / WorkflowExecution / CheckpointPayload (records) -> structs
//	AgentConversation / ConversationStep / ConversationPermissions (records)
//	IWorkflowDefinitionStore / IWorkflowRunner / IWorkflowState -> interfaces
//	IConversationExecutor                           -> ConversationExecutor
//	NullWorkflow* (impls)                           -> null impls
//	InMemoryWorkflow* (added)                        -> real in-memory impls
//	PacaConversationRuntime                          -> PacaConversationRuntime
//
// The C# Workflows Contracts ship only Null implementations; the port keeps those
// faithful AND adds trivial real in-memory stores (definition store, runner,
// checkpoint state) so the deterministic-in-memory mandate has non-null impls,
// matching every sibling module's InMemory* pairing.
//
// CONCURRENCY (PacaConversationRuntime.Start): the executor's onStep callback
// appends under the per-conversation step lock; the conversation is snapshotted
// into the Running state before RunAsync so a step callback that reads state sees
// a consistent view. Cancellation flips the conversation to Stopped; a returned
// error flips it to Failed (matching the C# catch arms).

package circleai

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"time"
)

// WorkflowPhase is a durable workflow's phase. Ports WorkflowPhase
// (declaration-order ordinals: Pending=0, Running=1, Suspended=2, Completed=3,
// Failed=4).
type WorkflowPhase int

const (
	// WorkflowPhasePending — created, not started.
	WorkflowPhasePending WorkflowPhase = 0
	// WorkflowPhaseRunning — running.
	WorkflowPhaseRunning WorkflowPhase = 1
	// WorkflowPhaseSuspended — suspended.
	WorkflowPhaseSuspended WorkflowPhase = 2
	// WorkflowPhaseCompleted — completed.
	WorkflowPhaseCompleted WorkflowPhase = 3
	// WorkflowPhaseFailed — failed.
	WorkflowPhaseFailed WorkflowPhase = 4
)

// WorkflowDefinition is a registered workflow definition. Ports the
// WorkflowDefinition record.
type WorkflowDefinition struct {
	DefinitionID string
	Name         string
	Version      string
	Description  string
}

// WorkflowExecution is a workflow run. Ports the WorkflowExecution record.
// FailureReason is empty on success (C# nullable string).
type WorkflowExecution struct {
	RunID        string
	DefinitionID string
	Phase        WorkflowPhase
	StartUTC     time.Time
	FailureReason string
}

// CheckpointPayload is a durable step checkpoint. Ports the CheckpointPayload
// record. StateBlob is a byte slice (C# ReadOnlyMemory<byte>).
type CheckpointPayload struct {
	RunID     string
	StepID    string
	StateBlob []byte
}

// WorkflowDefinitionStore stores workflow definitions. Ports IWorkflowDefinitionStore.
type WorkflowDefinitionStore interface {
	BackendID() string
	Upsert(ctx context.Context, d WorkflowDefinition) error
	// Get returns the definition for id and true, or (zero, false) if absent.
	Get(ctx context.Context, id string) (WorkflowDefinition, bool)
}

// WorkflowRunner starts, reads, and cancels workflow runs. Ports IWorkflowRunner.
type WorkflowRunner interface {
	BackendID() string
	Start(ctx context.Context, definitionID string, inputs map[string]any) (WorkflowExecution, error)
	// Get returns the run for runID and true, or (zero, false) if absent.
	Get(ctx context.Context, runID string) (WorkflowExecution, bool)
	Cancel(ctx context.Context, runID string) error
}

// WorkflowState persists + loads step checkpoints. Ports IWorkflowState.
type WorkflowState interface {
	BackendID() string
	Checkpoint(ctx context.Context, payload CheckpointPayload) error
	// Load returns the checkpoint for (runID, stepID) and true, or (zero, false).
	Load(ctx context.Context, runID, stepID string) (CheckpointPayload, bool)
}

// ── real in-memory implementations ──────────────────────────────────────────

// InMemoryWorkflowDefinitionStore is a real in-memory definition store.
// Construct with NewInMemoryWorkflowDefinitionStore.
type InMemoryWorkflowDefinitionStore struct {
	mu    sync.Mutex
	items map[string]WorkflowDefinition
}

// NewInMemoryWorkflowDefinitionStore constructs an empty store.
func NewInMemoryWorkflowDefinitionStore() *InMemoryWorkflowDefinitionStore {
	return &InMemoryWorkflowDefinitionStore{items: make(map[string]WorkflowDefinition)}
}

// BackendID returns "in-memory".
func (s *InMemoryWorkflowDefinitionStore) BackendID() string { return "in-memory" }

// Upsert stores (or replaces by DefinitionId) a definition.
func (s *InMemoryWorkflowDefinitionStore) Upsert(ctx context.Context, d WorkflowDefinition) error {
	s.mu.Lock()
	s.items[d.DefinitionID] = d
	s.mu.Unlock()
	return nil
}

// Get returns the definition for id.
func (s *InMemoryWorkflowDefinitionStore) Get(ctx context.Context, id string) (WorkflowDefinition, bool) {
	s.mu.Lock()
	d, ok := s.items[id]
	s.mu.Unlock()
	return d, ok
}

// InMemoryWorkflowRunner starts runs against a definition store and tracks them.
// A started run whose definition exists is Running; an unknown definition yields
// a Failed run. Construct with NewInMemoryWorkflowRunner.
type InMemoryWorkflowRunner struct {
	defs *InMemoryWorkflowDefinitionStore
	mu   sync.Mutex
	runs map[string]WorkflowExecution
	seq  int64
}

// NewInMemoryWorkflowRunner constructs a runner over defs (may be nil, in which
// case every definition is treated as unknown).
func NewInMemoryWorkflowRunner(defs *InMemoryWorkflowDefinitionStore) *InMemoryWorkflowRunner {
	return &InMemoryWorkflowRunner{defs: defs, runs: make(map[string]WorkflowExecution)}
}

// BackendID returns "in-memory".
func (r *InMemoryWorkflowRunner) BackendID() string { return "in-memory" }

// Start begins a run. A known definition yields a Running run; an unknown one
// yields a Failed run tagged with the reason.
func (r *InMemoryWorkflowRunner) Start(ctx context.Context, definitionID string, inputs map[string]any) (WorkflowExecution, error) {
	if definitionID == "" {
		return WorkflowExecution{}, errors.New("definitionId required")
	}
	runID := "wf-" + itoa64(atomic.AddInt64(&r.seq, 1))
	phase := WorkflowPhaseRunning
	failure := ""
	if r.defs != nil {
		if _, ok := r.defs.Get(ctx, definitionID); !ok {
			phase = WorkflowPhaseFailed
			failure = "Unknown workflow definition '" + definitionID + "'."
		}
	}
	exec := WorkflowExecution{
		RunID:         runID,
		DefinitionID:  definitionID,
		Phase:         phase,
		StartUTC:      time.Now().UTC(),
		FailureReason: failure,
	}
	r.mu.Lock()
	r.runs[runID] = exec
	r.mu.Unlock()
	return exec, nil
}

// Get returns the run for runID.
func (r *InMemoryWorkflowRunner) Get(ctx context.Context, runID string) (WorkflowExecution, bool) {
	r.mu.Lock()
	e, ok := r.runs[runID]
	r.mu.Unlock()
	return e, ok
}

// Cancel flips a run to Failed with reason "cancelled" (no-op if unknown).
func (r *InMemoryWorkflowRunner) Cancel(ctx context.Context, runID string) error {
	r.mu.Lock()
	if e, ok := r.runs[runID]; ok {
		e.Phase = WorkflowPhaseFailed
		e.FailureReason = "cancelled"
		r.runs[runID] = e
	}
	r.mu.Unlock()
	return nil
}

// InMemoryWorkflowState is a real in-memory checkpoint store. Construct with
// NewInMemoryWorkflowState.
type InMemoryWorkflowState struct {
	mu          sync.Mutex
	checkpoints map[string]CheckpointPayload // key = runID + "|" + stepID
}

// NewInMemoryWorkflowState constructs an empty checkpoint store.
func NewInMemoryWorkflowState() *InMemoryWorkflowState {
	return &InMemoryWorkflowState{checkpoints: make(map[string]CheckpointPayload)}
}

// BackendID returns "in-memory".
func (s *InMemoryWorkflowState) BackendID() string { return "in-memory" }

// Checkpoint stores (or replaces by run+step) a checkpoint.
func (s *InMemoryWorkflowState) Checkpoint(ctx context.Context, payload CheckpointPayload) error {
	s.mu.Lock()
	s.checkpoints[payload.RunID+"|"+payload.StepID] = payload
	s.mu.Unlock()
	return nil
}

// Load returns the checkpoint for (runID, stepID).
func (s *InMemoryWorkflowState) Load(ctx context.Context, runID, stepID string) (CheckpointPayload, bool) {
	s.mu.Lock()
	c, ok := s.checkpoints[runID+"|"+stepID]
	s.mu.Unlock()
	return c, ok
}

// ── Null implementations ────────────────────────────────────────────────────

// NullWorkflowDefinitionStore is a no-op definition store. Ports
// NullWorkflowDefinitionStore.
type NullWorkflowDefinitionStore struct{}

// NullWorkflowDefinitionStoreInstance mirrors NullWorkflowDefinitionStore.Instance.
var NullWorkflowDefinitionStoreInstance = NullWorkflowDefinitionStore{}

// BackendID returns "null".
func (NullWorkflowDefinitionStore) BackendID() string                          { return "null" }
func (NullWorkflowDefinitionStore) Upsert(context.Context, WorkflowDefinition) error { return nil }
func (NullWorkflowDefinitionStore) Get(context.Context, string) (WorkflowDefinition, bool) {
	return WorkflowDefinition{}, false
}

// NullWorkflowRunner is a no-op runner. Ports NullWorkflowRunner.
type NullWorkflowRunner struct{}

// NullWorkflowRunnerInstance mirrors NullWorkflowRunner.Instance.
var NullWorkflowRunnerInstance = NullWorkflowRunner{}

// BackendID returns "null".
func (NullWorkflowRunner) BackendID() string { return "null" }

// Start returns a failed run tagged "NullWorkflowRunner". Ports StartAsync (uses
// the empty-GUID run id + DateTimeOffset.MinValue).
func (NullWorkflowRunner) Start(ctx context.Context, definitionID string, inputs map[string]any) (WorkflowExecution, error) {
	return WorkflowExecution{
		RunID:         emptyGUID,
		DefinitionID:  definitionID,
		Phase:         WorkflowPhaseFailed,
		StartUTC:      time.Time{},
		FailureReason: "NullWorkflowRunner",
	}, nil
}
func (NullWorkflowRunner) Get(context.Context, string) (WorkflowExecution, bool) {
	return WorkflowExecution{}, false
}
func (NullWorkflowRunner) Cancel(context.Context, string) error { return nil }

// NullWorkflowState is a no-op checkpoint store. Ports NullWorkflowState.
type NullWorkflowState struct{}

// NullWorkflowStateInstance mirrors NullWorkflowState.Instance.
var NullWorkflowStateInstance = NullWorkflowState{}

// BackendID returns "null".
func (NullWorkflowState) BackendID() string                              { return "null" }
func (NullWorkflowState) Checkpoint(context.Context, CheckpointPayload) error { return nil }
func (NullWorkflowState) Load(context.Context, string, string) (CheckpointPayload, bool) {
	return CheckpointPayload{}, false
}

// ── Conversation state machine (PacaConversations) ──────────────────────────

// ConversationState is a conversation's lifecycle state. Ports ConversationState
// (declaration-order ordinals: Queued=0, Running=1, Finished=2, Failed=3,
// Stopped=4).
type ConversationState int

const (
	// ConversationQueued — queued, not started.
	ConversationQueued ConversationState = 0
	// ConversationRunning — running.
	ConversationRunning ConversationState = 1
	// ConversationFinished — finished successfully.
	ConversationFinished ConversationState = 2
	// ConversationFailed — failed.
	ConversationFailed ConversationState = 3
	// ConversationStopped — stopped by the user.
	ConversationStopped ConversationState = 4
)

// AgentConversation is one conversation between a human + agent(s). Ports the
// AgentConversation record. HumanMemberID / ResultJSON / FailureReason are empty
// and StartedAtUTC / FinishedAtUTC are zero when unset (C# nullable fields).
type AgentConversation struct {
	ID            string
	ProjectID     string
	AgentMemberID string
	HumanMemberID string
	OpeningPrompt string
	State         ConversationState
	QueuedAtUTC   time.Time
	StartedAtUTC  time.Time
	FinishedAtUTC time.Time
	ResultJSON    string
	FailureReason string
}

// ConversationStep is one executed step in a conversation. Ports the
// ConversationStep record. Speaker is "user" / "agent" / "tool".
type ConversationStep struct {
	ConversationID string
	Order          int
	Speaker        string
	ContentJSON    string
	At             time.Time
}

// ConversationPermissions is the flag set gating risky actions. Ports the
// ConversationPermissions record.
type ConversationPermissions struct {
	AllowCloneRepos bool
	AllowCreatePr   bool
}

// ConversationExecutor runs a conversation, emitting steps via onStep. Ports
// IConversationExecutor (the host-supplied OpenHands/Docker executor).
type ConversationExecutor interface {
	Run(ctx context.Context, conversation AgentConversation, permissions ConversationPermissions, onStep func(ConversationStep)) error
}

// PacaConversationRuntime is the conversation registry + state machine. Ports
// PacaConversationRuntime. Construct with NewPacaConversationRuntime.
type PacaConversationRuntime struct {
	mu            sync.Mutex
	conversations map[string]AgentConversation
	steps         map[string]*[]ConversationStep
	stepLocks     map[string]*sync.Mutex
	running       map[string]*pacaRun
	executor      ConversationExecutor
	clock         func() time.Time
}

// pacaRun tracks an in-flight conversation's cancel func + a stop flag that is
// set ONLY by Stop() — mirroring the C# per-conversation CancellationTokenSource,
// so a Stop-driven cancellation maps to Stopped while any other error maps to
// Failed (the C# `when (cts.IsCancellationRequested)` guard).
type pacaRun struct {
	cancel  context.CancelFunc
	stopped int32
}

// NewPacaConversationRuntime constructs a runtime over executor. clock may be nil
// (defaults to UTC now). Panics if executor is nil.
func NewPacaConversationRuntime(executor ConversationExecutor, clock func() time.Time) *PacaConversationRuntime {
	if executor == nil {
		panic("executor must not be nil")
	}
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &PacaConversationRuntime{
		conversations: make(map[string]AgentConversation),
		steps:         make(map[string]*[]ConversationStep),
		stepLocks:     make(map[string]*sync.Mutex),
		running:       make(map[string]*pacaRun),
		executor:      executor,
		clock:         clock,
	}
}

// Queue records a new Queued conversation. Ports Queue. Returns an error if a
// conversation with the id already exists.
func (r *PacaConversationRuntime) Queue(id, projectID, agentMemberID, openingPrompt, humanMemberID string) (AgentConversation, error) {
	c := AgentConversation{
		ID:            id,
		ProjectID:     projectID,
		AgentMemberID: agentMemberID,
		HumanMemberID: humanMemberID,
		OpeningPrompt: openingPrompt,
		State:         ConversationQueued,
		QueuedAtUTC:   r.clock(),
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, exists := r.conversations[id]; exists {
		return AgentConversation{}, errors.New("Conversation '" + id + "' already exists.")
	}
	r.conversations[id] = c
	empty := make([]ConversationStep, 0)
	r.steps[id] = &empty
	r.stepLocks[id] = &sync.Mutex{}
	return c, nil
}

// Get returns the conversation for id and true, or (zero, false). Ports Get.
func (r *PacaConversationRuntime) Get(id string) (AgentConversation, bool) {
	r.mu.Lock()
	c, ok := r.conversations[id]
	r.mu.Unlock()
	return c, ok
}

// Steps returns a snapshot of a conversation's steps. Ports Steps.
func (r *PacaConversationRuntime) Steps(id string) []ConversationStep {
	r.mu.Lock()
	sl := r.stepLocks[id]
	sp := r.steps[id]
	r.mu.Unlock()
	if sl == nil || sp == nil {
		return []ConversationStep{}
	}
	sl.Lock()
	out := make([]ConversationStep, len(*sp))
	copy(out, *sp)
	sl.Unlock()
	return out
}

// Start executes a queued conversation to completion (synchronously). Ports
// StartAsync — the C# awaits the executor; the Go port runs it inline so callers
// control concurrency (run it in a goroutine for background execution). Returns
// an error if the conversation is not in the Queued state.
func (r *PacaConversationRuntime) Start(ctx context.Context, id string, permissions ConversationPermissions) error {
	r.mu.Lock()
	current, ok := r.conversations[id]
	if !ok || current.State != ConversationQueued {
		r.mu.Unlock()
		return errors.New("Conversation '" + id + "' is not in Queued state.")
	}
	started := current
	started.State = ConversationRunning
	started.StartedAtUTC = r.clock()
	r.conversations[id] = started
	sl := r.stepLocks[id]
	sp := r.steps[id]
	runCtx, cancel := context.WithCancel(ctx)
	run := &pacaRun{cancel: cancel}
	r.running[id] = run
	r.mu.Unlock()

	onStep := func(step ConversationStep) {
		sl.Lock()
		*sp = append(*sp, step)
		sl.Unlock()
	}

	err := r.executor.Run(runCtx, started, permissions, onStep)

	stopRequested := atomic.LoadInt32(&run.stopped) == 1
	r.mu.Lock()
	delete(r.running, id)
	final := started
	final.FinishedAtUTC = r.clock()
	switch {
	case err != nil && stopRequested:
		// Stop-driven cancellation -> Stopped (C# catch-when(cts.IsCancellationRequested)).
		final.State = ConversationStopped
	case err != nil:
		final.State = ConversationFailed
		final.FailureReason = err.Error()
	default:
		final.State = ConversationFinished
		final.ResultJSON = "{}"
	}
	r.conversations[id] = final
	r.mu.Unlock()
	cancel()
	return nil
}

// Stop cancels a running conversation from the UI. Ports Stop (no-op if not
// running). Setting the stop flag makes the resulting terminal state Stopped.
func (r *PacaConversationRuntime) Stop(id string) {
	r.mu.Lock()
	run := r.running[id]
	r.mu.Unlock()
	if run != nil {
		atomic.StoreInt32(&run.stopped, 1)
		run.cancel()
	}
}

// Interface guards.
var (
	_ WorkflowDefinitionStore = (*InMemoryWorkflowDefinitionStore)(nil)
	_ WorkflowRunner          = (*InMemoryWorkflowRunner)(nil)
	_ WorkflowState           = (*InMemoryWorkflowState)(nil)
	_ WorkflowDefinitionStore = NullWorkflowDefinitionStore{}
	_ WorkflowRunner          = NullWorkflowRunner{}
	_ WorkflowState           = NullWorkflowState{}
)
