// memory_multimodal.go
//
// Compressed semantic memory for media artefacts (image / audio / video /
// document). Ported from CircleAI.Memory.Multimodal (C#) and mirrors the
// TypeScript pilot (memory/multimodal.ts) 1:1:
//   • MediaModality, MultimodalMemoryEntry
//   • IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
//   • IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
//   • MultimodalMemoryIngester (+ IngestionResult)
//
// The whole point: we DO NOT store the pixels / audio samples / video frames —
// we store the caption, the embedding, and a SHA-256 of the original so the
// host can reference it back if it kept the file elsewhere. Raw bytes never
// leave the captioner; the store only ever holds the semantic record.

package circleai

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// MediaModality
// ---------------------------------------------------------------------------

// MediaModality is the modality of a multimodal memory entry. Drives how the
// ingester routes the raw bytes to the captioner and which side-channel
// metadata is captured.
type MediaModality int

const (
	// MediaImage is a still image — JPEG, PNG, HEIC, WebP, AVIF.
	MediaImage MediaModality = iota
	// MediaAudio is an audio clip — Opus, WAV, MP3, M4A.
	MediaAudio
	// MediaVideo is video — MP4, MOV, WebM. Captioned via key-frame extraction.
	MediaVideo
	// MediaTextDocument is a text document — PDF, DOCX, plain text snippet.
	MediaTextDocument
)

// ---------------------------------------------------------------------------
// MultimodalMemoryEntry
// ---------------------------------------------------------------------------

// MultimodalMemoryEntry is one semantically-compressed media memory. The
// caption + embedding capture the meaning; raw bytes are never retained by the
// memory layer.
//
// ReferenceCount is mutable (incremented on dedup hits); everything else is
// effectively write-once, matching the C# init/set split. Nullable C# value
// types map to Go pointers.
type MultimodalMemoryEntry struct {
	// ID is the stable identifier (UUID v4).
	ID uuid.UUID
	// RecordedAtUTC is the UTC timestamp the memory was recorded.
	RecordedAtUTC time.Time
	// Modality is which kind of media this came from.
	Modality MediaModality
	// Caption is the semantic content.
	Caption string
	// Embedding of the caption (and, for richer captioners, the joint
	// embedding). nil when the captioner could not produce one.
	Embedding []float32
	// SourceSha256 is the SHA-256 of the original bytes, hex-lower.
	SourceSha256 string
	// SourceMimeType is the original MIME type (e.g. image/jpeg). nil if unknown.
	SourceMimeType *string
	// SourceByteCount is the size in bytes of the original artefact.
	SourceByteCount int64
	// SourceURI is the optional URI of the original artefact if the host
	// retained it elsewhere. nil when the host did not preserve the original.
	SourceURI *string
	// WidthPx is the image / video width in pixels, when applicable.
	WidthPx *int
	// HeightPx is the image / video height in pixels, when applicable.
	HeightPx *int
	// DurationMs is the audio / video duration in milliseconds, when applicable.
	DurationMs *int64
	// ReferenceCount is how many times this artefact has been re-presented to
	// the ingester. Incremented on every dedup hit instead of creating a new
	// entry. Mutable.
	ReferenceCount int
	// Tags holds optional tags (e.g. location, person, topic).
	Tags map[string]string
}

// NewMultimodalMemoryEntry builds a MultimodalMemoryEntry filling the same
// defaults the C# record's initialisers do: fresh UUID id, RecordedAtUTC = now,
// ReferenceCount = 1. Callers set the remaining fields directly.
func NewMultimodalMemoryEntry() MultimodalMemoryEntry {
	return MultimodalMemoryEntry{
		ID:             uuid.New(),
		RecordedAtUTC:  time.Now().UTC(),
		Modality:       MediaImage,
		ReferenceCount: 1,
	}
}

// ---------------------------------------------------------------------------
// IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
// ---------------------------------------------------------------------------

// CaptionResult is the output of a single captioning call.
type CaptionResult struct {
	// Caption is the human-readable semantic description. Must not be empty.
	Caption string
	// Embedding of the artefact. nil when the captioner has no embedding backend.
	Embedding []float32
	// WidthPx is the image / video width when known.
	WidthPx *int
	// HeightPx is the image / video height when known.
	HeightPx *int
	// DurationMs is the audio / video duration when known.
	DurationMs *int64
}

// IMultimodalCaptioner converts raw media bytes into a semantic representation.
type IMultimodalCaptioner interface {
	// CanCaption reports whether this captioner can handle the given modality +
	// mime. The ingester picks among multiple captioners using this predicate.
	CanCaption(modality MediaModality, mimeType *string) bool

	// Caption produces a CaptionResult for the given source bytes.
	// Implementations must not retain the bytes after the call returns.
	Caption(ctx context.Context, modality MediaModality, sourceBytes []byte, mimeType *string) (CaptionResult, error)
}

