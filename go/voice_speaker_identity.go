// voice_speaker_identity.go
//
// Ports CircleAI.Voice.OnnxSpeakerIdentity.cs — the ISpeakerIdentity
// implementation, refactored around an injected ISpeakerEmbedder seam (the C#
// ONNX InferenceSession + log-mel front-end is the injected dependency;
// native/ONNX is out of scope).
//
// PORTED FAITHFULLY:
//   - Enroll: running-mean centroid update (prev*n + new)/(n+1) then L2-normalise,
//     SampleCount++,
//   - Identify: cosine similarity against every enrolled centroid, best match wins
//     iff bestSim >= MatchThreshold (else no identification),
//   - control flow: empty audio -> no ID; no enrollees -> no ID; sample-rate
//     mismatch -> embedding failure -> no ID; Min/MaxUtteranceMs clamp,
//   - L2Normalise / CosineSimilarity helpers (verbatim math).
//
// A deterministic default embedder (HashSpeakerEmbedder) makes enroll/identify
// genuinely functional with no external dependency: it maps audio bytes to a
// stable L2-normalised vector, so re-enrolling the same clip identifies the same
// user. Enrollment persists in-memory (thread-safe); the JSON store-path field is
// retained for parity but file I/O is left to the host (kept hermetic).

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"strings"
	"sync"
)

// errDisposed builds a consistent disposed-object error.
func errDisposed(what string) error { return fmt.Errorf("%s disposed", what) }

// ISpeakerEmbedder is the injected seam that maps a normalised float32 waveform
// window to a fixed-dimension speaker embedding (the C# ONNX session's role). The
// returned vector need not be normalised — the detector L2-normalises it.
type ISpeakerEmbedder interface {
	// Embed returns a speaker embedding for the given normalised waveform.
	Embed(window []float32) []float32
}

// InMemorySpeakerIdentity is a deterministic ISpeakerIdentity. It windows PCM-16
// audio to float32, delegates embedding to an ISpeakerEmbedder, and does
// enroll/identify by running-mean centroids + cosine similarity. Ports
// OnnxSpeakerIdentity's logic (ONNX session -> injected embedder).
type InMemorySpeakerIdentity struct {
	embedder       ISpeakerEmbedder
	sampleRateHz   int
	minUtteranceMs int
	maxUtteranceMs int
	matchThreshold float64

	mu       sync.Mutex
	enrolled map[string]EnrolledSpeaker
	disposed bool
}

// NewInMemorySpeakerIdentity constructs an engine. Pass nil embedder to use the
// deterministic HashSpeakerEmbedder (192-D). cfg fields default like the C# record
// (SampleRateHz<=0 -> 16000; MinUtteranceMs<=0 -> 1000; MaxUtteranceMs<=0 -> 8000;
// MatchThreshold<=0 -> 0.55).
func NewInMemorySpeakerIdentity(cfg SpeakerIdentityConfig, embedder ISpeakerEmbedder) *InMemorySpeakerIdentity {
	sr := cfg.SampleRateHz
	if sr <= 0 {
		sr = 16000
	}
	minMs := cfg.MinUtteranceMs
	if minMs <= 0 {
		minMs = 1000
	}
	maxMs := cfg.MaxUtteranceMs
	if maxMs <= 0 {
		maxMs = 8000
	}
	thr := cfg.MatchThreshold
	if thr <= 0 {
		thr = 0.55
	}
	if embedder == nil {
		embedder = HashSpeakerEmbedder{Dim: 192}
	}
	return &InMemorySpeakerIdentity{
		embedder:       embedder,
		sampleRateHz:   sr,
		minUtteranceMs: minMs,
		maxUtteranceMs: maxMs,
		matchThreshold: thr,
		enrolled:       map[string]EnrolledSpeaker{},
	}
}

// Identify identifies the speaker of audioPcm16. Returns (userId, true) on a match
// above the threshold, or ("", false) otherwise. Ports OnnxSpeakerIdentity.IdentifyAsync.
func (s *InMemorySpeakerIdentity) Identify(ctx context.Context, audioPcm16 []byte, sampleRateHz int) (string, bool, error) {
	s.mu.Lock()
	if s.disposed {
		s.mu.Unlock()
		return "", false, errDisposed("speaker identity")
	}
	if len(audioPcm16) == 0 || len(s.enrolled) == 0 {
		s.mu.Unlock()
		return "", false, nil
	}
	// Snapshot centroids under the lock so identification is consistent.
	snapshot := make([]EnrolledSpeaker, 0, len(s.enrolled))
	for _, sp := range s.enrolled {
		snapshot = append(snapshot, sp)
	}
	s.mu.Unlock()

	if err := ctx.Err(); err != nil {
		return "", false, err
	}

	embedding, ok := s.computeEmbedding(audioPcm16, sampleRateHz)
	if !ok {
		return "", false, nil
	}

	best := ""
	bestSim := math.Inf(-1)
	for _, sp := range snapshot {
		if err := ctx.Err(); err != nil {
			return "", false, err
		}
		sim := cosineSimilarityF64(embedding, sp.Centroid)
		if sim > bestSim {
			bestSim = sim
			best = sp.UserId
		}
	}
	if bestSim >= s.matchThreshold {
		return best, true, nil
	}
	return "", false, nil
}

