// circle_engine.go
//
// Ports CircleAI.Core.CircleEngine (CircleEngine.cs),
// CircleAI.Core.ICircleModule (ICircleModule.cs), and
// CircleAI.Core.IEmbeddingService (IEmbeddingService.cs).
//
// CircleEngine is the top-level facade: it holds the IModelLoader and a
// type-keyed module bag that downstream modules attach to. C# keys the bag on
// typeof(T); Go keys it on the module's concrete reflect.Type, so
// GetModule/HasModule resolve by the same runtime type identity.

package circleai

import (
	"context"
	"errors"
	"reflect"
	"sync"
)

// ICircleModule is a pluggable engine module. Ports CircleAI.Core.ICircleModule.
type ICircleModule interface {
	// ModuleName is the module's canonical name.
	ModuleName() string

	// Init wires the module to the engine.
	Init(ctx context.Context, engine *CircleEngine) error

	// IsModelLoaded reports whether the module's backing model is loaded.
	IsModelLoaded() bool

	// Close releases module resources.
	Close() error
}

// IEmbeddingService is an ICircleModule that produces fixed-size embeddings.
// Ports CircleAI.Core.IEmbeddingService.
type IEmbeddingService interface {
	ICircleModule

	// GenerateEmbedding embeds text into a dense vector of length EmbeddingSize.
	GenerateEmbedding(text string) []float32

	// EmbeddingSize is the vector dimension produced by GenerateEmbedding.
	EmbeddingSize() int
}

// CircleEngine is the on-device stack facade. Ports CircleAI.Core.CircleEngine.
type CircleEngine struct {
	modelLoader IModelLoader

	// EmbeddingService is an optional embedding service, wired in by the
	// embeddings module. Kept as any so Core needs no embedding dependency —
	// mirrors the C# settable object? EmbeddingService.
	EmbeddingService any

	mu      sync.Mutex
	modules map[reflect.Type]any
}

// NewCircleEngine builds an engine around a model loader (required).
func NewCircleEngine(modelLoader IModelLoader) (*CircleEngine, error) {
	if modelLoader == nil {
		return nil, errors.New("modelLoader is required")
	}
	return &CircleEngine{
		modelLoader: modelLoader,
		modules:     make(map[reflect.Type]any),
	}, nil
}

// ModelLoader returns the engine's model loader.
func (e *CircleEngine) ModelLoader() IModelLoader { return e.modelLoader }

// RegisterModule registers module keyed by its concrete runtime type. Returns
// the engine for chaining. Ports RegisterModule<T>.
func (e *CircleEngine) RegisterModule(module any) (*CircleEngine, error) {
	if module == nil {
		return e, errors.New("module is required")
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	e.modules[reflect.TypeOf(module)] = module
	return e, nil
}

// GetModule returns the module previously registered for target's runtime type,
// or nil if none. target is a typed nil used only to carry the type, e.g.
//
//	svc, _ := engine.GetModule((*IEmbeddingService)(nil)).(IEmbeddingService)
//
// For a concrete type registered directly, pass a zero value of that type.
// Ports GetModule<T> — resolution is by the same runtime type used to register.
func (e *CircleEngine) GetModule(target any) any {
	if target == nil {
		return nil
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.modules[reflect.TypeOf(target)]
}

// GetModuleByType returns the module registered under the exact reflect.Type t.
// Convenience for callers that already hold a reflect.Type.
func (e *CircleEngine) GetModuleByType(t reflect.Type) any {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.modules[t]
}

// HasModule reports whether a module of target's runtime type is registered.
// Ports HasModule<T>.
func (e *CircleEngine) HasModule(target any) bool {
	if target == nil {
		return false
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	_, ok := e.modules[reflect.TypeOf(target)]
	return ok
}
