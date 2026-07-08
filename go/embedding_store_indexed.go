// embedding_store_indexed.go
//
// Ports CircleAI.Embeddings.Local.HnswEmbeddingStore (HnswEmbeddingStore.cs) —
// an ICircleEmbeddingStore that layers documents + metadata over an
// IEmbeddingIndex. Backed here by InMemoryEmbeddingIndex (the deterministic
// stand-in for the turbovec bridge). Semantics match the C# reference:
//   - Add is add-only; replacing an existing id errors ("Remove first").
//   - Remove is a soft delete (drops the id→slot mapping); the slot stays in
//     the index and search skips removed docs.
//   - Search over-fetches to compensate for removed slots, then filters.
//   - Save writes the index plus a ".docs" sidecar (id/text/live/metadata);
//     Load reads both.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

const (
	indexedStoreDocsMagic   uint32 = 0x53434847 // "HGCS"
	indexedStoreDocsVersion uint16 = 1
	indexedStoreDefaultBits        = 4
)

// IndexedEmbeddingStore layers documents over an IEmbeddingIndex. Ports
// CircleAI.Embeddings.Local.HnswEmbeddingStore (named IndexedEmbeddingStore
// since the Go backend is a generic index, not literally HNSW). Safe for
// concurrent use.
type IndexedEmbeddingStore struct {
	encoder IEmbeddingEncoder
	index   IEmbeddingIndex

	mu       sync.Mutex
	byID     []EmbeddingDocument // ordinal internal-id -> document
	idLookup map[string]int64    // external id -> internal id (live only)
	disposed bool
}

// NewIndexedEmbeddingStore builds a store over encoder using a fresh
// InMemoryEmbeddingIndex. The encoder dimension must be > 0 and a multiple of 8
// (index alignment); bitWidth must be 2, 3, or 4. Ports the C# ctor guards.
func NewIndexedEmbeddingStore(encoder IEmbeddingEncoder, bitWidth int) (*IndexedEmbeddingStore, error) {
	if encoder == nil {
		return nil, errors.New("encoder is required")
	}
	if bitWidth == 0 {
		bitWidth = indexedStoreDefaultBits
	}
	if encoder.Dimension() <= 0 || encoder.Dimension()%8 != 0 {
		return nil, fmt.Errorf("encoder dimension %d must be > 0 and a multiple of 8", encoder.Dimension())
	}
	index, err := NewInMemoryEmbeddingIndex(encoder.Dimension(), bitWidth)
	if err != nil {
		return nil, err
	}
	return &IndexedEmbeddingStore{
		encoder:  encoder,
		index:    index,
		idLookup: make(map[string]int64),
	}, nil
}

// NewIndexedEmbeddingStoreWithIndex builds a store over an injected index (must
// share the encoder's dimension). Lets callers supply a different
// IEmbeddingIndex implementation.
func NewIndexedEmbeddingStoreWithIndex(encoder IEmbeddingEncoder, index IEmbeddingIndex) (*IndexedEmbeddingStore, error) {
	if encoder == nil {
		return nil, errors.New("encoder is required")
	}
	if index == nil {
		return nil, errors.New("index is required")
	}
	if index.Dimension() != encoder.Dimension() {
		return nil, fmt.Errorf("index dim %d != encoder dim %d", index.Dimension(), encoder.Dimension())
	}
	return &IndexedEmbeddingStore{
		encoder:  encoder,
		index:    index,
		idLookup: make(map[string]int64),
	}, nil
}

// Dimension returns the encoder dimension.
func (s *IndexedEmbeddingStore) Dimension() int { return s.encoder.Dimension() }

// Count returns the number of documents ever added (including soft-deleted),
// matching the C# HnswEmbeddingStore.Count == _byId.Count.
func (s *IndexedEmbeddingStore) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.byID)
}

// LiveCount returns the number of documents still live (not soft-deleted).
func (s *IndexedEmbeddingStore) LiveCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.idLookup)
}

// Add encodes document.Text and appends it. Ports AddAsync(document).
func (s *IndexedEmbeddingStore) Add(ctx context.Context, document EmbeddingDocument) error {
	vector, err := s.encoder.Encode(ctx, document.Text)
	if err != nil {
		return err
	}
	return s.AddVector(ctx, document, vector)
}

