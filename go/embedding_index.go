// embedding_index.go
//
// Ports CircleAI.Embeddings.Local.IEmbeddingIndex + EmbeddingIndexHit
// (IEmbeddingIndex.cs) and CircleAI.Embeddings.Local.TurboVecEmbeddingIndex
// (TurboVecEmbeddingIndex.cs).
//
// The C# TurboVecEmbeddingIndex wraps a native turbovec (Rust) bridge. Per the
// port NOTE, the native search is replaced by a deterministic in-memory
// brute-force index (InMemoryEmbeddingIndex) that reproduces the OBSERVABLE
// contract: Add returns the insertion-order internal id, Search returns up to
// topK hits ordered by descending cosine score (packing fewer-than-topK exactly
// like the native "-1 in the id slot" behaviour), the same dimension (multiple
// of 8) and bit-width (2..4) construction guards, and Save/Load round-trips.
// Vectors are stored full-precision here; the bit-width is retained as metadata
// so the contract and persisted header match.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

// EmbeddingIndexHit is one hit from a vector index. InternalId is the
// insertion-order id; higher Score = closer. Ports EmbeddingIndexHit.
type EmbeddingIndexHit struct {
	InternalID int64
	Score      float32
}

// IEmbeddingIndex is the vector-search primitive. Ports IEmbeddingIndex.
type IEmbeddingIndex interface {
	// Dimension is the vector dimensionality (locked at construction).
	Dimension() int
	// Count is how many vectors are currently indexed.
	Count() int64
	// Add appends one vector and returns the internal id assigned.
	Add(ctx context.Context, vector []float32) (int64, error)
	// Search returns the topK nearest neighbours by cosine score.
	Search(ctx context.Context, queryVector []float32, topK int) ([]EmbeddingIndexHit, error)
	// Save persists the index to path.
	Save(ctx context.Context, path string) error
	// Load reloads from path, replacing in-memory state.
	Load(ctx context.Context, path string) error
	// Close releases the index.
	Close() error
}

const (
	embeddingIndexFileMagic   uint32 = 0x58455649 // "IVEX"
	embeddingIndexFileVersion uint16 = 1
	// EmbeddingIndexNativeAbiVersion mirrors the ABI-version surface of the
	// C# TurboVecEmbeddingIndex.NativeAbiVersion(). The in-memory port reports 1.
	EmbeddingIndexNativeAbiVersion = 1
)

// InMemoryEmbeddingIndex is the deterministic in-memory IEmbeddingIndex. Ports
// the observable contract of CircleAI.Embeddings.Local.TurboVecEmbeddingIndex.
// Safe for concurrent use.
type InMemoryEmbeddingIndex struct {
	dimension int
	bitWidth  int

	mu       sync.RWMutex
	vectors  [][]float32
	disposed bool
}

// NewInMemoryEmbeddingIndex builds a fresh index. dimension must be > 0 and a
// multiple of 8; bitWidth must be 2, 3, or 4. Ports the TurboVecEmbeddingIndex
// ctor guards.
func NewInMemoryEmbeddingIndex(dimension, bitWidth int) (*InMemoryEmbeddingIndex, error) {
	if dimension <= 0 {
		return nil, errors.New("dimension must be positive")
	}
	if dimension%8 != 0 {
		return nil, errors.New("dimension must be a multiple of 8")
	}
	if bitWidth < 2 || bitWidth > 4 {
		return nil, errors.New("bitWidth must be 2, 3, or 4")
	}
	return &InMemoryEmbeddingIndex{dimension: dimension, bitWidth: bitWidth}, nil
}

// Dimension returns the locked vector dimension.
func (idx *InMemoryEmbeddingIndex) Dimension() int { return idx.dimension }

// BitWidth returns the configured quantisation bit-width (2..4). Mirrors the C#
// BitWidth property.
func (idx *InMemoryEmbeddingIndex) BitWidth() int { return idx.bitWidth }

// Count returns the number of indexed vectors.
func (idx *InMemoryEmbeddingIndex) Count() int64 {
	idx.mu.RLock()
	defer idx.mu.RUnlock()
	return int64(len(idx.vectors))
}

// Add appends vector and returns its insertion-order id. Ports AddAsync.
func (idx *InMemoryEmbeddingIndex) Add(_ context.Context, vector []float32) (int64, error) {
	if idx.disposed {
		return 0, errors.New("InMemoryEmbeddingIndex is disposed")
	}
	if len(vector) != idx.dimension {
		return 0, fmt.Errorf("vector length %d != index dimension %d", len(vector), idx.dimension)
	}
	cp := make([]float32, len(vector))
	copy(cp, vector)
	idx.mu.Lock()
	defer idx.mu.Unlock()
	id := int64(len(idx.vectors))
	idx.vectors = append(idx.vectors, cp)
	return id, nil
}

