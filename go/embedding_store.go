// embedding_store.go
//
// Ports CircleAI.Embeddings.Local.ICircleEmbeddingStore + EmbeddingDocument +
// EmbeddingSearchHit + IEmbeddingEncoder (ICircleEmbeddingStore.cs) and
// CircleAI.Embeddings.Local.InMemoryEmbeddingStore (InMemoryEmbeddingStore.cs).
//
// The store holds documents whose vectors are TurboQuant-compressed at 4
// bits/dim (reusing the flat package's TurboQuantEncode/Decode) and does
// brute-force cosine search over the decoded vectors. Persistence is a custom
// binary file with a "CELQ" magic — the Go writer mirrors the C# BinaryWriter
// field order so a Go-written file round-trips through the Go reader.

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

// EmbeddingDocument is one document in an embedding store. Ports EmbeddingDocument.
type EmbeddingDocument struct {
	ID       string
	Text     string
	Metadata map[string]string // nil when none
}

// EmbeddingSearchHit is one search result; higher Score = closer (cosine).
// Ports EmbeddingSearchHit.
type EmbeddingSearchHit struct {
	Document EmbeddingDocument
	Score    float32
}

// IEmbeddingEncoder turns text into a dense vector. Ports IEmbeddingEncoder.
type IEmbeddingEncoder interface {
	// Dimension is the vector length this encoder produces.
	Dimension() int
	// Encode embeds text into a dense vector.
	Encode(ctx context.Context, text string) ([]float32, error)
}

// ICircleEmbeddingStore is the embedding-store contract. Ports ICircleEmbeddingStore.
type ICircleEmbeddingStore interface {
	// Dimension is the vector dimension the store was created with.
	Dimension() int
	// Count is how many documents are currently stored.
	Count() int
	// Add encodes document.Text and stores it.
	Add(ctx context.Context, document EmbeddingDocument) error
	// AddVector stores document with a caller-supplied vector (len == Dimension).
	AddVector(ctx context.Context, document EmbeddingDocument, vector []float32) error
	// Remove deletes a document by id; returns true when one was removed.
	Remove(ctx context.Context, id string) (bool, error)
	// Search encodes queryText and returns the topK closest documents.
	Search(ctx context.Context, queryText string, topK int) ([]EmbeddingSearchHit, error)
	// SearchVector returns the topK closest documents to a query vector.
	SearchVector(ctx context.Context, queryVector []float32, topK int) ([]EmbeddingSearchHit, error)
	// Save persists the store to path (atomic tmp-then-rename).
	Save(ctx context.Context, path string) error
	// Load replaces in-memory state from path.
	Load(ctx context.Context, path string) error
	// Close releases the store.
	Close() error
}

const (
	embeddingStoreFileMagic   uint32 = 0x4C455143 // "CELQ" little-endian
	embeddingStoreFileVersion uint16 = 1
	embeddingStoreDefaultBits        = 4
)

// storeEntry is one stored document + its compressed payload.
type storeEntry struct {
	doc     EmbeddingDocument
	payload TurboQuantPayload
}

// InMemoryEmbeddingStore is the brute-force, TurboQuant-compressed store.
// Ports CircleAI.Embeddings.Local.InMemoryEmbeddingStore. Safe for concurrent use.
type InMemoryEmbeddingStore struct {
	encoder    IEmbeddingEncoder
	bitsPerDim int

	mu       sync.Mutex
	entries  map[string]storeEntry
	disposed bool
}

// NewInMemoryEmbeddingStore builds a store over encoder. bitsPerDim (1..8)
// controls TurboQuant depth; the C# default is 4. Ports the C# ctor guards.
func NewInMemoryEmbeddingStore(encoder IEmbeddingEncoder, bitsPerDim int) (*InMemoryEmbeddingStore, error) {
	if encoder == nil {
		return nil, errors.New("encoder is required")
	}
	if bitsPerDim == 0 {
		bitsPerDim = embeddingStoreDefaultBits
	}
	if bitsPerDim < 1 || bitsPerDim > 8 {
		return nil, errors.New("bitsPerDim valid range: 1..8")
	}
	return &InMemoryEmbeddingStore{
		encoder:    encoder,
		bitsPerDim: bitsPerDim,
		entries:    make(map[string]storeEntry),
	}, nil
}