// AddVector appends document with a supplied vector. Add-only: an existing id
// errors. Ports AddAsync(document, vector).
func (s *IndexedEmbeddingStore) AddVector(ctx context.Context, document EmbeddingDocument, vector []float32) error {
	if s.disposed {
		return errors.New("IndexedEmbeddingStore is disposed")
	}
	if len(vector) != s.Dimension() {
		return fmt.Errorf("vector length %d != store dimension %d", len(vector), s.Dimension())
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.idLookup[document.ID]; exists {
		return fmt.Errorf("document id %q already exists. Call Remove first", document.ID)
	}
	internalID, err := s.index.Add(ctx, vector)
	if err != nil {
		return err
	}
	s.byID = append(s.byID, document)
	s.idLookup[document.ID] = internalID
	return nil
}

// Remove soft-deletes a document by id. Returns true when one was live. Ports
// RemoveAsync (drops the lookup; the index slot remains).
func (s *IndexedEmbeddingStore) Remove(_ context.Context, id string) (bool, error) {
	if strings.TrimSpace(id) == "" {
		return false, errors.New("id is required")
	}
	if s.disposed {
		return false, errors.New("IndexedEmbeddingStore is disposed")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.idLookup[id]; ok {
		delete(s.idLookup, id)
		return true, nil
	}
	return false, nil
}

// Search encodes queryText then delegates to SearchVector. Ports SearchAsync(text).
func (s *IndexedEmbeddingStore) Search(ctx context.Context, queryText string, topK int) ([]EmbeddingSearchHit, error) {
	if queryText == "" {
		return nil, errors.New("queryText is required")
	}
	vector, err := s.encoder.Encode(ctx, queryText)
	if err != nil {
		return nil, err
	}
	return s.SearchVector(ctx, vector, topK)
}

// SearchVector over-fetches from the index then filters removed slots. Ports
// SearchAsync(vector): overFetch = min(count, max(topK*2, topK+10)).
func (s *IndexedEmbeddingStore) SearchVector(ctx context.Context, queryVector []float32, topK int) ([]EmbeddingSearchHit, error) {
	if s.disposed {
		return nil, errors.New("IndexedEmbeddingStore is disposed")
	}
	if len(queryVector) != s.Dimension() {
		return nil, fmt.Errorf("query length %d != store dimension %d", len(queryVector), s.Dimension())
	}
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}

	count := int(s.index.Count())
	overFetch := max2(topK*2, topK+10)
	if overFetch > count {
		overFetch = count
	}
	if overFetch == 0 {
		return []EmbeddingSearchHit{}, nil
	}

	rawHits, err := s.index.Search(ctx, queryVector, overFetch)
	if err != nil {
		return nil, err
	}
	if len(rawHits) == 0 {
		return []EmbeddingSearchHit{}, nil
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	results := make([]EmbeddingSearchHit, 0, topK)
	for _, hit := range rawHits {
		if hit.InternalID < 0 || hit.InternalID >= int64(len(s.byID)) {
			continue
		}
		doc := s.byID[hit.InternalID]
		if _, live := s.idLookup[doc.ID]; !live {
			continue
		}
		results = append(results, EmbeddingSearchHit{Document: doc, Score: hit.Score})
		if len(results) == topK {
			break
		}
	}
	return results, nil
}

// Save writes the index plus a ".docs" sidecar. Ports SaveAsync.
func (s *IndexedEmbeddingStore) Save(ctx context.Context, path string) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("path is required")
	}
	if s.disposed {
		return errors.New("IndexedEmbeddingStore is disposed")
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	if err := s.index.Save(ctx, path); err != nil {
		return err
	}

	docsPath := path + ".docs"
	tmp := docsPath + ".tmp"
	w := &binWriter{}
	w.u32(indexedStoreDocsMagic)
	w.u16(indexedStoreDocsVersion)
	w.i32(int32(s.Dimension()))
	w.i32(int32(len(s.byID)))
	for _, doc := range s.byID {
		if err := ctx.Err(); err != nil {
			return err
		}
		w.str(doc.ID)
		w.str(doc.Text)
		_, live := s.idLookup[doc.ID]
		w.boolByte(live)
		metaKeys := sortedMetaKeys(doc.Metadata)
		w.i32(int32(len(metaKeys)))
		for _, k := range metaKeys {
			w.str(k)
			w.str(doc.Metadata[k])
		}
	}
	if err := os.WriteFile(tmp, w.bytes(), 0o644); err != nil {
		return err
	}
	if fileExists(docsPath) {
		_ = os.Remove(docsPath)
	}
	return os.Rename(tmp, docsPath)
}

// Load reads the index plus its ".docs" sidecar. Ports LoadAsync.
func (s *IndexedEmbeddingStore) Load(ctx context.Context, path string) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("path is required")
	}
	if s.disposed {
		return errors.New("IndexedEmbeddingStore is disposed")
	}
	docsPath := path + ".docs"
	if !fileExists(path) {
		return fmt.Errorf("index file not found: %s", path)
	}
	if !fileExists(docsPath) {
		return fmt.Errorf("docs sidecar not found: %s", docsPath)
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.index.Load(ctx, path); err != nil {
		return err
	}

	data, err := os.ReadFile(docsPath)
	if err != nil {
		return err
	}
	r := &binReader{buf: data}
	magic, err := r.u32()
	if err != nil {
		return err
	}
	if magic != indexedStoreDocsMagic {
		return errors.New("not an IndexedEmbeddingStore docs sidecar")
	}
	version, err := r.u16()
	if err != nil {
		return err
	}
	if version != indexedStoreDocsVersion {
		return fmt.Errorf("unsupported docs version %d", version)
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

	s.byID = s.byID[:0]
	s.idLookup = make(map[string]int64)
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
		live, err := r.boolByte()
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
		doc := EmbeddingDocument{ID: id, Text: text, Metadata: metadata}
		s.byID = append(s.byID, doc)
		if live {
			s.idLookup[id] = int64(i)
		}
	}
	return nil
}

// Close disposes the store and its index. Ports DisposeAsync.
func (s *IndexedEmbeddingStore) Close() error {
	if s.disposed {
		return nil
	}
	s.disposed = true
	err := s.index.Close()
	s.mu.Lock()
	s.byID = nil
	s.idLookup = make(map[string]int64)
	s.mu.Unlock()
	return err
}

func max2(a, b int) int {
	if a > b {
		return a
	}
	return b
}

// boolByte helpers extend the shared binWriter/binReader (defined in
// embedding_store.go) with a 1-byte bool, matching C# BinaryWriter.Write(bool).
func (w *binWriter) boolByte(v bool) {
	if v {
		w.b = append(w.b, 1)
	} else {
		w.b = append(w.b, 0)
	}
}

func (r *binReader) boolByte() (bool, error) {
	if r.pos >= len(r.buf) {
		return false, errShortRead
	}
	b := r.buf[r.pos]
	r.pos++
	return b != 0, nil
}

var _ ICircleEmbeddingStore = (*IndexedEmbeddingStore)(nil)
