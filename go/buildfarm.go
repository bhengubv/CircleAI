// buildfarm.go
//
// Ports CircleAI.BuildFarm (Contracts.cs + InMemoryBuildFarm.cs +
// NullImplementations.cs): agent-pool / job-runner / artifact-store primitives.
//
//	BuildAgentKind / BuildJobPhase (enums)          -> int consts (stable ordinals)
//	BuildAgent / BuildJob / BuildArtifact (records)  -> value structs
//	IBuildAgentPool / IBuildJobRunner / IBuildArtifactStore -> interfaces
//	InMemoryBuildAgentPool / JobRunner / ArtifactStore -> in-memory impls
//	NullBuildAgentPool / JobRunner / ArtifactStore    -> null impls
//
// InMemoryBuildAgentPool.Acquire hands out the first free agent of the requested
// kind and marks it busy; Release frees it. The runner is a Running->Succeeded/
// Failed state machine (Complete flips the phase). BuildArtifact.Payload is a
// byte slice (C# ReadOnlyMemory<byte>).

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// BuildAgentKind is the platform class of a build agent. Ports BuildAgentKind
// (stable ordinals).
type BuildAgentKind int

const (
	// BuildAgentLinux — Linux build agent.
	BuildAgentLinux BuildAgentKind = 0
	// BuildAgentMac — macOS build agent.
	BuildAgentMac BuildAgentKind = 1
	// BuildAgentWindows — Windows build agent.
	BuildAgentWindows BuildAgentKind = 2
	// BuildAgentAndroid — Android build agent.
	BuildAgentAndroid BuildAgentKind = 3
	// BuildAgentIos — iOS build agent.
	BuildAgentIos BuildAgentKind = 4
)

// BuildJobPhase is the lifecycle phase of a build job. Ports BuildJobPhase
// (stable ordinals).
type BuildJobPhase int

const (
	// BuildJobPending — created, not started.
	BuildJobPending BuildJobPhase = 0
	// BuildJobRunning — running.
	BuildJobRunning BuildJobPhase = 1
	// BuildJobSucceeded — completed successfully.
	BuildJobSucceeded BuildJobPhase = 2
	// BuildJobFailed — completed with failure.
	BuildJobFailed BuildJobPhase = 3
)

// BuildAgent is a registered build agent. Ports the BuildAgent record. Hardware
// is empty when unknown (C# nullable string).
type BuildAgent struct {
	AgentID  string
	Kind     BuildAgentKind
	Os       string
	Hardware string
}

// BuildJob is a build job. Ports the BuildJob record.
type BuildJob struct {
	JobID    string
	AgentID  string
	Repo     string
	Branch   string
	Phase    BuildJobPhase
	StartUTC time.Time
}

// BuildArtifact is a stored build artifact. Ports the BuildArtifact record.
type BuildArtifact struct {
	ArtifactID string
	JobID      string
	Name       string
	Payload    []byte
}

// BuildAgentPool acquires and releases build agents. Ports IBuildAgentPool.
type BuildAgentPool interface {
	BackendID() string
	// Acquire returns a free agent of kind and true, or (zero, false) when none
	// is available.
	Acquire(ctx context.Context, kind BuildAgentKind) (BuildAgent, bool)
	Release(ctx context.Context, agentID string) error
	List(ctx context.Context) ([]BuildAgent, error)
}

// BuildJobRunner starts and reports build jobs. Ports IBuildJobRunner.
type BuildJobRunner interface {
	BackendID() string
	Start(ctx context.Context, agentID, repo, branch string) (BuildJob, error)
	// Get returns the job for jobID and true, or (zero, false) if absent.
	Get(ctx context.Context, jobID string) (BuildJob, bool)
}

// BuildArtifactStore saves and fetches build artifacts. Ports IBuildArtifactStore.
type BuildArtifactStore interface {
	BackendID() string
	Save(ctx context.Context, artifact BuildArtifact) error
	// Get returns the artifact for artifactID and true, or (zero, false) if absent.
	Get(ctx context.Context, artifactID string) (BuildArtifact, bool)
}

// InMemoryBuildAgentPool tracks agents and a busy set. Ports
// InMemoryBuildAgentPool. Construct with NewInMemoryBuildAgentPool.
type InMemoryBuildAgentPool struct {
	mu   sync.Mutex
	all  map[string]BuildAgent
	busy map[string]bool
	// order preserves registration order so Acquire is deterministic (the C#
	// ConcurrentDictionary.Values enumeration order is unspecified; the port
	// hands out the earliest-registered free agent of the kind).
	order []string
}