// Dimension returns the encoder's dimension.
func (s *InMemoryEmbeddingStore) Dimension() int { return s.encoder.Dimension() }

// Count returns the number of stored documents.
func (s *InMemoryEmbeddingStore) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.entries)
}

// Add encodes document.Text then stores it. Ports AddAsync(document).
func (s *InMemoryEmbeddingStore) Add(ctx context.Context, document EmbeddingDocument) error {
	vector, err := s.encoder.Encode(ctx, document.Text)
	if err != nil {
		return err
	}
	return s.AddVector(ctx, document, vector)
}

// AddVector stores document with a supplied vector. Ports AddAsync(document, vector).
func (s *InMemoryEmbeddingStore) AddVector(_ context.Context, document EmbeddingDocument, vector []float32) error {
	if s.disposed {
		return errors.New("InMemoryEmbeddingStore is disposed")
	}
	if len(vector) != s.Dimension() {
		return fmt.Errorf("vector length %d != store dimension %d", len(vector), s.Dimension())
	}
	payload, err := TurboQuantEncode(vector, s.bitsPerDim)
	if err != nil {
		return err
	}
	s.mu.Lock()
	s.entries[document.ID] = storeEntry{doc: document, payload: payload}
	s.mu.Unlock()
	return nil
}

// Remove deletes a document by id. Ports RemoveAsync.
func (s *InMemoryEmbeddingStore) Remove(_ context.Context, id string) (bool, error) {
	if strings.TrimSpace(id) == "" {
		return false, errors.New("id is required")
	}
	if s.disposed {
		return false, errors.New("InMemoryEmbeddingStore is disposed")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.entries[id]; ok {
		delete(s.entries, id)
		return true, nil
	}
	return false, nil
}

// Search encodes queryText then delegates to SearchVector. Ports SearchAsync(text).
func (s *InMemoryEmbeddingStore) Search(ctx context.Context, queryText string, topK int) ([]EmbeddingSearchHit, error) {
	if queryText == "" {
		return nil, errors.New("queryText is required")
	}
	vector, err := s.encoder.Encode(ctx, queryText)
	if err != nil {
		return nil, err
	}
	return s.SearchVector(ctx, vector, topK)
}

// SearchVector does brute-force cosine search over the decoded vectors and
// returns the topK highest scores. Ports SearchAsync(vector): the query is
// L2-normalised, each entry decoded + normalised on demand, and ties broken by
// ordinal id (matching the C# ScoreComparer) before the descending sort.
func (s *InMemoryEmbeddingStore) SearchVector(_ context.Context, queryVector []float32, topK int) ([]EmbeddingSearchHit, error) {
	if s.disposed {
		return nil, errors.New("InMemoryEmbeddingStore is disposed")
	}
	dim := s.Dimension()
	if len(queryVector) != dim {
		return nil, fmt.Errorf("vector length %d != store dimension %d", len(queryVector), dim)
	}
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}

	qNorm := normSafe(queryVector)
	q := make([]float32, dim)
	copy(q, queryVector)
	if qNorm > 0 {
		for i := range q {
			q[i] /= qNorm
		}
	}

	type scored struct {
		score float32
		id    string
	}

	s.mu.Lock()
	candidates := make([]scored, 0, len(s.entries))
	for id, entry := range s.entries {
		decoded, err := TurboQuantDecode(entry.payload, dim, s.bitsPerDim)
		if err != nil {
			s.mu.Unlock()
			return nil, err
		}
		entryNorm := normSafe(decoded)
		if entryNorm <= 0 {
			continue
		}
		var dot float32
		for i := 0; i < dim; i++ {
			dot += q[i] * (decoded[i] / entryNorm)
		}
		candidates = append(candidates, scored{score: dot, id: id})
	}
	// snapshot docs while locked
	docs := make(map[string]EmbeddingDocument, len(s.entries))
	for id, e := range s.entries {
		docs[id] = e.doc
	}
	s.mu.Unlock()

	// Order by score desc, then id ordinal asc (mirrors the C# top-K comparer +
	// OrderByDescending(Score)).
	sort.Slice(candidates, func(i, j int) bool {
		if candidates[i].score != candidates[j].score {
			return candidates[i].score > candidates[j].score
		}
		return candidates[i].id < candidates[j].id
	})
	if topK > len(candidates) {
		topK = len(candidates)
	}
	hits := make([]EmbeddingSearchHit, 0, topK)
	for i := 0; i < topK; i++ {
		hits = append(hits, EmbeddingSearchHit{Document: docs[candidates[i].id], Score: candidates[i].score})
	}
	return hits, nil
}

