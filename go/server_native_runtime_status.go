// server_native_runtime_status.go
//
// Ports CircleAI.Inference.Server.Lifecycle.INativeRuntimeStatus +
// NativeRuntimeStatus (INativeRuntimeStatus.cs) and the minimal
// NativeRuntimePrep.NativeRuntimePaths shape it holds.
//
// Singleton holder of the last-known native-runtime paths produced when the
// bridge factory materialises a model — surfaced through diagnostics so
// DLL-not-found failures are debuggable from the wire.

package circleai

import "sync"

// NativeRuntimePaths is the resolved native-runtime path set. Minimal port of
// NativeRuntimePrep.NativeRuntimePaths (the fields diagnostics reads).
type NativeRuntimePaths struct {
	// MnnCorePath is the absolute path to the loaded MNN core / model config.
	MnnCorePath string
	// ResolvedRoot is the directory the native resolver was pointed at.
	ResolvedRoot string
	// SelfCheckOK reports whether the post-prep native self-check passed.
	SelfCheckOK bool
}

// INativeRuntimeStatus holds the last-known native prep result. Ports
// CircleAI.Inference.Server.Lifecycle.INativeRuntimeStatus.
type INativeRuntimeStatus interface {
	// Latest returns the most recent prep result, or (zero, false) before the
	// first model load.
	Latest() (NativeRuntimePaths, bool)
	// Update records the result of a successful prep run.
	Update(paths NativeRuntimePaths)
}

// NativeRuntimeStatus is the default thread-safe implementation. Ports
// CircleAI.Inference.Server.Lifecycle.NativeRuntimeStatus.
type NativeRuntimeStatus struct {
	mu     sync.RWMutex
	latest *NativeRuntimePaths
}

// NewNativeRuntimeStatus builds an empty status holder.
func NewNativeRuntimeStatus() *NativeRuntimeStatus { return &NativeRuntimeStatus{} }

// Latest returns the most recent prep result.
func (s *NativeRuntimeStatus) Latest() (NativeRuntimePaths, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if s.latest == nil {
		return NativeRuntimePaths{}, false
	}
	return *s.latest, true
}

// Update records a successful prep run.
func (s *NativeRuntimeStatus) Update(paths NativeRuntimePaths) {
	s.mu.Lock()
	p := paths
	s.latest = &p
	s.mu.Unlock()
}

var _ INativeRuntimeStatus = (*NativeRuntimeStatus)(nil)
