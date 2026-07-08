// server_lifecycle.go
//
// Ports CircleAI.Inference.Server.Lifecycle:
//   ModelLoadDescriptor, ModelLoadState, LoadOutcome, LoadResult, UnloadOutcome
//   (ModelLifecycleTypes.cs),
//   IModelLifecycleManager (IModelLifecycleManager.cs),
//   ModelLifecycleManager (ModelLifecycleManager.cs).
//
// The lifecycle manager is the policy gate around the in-memory model registry:
// it runs the admission gate (already-loaded? VRAM headroom? RAM headroom?)
// before invoking the bridge factory, tracks the on-host footprint, and is the
// sole authorised writer to IInferenceServerModelRegistry for the process.
//
// Per the port NOTE the ICapabilityProbe/HostProfile dependency is injected
// behind IServerCapabilityProbe (returns the VRAM ceiling + total RAM) so the
// admission math is exercised deterministically without a real hardware probe.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"
)

// ServerHostProfile is the minimal host view the lifecycle admission gate needs.
// Projects the fields of CircleAI.Runtime.Capabilities.HostProfile the manager reads.
type ServerHostProfile struct {
	// GpuVramBytes is the dedicated VRAM ceiling (0 when no GPU).
	GpuVramBytes int64
	// TotalPhysicalMemoryBytes is the system RAM ceiling.
	TotalPhysicalMemoryBytes int64
}

// IServerCapabilityProbe probes the host once for the admission gate. Ports the
// ICapabilityProbe.ProbeAsync seam (narrowed to what the manager consumes).
type IServerCapabilityProbe interface {
	Probe(ctx context.Context) (ServerHostProfile, error)
}

// StaticServerCapabilityProbe returns a fixed profile. Deterministic drop-in.
type StaticServerCapabilityProbe struct {
	Profile ServerHostProfile
}

// Probe returns the fixed profile.
func (p StaticServerCapabilityProbe) Probe(context.Context) (ServerHostProfile, error) {
	return p.Profile, nil
}

// ServerBridgeFactoryFunc produces the bridge for a load. Called only after the
// admission gate passes. Ports ModelLoadDescriptor.BridgeFactory
// (Func<CancellationToken, Task<IInferenceBridge>>).
type ServerBridgeFactoryFunc func(ctx context.Context) (IInferenceBridge, error)

// ModelLoadDescriptor is what the caller wants to load. Ports
// CircleAI.Inference.Server.Lifecycle.ModelLoadDescriptor.
type ModelLoadDescriptor struct {
	ModelID           string
	Backend           BackendKind
	RequestedTier     CapabilityTier
	VramRequiredBytes int64
	RamRequiredBytes  int64
	BridgeFactory     ServerBridgeFactoryFunc
}

// ModelLoadState is the runtime view of one loaded model. Ports ModelLoadState.
type ModelLoadState struct {
	ModelID   string
	Backend   BackendKind
	Tier      CapabilityTier
	VramBytes int64
	RamBytes  int64
	LoadedAt  time.Time
}

// LoadOutcome is the outcome enum for a load attempt. Ports LoadOutcome.
type LoadOutcome int

const (
	// LoadOutcomeLoaded — bridge factory ran, registry was updated.
	LoadOutcomeLoaded LoadOutcome = 0
	// LoadOutcomeAlreadyLoaded — the model was already loaded (no-op success).
	LoadOutcomeAlreadyLoaded LoadOutcome = 1
	// LoadOutcomeInsufficientVram — insufficient VRAM headroom.
	LoadOutcomeInsufficientVram LoadOutcome = 2
	// LoadOutcomeInsufficientRam — insufficient RAM headroom.
	LoadOutcomeInsufficientRam LoadOutcome = 3
	// LoadOutcomeFactoryFailed — bridge factory threw (registry untouched).
	LoadOutcomeFactoryFailed LoadOutcome = 4
)

// String returns the C# enum member name.
func (o LoadOutcome) String() string {
	switch o {
	case LoadOutcomeLoaded:
		return "Loaded"
	case LoadOutcomeAlreadyLoaded:
		return "AlreadyLoaded"
	case LoadOutcomeInsufficientVram:
		return "InsufficientVram"
	case LoadOutcomeInsufficientRam:
		return "InsufficientRam"
	case LoadOutcomeFactoryFailed:
		return "FactoryFailed"
	default:
		return "Unknown"
	}
}

// LoadResult is the result of a load attempt. Ports LoadResult. State is nil
// when the load did not (re)establish a state.
type LoadResult struct {
	Outcome   LoadOutcome
	State     *ModelLoadState
	Rationale string
}