// HeuristicMultimodalCaptioner is the default IMultimodalCaptioner. It returns a
// descriptive shell caption — never fabricates semantic content. Always
// available, zero model dependency, zero token cost.
type HeuristicMultimodalCaptioner struct{}

// CanCaption always returns true — the heuristic captioner is the universal
// fallback.
func (HeuristicMultimodalCaptioner) CanCaption(_ MediaModality, _ *string) bool {
	return true
}

// Caption produces a descriptive shell caption identifying the detected MIME
// type and byte count.
func (HeuristicMultimodalCaptioner) Caption(_ context.Context, modality MediaModality, sourceBytes []byte, mimeType *string) (CaptionResult, error) {
	detected := detectMime(sourceBytes, mimeType)
	length := len(sourceBytes)
	var caption string
	switch modality {
	case MediaImage:
		caption = fmt.Sprintf("[Image — no captioner wired. %s, %d bytes.]", detected, length)
	case MediaAudio:
		caption = fmt.Sprintf("[Audio — no captioner wired. %s, %d bytes.]", detected, length)
	case MediaVideo:
		caption = fmt.Sprintf("[Video — no captioner wired. %s, %d bytes.]", detected, length)
	case MediaTextDocument:
		caption = fmt.Sprintf("[Document — no captioner wired. %s, %d bytes.]", detected, length)
	default:
		caption = fmt.Sprintf("[Media — no captioner wired. %s, %d bytes.]", detected, length)
	}
	return CaptionResult{Caption: caption}, nil
}

func detectMime(bytes []byte, declared *string) string {
	if declared != nil && strings.TrimSpace(*declared) != "" {
		return *declared
	}
	if len(bytes) >= 4 {
		if bytes[0] == 0xff && bytes[1] == 0xd8 {
			return "image/jpeg"
		}
		if bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 {
			return "image/png"
		}
		if bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 {
			return "image/gif"
		}
		if bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 {
			return "audio/wav"
		}
		if bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 {
			return "application/pdf"
		}
	}
	return "application/octet-stream"
}

// Compile-time assertion that the heuristic captioner satisfies the interface.
var _ IMultimodalCaptioner = HeuristicMultimodalCaptioner{}

// ---------------------------------------------------------------------------
// IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
// ---------------------------------------------------------------------------

// IMultimodalMemoryStore is a persistent store of compressed multimodal
// memories.
type IMultimodalMemoryStore interface {
	// Add adds an entry. Duplicate SHA-256 hits should be handled via GetByHash.
	Add(ctx context.Context, entry MultimodalMemoryEntry) error
	// GetByHash returns the entry with the given hash, or nil if unknown.
	GetByHash(ctx context.Context, sourceSha256 string) (*MultimodalMemoryEntry, error)
	// Reinforce increments ReferenceCount for the entry whose hash matches.
	// No-op when unknown.
	Reinforce(ctx context.Context, sourceSha256 string) error
	// Search returns the top-topK entries whose embedding is most similar
	// (cosine) to queryEmbedding. When the query is nil, falls back to
	// most-recent.
	Search(ctx context.Context, queryEmbedding []float32, topK int) ([]MultimodalMemoryEntry, error)
	// GetRecent returns the most recent count entries.
	GetRecent(ctx context.Context, count int) ([]MultimodalMemoryEntry, error)
	// PruneOlderThan removes entries older than cutoff. Returns count removed.
	PruneOlderThan(ctx context.Context, cutoff time.Time) (int, error)
	// Count returns the total entries currently stored.
	Count(ctx context.Context) (int, error)
}

// InMemoryMultimodalMemoryStore is an in-memory IMultimodalMemoryStore keyed by
// SHA-256 (case-insensitive). Mirrors the C# ConcurrentDictionary with
// OrdinalIgnoreCase.
type InMemoryMultimodalMemoryStore struct {
	mu     sync.Mutex
	byHash map[string]MultimodalMemoryEntry
}

// NewInMemoryMultimodalMemoryStore creates an empty store.
func NewInMemoryMultimodalMemoryStore() *InMemoryMultimodalMemoryStore {
	return &InMemoryMultimodalMemoryStore{byHash: make(map[string]MultimodalMemoryEntry)}
}