// NewInMemoryBuildAgentPool constructs an empty pool.
func NewInMemoryBuildAgentPool() *InMemoryBuildAgentPool {
	return &InMemoryBuildAgentPool{all: make(map[string]BuildAgent), busy: make(map[string]bool)}
}

// BackendID returns "in-memory".
func (p *InMemoryBuildAgentPool) BackendID() string { return "in-memory" }

// Register stores (or replaces by AgentId) an agent. Ports Register.
func (p *InMemoryBuildAgentPool) Register(a BuildAgent) {
	p.mu.Lock()
	if _, exists := p.all[a.AgentID]; !exists {
		p.order = append(p.order, a.AgentID)
	}
	p.all[a.AgentID] = a
	p.mu.Unlock()
}

// Acquire hands out the earliest-registered free agent of kind, marking it busy.
// Ports AcquireAsync (C# returns BuildAgent? -> (agent, found)).
func (p *InMemoryBuildAgentPool) Acquire(ctx context.Context, kind BuildAgentKind) (BuildAgent, bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	for _, id := range p.order {
		a := p.all[id]
		if a.Kind == kind && !p.busy[id] {
			p.busy[id] = true
			return a, true
		}
	}
	return BuildAgent{}, false
}

// Release frees an agent. Ports ReleaseAsync. Returns an error if agentID is
// blank.
func (p *InMemoryBuildAgentPool) Release(ctx context.Context, agentID string) error {
	if strings.TrimSpace(agentID) == "" {
		return errors.New("agentId required")
	}
	p.mu.Lock()
	delete(p.busy, agentID)
	p.mu.Unlock()
	return nil
}

// List returns every registered agent (registration order). Ports ListAsync.
func (p *InMemoryBuildAgentPool) List(ctx context.Context) ([]BuildAgent, error) {
	p.mu.Lock()
	out := make([]BuildAgent, 0, len(p.order))
	for _, id := range p.order {
		out = append(out, p.all[id])
	}
	p.mu.Unlock()
	return out, nil
}

// InMemoryBuildJobRunner runs a Running->Succeeded/Failed state machine. Ports
// InMemoryBuildJobRunner. Construct with NewInMemoryBuildJobRunner.
type InMemoryBuildJobRunner struct {
	mu   sync.Mutex
	jobs map[string]BuildJob
	seq  int64
}

// NewInMemoryBuildJobRunner constructs an empty runner.
func NewInMemoryBuildJobRunner() *InMemoryBuildJobRunner {
	return &InMemoryBuildJobRunner{jobs: make(map[string]BuildJob)}
}

// BackendID returns "in-memory".
func (r *InMemoryBuildJobRunner) BackendID() string { return "in-memory" }

// Start creates a Running job. Ports StartAsync. Returns an error if any of
// agentID/repo/branch is blank.
func (r *InMemoryBuildJobRunner) Start(ctx context.Context, agentID, repo, branch string) (BuildJob, error) {
	if strings.TrimSpace(agentID) == "" {
		return BuildJob{}, errors.New("agentId required")
	}
	if strings.TrimSpace(repo) == "" {
		return BuildJob{}, errors.New("repo required")
	}
	if strings.TrimSpace(branch) == "" {
		return BuildJob{}, errors.New("branch required")
	}
	jobID := "job-" + itoa64(atomic.AddInt64(&r.seq, 1))
	job := BuildJob{
		JobID:    jobID,
		AgentID:  agentID,
		Repo:     repo,
		Branch:   branch,
		Phase:    BuildJobRunning,
		StartUTC: time.Now().UTC(),
	}
	r.mu.Lock()
	r.jobs[jobID] = job
	r.mu.Unlock()
	return job, nil
}

// Get returns the job for jobID. Ports GetAsync.
func (r *InMemoryBuildJobRunner) Get(ctx context.Context, jobID string) (BuildJob, bool) {
	r.mu.Lock()
	j, ok := r.jobs[jobID]
	r.mu.Unlock()
	return j, ok
}

// Complete flips a job to Succeeded or Failed. Ports Complete. Returns an error
// if the job is unknown.
func (r *InMemoryBuildJobRunner) Complete(jobID string, success bool) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	j, ok := r.jobs[jobID]
	if !ok {
		return errors.New("Unknown job " + jobID)
	}
	if success {
		j.Phase = BuildJobSucceeded
	} else {
		j.Phase = BuildJobFailed
	}
	r.jobs[jobID] = j
	return nil
}