// UnloadOutcome is the outcome enum for an unload. Ports UnloadOutcome.
type UnloadOutcome int

const (
	// UnloadOutcomeUnloaded — model was loaded; bridge disposed and removed.
	UnloadOutcomeUnloaded UnloadOutcome = 0
	// UnloadOutcomeNotLoaded — model was not loaded; nothing to do.
	UnloadOutcomeNotLoaded UnloadOutcome = 1
)

// IModelLifecycleManager admits/rejects loads and keeps the loaded-model ledger.
// Ports CircleAI.Inference.Server.Lifecycle.IModelLifecycleManager.
type IModelLifecycleManager interface {
	Load(ctx context.Context, descriptor ModelLoadDescriptor) (LoadResult, error)
	Unload(ctx context.Context, modelID string) (UnloadOutcome, error)
	List() []ModelLoadState
	TotalAllocatedVramBytes() int64
	TotalAllocatedRamBytes() int64
}

// ModelLifecycleManager is the default IModelLifecycleManager. Ports
// CircleAI.Inference.Server.Lifecycle.ModelLifecycleManager.
type ModelLifecycleManager struct {
	registry IInferenceServerModelRegistry
	probe    IServerCapabilityProbe

	gate   sync.Mutex
	mu     sync.RWMutex
	loaded map[string]ModelLoadState

	probeOnce sync.Once
	cached    ServerHostProfile
	probeErr  error
}

// NewModelLifecycleManager builds the manager over a registry + capability probe.
func NewModelLifecycleManager(registry IInferenceServerModelRegistry, probe IServerCapabilityProbe) (*ModelLifecycleManager, error) {
	if registry == nil {
		return nil, errors.New("registry is required")
	}
	if probe == nil {
		return nil, errors.New("probe is required")
	}
	return &ModelLifecycleManager{
		registry: registry,
		probe:    probe,
		loaded:   make(map[string]ModelLoadState),
	}, nil
}

// TotalAllocatedVramBytes sums VRAM across loaded models.
func (m *ModelLifecycleManager) TotalAllocatedVramBytes() int64 {
	m.mu.RLock()
	defer m.mu.RUnlock()
	var sum int64
	for _, s := range m.loaded {
		sum += s.VramBytes
	}
	return sum
}

// TotalAllocatedRamBytes sums RAM across loaded models.
func (m *ModelLifecycleManager) TotalAllocatedRamBytes() int64 {
	m.mu.RLock()
	defer m.mu.RUnlock()
	var sum int64
	for _, s := range m.loaded {
		sum += s.RamBytes
	}
	return sum
}