// Add adds an entry keyed by its (lower-cased) SHA-256. The hash is required.
func (s *InMemoryMultimodalMemoryStore) Add(_ context.Context, entry MultimodalMemoryEntry) error {
	if strings.TrimSpace(entry.SourceSha256) == "" {
		return errors.New("SourceSha256 is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.byHash[keyOfHash(entry.SourceSha256)] = entry
	return nil
}

// GetByHash returns the entry with the given hash, or nil if unknown.
func (s *InMemoryMultimodalMemoryStore) GetByHash(_ context.Context, sourceSha256 string) (*MultimodalMemoryEntry, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e, ok := s.byHash[keyOfHash(sourceSha256)]
	if !ok {
		return nil, nil
	}
	return &e, nil
}

// Reinforce increments ReferenceCount for the entry whose hash matches. No-op
// when unknown.
func (s *InMemoryMultimodalMemoryStore) Reinforce(_ context.Context, sourceSha256 string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := keyOfHash(sourceSha256)
	if e, ok := s.byHash[key]; ok {
		e.ReferenceCount++
		s.byHash[key] = e
	}
	return nil
}

// Search returns the top-topK entries whose embedding is most similar (cosine)
// to queryEmbedding. When the query is nil, falls back to most-recent.
func (s *InMemoryMultimodalMemoryStore) Search(_ context.Context, queryEmbedding []float32, topK int) ([]MultimodalMemoryEntry, error) {
	snapshot := s.snapshotValues()

	if queryEmbedding == nil {
		sortMultimodalRecencyDesc(snapshot)
		return takeMultimodal(snapshot, topK), nil
	}

	type scored struct {
		entry MultimodalMemoryEntry
		score float64
	}
	var candidates []scored
	for _, e := range snapshot {
		if len(e.Embedding) > 0 {
			candidates = append(candidates, scored{entry: e, score: cosineScore(queryEmbedding, e.Embedding)})
		}
	}
	sort.SliceStable(candidates, func(i, j int) bool {
		return candidates[i].score > candidates[j].score
	})
	limit := topK
	if limit > len(candidates) {
		limit = len(candidates)
	}
	if limit < 0 {
		limit = 0
	}
	out := make([]MultimodalMemoryEntry, 0, limit)
	for i := 0; i < limit; i++ {
		out = append(out, candidates[i].entry)
	}
	return out, nil
}

// GetRecent returns the most recent count entries, newest-first.
func (s *InMemoryMultimodalMemoryStore) GetRecent(_ context.Context, count int) ([]MultimodalMemoryEntry, error) {
	snapshot := s.snapshotValues()
	sortMultimodalRecencyDesc(snapshot)
	return takeMultimodal(snapshot, count), nil
}

// PruneOlderThan removes entries recorded strictly before cutoff. Returns the
// number removed.
func (s *InMemoryMultimodalMemoryStore) PruneOlderThan(_ context.Context, cutoff time.Time) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var doomed []string
	for k, e := range s.byHash {
		if e.RecordedAtUTC.Before(cutoff) {
			doomed = append(doomed, k)
		}
	}
	for _, k := range doomed {
		delete(s.byHash, k)
	}
	return len(doomed), nil
}

// Count returns the total entries currently stored.
func (s *InMemoryMultimodalMemoryStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.byHash), nil
}

func (s *InMemoryMultimodalMemoryStore) snapshotValues() []MultimodalMemoryEntry {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]MultimodalMemoryEntry, 0, len(s.byHash))
	for _, e := range s.byHash {
		out = append(out, e)
	}
	return out
}

func keyOfHash(sha string) string {
	return strings.ToLower(sha)
}

func sortMultimodalRecencyDesc(entries []MultimodalMemoryEntry) {
	sort.SliceStable(entries, func(i, j int) bool {
		return entries[i].RecordedAtUTC.After(entries[j].RecordedAtUTC)
	})
}

func takeMultimodal(entries []MultimodalMemoryEntry, n int) []MultimodalMemoryEntry {
	if n < 0 {
		n = 0
	}
	if n > len(entries) {
		n = len(entries)
	}
	out := make([]MultimodalMemoryEntry, n)
	copy(out, entries[:n])
	return out
}

// cosineScore matches the C# stores' internal CosineSimilarity.Score: full
// cosine (with magnitude normalisation), returning 0 for mismatched lengths or
// a near-zero denominator. Accumulates in float64 like the C# reference.
func cosineScore(a, b []float32) float64 {
	if len(a) != len(b) {
		return 0
	}
	var dot, magA, magB float64
	for i := 0; i < len(a); i++ {
		dot += float64(a[i]) * float64(b[i])
		magA += float64(a[i]) * float64(a[i])
		magB += float64(b[i]) * float64(b[i])
	}
	denom := math.Sqrt(magA) * math.Sqrt(magB)
	if denom < 2.220446049250313e-16 { // double.Epsilon-scale guard (Number.EPSILON)
		return 0
	}
	return dot / denom
}

// Compile-time assertion that the concrete store satisfies the interface.
var _ IMultimodalMemoryStore = (*InMemoryMultimodalMemoryStore)(nil)

// ---------------------------------------------------------------------------
// MultimodalMemoryIngester
// ---------------------------------------------------------------------------