// InMemoryBuildArtifactStore stores artifacts by id. Ports
// InMemoryBuildArtifactStore. Construct with NewInMemoryBuildArtifactStore.
type InMemoryBuildArtifactStore struct {
	mu    sync.Mutex
	items map[string]BuildArtifact
}

// NewInMemoryBuildArtifactStore constructs an empty store.
func NewInMemoryBuildArtifactStore() *InMemoryBuildArtifactStore {
	return &InMemoryBuildArtifactStore{items: make(map[string]BuildArtifact)}
}

// BackendID returns "in-memory".
func (s *InMemoryBuildArtifactStore) BackendID() string { return "in-memory" }

// Save stores (or replaces by ArtifactId) an artifact. Ports SaveAsync. Returns
// an error if ArtifactId is blank.
func (s *InMemoryBuildArtifactStore) Save(ctx context.Context, artifact BuildArtifact) error {
	if strings.TrimSpace(artifact.ArtifactID) == "" {
		return errors.New("ArtifactId required")
	}
	s.mu.Lock()
	s.items[artifact.ArtifactID] = artifact
	s.mu.Unlock()
	return nil
}

// Get returns the artifact for artifactID. Ports GetAsync.
func (s *InMemoryBuildArtifactStore) Get(ctx context.Context, artifactID string) (BuildArtifact, bool) {
	s.mu.Lock()
	a, ok := s.items[artifactID]
	s.mu.Unlock()
	return a, ok
}

// ── Null implementations ────────────────────────────────────────────────────

// NullBuildAgentPool is a no-op agent pool. Ports NullBuildAgentPool.
type NullBuildAgentPool struct{}

// NullBuildAgentPoolInstance mirrors NullBuildAgentPool.Instance.
var NullBuildAgentPoolInstance = NullBuildAgentPool{}

// BackendID returns "null".
func (NullBuildAgentPool) BackendID() string { return "null" }
func (NullBuildAgentPool) Acquire(context.Context, BuildAgentKind) (BuildAgent, bool) {
	return BuildAgent{}, false
}
func (NullBuildAgentPool) Release(context.Context, string) error { return nil }
func (NullBuildAgentPool) List(context.Context) ([]BuildAgent, error) {
	return []BuildAgent{}, nil
}

// NullBuildJobRunner is a no-op job runner. Ports NullBuildJobRunner.
type NullBuildJobRunner struct{}

// NullBuildJobRunnerInstance mirrors NullBuildJobRunner.Instance.
var NullBuildJobRunnerInstance = NullBuildJobRunner{}

// BackendID returns "null".
func (NullBuildJobRunner) BackendID() string { return "null" }

// Start returns a failed job with the empty-GUID id. Ports StartAsync.
func (NullBuildJobRunner) Start(ctx context.Context, agentID, repo, branch string) (BuildJob, error) {
	return BuildJob{
		JobID:    emptyGUID,
		AgentID:  agentID,
		Repo:     repo,
		Branch:   branch,
		Phase:    BuildJobFailed,
		StartUTC: time.Time{},
	}, nil
}
func (NullBuildJobRunner) Get(context.Context, string) (BuildJob, bool) { return BuildJob{}, false }

// NullBuildArtifactStore is a no-op artifact store. Ports NullBuildArtifactStore.
type NullBuildArtifactStore struct{}

// NullBuildArtifactStoreInstance mirrors NullBuildArtifactStore.Instance.
var NullBuildArtifactStoreInstance = NullBuildArtifactStore{}

// BackendID returns "null".
func (NullBuildArtifactStore) BackendID() string                       { return "null" }
func (NullBuildArtifactStore) Save(context.Context, BuildArtifact) error { return nil }
func (NullBuildArtifactStore) Get(context.Context, string) (BuildArtifact, bool) {
	return BuildArtifact{}, false
}

// Interface guards.
var (
	_ BuildAgentPool     = (*InMemoryBuildAgentPool)(nil)
	_ BuildJobRunner     = (*InMemoryBuildJobRunner)(nil)
	_ BuildArtifactStore = (*InMemoryBuildArtifactStore)(nil)
	_ BuildAgentPool     = NullBuildAgentPool{}
	_ BuildJobRunner     = NullBuildJobRunner{}
	_ BuildArtifactStore = NullBuildArtifactStore{}
)