// Enroll enrolls audioPcm16 under userId, updating the running centroid. Ports
// OnnxSpeakerIdentity.EnrollAsync.
func (s *InMemorySpeakerIdentity) Enroll(ctx context.Context, userId string, audioPcm16 []byte, sampleRateHz int) error {
	s.mu.Lock()
	disposed := s.disposed
	s.mu.Unlock()
	if disposed {
		return errDisposed("speaker identity")
	}
	if strings.TrimSpace(userId) == "" {
		return errors.New("userId required")
	}
	if len(audioPcm16) == 0 {
		return errors.New("audio required")
	}
	if err := ctx.Err(); err != nil {
		return err
	}

	embedding, ok := s.computeEmbedding(audioPcm16, sampleRateHz)
	if !ok {
		return errors.New("embedding extraction failed")
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	prev, exists := s.enrolled[userId]
	if !exists {
		s.enrolled[userId] = EnrolledSpeaker{UserId: userId, Centroid: embedding, SampleCount: 1}
		return nil
	}
	n := prev.SampleCount
	newCentroid := make([]float32, len(prev.Centroid))
	for i := 0; i < len(newCentroid); i++ {
		newCentroid[i] = (prev.Centroid[i]*float32(n) + embedding[i]) / float32(n+1)
	}
	l2Normalise(newCentroid)
	s.enrolled[userId] = EnrolledSpeaker{UserId: userId, Centroid: newCentroid, SampleCount: n + 1}
	return nil
}

// Close disposes the engine.
func (s *InMemorySpeakerIdentity) Close(context.Context) error {
	s.mu.Lock()
	s.disposed = true
	s.mu.Unlock()
	return nil
}

// EnrolledCount returns the number of enrolled speakers (test helper).
func (s *InMemorySpeakerIdentity) EnrolledCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.enrolled)
}

// computeEmbedding windows PCM-16 to float32 (with Min/MaxUtteranceMs gating),
// delegates to the embedder, and L2-normalises the result. Returns (nil, false)
// on sample-rate mismatch or too-short audio. Ports OnnxSpeakerIdentity.ComputeEmbedding.
func (s *InMemorySpeakerIdentity) computeEmbedding(pcm16 []byte, sampleRateHz int) ([]float32, bool) {
	if sampleRateHz != s.sampleRateHz {
		return nil, false
	}
	minSamples := sampleRateHz * s.minUtteranceMs / 1000
	maxSamples := sampleRateHz * s.maxUtteranceMs / 1000
	nSamples := len(pcm16) / 2
	if nSamples < minSamples {
		return nil, false
	}
	if nSamples > maxSamples {
		nSamples = maxSamples
	}

	window := make([]float32, nSamples)
	for i := 0; i < nSamples; i++ {
		s := int16(binary.LittleEndian.Uint16(pcm16[i*2 : i*2+2]))
		window[i] = float32(s) / 32768.0
	}

	output := s.embedder.Embed(window)
	if len(output) == 0 {
		return nil, false
	}
	// Copy so we never mutate the embedder's internal buffer.
	out := append([]float32(nil), output...)
	l2Normalise(out)
	return out, true
}

// l2Normalise normalises v in place. Ports OnnxSpeakerIdentity.L2Normalise.
func l2Normalise(v []float32) {
	var sumSq float64
	for i := 0; i < len(v); i++ {
		sumSq += float64(v[i]) * float64(v[i])
	}
	norm := math.Sqrt(sumSq)
	if norm < 1e-9 {
		return
	}
	for i := 0; i < len(v); i++ {
		v[i] = float32(float64(v[i]) / norm)
	}
}

// cosineSimilarityF64 returns the dot product of a and b (both assumed
// L2-normalised) in double precision. Ports OnnxSpeakerIdentity.CosineSimilarity
// (length mismatch -> -1). Named distinctly from the package's float32
// cosineSimilarity because OnnxSpeakerIdentity computes in double and compares
// against a double MatchThreshold — the wider type is load-bearing for parity.
func cosineSimilarityF64(a, b []float32) float64 {
	if len(a) != len(b) {
		return -1
	}
	var dot float64
	for i := 0; i < len(a); i++ {
		dot += float64(a[i]) * float64(b[i])
	}
	return dot
}

// HashSpeakerEmbedder is a deterministic ISpeakerEmbedder: it maps the waveform to
// a stable Dim-dimensional vector (energy buckets by sample index modulo Dim, plus
// a spectral-tilt term), so identical audio yields identical embeddings. Purely for
// hermetic operation in place of an injected ONNX embedding model.
type HashSpeakerEmbedder struct {
	// Dim is the embedding dimension (defaults to 192 when <= 0, like ECAPA-TDNN).
	Dim int
}

// Embed produces a Dim-dimensional deterministic embedding for window.
func (e HashSpeakerEmbedder) Embed(window []float32) []float32 {
	d := e.Dim
	if d <= 0 {
		d = 192
	}
	vec := make([]float32, d)
	if len(window) == 0 {
		// Non-zero constant so distinct empty inputs still normalise (avoids a
		// zero vector that L2Normalise would leave un-normalised); harmless because
		// too-short audio is gated out before Embed is reached in practice.
		vec[0] = 1
		return vec
	}
	for i, s := range window {
		vec[i%d] += s * s
		// Add a phase term coupling neighbouring samples so different speakers
		// (different waveforms) diverge in direction, not just magnitude.
		if i > 0 {
			vec[(i*7)%d] += (s - window[i-1]) * (s - window[i-1])
		}
	}
	return vec
}

// Interface guards.
var (
	_ ISpeakerIdentity = (*InMemorySpeakerIdentity)(nil)
	_ ISpeakerEmbedder = HashSpeakerEmbedder{}
)