// IngestionResult is the outcome of a MultimodalMemoryIngester.Ingest call.
type IngestionResult struct {
	Entry           MultimodalMemoryEntry
	WasDeduplicated bool
}

// IngestOptions holds optional per-call inputs for MultimodalMemoryIngester.Ingest.
type IngestOptions struct {
	// MimeType is the optional MIME type for the source.
	MimeType *string
	// SourceURI is the optional URI of the original (host-retained).
	SourceURI *string
	// Tags holds optional caller-supplied tags.
	Tags map[string]string
}

// MultimodalMemoryIngester ingests raw media bytes into compressed semantic
// memory:
//
//  1. Hashes the source (SHA-256, hex-lower).
//  2. Dedupes — if the hash is known, reinforces the existing entry and returns
//     it (no re-captioning, no duplicate storage).
//  3. Picks a captioner via CanCaption().
//  4. Asks the captioner for a CaptionResult.
//  5. Persists a MultimodalMemoryEntry to the store.
//
// Raw bytes are never persisted. The hash is the only durable handle the memory
// layer keeps for the original artefact.
type MultimodalMemoryIngester struct {
	captioners []IMultimodalCaptioner
	store      IMultimodalMemoryStore
}

// NewMultimodalMemoryIngester creates an ingester. Captioners are tried in
// order — the first one whose CanCaption() returns true wins. The host
// typically registers richer captioners first and the heuristic fallback last.
// At least one captioner is required.
func NewMultimodalMemoryIngester(captioners []IMultimodalCaptioner, store IMultimodalMemoryStore) (*MultimodalMemoryIngester, error) {
	if store == nil {
		return nil, errors.New("store required")
	}
	cs := make([]IMultimodalCaptioner, len(captioners))
	copy(cs, captioners)
	if len(cs) == 0 {
		return nil, errors.New("at least one captioner is required")
	}
	return &MultimodalMemoryIngester{captioners: cs, store: store}, nil
}

// Ingest ingests an artefact. When the SHA-256 matches an existing entry the
// stored record is reinforced rather than re-captioned, and the result's
// WasDeduplicated is true.
func (ing *MultimodalMemoryIngester) Ingest(ctx context.Context, modality MediaModality, sourceBytes []byte, options IngestOptions) (IngestionResult, error) {
	if len(sourceBytes) == 0 {
		return IngestionResult{}, errors.New("source bytes are empty")
	}

	hash := computeSha256(sourceBytes)
	existing, err := ing.store.GetByHash(ctx, hash)
	if err != nil {
		return IngestionResult{}, err
	}
	if existing != nil {
		if err := ing.store.Reinforce(ctx, hash); err != nil {
			return IngestionResult{}, err
		}
		// C#/TS return the same (reference-typed) entry the store mutated, so the
		// caller observes the incremented ReferenceCount. Go stores hold value
		// copies, so re-fetch the post-reinforce state to preserve that
		// behaviour (falling back to the pre-reinforce copy if it vanished).
		if refreshed, rerr := ing.store.GetByHash(ctx, hash); rerr == nil && refreshed != nil {
			return IngestionResult{Entry: *refreshed, WasDeduplicated: true}, nil
		}
		return IngestionResult{Entry: *existing, WasDeduplicated: true}, nil
	}

	captioner := ing.pickCaptioner(modality, options.MimeType)
	caption, err := captioner.Caption(ctx, modality, sourceBytes, options.MimeType)
	if err != nil {
		return IngestionResult{}, err
	}

	entry := NewMultimodalMemoryEntry()
	entry.Modality = modality
	entry.Caption = caption.Caption
	entry.Embedding = caption.Embedding
	entry.SourceSha256 = hash
	entry.SourceMimeType = options.MimeType
	entry.SourceByteCount = int64(len(sourceBytes))
	entry.SourceURI = options.SourceURI
	entry.WidthPx = caption.WidthPx
	entry.HeightPx = caption.HeightPx
	entry.DurationMs = caption.DurationMs
	entry.Tags = options.Tags

	if err := ing.store.Add(ctx, entry); err != nil {
		return IngestionResult{}, err
	}
	return IngestionResult{Entry: entry, WasDeduplicated: false}, nil
}

func (ing *MultimodalMemoryIngester) pickCaptioner(modality MediaModality, mime *string) IMultimodalCaptioner {
	for _, c := range ing.captioners {
		if c.CanCaption(modality, mime) {
			return c
		}
	}
	// The last registered captioner should accept everything; if no
	// host-supplied captioner matches, the fallback wins.
	return ing.captioners[len(ing.captioners)-1]
}

func computeSha256(bytes []byte) string {
	sum := sha256.Sum256(bytes)
	return hex.EncodeToString(sum[:])
}