// Search returns up to topK nearest neighbours by cosine score, descending.
// Ports SearchAsync: an empty index yields no hits; when fewer than topK
// vectors exist, only the available hits are returned (the native "-1 padding"
// is simply not surfaced).
func (idx *InMemoryEmbeddingIndex) Search(_ context.Context, queryVector []float32, topK int) ([]EmbeddingIndexHit, error) {
	if idx.disposed {
		return nil, errors.New("InMemoryEmbeddingIndex is disposed")
	}
	if len(queryVector) != idx.dimension {
		return nil, fmt.Errorf("query length %d != index dimension %d", len(queryVector), idx.dimension)
	}
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}

	idx.mu.RLock()
	if len(idx.vectors) == 0 {
		idx.mu.RUnlock()
		return []EmbeddingIndexHit{}, nil
	}
	qNorm := normSafe(queryVector)
	all := make([]EmbeddingIndexHit, 0, len(idx.vectors))
	for i, v := range idx.vectors {
		vNorm := normSafe(v)
		var score float32
		if qNorm > 0 && vNorm > 0 {
			var dot float32
			for d := 0; d < idx.dimension; d++ {
				dot += queryVector[d] * v[d]
			}
			score = dot / (qNorm * vNorm)
		}
		all = append(all, EmbeddingIndexHit{InternalID: int64(i), Score: score})
	}
	idx.mu.RUnlock()

	sort.SliceStable(all, func(i, j int) bool {
		if all[i].Score != all[j].Score {
			return all[i].Score > all[j].Score
		}
		return all[i].InternalID < all[j].InternalID
	})
	if topK > len(all) {
		topK = len(all)
	}
	return all[:topK], nil
}

// Save persists the index to path (atomic tmp-then-rename). Ports SaveAsync.
func (idx *InMemoryEmbeddingIndex) Save(_ context.Context, path string) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("path is required")
	}
	if idx.disposed {
		return errors.New("InMemoryEmbeddingIndex is disposed")
	}
	idx.mu.RLock()
	defer idx.mu.RUnlock()

	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	w := &binWriter{}
	w.u32(embeddingIndexFileMagic)
	w.u16(embeddingIndexFileVersion)
	w.i32(int32(idx.dimension))
	w.i32(int32(idx.bitWidth))
	w.i32(int32(len(idx.vectors)))
	for _, v := range idx.vectors {
		for _, f := range v {
			var t [4]byte
			binary.LittleEndian.PutUint32(t[:], math.Float32bits(f))
			w.raw(t[:])
		}
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, w.bytes(), 0o644); err != nil {
		return err
	}
	if fileExists(path) {
		_ = os.Remove(path)
	}
	return os.Rename(tmp, path)
}

// Load reloads state from path. Ports LoadAsync incl. the dim-mismatch guard.
func (idx *InMemoryEmbeddingIndex) Load(_ context.Context, path string) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("path is required")
	}
	if idx.disposed {
		return errors.New("InMemoryEmbeddingIndex is disposed")
	}
	if !fileExists(path) {
		return fmt.Errorf("index file not found: %s", path)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	r := &binReader{buf: data}
	magic, err := r.u32()
	if err != nil {
		return err
	}
	if magic != embeddingIndexFileMagic {
		return errors.New("not a CircleAI embedding index file")
	}
	version, err := r.u16()
	if err != nil {
		return err
	}
	if version != embeddingIndexFileVersion {
		return fmt.Errorf("unsupported index version %d", version)
	}
	dim, err := r.i32()
	if err != nil {
		return err
	}
	if int(dim) != idx.dimension {
		return fmt.Errorf("loaded index dim %d != configured dim %d", dim, idx.dimension)
	}
	bw, err := r.i32()
	if err != nil {
		return err
	}
	count, err := r.i32()
	if err != nil {
		return err
	}
	loaded := make([][]float32, 0, count)
	for i := int32(0); i < count; i++ {
		v := make([]float32, dim)
		for d := int32(0); d < dim; d++ {
			bits, err := r.u32()
			if err != nil {
				return err
			}
			v[d] = math.Float32frombits(bits)
		}
		loaded = append(loaded, v)
	}

	idx.mu.Lock()
	idx.bitWidth = int(bw)
	idx.vectors = loaded
	idx.mu.Unlock()
	return nil
}

// Close disposes the index. Ports Dispose.
func (idx *InMemoryEmbeddingIndex) Close() error {
	if idx.disposed {
		return nil
	}
	idx.disposed = true
	idx.mu.Lock()
	idx.vectors = nil
	idx.mu.Unlock()
	return nil
}

var _ IEmbeddingIndex = (*InMemoryEmbeddingIndex)(nil)