// Save writes the store to path atomically. Ports SaveAsync — same field order
// as the C# BinaryWriter so a Go-written file reloads through Load.
func (s *InMemoryEmbeddingStore) Save(ctx context.Context, path string) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("path is required")
	}
	if s.disposed {
		return errors.New("InMemoryEmbeddingStore is disposed")
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	tmp := path + ".tmp"
	buf := &binWriter{}
	buf.u32(embeddingStoreFileMagic)
	buf.u16(embeddingStoreFileVersion)
	buf.u16(uint16(s.bitsPerDim))
	buf.i32(int32(s.Dimension()))
	buf.i32(int32(len(s.entries)))

	// Deterministic order for reproducible files.
	ids := make([]string, 0, len(s.entries))
	for id := range s.entries {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		if err := ctx.Err(); err != nil {
			return err
		}
		entry := s.entries[id]
		buf.str(id)
		buf.str(entry.doc.Text)
		metaKeys := sortedMetaKeys(entry.doc.Metadata)
		buf.i32(int32(len(metaKeys)))
		for _, k := range metaKeys {
			buf.str(k)
			buf.str(entry.doc.Metadata[k])
		}
		buf.f32(entry.payload.Norm)
		buf.i32(int32(len(entry.payload.PackedIndices)))
		buf.raw(entry.payload.PackedIndices)
	}

	if err := os.WriteFile(tmp, buf.bytes(), 0o644); err != nil {
		return err
	}
	if fileExists(path) {
		_ = os.Remove(path)
	}
	return os.Rename(tmp, path)
}

// Load replaces state from path. Ports LoadAsync with the mismatch guards.
func (s *InMemoryEmbeddingStore) Load(ctx context.Context, path string) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("path is required")
	}
	if s.disposed {
		return errors.New("InMemoryEmbeddingStore is disposed")
	}
	if !fileExists(path) {
		return fmt.Errorf("embedding store file not found: %s", path)
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
	if magic != embeddingStoreFileMagic {
		return errors.New("not a CircleAI embedding store file")
	}
	version, err := r.u16()
	if err != nil {
		return err
	}
	if version != embeddingStoreFileVersion {
		return fmt.Errorf("unsupported file version %d", version)
	}
	fileBits, err := r.u16()
	if err != nil {
		return err
	}
	if int(fileBits) != s.bitsPerDim {
		return fmt.Errorf("bits-per-dim mismatch: store=%d, file=%d", s.bitsPerDim, fileBits)
	}
	fileDim, err := r.i32()
	if err != nil {
		return err
	}
	if int(fileDim) != s.Dimension() {
		return fmt.Errorf("dimension mismatch: store=%d, file=%d", s.Dimension(), fileDim)
	}
	count, err := r.i32()
	if err != nil {
		return err
	}

	loaded := make(map[string]storeEntry, count)
	for i := int32(0); i < count; i++ {
		if err := ctx.Err(); err != nil {
			return err
		}
		id, err := r.str()
		if err != nil {
			return err
		}
		text, err := r.str()
		if err != nil {
			return err
		}
		metaCount, err := r.i32()
		if err != nil {
			return err
		}
		var metadata map[string]string
		if metaCount > 0 {
			metadata = make(map[string]string, metaCount)
			for m := int32(0); m < metaCount; m++ {
				k, err := r.str()
				if err != nil {
					return err
				}
				v, err := r.str()
				if err != nil {
					return err
				}
				metadata[k] = v
			}
		}
		norm, err := r.f32()
		if err != nil {
			return err
		}
		packedLen, err := r.i32()
		if err != nil {
			return err
		}
		packed, err := r.rawN(int(packedLen))
		if err != nil {
			return err
		}
		loaded[id] = storeEntry{
			doc:     EmbeddingDocument{ID: id, Text: text, Metadata: metadata},
			payload: TurboQuantPayload{Norm: norm, PackedIndices: packed},
		}
	}

	s.mu.Lock()
	s.entries = loaded
	s.mu.Unlock()
	return nil
}

