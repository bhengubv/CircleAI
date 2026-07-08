// model_handle.go
//
// Ports CircleAI.Core.SafeModelHandle (SafeModelHandle.cs) and
// CircleAI.Core.PlatformInterop (PlatformInterop.cs).
//
// SafeModelHandle wraps an opaque native model pointer with a release callback
// supplied by the loader, so this package stays free of native imports. In Go
// the "native pointer" is modelled as a uintptr and the release is a func —
// the injection point that replaces the llama.cpp P/Invoke. Release is
// idempotent and guarded so a double-close never double-frees.

package circleai

import (
	"errors"
	"os"
	"strings"
	"sync"
)

// SafeModelHandle wraps an opaque native model pointer plus a release callback.
// Ports CircleAI.Core.SafeModelHandle.
type SafeModelHandle struct {
	mu       sync.Mutex
	handle   uintptr
	release  func(uintptr)
	released bool
}

// NewSafeModelHandle wraps nativeHandle with an explicit release callback.
// Mirrors the C# SafeModelHandle(IntPtr, Action<IntPtr>) ctor; releaseCallback
// is required.
func NewSafeModelHandle(nativeHandle uintptr, releaseCallback func(uintptr)) (*SafeModelHandle, error) {
	if releaseCallback == nil {
		return nil, errors.New("releaseCallback is required")
	}
	return &SafeModelHandle{handle: nativeHandle, release: releaseCallback}, nil
}

// WithReleaseCallback wires up (or replaces) the release callback after
// construction. Ports SafeModelHandle.WithReleaseCallback.
func (h *SafeModelHandle) WithReleaseCallback(releaseCallback func(uintptr)) (*SafeModelHandle, error) {
	if releaseCallback == nil {
		return nil, errors.New("releaseCallback is required")
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	h.release = releaseCallback
	return h, nil
}

// SetHandle assigns the native pointer (used when the handle is constructed
// empty and filled in by the loader). Mirrors SafeHandle.SetHandle.
func (h *SafeModelHandle) SetHandle(nativeHandle uintptr) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.handle = nativeHandle
}

// Handle returns the raw native pointer value.
func (h *SafeModelHandle) Handle() uintptr {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.handle
}

// IsInvalid reports whether the handle is the zero pointer. Ports
// SafeModelHandle.IsInvalid.
func (h *SafeModelHandle) IsInvalid() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.handle == 0
}

// Close releases the native handle exactly once via the callback, then zeroes
// it. Ports ReleaseHandle + SafeHandle.Dispose. Safe to call repeatedly.
func (h *SafeModelHandle) Close() error {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.released {
		return nil
	}
	h.released = true
	if h.handle != 0 {
		if h.release != nil {
			h.release(h.handle)
		}
		h.handle = 0
	}
	return nil
}

// ── PlatformInterop ───────────────────────────────────────────────────────

// NativeModelLoadFunc loads a native model from a file path and returns an
// opaque pointer plus its release function. This is the injection seam that
// replaces the llama.cpp DllImports in PlatformInterop.cs — production wires a
// cgo/llama.cpp loader; tests wire a deterministic fake.
type NativeModelLoadFunc func(path string) (handle uintptr, release func(uintptr), err error)

// PlatformInterop loads native models and returns them wrapped in a
// SafeModelHandle. Ports the static CircleAI.PlatformInterop. Unlike the C#
// static-with-DllImports shape, the native loader is injected so this package
// takes no native dependency.
type PlatformInterop struct {
	load NativeModelLoadFunc
}

// NewPlatformInterop builds an interop over the given native loader.
func NewPlatformInterop(load NativeModelLoadFunc) (*PlatformInterop, error) {
	if load == nil {
		return nil, errors.New("native load func is required")
	}
	return &PlatformInterop{load: load}, nil
}

// LoadModel loads the model at path and returns it wrapped in a
// SafeModelHandle. Ports PlatformInterop.LoadModel: validates the path,
// confirms the file exists, invokes the native loader, and rejects a null
// handle.
func (p *PlatformInterop) LoadModel(path string) (*SafeModelHandle, error) {
	if strings.TrimSpace(path) == "" {
		return nil, errors.New("model path is required")
	}
	if !fileExists(path) {
		return nil, &os.PathError{Op: "open", Path: path, Err: os.ErrNotExist}
	}
	handle, release, err := p.load(path)
	if err != nil {
		return nil, err
	}
	if handle == 0 {
		return nil, errors.New(
			"native loader failed to load model at '" + path + "'. " +
				"Verify the file is valid and the native library is on the search path")
	}
	if release == nil {
		release = func(uintptr) {}
	}
	return NewSafeModelHandle(handle, release)
}