// Load runs the admission gate then the bridge factory. Ports LoadAsync.
func (m *ModelLifecycleManager) Load(ctx context.Context, descriptor ModelLoadDescriptor) (LoadResult, error) {
	if strings.TrimSpace(descriptor.ModelID) == "" {
		return LoadResult{}, errors.New("descriptor.ModelId is required")
	}
	if descriptor.BridgeFactory == nil {
		return LoadResult{}, errors.New("descriptor.BridgeFactory is required")
	}

	// Idempotent fast path — already loaded is a success.
	m.mu.RLock()
	existing, ok := m.loaded[descriptor.ModelID]
	m.mu.RUnlock()
	if ok {
		e := existing
		return LoadResult{
			Outcome:   LoadOutcomeAlreadyLoaded,
			State:     &e,
			Rationale: fmt.Sprintf("Model '%s' is already loaded (%s, %s).", descriptor.ModelID, existing.Backend, existing.Tier),
		}, nil
	}

	profile, err := m.getOrProbe(ctx)
	if err != nil {
		return LoadResult{}, err
	}

	// VRAM admission — only enforced on GPU-class backends.
	if descriptor.Backend.IsGPUBackend() {
		vramCeiling := profile.GpuVramBytes
		vramFree := vramCeiling - m.TotalAllocatedVramBytes()
		if vramFree < descriptor.VramRequiredBytes {
			return LoadResult{
				Outcome: LoadOutcomeInsufficientVram,
				Rationale: fmt.Sprintf("Need %d MiB VRAM, have %d MiB free (%d MiB of %d MiB in use).",
					descriptor.VramRequiredBytes/(1024*1024), maxI64(0, vramFree)/(1024*1024),
					m.TotalAllocatedVramBytes()/(1024*1024), vramCeiling/(1024*1024)),
			}, nil
		}
	}

	// RAM admission — always enforced.
	ramFree := profile.TotalPhysicalMemoryBytes - m.TotalAllocatedRamBytes()
	if ramFree < descriptor.RamRequiredBytes {
		return LoadResult{
			Outcome: LoadOutcomeInsufficientRam,
			Rationale: fmt.Sprintf("Need %d MiB RAM, have %d MiB free (%d MiB of %d MiB in use).",
				descriptor.RamRequiredBytes/(1024*1024), maxI64(0, ramFree)/(1024*1024),
				m.TotalAllocatedRamBytes()/(1024*1024), profile.TotalPhysicalMemoryBytes/(1024*1024)),
		}, nil
	}

	reserveState := ModelLoadState{
		ModelID:   descriptor.ModelID,
		Backend:   descriptor.Backend,
		Tier:      descriptor.RequestedTier,
		VramBytes: descriptor.VramRequiredBytes,
		RamBytes:  descriptor.RamRequiredBytes,
		LoadedAt:  time.Now().UTC(),
	}

	// Reserve before invoking the factory so concurrent loads see the accounting.
	m.gate.Lock()
	m.mu.RLock()
	raceWinner, raced := m.loaded[descriptor.ModelID]
	m.mu.RUnlock()
	if raced {
		m.gate.Unlock()
		w := raceWinner
		return LoadResult{
			Outcome:   LoadOutcomeAlreadyLoaded,
			State:     &w,
			Rationale: fmt.Sprintf("Model '%s' was loaded by a concurrent request.", descriptor.ModelID),
		}, nil
	}
	m.mu.Lock()
	m.loaded[descriptor.ModelID] = reserveState
	m.mu.Unlock()
	m.gate.Unlock()

	bridge, ferr := descriptor.BridgeFactory(ctx)
	if ferr == nil && bridge == nil {
		ferr = fmt.Errorf("BridgeFactory for '%s' returned nil", descriptor.ModelID)
	}
	if ferr != nil {
		// Roll the reservation back.
		m.mu.Lock()
		delete(m.loaded, descriptor.ModelID)
		m.mu.Unlock()
		return LoadResult{
			Outcome:   LoadOutcomeFactoryFailed,
			Rationale: fmt.Sprintf("Bridge factory for '%s' failed: %v", descriptor.ModelID, ferr),
		}, nil
	}

	if err := m.registry.Register(descriptor.ModelID, bridge); err != nil {
		m.mu.Lock()
		delete(m.loaded, descriptor.ModelID)
		m.mu.Unlock()
		return LoadResult{
			Outcome:   LoadOutcomeFactoryFailed,
			Rationale: fmt.Sprintf("Registry rejected '%s': %v", descriptor.ModelID, err),
		}, nil
	}

	rs := reserveState
	return LoadResult{
		Outcome:   LoadOutcomeLoaded,
		State:     &rs,
		Rationale: fmt.Sprintf("Loaded '%s' on %s at %s.", descriptor.ModelID, descriptor.Backend, descriptor.RequestedTier),
	}, nil
}

// Unload disposes and removes the model's bridge. Ports UnloadAsync.
func (m *ModelLifecycleManager) Unload(_ context.Context, modelID string) (UnloadOutcome, error) {
	if strings.TrimSpace(modelID) == "" {
		return 0, errors.New("modelId is required")
	}
	m.mu.Lock()
	_, ok := m.loaded[modelID]
	if ok {
		delete(m.loaded, modelID)
	}
	m.mu.Unlock()
	if !ok {
		return UnloadOutcomeNotLoaded, nil
	}

	// Dispose the bridge if it is closable, then deregister.
	if bridge := m.registry.Resolve(modelID); bridge != nil {
		if closer, isCloser := bridge.(interface{ Close() error }); isCloser {
			_ = closer.Close()
		}
	}
	m.registry.Deregister(modelID)
	return UnloadOutcomeUnloaded, nil
}

// List snapshots every loaded model. Ports List.
func (m *ModelLifecycleManager) List() []ModelLoadState {
	m.mu.RLock()
	defer m.mu.RUnlock()
	out := make([]ModelLoadState, 0, len(m.loaded))
	for _, s := range m.loaded {
		out = append(out, s)
	}
	return out
}

// getOrProbe probes at most once per process. Ports GetOrProbeAsync.
func (m *ModelLifecycleManager) getOrProbe(ctx context.Context) (ServerHostProfile, error) {
	m.probeOnce.Do(func() {
		m.cached, m.probeErr = m.probe.Probe(ctx)
	})
	return m.cached, m.probeErr
}

func maxI64(a, b int64) int64 {
	if a > b {
		return a
	}
	return b
}

var _ IModelLifecycleManager = (*ModelLifecycleManager)(nil)