// Close disposes the store. Ports DisposeAsync.
func (s *InMemoryEmbeddingStore) Close() error {
	if s.disposed {
		return nil
	}
	s.disposed = true
	s.mu.Lock()
	s.entries = make(map[string]storeEntry)
	s.mu.Unlock()
	return nil
}

func normSafe(v []float32) float32 {
	var sum float64
	for _, x := range v {
		sum += float64(x) * float64(x)
	}
	return float32(math.Sqrt(sum))
}

func sortedMetaKeys(m map[string]string) []string {
	if len(m) == 0 {
		return nil
	}
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// ── minimal length-prefixed binary reader/writer (mirrors C# BinaryWriter) ──
//
// BinaryWriter.Write(string) is a 7-bit-encoded-length prefix then UTF-8 bytes;
// Write(int) is 4-byte little-endian; Write(float) is IEEE-754 LE. The Go
// writer reproduces the same layout so files it produces read back through the
// reader below. (Cross-runtime parity with a C#-written file also holds because
// the 7-bit length prefix + LE primitives match.)

type binWriter struct{ b []byte }

func (w *binWriter) bytes() []byte { return w.b }
func (w *binWriter) raw(p []byte)  { w.b = append(w.b, p...) }
func (w *binWriter) u16(v uint16) {
	var t [2]byte
	binary.LittleEndian.PutUint16(t[:], v)
	w.b = append(w.b, t[:]...)
}
func (w *binWriter) u32(v uint32) {
	var t [4]byte
	binary.LittleEndian.PutUint32(t[:], v)
	w.b = append(w.b, t[:]...)
}
func (w *binWriter) i32(v int32) { w.u32(uint32(v)) }
func (w *binWriter) f32(v float32) {
	var t [4]byte
	binary.LittleEndian.PutUint32(t[:], math.Float32bits(v))
	w.b = append(w.b, t[:]...)
}
func (w *binWriter) str(s string) {
	w.write7BitEncodedInt(len(s))
	w.b = append(w.b, []byte(s)...)
}
func (w *binWriter) write7BitEncodedInt(n int) {
	v := uint32(n)
	for v >= 0x80 {
		w.b = append(w.b, byte(v)|0x80)
		v >>= 7
	}
	w.b = append(w.b, byte(v))
}

type binReader struct {
	buf []byte
	pos int
}

func (r *binReader) u16() (uint16, error) {
	if r.pos+2 > len(r.buf) {
		return 0, errShortRead
	}
	v := binary.LittleEndian.Uint16(r.buf[r.pos:])
	r.pos += 2
	return v, nil
}
func (r *binReader) u32() (uint32, error) {
	if r.pos+4 > len(r.buf) {
		return 0, errShortRead
	}
	v := binary.LittleEndian.Uint32(r.buf[r.pos:])
	r.pos += 4
	return v, nil
}
func (r *binReader) i32() (int32, error) {
	v, err := r.u32()
	return int32(v), err
}
func (r *binReader) f32() (float32, error) {
	v, err := r.u32()
	if err != nil {
		return 0, err
	}
	return math.Float32frombits(v), nil
}
func (r *binReader) str() (string, error) {
	n, err := r.read7BitEncodedInt()
	if err != nil {
		return "", err
	}
	if r.pos+n > len(r.buf) {
		return "", errShortRead
	}
	s := string(r.buf[r.pos : r.pos+n])
	r.pos += n
	return s, nil
}
func (r *binReader) rawN(n int) ([]byte, error) {
	if n < 0 || r.pos+n > len(r.buf) {
		return nil, errShortRead
	}
	out := make([]byte, n)
	copy(out, r.buf[r.pos:r.pos+n])
	r.pos += n
	return out, nil
}
func (r *binReader) read7BitEncodedInt() (int, error) {
	var result uint32
	var shift uint
	for {
		if r.pos >= len(r.buf) {
			return 0, errShortRead
		}
		b := r.buf[r.pos]
		r.pos++
		result |= uint32(b&0x7f) << shift
		if b&0x80 == 0 {
			break
		}
		shift += 7
		if shift > 35 {
			return 0, errors.New("bad 7-bit encoded int")
		}
	}
	return int(result), nil
}

var errShortRead = errors.New("unexpected end of embedding store file")

var _ ICircleEmbeddingStore = (*InMemoryEmbeddingStore)(nil)
