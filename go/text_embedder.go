// text_embedder.go
//
// Ports CircleAI.Embeddings.ITextEmbedder (ITextEmbedder.cs) and
// CircleAI.Embeddings.TextEmbedder (+ its internal IEmbeddingBackend seam)
// (TextEmbedder.cs).
//
// TextEmbedder resolves + verifies an embedding model path via IModelManager,
// then lazily constructs an embedding backend (once, behind a gate) and embeds
// text into an L2-normalised vector. The native MNN backend is replaced by an
// injected EmbeddingBackendFactory — production wires an MNN/cgo backend, tests
// wire a deterministic fake. This is exactly the C# backendFactory seam.

package circleai

import (
	"context"
	"errors"
	"math"
	"strings"
	"sync"
)

// ITextEmbedder (CircleAI.Embeddings.ITextEmbedder) is already declared in
// memory_rag.go with the identical signature Generate(ctx, text) ([]float32,
// error); TextEmbedder below implements that existing interface rather than
// redeclaring it.

// EmbeddingBackend is the low-level, not-thread-safe embedder the TextEmbedder
// serialises access to. Ports the internal CircleAI.Embeddings.IEmbeddingBackend.
type EmbeddingBackend interface {
	// Dimension is the number of floats Embed returns.
	Dimension() int
	// Embed returns an L2-normalised vector for text.
	Embed(text string) ([]float32, error)
	// Close releases the backend's resources.
	Close() error
}

// EmbeddingBackendFactory constructs an EmbeddingBackend from a resolved model
// path. Injection point replacing MnnEmbeddingBackend construction.
type EmbeddingBackendFactory func(modelPath string) (EmbeddingBackend, error)

// L2NormalizeEmbedding L2-normalises v in place (leaves a ~zero vector as-is),
// matching MnnEmbeddingBackend.L2Normalize so cosine similarity reduces to a
// dot product downstream. Exported so custom backends can reuse it.
func L2NormalizeEmbedding(v []float32) {
	var norm float64
	for _, x := range v {
		norm += float64(x) * float64(x)
	}
	norm = math.Sqrt(norm)
	if norm < 1e-12 {
		return
	}
	scale := float32(1.0 / norm)
	for i := range v {
		v[i] *= scale
	}
}

// TextEmbedder is the on-device text embedder. Ports CircleAI.Embeddings.TextEmbedder.
type TextEmbedder struct {
	modelManager     IModelManager
	expectedChecksum []byte
	backendFactory   EmbeddingBackendFactory

	initGate sync.Mutex
	backend  EmbeddingBackend
	disposed bool
}

// NewTextEmbedder builds a TextEmbedder over an injected backend factory.
// Mirrors the internal C# ctor used for testing; production callers pass an
// MNN-backed factory. modelManager, expectedChecksum, and backendFactory are
// all required.
func NewTextEmbedder(modelManager IModelManager, expectedChecksum []byte, backendFactory EmbeddingBackendFactory) (*TextEmbedder, error) {
	if modelManager == nil {
		return nil, errors.New("modelManager is required")
	}
	if expectedChecksum == nil {
		return nil, errors.New("expectedChecksum is required")
	}
	if backendFactory == nil {
		return nil, errors.New("backendFactory is required")
	}
	return &TextEmbedder{
		modelManager:     modelManager,
		expectedChecksum: expectedChecksum,
		backendFactory:   backendFactory,
	}, nil
}

// Generate embeds text into an L2-normalised vector, lazily initialising the
// backend on first call. Ports GenerateAsync.
func (e *TextEmbedder) Generate(ctx context.Context, text string) ([]float32, error) {
	if e.disposed {
		return nil, errors.New("TextEmbedder is disposed")
	}
	if strings.TrimSpace(text) == "" {
		return nil, errors.New("text cannot be empty")
	}
	backend, err := e.ensureBackend(ctx)
	if err != nil {
		return nil, err
	}
	return backend.Embed(text)
}

// ensureBackend resolves + verifies the model path then constructs the backend
// exactly once. Ports EnsureBackendAsync (double-checked under the init gate).
func (e *TextEmbedder) ensureBackend(ctx context.Context) (EmbeddingBackend, error) {
	if e.backend != nil {
		return e.backend, nil
	}
	e.initGate.Lock()
	defer e.initGate.Unlock()
	if e.backend != nil {
		return e.backend, nil
	}

	path, err := e.modelManager.GetModelPath(ctx, "embedding")
	if err != nil {
		return nil, err
	}
	verified, err := e.modelManager.VerifyModel(ctx, path, e.expectedChecksum)
	if err != nil {
		return nil, err
	}
	if !verified {
		return nil, errors.New(
			"embedding model checksum verification failed. The file may be corrupt or tampered with")
	}
	backend, err := e.backendFactory(path)
	if err != nil {
		return nil, err
	}
	e.backend = backend
	return e.backend, nil
}

// Close disposes the embedder and its backend. Ports Dispose.
func (e *TextEmbedder) Close() error {
	if e.disposed {
		return nil
	}
	e.disposed = true
	if e.backend != nil {
		return e.backend.Close()
	}
	return nil
}

var _ ITextEmbedder = (*TextEmbedder)(nil)
