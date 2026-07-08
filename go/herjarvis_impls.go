// herjarvis_impls.go
//
// Ported from CircleAI.Companion (HerJarvisRealImplementations.cs) — the C#
// reference. Real, working in-process implementations for every HER/Jarvis
// contract: in-memory maps, channels, and plain math so tests and hosts both
// get behaviour, not no-ops. Production hosts that need cloud-scale variants
// swap any of these behind the same interface.
//
// Contracts already implemented elsewhere are NOT reimplemented here (world
// model, theory of mind, inner monologue, predictive engine). This file covers
// contracts 1-4, 6-9, 11-12, 15-24.
//
// Native/cloud bindings are injected as function fields (trainer, generator,
// test runner, bench runner) — never empty stubs — so the deterministic default
// runs standalone while a host can plug an LLM/MNN/ONNX backend behind the same
// type.

package circleai

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// ===================================================================
// 1. AlwaysOnPresence — tick-driven heartbeat with start/stop.
//
// The C# HeartbeatAlwaysOnPresence uses a System.Threading.Timer to increment a
// heartbeat counter every interval. The Go port uses a goroutine + ticker with
// the same semantics: Start is idempotent, the counter increments once at t=0
// then every interval, Stop halts and is idempotent.
// ===================================================================

// HeartbeatAlwaysOnPresence is a heartbeat-driven IAlwaysOnPresence.
type HeartbeatAlwaysOnPresence struct {
	interval time.Duration
	mu       sync.Mutex
	cancel   context.CancelFunc
	ticks    int64
	running  bool
}

// NewHeartbeatAlwaysOnPresence returns a presence beating every interval.
// A non-positive interval defaults to 30 seconds (matching the C# default).
func NewHeartbeatAlwaysOnPresence(interval time.Duration) *HeartbeatAlwaysOnPresence {
	if interval <= 0 {
		interval = 30 * time.Second
	}
	return &HeartbeatAlwaysOnPresence{interval: interval}
}

// IsRunning reports whether the heartbeat is active.
func (p *HeartbeatAlwaysOnPresence) IsRunning() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.running
}

// Heartbeats returns the number of heartbeats emitted so far.
func (p *HeartbeatAlwaysOnPresence) Heartbeats() int64 {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.ticks
}

// Start begins the heartbeat. Idempotent — a second call is a no-op.
func (p *HeartbeatAlwaysOnPresence) Start(_ context.Context) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.running {
		return nil
	}
	loopCtx, cancel := context.WithCancel(context.Background())
	p.cancel = cancel
	p.running = true
	// Immediate tick at t=0 (C# Timer dueTime = TimeSpan.Zero).
	p.ticks++
	go func() {
		ticker := time.NewTicker(p.interval)
		defer ticker.Stop()
		for {
			select {
			case <-loopCtx.Done():
				return
			case <-ticker.C:
				p.mu.Lock()
				p.ticks++
				p.mu.Unlock()
			}
		}
	}()
	return nil
}

// Stop halts the heartbeat. Idempotent.
func (p *HeartbeatAlwaysOnPresence) Stop(_ context.Context) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if !p.running {
		return nil
	}
	if p.cancel != nil {
		p.cancel()
		p.cancel = nil
	}
	p.running = false
	return nil
}

var _ IAlwaysOnPresence = (*HeartbeatAlwaysOnPresence)(nil)

// ===================================================================
// 2. FusedPerception — channel-based pub/sub with Publish hook.
// ===================================================================

// ChannelFusedPerception is a channel-backed IFusedPerception. Publish feeds
// percepts to all active streams; Complete closes them.
type ChannelFusedPerception struct {
	mu   sync.Mutex
	subs map[chan FusedPercept]struct{}
	done bool
}

// NewChannelFusedPerception returns an empty perception broker.
func NewChannelFusedPerception() *ChannelFusedPerception {
	return &ChannelFusedPerception{subs: make(map[chan FusedPercept]struct{})}
}

// Publish delivers a percept to every active subscriber. Nil percept sensors are
// tolerated; a fully-empty percept is still delivered (parity with C# TryWrite).
func (p *ChannelFusedPerception) Publish(percept FusedPercept) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.done {
		return
	}
	for ch := range p.subs {
		select {
		case ch <- percept:
		default:
			// Slow consumer: drop rather than block the publisher. The C#
			// unbounded channel never blocks the writer either.
		}
	}
}

// Complete closes all active subscriber streams.
func (p *ChannelFusedPerception) Complete() {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.done {
		return
	}
	p.done = true
	for ch := range p.subs {
		close(ch)
		delete(p.subs, ch)
	}
}

// Stream returns a channel of percepts. It closes when Complete is called or ctx
// is cancelled.
func (p *ChannelFusedPerception) Stream(ctx context.Context) <-chan FusedPercept {
	ch := make(chan FusedPercept, 64)
	p.mu.Lock()
	if p.done {
		p.mu.Unlock()
		close(ch)
		return ch
	}
	p.subs[ch] = struct{}{}
	p.mu.Unlock()

	out := make(chan FusedPercept)
	go func() {
		defer close(out)
		defer func() {
			p.mu.Lock()
			if _, ok := p.subs[ch]; ok {
				delete(p.subs, ch)
			}
			p.mu.Unlock()
		}()
		for {
			select {
			case <-ctx.Done():
				return
			case v, ok := <-ch:
				if !ok {
					return
				}
				select {
				case out <- v:
				case <-ctx.Done():
					return
				}
			}
		}
	}()
	return out
}

var _ IFusedPerception = (*ChannelFusedPerception)(nil)

// ===================================================================
// 3. IdentitySync — append-only delta log with monotonic cursor.
//
// Pull emits {"cursor":N,"deltas":[...]} where each delta is spliced in raw
// (the deltas are assumed to be JSON fragments), byte-identical to the C#
// StringBuilder assembly.
// ===================================================================

type identityDelta struct {
	cursor    int64
	deltaJSON string
}

// JSONIdentitySync is an append-only delta log IIdentitySync.
type JSONIdentitySync struct {
	mu   sync.Mutex
	log  []identityDelta
	next int64
}

// NewJSONIdentitySync returns an empty delta log.
func NewJSONIdentitySync() *JSONIdentitySync { return &JSONIdentitySync{} }

// Push appends deltaJSON to the log under the next monotonic cursor.
func (s *JSONIdentitySync) Push(_ context.Context, deltaJSON string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.next++
	s.log = append(s.log, identityDelta{cursor: s.next, deltaJSON: deltaJSON})
	return nil
}

// Pull returns the JSON envelope of all deltas with cursor > sinceCursor. An
// unparseable sinceCursor is treated as 0 (parity with C# long.TryParse).
func (s *JSONIdentitySync) Pull(_ context.Context, sinceCursor string) (string, error) {
	since, err := strconv.ParseInt(strings.TrimSpace(sinceCursor), 10, 64)
	if err != nil {
		since = 0
	}
	s.mu.Lock()
	var taken []string
	maxCursor := since
	for _, e := range s.log {
		if e.cursor > since {
			taken = append(taken, e.deltaJSON)
			maxCursor = e.cursor
		}
	}
	s.mu.Unlock()

	var b strings.Builder
	b.WriteString(`{"cursor":`)
	b.WriteString(strconv.FormatInt(maxCursor, 10))
	b.WriteString(`,"deltas":[`)
	for i, d := range taken {
		if i > 0 {
			b.WriteByte(',')
		}
		b.WriteString(d)
	}
	b.WriteString("]}")
	return b.String(), nil
}

var _ IIdentitySync = (*JSONIdentitySync)(nil)

// ===================================================================
// 4. ContinuousLearner — exponentially weighted average reward per id.
// ===================================================================

type ewaState struct {
	avg    float64
	weight float64
}

// EwaContinuousLearner keeps an exponentially-weighted average reward per
// interaction id. Ported from the C# EwaContinuousLearner.
type EwaContinuousLearner struct {
	mu    sync.Mutex
	state map[string]ewaState
	alpha float64
}

// NewEwaContinuousLearner returns a learner with the given smoothing factor
// alpha in (0,1]. A non-positive or >1 alpha returns an error (parity with the
// C# ArgumentOutOfRangeException).
func NewEwaContinuousLearner(alpha float64) (*EwaContinuousLearner, error) {
	if alpha <= 0 || alpha > 1 {
		return nil, fmt.Errorf("alpha out of range (0,1]: %v", alpha)
	}
	return &EwaContinuousLearner{state: make(map[string]ewaState), alpha: alpha}, nil
}

// NewEwaContinuousLearnerDefault returns a learner with alpha = 0.2.
func NewEwaContinuousLearnerDefault() *EwaContinuousLearner {
	l, _ := NewEwaContinuousLearner(0.2)
	return l
}

// RegisterFeedback folds reward into the running average for interactionID.
func (l *EwaContinuousLearner) RegisterFeedback(_ context.Context, interactionID string, reward float64, _ string) error {
	if strings.TrimSpace(interactionID) == "" {
		return errors.New("interactionId required")
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	prev, ok := l.state[interactionID]
	if !ok {
		l.state[interactionID] = ewaState{avg: reward, weight: 1.0}
	} else {
		l.state[interactionID] = ewaState{
			avg:    prev.avg*(1-l.alpha) + reward*l.alpha,
			weight: prev.weight + 1,
		}
	}
	return nil
}

// AverageRewardOf returns the current average reward for interactionID, or
// (0, false) if unseen.
func (l *EwaContinuousLearner) AverageRewardOf(interactionID string) (float64, bool) {
	l.mu.Lock()
	defer l.mu.Unlock()
	s, ok := l.state[interactionID]
	return s.avg, ok
}

// ObservationsOf returns the number of feedback observations for interactionID.
func (l *EwaContinuousLearner) ObservationsOf(interactionID string) int64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	s, ok := l.state[interactionID]
	if !ok {
		return 0
	}
	return int64(s.weight)
}

var _ IContinuousLearner = (*EwaContinuousLearner)(nil)

// ===================================================================
// 6. GoalPursuer — store goal + milestones; replan recalculates plan.
// ===================================================================

// InMemoryGoalPursuer is an in-memory IGoalPursuer. The plan is a JSON object
// with evenly-spaced milestones, matching the C# BuildPlan byte-for-byte.
type InMemoryGoalPursuer struct {
	mu    sync.Mutex
	goals map[string]LongHorizonGoal
	now   func() time.Time
}

// NewInMemoryGoalPursuer returns an empty pursuer using the real UTC clock.
func NewInMemoryGoalPursuer() *InMemoryGoalPursuer {
	return NewInMemoryGoalPursuerAt(func() time.Time { return time.Now().UTC() })
}

// NewInMemoryGoalPursuerAt returns a pursuer using the supplied clock (tests).
func NewInMemoryGoalPursuerAt(clock func() time.Time) *InMemoryGoalPursuer {
	return &InMemoryGoalPursuer{goals: make(map[string]LongHorizonGoal), now: clock}
}

// Register creates a new goal with a milestone plan. The deadline must be in the
// future (parity with the C# ArgumentException).
func (p *InMemoryGoalPursuer) Register(_ context.Context, description string, deadlineUTC time.Time) (LongHorizonGoal, error) {
	if strings.TrimSpace(description) == "" {
		return LongHorizonGoal{}, errors.New("description required")
	}
	now := p.now()
	if !deadlineUTC.After(now) {
		return LongHorizonGoal{}, errors.New("deadline must be in the future")
	}
	id := uuid.New().String()
	id = strings.ReplaceAll(id, "-", "") // C# Guid "n" format = no dashes.
	g := LongHorizonGoal{
		ID:               id,
		Description:      description,
		DeadlineUTC:      deadlineUTC,
		PlanJSON:         buildGoalPlan(description, now, deadlineUTC),
		ProgressFraction: 0,
	}
	p.mu.Lock()
	p.goals[id] = g
	p.mu.Unlock()
	return g, nil
}

// Current returns the goal for id, or (nil, nil) if unknown.
func (p *InMemoryGoalPursuer) Current(_ context.Context, id string) (*LongHorizonGoal, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	g, ok := p.goals[id]
	if !ok {
		return nil, nil
	}
	return &g, nil
}

// Replan recomputes the milestone plan for id from the current time.
func (p *InMemoryGoalPursuer) Replan(_ context.Context, id string) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	g, ok := p.goals[id]
	if !ok {
		return fmt.Errorf("Unknown goal %s", id)
	}
	g.PlanJSON = buildGoalPlan(g.Description, p.now(), g.DeadlineUTC)
	p.goals[id] = g
	return nil
}

// Progress sets the progress fraction (0..1) for id.
func (p *InMemoryGoalPursuer) Progress(id string, fraction float64) error {
	if fraction < 0 || fraction > 1 {
		return fmt.Errorf("fraction out of range [0,1]: %v", fraction)
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	g, ok := p.goals[id]
	if !ok {
		return fmt.Errorf("Unknown goal %s", id)
	}
	g.ProgressFraction = fraction
	p.goals[id] = g
	return nil
}

// buildGoalPlan reproduces the C# BuildPlan: milestone count = clamp(totalDays/14,
// 2, 8), evenly spaced, each with an "O"-format ISO-8601 UTC due date.
func buildGoalPlan(description string, now, deadlineUTC time.Time) string {
	totalDays := int((deadlineUTC.Sub(now)) / (24 * time.Hour))
	if totalDays < 1 {
		totalDays = 1
	}
	milestones := totalDays / 14
	if milestones < 2 {
		milestones = 2
	}
	if milestones > 8 {
		milestones = 8
	}
	step := deadlineUTC.Sub(now) / time.Duration(milestones)
	descJSON, _ := json.Marshal(description)
	var b strings.Builder
	b.WriteString(`{"description":`)
	b.Write(descJSON)
	b.WriteString(`,"milestones":[`)
	for i := 1; i <= milestones; i++ {
		if i > 1 {
			b.WriteByte(',')
		}
		due := now.Add(step * time.Duration(i))
		b.WriteString(`{"index":`)
		b.WriteString(strconv.Itoa(i))
		b.WriteString(`,"due":"`)
		b.WriteString(formatRoundTripUTC(due))
		b.WriteString(`"}`)
	}
	b.WriteString("]}")
	return b.String()
}

// formatRoundTripUTC renders t as .NET's "O" round-trip format in UTC:
// yyyy-MM-ddTHH:mm:ss.fffffffZ (7 fractional digits).
func formatRoundTripUTC(t time.Time) string {
	t = t.UTC()
	// 100-ns ticks: .NET "O" prints exactly 7 fractional digits.
	frac := t.Nanosecond() / 100
	return fmt.Sprintf("%04d-%02d-%02dT%02d:%02d:%02d.%07dZ",
		t.Year(), int(t.Month()), t.Day(), t.Hour(), t.Minute(), t.Second(), frac)
}

var _ IGoalPursuer = (*InMemoryGoalPursuer)(nil)

// ===================================================================
// 7. EpisodicMemory — term-frequency similarity recall.
// ===================================================================

var episodeTermSplit = regexp.MustCompile(`[^A-Za-z0-9]+`)

// TfEpisodicMemory is a term-frequency-similarity IEpisodicMemory.
type TfEpisodicMemory struct {
	mu       sync.Mutex
	episodes map[string]EpisodeRecord
	terms    map[string]map[string]int
}

// NewTfEpisodicMemory returns an empty episodic memory.
func NewTfEpisodicMemory() *TfEpisodicMemory {
	return &TfEpisodicMemory{
		episodes: make(map[string]EpisodeRecord),
		terms:    make(map[string]map[string]int),
	}
}

// Record stores an episode and indexes its title+content terms.
func (m *TfEpisodicMemory) Record(_ context.Context, episode EpisodeRecord) error {
	if strings.TrimSpace(episode.ID) == "" {
		return errors.New("Id required")
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.episodes[episode.ID] = episode
	m.terms[episode.ID] = toTermFrequency(episode.Title + " " + episode.ContentJSON)
	return nil
}

// Recall returns up to take episodes ranked by term-overlap score (desc).
func (m *TfEpisodicMemory) Recall(_ context.Context, query string, take int) ([]EpisodeRecord, error) {
	if take <= 0 {
		return nil, fmt.Errorf("take must be positive: %d", take)
	}
	qTerms := toTermFrequency(query)
	if len(qTerms) == 0 {
		return []EpisodeRecord{}, nil
	}
	m.mu.Lock()
	type scored struct {
		e     EpisodeRecord
		score float64
	}
	var hits []scored
	for id, e := range m.episodes {
		s := tfScore(qTerms, m.terms[id])
		if s > 0 {
			hits = append(hits, scored{e: e, score: s})
		}
	}
	m.mu.Unlock()

	sort.SliceStable(hits, func(i, j int) bool { return hits[i].score > hits[j].score })
	if len(hits) > take {
		hits = hits[:take]
	}
	out := make([]EpisodeRecord, len(hits))
	for i, h := range hits {
		out[i] = h.e
	}
	return out, nil
}

func toTermFrequency(text string) map[string]int {
	d := make(map[string]int)
	for _, t := range episodeTermSplit.Split(text, -1) {
		if len(t) >= 2 {
			d[strings.ToLower(t)]++
		}
	}
	return d
}

func tfScore(q, d map[string]int) float64 {
	if d == nil {
		return 0
	}
	var s float64
	for k, qv := range q {
		if dv, ok := d[k]; ok {
			s += float64(qv * dv)
		}
	}
	return s
}

var _ IEpisodicMemory = (*TfEpisodicMemory)(nil)

// ===================================================================
// 8. VoiceIdentity — MFCC fingerprint over windowed audio.
//
// The standard speech-recognition pipeline (pre-emphasis → framing → Hamming
// window → DFT power spectrum → mel filterbank → log → DCT → mean across
// frames) is ported verbatim from the C# EnergyBandVoiceIdentity. Mean-MFCC +
// cosine similarity is the standard in-process speaker fingerprint baseline; a
// neural model is the injected production upgrade.
// ===================================================================

const (
	mfccNumCoefficients = 13
	mfccNumMelFilters   = 26
	mfccFrameSize       = 400 // 25ms @ 16kHz
	mfccFrameStep       = 160 // 10ms @ 16kHz
	mfccPreEmphasis     = 0.97
	voiceMatchThreshold = 0.85
)

// EnergyBandVoiceIdentity is an MFCC-fingerprint IVoiceIdentity.
type EnergyBandVoiceIdentity struct {
	mu       sync.Mutex
	enrolled map[string][][]float64
}

// NewEnergyBandVoiceIdentity returns an empty voice-identity store.
func NewEnergyBandVoiceIdentity() *EnergyBandVoiceIdentity {
	return &EnergyBandVoiceIdentity{enrolled: make(map[string][][]float64)}
}

// Enroll adds a voice sample for userID.
func (v *EnergyBandVoiceIdentity) Enroll(_ context.Context, userID string, audioPCM16 []byte, sampleRateHz int) error {
	if strings.TrimSpace(userID) == "" {
		return errors.New("userId required")
	}
	fp := mfcc(audioPCM16, sampleRateHz)
	v.mu.Lock()
	v.enrolled[userID] = append(v.enrolled[userID], fp)
	v.mu.Unlock()
	return nil
}

// Identify returns the best-matching enrolled userID if cosine similarity
// exceeds 0.85, else nil.
func (v *EnergyBandVoiceIdentity) Identify(_ context.Context, audioPCM16 []byte, sampleRateHz int) (*string, error) {
	fp := mfcc(audioPCM16, sampleRateHz)
	var best string
	bestSim := -1.0
	v.mu.Lock()
	for user, refs := range v.enrolled {
		for _, ref := range refs {
			sim := cosineSimilarity64(fp, ref)
			if sim > bestSim {
				bestSim = sim
				best = user
			}
		}
	}
	v.mu.Unlock()
	if bestSim > voiceMatchThreshold {
		b := best
		return &b, nil
	}
	return nil, nil
}

// mfcc computes the mean MFCC vector across all frames of the PCM-16 signal.
func mfcc(pcm16 []byte, sampleRateHz int) []float64 {
	samples := decodePCM16(pcm16)
	if len(samples) < mfccFrameSize {
		return make([]float64, mfccNumCoefficients)
	}
	preEmphasisFilter(samples)
	filters := melFilterbank(mfccNumMelFilters, mfccFrameSize, sampleRateHz)

	sum := make([]float64, mfccNumCoefficients)
	count := 0
	window := hammingWindow(mfccFrameSize)
	for start := 0; start+mfccFrameSize <= len(samples); start += mfccFrameStep {
		frame := make([]float64, mfccFrameSize)
		for i := 0; i < mfccFrameSize; i++ {
			frame[i] = samples[start+i] * window[i]
		}
		powerSpec := powerSpectrum(frame)
		melEnergies := applyFilterbank(powerSpec, filters)
		logEnergies := make([]float64, mfccNumMelFilters)
		for i := 0; i < mfccNumMelFilters; i++ {
			logEnergies[i] = math.Log(math.Max(1e-10, melEnergies[i]))
		}
		coeffs := dct(logEnergies, mfccNumCoefficients)
		for i := 0; i < mfccNumCoefficients; i++ {
			sum[i] += coeffs[i]
		}
		count++
	}
	if count == 0 {
		return sum
	}
	for i := 0; i < mfccNumCoefficients; i++ {
		sum[i] /= float64(count)
	}
	return sum
}

func decodePCM16(pcm16 []byte) []float64 {
	n := len(pcm16) / 2
	samples := make([]float64, n)
	for i := 0; i < n; i++ {
		s := int16(uint16(pcm16[i*2]) | uint16(pcm16[i*2+1])<<8)
		samples[i] = float64(s) / 32768.0
	}
	return samples
}

func preEmphasisFilter(samples []float64) {
	for i := len(samples) - 1; i > 0; i-- {
		samples[i] -= mfccPreEmphasis * samples[i-1]
	}
}

func hammingWindow(n int) []float64 {
	w := make([]float64, n)
	for i := 0; i < n; i++ {
		w[i] = 0.54 - 0.46*math.Cos(2*math.Pi*float64(i)/float64(n-1))
	}
	return w
}

func powerSpectrum(frame []float64) []float64 {
	n := len(frame)
	half := n/2 + 1
	spec := make([]float64, half)
	for k := 0; k < half; k++ {
		var re, im float64
		omega := -2.0 * math.Pi * float64(k) / float64(n)
		for t := 0; t < n; t++ {
			re += frame[t] * math.Cos(omega*float64(t))
			im += frame[t] * math.Sin(omega*float64(t))
		}
		spec[k] = re*re + im*im
	}
	return spec
}

func melFilterbank(numFilters, frameSize, sampleRateHz int) [][]float64 {
	hzToMel := func(hz float64) float64 { return 2595 * math.Log10(1+hz/700.0) }
	melToHz := func(mel float64) float64 { return 700 * (math.Pow(10, mel/2595) - 1) }
	lowMel := hzToMel(0)
	highMel := hzToMel(float64(sampleRateHz) / 2.0)
	melPoints := make([]float64, numFilters+2)
	for i := range melPoints {
		melPoints[i] = lowMel + (highMel-lowMel)*float64(i)/float64(len(melPoints)-1)
	}
	binPoints := make([]int, len(melPoints))
	for i := range melPoints {
		binPoints[i] = int(math.Floor(float64(frameSize+1) * melToHz(melPoints[i]) / float64(sampleRateHz)))
	}
	half := frameSize/2 + 1
	filters := make([][]float64, numFilters)
	for m := 0; m < numFilters; m++ {
		filters[m] = make([]float64, half)
		left := binPoints[m]
		centre := binPoints[m+1]
		right := binPoints[m+2]
		for k := left; k < centre && k < half; k++ {
			if centre != left {
				filters[m][k] = float64(k-left) / float64(centre-left)
			}
		}
		for k := centre; k < right && k < half; k++ {
			if right != centre {
				filters[m][k] = float64(right-k) / float64(right-centre)
			}
		}
	}
	return filters
}

func applyFilterbank(powerSpec []float64, filters [][]float64) []float64 {
	energies := make([]float64, len(filters))
	for m := range filters {
		var sum float64
		filter := filters[m]
		length := len(powerSpec)
		if len(filter) < length {
			length = len(filter)
		}
		for k := 0; k < length; k++ {
			sum += powerSpec[k] * filter[k]
		}
		energies[m] = sum
	}
	return energies
}

func dct(input []float64, numCoeffs int) []float64 {
	n := len(input)
	output := make([]float64, numCoeffs)
	for k := 0; k < numCoeffs; k++ {
		var sum float64
		for i := 0; i < n; i++ {
			sum += input[i] * math.Cos(math.Pi*float64(k)*(float64(i)+0.5)/float64(n))
		}
		output[k] = sum
	}
	return output
}

func cosineSimilarity64(a, b []float64) float64 {
	var dot, na, nb float64
	for i := range a {
		dot += a[i] * b[i]
		na += a[i] * a[i]
		nb += b[i] * b[i]
	}
	if na == 0 || nb == 0 {
		return 0
	}
	return dot / (math.Sqrt(na) * math.Sqrt(nb))
}

var _ IVoiceIdentity = (*EnergyBandVoiceIdentity)(nil)

// ===================================================================
// 9. CalibratedConfidence — nearest-neighbour calibration over history.
// ===================================================================

type calibOutcome struct {
	rawScore   float64
	wasCorrect bool
}

var hedgeRx = regexp.MustCompile(`(?i)\b(maybe|perhaps|might|possibly|unclear|don't know)\b`)

// HistoricalCalibratedConfidence calibrates a raw answer score against the
// correctness history of the 5 nearest previously-seen raw scores. Ported from
// the C# HistoricalCalibratedConfidence.
type HistoricalCalibratedConfidence struct {
	mu      sync.Mutex
	history []calibOutcome
}

// NewHistoricalCalibratedConfidence returns an empty calibrator.
func NewHistoricalCalibratedConfidence() *HistoricalCalibratedConfidence {
	return &HistoricalCalibratedConfidence{}
}

// RecordOutcome records a (rawScore, wasCorrect) sample used for calibration.
func (c *HistoricalCalibratedConfidence) RecordOutcome(rawScore float64, wasCorrect bool) {
	c.mu.Lock()
	c.history = append(c.history, calibOutcome{rawScore: clampFloat(rawScore, 0, 1), wasCorrect: wasCorrect})
	c.mu.Unlock()
}

// Evaluate returns a calibrated confidence band for answer.
func (c *HistoricalCalibratedConfidence) Evaluate(_ context.Context, answer, contextJSON string) (ConfidenceBand, error) {
	raw := computeRawScore(answer, contextJSON)
	c.mu.Lock()
	var calibrated float64
	if len(c.history) < 5 {
		calibrated = raw
	} else {
		// 5 nearest by |rawScore - raw|; fraction correct.
		hist := make([]calibOutcome, len(c.history))
		copy(hist, c.history)
		sort.SliceStable(hist, func(i, j int) bool {
			return math.Abs(hist[i].rawScore-raw) < math.Abs(hist[j].rawScore-raw)
		})
		nearby := hist[:5]
		correct := 0
		for _, h := range nearby {
			if h.wasCorrect {
				correct++
			}
		}
		calibrated = float64(correct) / float64(len(nearby))
	}
	c.mu.Unlock()

	halfBand := math.Max(0.05, 0.25-calibrated*0.2)
	return ConfidenceBand{
		Lower: math.Max(0, calibrated-halfBand),
		Upper: math.Min(1, calibrated+halfBand),
	}, nil
}

func computeRawScore(answer, contextJSON string) float64 {
	trimmed := strings.TrimSpace(answer)
	length := len(trimmed)
	if length < 1 {
		length = 1
	}
	hedges := len(hedgeRx.FindAllString(answer, -1))
	hedgePenalty := math.Min(0.5, float64(hedges)*0.1)
	hasContext := 0.0
	if strings.TrimSpace(contextJSON) != "" && len(contextJSON) > 2 {
		hasContext = 0.1
	}
	return clampFloat(math.Log(float64(length))/10.0+hasContext-hedgePenalty, 0, 1)
}

var _ ICalibratedConfidence = (*HistoricalCalibratedConfidence)(nil)

// ===================================================================
// 11. EmotionSensor — keyword + arousal-valence inference.
// ===================================================================

type emotionPattern struct {
	label   string
	arousal float64
	valence float64
	rx      *regexp.Regexp
}

var emotionPatterns = []emotionPattern{
	{"joy", 0.8, 0.9, regexp.MustCompile(`(?i)\b(happy|joy|delight|excited|love|wonderful)\b`)},
	{"anger", 0.9, -0.8, regexp.MustCompile(`(?i)\b(angry|furious|rage|hate|annoyed)\b`)},
	{"sad", 0.3, -0.7, regexp.MustCompile(`(?i)\b(sad|lonely|grief|cry|depressed|down)\b`)},
	{"fear", 0.85, -0.6, regexp.MustCompile(`(?i)\b(afraid|scared|terrified|anxious|worried)\b`)},
	{"surprise", 0.7, 0.3, regexp.MustCompile(`(?i)\b(surprised|amazed|astonished|wow)\b`)},
	{"calm", 0.1, 0.5, regexp.MustCompile(`(?i)\b(calm|peaceful|relaxed|content|fine)\b`)},
}

// KeywordEmotionSensor infers an EmotionFrame from keyword hits, weighting
// arousal/valence by match count. Ported from the C# KeywordEmotionSensor.
type KeywordEmotionSensor struct{}

// Sense returns the weighted emotion frame for fusedJSON, or "neutral" if no
// keyword matched.
func (KeywordEmotionSensor) Sense(_ context.Context, fusedJSON string) (EmotionFrame, error) {
	type hit struct {
		label   string
		arousal float64
		valence float64
		count   int
	}
	var hits []hit
	for _, p := range emotionPatterns {
		c := len(p.rx.FindAllString(fusedJSON, -1))
		if c > 0 {
			hits = append(hits, hit{p.label, p.arousal, p.valence, c})
		}
	}
	if len(hits) == 0 {
		return EmotionFrame{Label: "neutral", Arousal: 0, Valence: 0}, nil
	}
	totalWeight := 0
	var arousal, valence float64
	for _, h := range hits {
		totalWeight += h.count
		arousal += h.arousal * float64(h.count)
		valence += h.valence * float64(h.count)
	}
	arousal /= float64(totalWeight)
	valence /= float64(totalWeight)
	// Top by count (stable — first defined wins ties, matching C# OrderByDescending
	// stable order over the source array).
	top := hits[0]
	for _, h := range hits[1:] {
		if h.count > top.count {
			top = h
		}
	}
	return EmotionFrame{Label: top.label, Arousal: arousal, Valence: valence}, nil
}

var _ IEmotionSensor = KeywordEmotionSensor{}

// ===================================================================
// 12. SkillAcquisition — demo-store with name extraction.
// ===================================================================

// DemoStoreSkillAcquisition stores skills keyed by generated id and lists them
// ordered by name. Ported from the C# DemoStoreSkillAcquisition.
type DemoStoreSkillAcquisition struct {
	mu     sync.Mutex
	skills map[string]AcquiredSkill
}

// NewDemoStoreSkillAcquisition returns an empty skill store.
func NewDemoStoreSkillAcquisition() *DemoStoreSkillAcquisition {
	return &DemoStoreSkillAcquisition{skills: make(map[string]AcquiredSkill)}
}

// Acquire records a skill from a demonstration JSON blob, deriving its name from
// a top-level "name" string when present.
func (s *DemoStoreSkillAcquisition) Acquire(_ context.Context, demonstrationJSON string) (AcquiredSkill, error) {
	id := strings.ReplaceAll(uuid.New().String(), "-", "")
	name := extractSkillName(demonstrationJSON)
	if name == "" {
		name = "skill-" + id[:6]
	}
	skill := AcquiredSkill{ID: id, Name: name, DescriptionJSON: demonstrationJSON}
	s.mu.Lock()
	s.skills[id] = skill
	s.mu.Unlock()
	return skill, nil
}

// List returns all skills ordered by name.
func (s *DemoStoreSkillAcquisition) List(_ context.Context) ([]AcquiredSkill, error) {
	s.mu.Lock()
	out := make([]AcquiredSkill, 0, len(s.skills))
	for _, v := range s.skills {
		out = append(out, v)
	}
	s.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out, nil
}

func extractSkillName(demonstrationJSON string) string {
	var m map[string]json.RawMessage
	if err := json.Unmarshal([]byte(demonstrationJSON), &m); err != nil {
		return ""
	}
	raw, ok := m["name"]
	if !ok {
		return ""
	}
	var name string
	if err := json.Unmarshal(raw, &name); err != nil {
		return ""
	}
	return name
}

var _ ISkillAcquisition = (*DemoStoreSkillAcquisition)(nil)

// ===================================================================
// 15. PersonalKnowledgeGraph — adjacency-list graph with relation kinds.
// ===================================================================

// AdjacencyPersonalKnowledgeGraph is an adjacency-list IPersonalKnowledgeGraph.
// Ported from the C# AdjacencyPersonalKnowledgeGraph. KnowledgeNode is the
// shared type from memory_graph.go.
type AdjacencyPersonalKnowledgeGraph struct {
	mu       sync.Mutex
	nodes    map[string]KnowledgeNode
	outEdges map[string][]KnowledgeRelation
}

// NewAdjacencyPersonalKnowledgeGraph returns an empty graph.
func NewAdjacencyPersonalKnowledgeGraph() *AdjacencyPersonalKnowledgeGraph {
	return &AdjacencyPersonalKnowledgeGraph{
		nodes:    make(map[string]KnowledgeNode),
		outEdges: make(map[string][]KnowledgeRelation),
	}
}

// UpsertNode inserts or replaces a node by ID.
func (g *AdjacencyPersonalKnowledgeGraph) UpsertNode(_ context.Context, node KnowledgeNode) error {
	if strings.TrimSpace(node.ID) == "" {
		return errors.New("Id required")
	}
	g.mu.Lock()
	g.nodes[node.ID] = node
	g.mu.Unlock()
	return nil
}

// UpsertRelation inserts or replaces a (from,to,relation) edge.
func (g *AdjacencyPersonalKnowledgeGraph) UpsertRelation(_ context.Context, rel KnowledgeRelation) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	list := g.outEdges[rel.FromID]
	filtered := list[:0]
	for _, r := range list {
		if r.ToID == rel.ToID && r.Relation == rel.Relation {
			continue
		}
		filtered = append(filtered, r)
	}
	filtered = append(filtered, rel)
	g.outEdges[rel.FromID] = filtered
	return nil
}

// Neighbours returns the target nodes of id's outgoing edges (in edge order).
func (g *AdjacencyPersonalKnowledgeGraph) Neighbours(_ context.Context, id string) ([]KnowledgeNode, error) {
	if strings.TrimSpace(id) == "" {
		return nil, errors.New("id required")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	rels, ok := g.outEdges[id]
	if !ok {
		return []KnowledgeNode{}, nil
	}
	var hits []KnowledgeNode
	for _, r := range rels {
		if n, ok := g.nodes[r.ToID]; ok {
			hits = append(hits, n)
		}
	}
	if hits == nil {
		hits = []KnowledgeNode{}
	}
	return hits, nil
}

var _ IPersonalKnowledgeGraph = (*AdjacencyPersonalKnowledgeGraph)(nil)

// ===================================================================
// 16. LiveWorldKnowledge — topic-pub/sub broker.
// ===================================================================

// TopicLiveWorldKnowledge is a per-topic pub/sub broker. Publish delivers a fact
// to subscribers of its topic; Subscribe merges the requested topics into one
// stream. Ported from the C# TopicLiveWorldKnowledge.
type TopicLiveWorldKnowledge struct {
	mu      sync.Mutex
	byTopic map[string]map[chan WorldFact]struct{}
}

// NewTopicLiveWorldKnowledge returns an empty broker.
func NewTopicLiveWorldKnowledge() *TopicLiveWorldKnowledge {
	return &TopicLiveWorldKnowledge{byTopic: make(map[string]map[chan WorldFact]struct{})}
}

// Publish delivers fact to all subscribers of fact.Topic.
func (b *TopicLiveWorldKnowledge) Publish(fact WorldFact) {
	b.mu.Lock()
	defer b.mu.Unlock()
	subs, ok := b.byTopic[fact.Topic]
	if !ok {
		return
	}
	for ch := range subs {
		select {
		case ch <- fact:
		default:
		}
	}
}

// Subscribe returns a merged stream of facts for topics. Closes on ctx cancel.
func (b *TopicLiveWorldKnowledge) Subscribe(ctx context.Context, topics []string) <-chan WorldFact {
	ch := make(chan WorldFact, 64)
	b.mu.Lock()
	for _, t := range topics {
		set, ok := b.byTopic[t]
		if !ok {
			set = make(map[chan WorldFact]struct{})
			b.byTopic[t] = set
		}
		set[ch] = struct{}{}
	}
	b.mu.Unlock()

	out := make(chan WorldFact)
	go func() {
		defer close(out)
		defer func() {
			b.mu.Lock()
			for _, t := range topics {
				if set, ok := b.byTopic[t]; ok {
					delete(set, ch)
				}
			}
			b.mu.Unlock()
		}()
		for {
			select {
			case <-ctx.Done():
				return
			case f := <-ch:
				select {
				case out <- f:
				case <-ctx.Done():
					return
				}
			}
		}
	}()
	return out
}

var _ ILiveWorldKnowledge = (*TopicLiveWorldKnowledge)(nil)

// ===================================================================
// 17. BioSignalStream — fan-in channel with Publish hook.
// ===================================================================

// ChannelBioSignalStream is a channel-backed IBioSignalStream.
type ChannelBioSignalStream struct {
	mu   sync.Mutex
	subs map[chan BioSignal]struct{}
	done bool
}

// NewChannelBioSignalStream returns an empty bio-signal broker.
func NewChannelBioSignalStream() *ChannelBioSignalStream {
	return &ChannelBioSignalStream{subs: make(map[chan BioSignal]struct{})}
}

// Publish delivers a bio-signal to every active subscriber.
func (b *ChannelBioSignalStream) Publish(s BioSignal) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.done {
		return
	}
	for ch := range b.subs {
		select {
		case ch <- s:
		default:
		}
	}
}

// Complete closes all active subscriber streams.
func (b *ChannelBioSignalStream) Complete() {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.done {
		return
	}
	b.done = true
	for ch := range b.subs {
		close(ch)
		delete(b.subs, ch)
	}
}

// Stream returns a channel of bio-signals. Closes on Complete or ctx cancel.
func (b *ChannelBioSignalStream) Stream(ctx context.Context) <-chan BioSignal {
	ch := make(chan BioSignal, 64)
	b.mu.Lock()
	if b.done {
		b.mu.Unlock()
		close(ch)
		return ch
	}
	b.subs[ch] = struct{}{}
	b.mu.Unlock()

	out := make(chan BioSignal)
	go func() {
		defer close(out)
		defer func() {
			b.mu.Lock()
			delete(b.subs, ch)
			b.mu.Unlock()
		}()
		for {
			select {
			case <-ctx.Done():
				return
			case v, ok := <-ch:
				if !ok {
					return
				}
				select {
				case out <- v:
				case <-ctx.Done():
					return
				}
			}
		}
	}()
	return out
}

var _ IBioSignalStream = (*ChannelBioSignalStream)(nil)

// ===================================================================
// 18. PhysicalActuator — device-handler registry with per-action dispatch.
// ===================================================================

// PhysicalDeviceHandler executes one command for a registered device.
type PhysicalDeviceHandler func(ctx context.Context, cmd PhysicalCommand) (PhysicalCommandResult, error)

// RegistryPhysicalActuator dispatches commands to registered device handlers.
// Ported from the C# RegistryPhysicalActuator.
type RegistryPhysicalActuator struct {
	mu       sync.Mutex
	handlers map[string]PhysicalDeviceHandler
}

// NewRegistryPhysicalActuator returns an actuator with no registered devices.
func NewRegistryPhysicalActuator() *RegistryPhysicalActuator {
	return &RegistryPhysicalActuator{handlers: make(map[string]PhysicalDeviceHandler)}
}

// RegisterDevice registers a handler for deviceID.
func (a *RegistryPhysicalActuator) RegisterDevice(deviceID string, handler PhysicalDeviceHandler) error {
	if strings.TrimSpace(deviceID) == "" {
		return errors.New("deviceId required")
	}
	if handler == nil {
		return errors.New("handler required")
	}
	a.mu.Lock()
	a.handlers[deviceID] = handler
	a.mu.Unlock()
	return nil
}

// Invoke dispatches command to its device handler, or returns a failure result
// naming the unknown device.
func (a *RegistryPhysicalActuator) Invoke(ctx context.Context, command PhysicalCommand) (PhysicalCommandResult, error) {
	a.mu.Lock()
	h, ok := a.handlers[command.DeviceID]
	a.mu.Unlock()
	if !ok {
		msg := fmt.Sprintf("Unknown device '%s'", command.DeviceID)
		return PhysicalCommandResult{Succeeded: false, Error: &msg}, nil
	}
	return h(ctx, command)
}

var _ IPhysicalActuator = (*RegistryPhysicalActuator)(nil)

// ===================================================================
// 19. AgentPeerNetwork — in-memory mailbox per agent id.
// ===================================================================

// MailboxAgentPeerNetwork is an in-memory per-agent mailbox network. Messages
// sent to an agent are queued and delivered to its Receive stream in order.
// Ported from the C# MailboxAgentPeerNetwork.
type MailboxAgentPeerNetwork struct {
	mu        sync.Mutex
	mailboxes map[string]*agentMailbox
}

type agentMailbox struct {
	mu     sync.Mutex
	buf    []AgentToAgentMessage
	notify chan struct{} // buffered(1); signals a new message
}

func newAgentMailbox() *agentMailbox {
	return &agentMailbox{notify: make(chan struct{}, 1)}
}

func (m *agentMailbox) push(msg AgentToAgentMessage) {
	m.mu.Lock()
	m.buf = append(m.buf, msg)
	m.mu.Unlock()
	select {
	case m.notify <- struct{}{}:
	default:
	}
}

func (m *agentMailbox) drain() []AgentToAgentMessage {
	m.mu.Lock()
	out := m.buf
	m.buf = nil
	m.mu.Unlock()
	return out
}

// NewMailboxAgentPeerNetwork returns an empty peer network.
func NewMailboxAgentPeerNetwork() *MailboxAgentPeerNetwork {
	return &MailboxAgentPeerNetwork{mailboxes: make(map[string]*agentMailbox)}
}

func (n *MailboxAgentPeerNetwork) box(agentID string) *agentMailbox {
	n.mu.Lock()
	defer n.mu.Unlock()
	b, ok := n.mailboxes[agentID]
	if !ok {
		b = newAgentMailbox()
		n.mailboxes[agentID] = b
	}
	return b
}

// Send queues message in the recipient's mailbox.
func (n *MailboxAgentPeerNetwork) Send(_ context.Context, message AgentToAgentMessage) error {
	n.box(message.ToAgentID).push(message)
	return nil
}

// Receive returns a stream of messages addressed to forAgentID. It drains any
// already-queued messages first, then blocks for new ones. Closes on ctx cancel.
func (n *MailboxAgentPeerNetwork) Receive(ctx context.Context, forAgentID string) <-chan AgentToAgentMessage {
	out := make(chan AgentToAgentMessage)
	if strings.TrimSpace(forAgentID) == "" {
		close(out)
		return out
	}
	box := n.box(forAgentID)
	go func() {
		defer close(out)
		for {
			for _, msg := range box.drain() {
				select {
				case out <- msg:
				case <-ctx.Done():
					return
				}
			}
			select {
			case <-ctx.Done():
				return
			case <-box.notify:
			}
		}
	}()
	return out
}

var _ IAgentPeerNetwork = (*MailboxAgentPeerNetwork)(nil)

// ===================================================================
// 20. FederatedFineTuner — job runner with status tracking.
//
// The trainer is an injected dependency (default: a deterministic progress ramp
// over a supplied line count). A host wires a real MNN/LoRA trainer behind the
// same TrainerFunc signature.
// ===================================================================

// FineTuneProgress reports fractional training progress in [0,1].
type FineTuneProgress func(fraction float64)

// TrainerFunc runs a training job, reporting progress. baseModel and
// trainingDataPath identify the run.
type TrainerFunc func(ctx context.Context, baseModel, trainingDataPath string, progress FineTuneProgress) error

// InMemoryFederatedFineTuner runs fine-tune jobs and tracks their status.
// Ported from the C# InMemoryFederatedFineTuner.
type InMemoryFederatedFineTuner struct {
	mu      sync.Mutex
	jobs    map[string]FineTuneJobStatus
	trainer TrainerFunc
	// lineCount backs the default trainer's ramp length. Hosts using the real
	// trainer ignore it.
	defaultSteps int
}

// NewInMemoryFederatedFineTuner returns a fine-tuner. A nil trainer uses the
// deterministic default ramp (defaultSteps steps, min 1).
func NewInMemoryFederatedFineTuner(trainer TrainerFunc, defaultSteps int) *InMemoryFederatedFineTuner {
	if defaultSteps <= 0 {
		defaultSteps = 100
	}
	t := &InMemoryFederatedFineTuner{
		jobs:         make(map[string]FineTuneJobStatus),
		trainer:      trainer,
		defaultSteps: defaultSteps,
	}
	if t.trainer == nil {
		t.trainer = t.defaultTrainer
	}
	return t
}

// NewInMemoryFederatedFineTunerDefault returns a fine-tuner with the default
// deterministic trainer and a 100-step ramp.
func NewInMemoryFederatedFineTunerDefault() *InMemoryFederatedFineTuner {
	return NewInMemoryFederatedFineTuner(nil, 100)
}

func (t *InMemoryFederatedFineTuner) setJob(jobID string, mutate func(s FineTuneJobStatus) FineTuneJobStatus) {
	t.mu.Lock()
	t.jobs[jobID] = mutate(t.jobs[jobID])
	t.mu.Unlock()
}

// Start launches a fine-tune job and returns its id. Training runs in the
// background; poll Status for progress.
func (t *InMemoryFederatedFineTuner) Start(ctx context.Context, baseModel, trainingDataPath string) (string, error) {
	if strings.TrimSpace(baseModel) == "" {
		return "", errors.New("baseModel required")
	}
	if strings.TrimSpace(trainingDataPath) == "" {
		return "", errors.New("trainingDataPath required")
	}
	jobID := strings.ReplaceAll(uuid.New().String(), "-", "")
	t.mu.Lock()
	t.jobs[jobID] = FineTuneJobStatus{JobID: jobID, Progress: 0, Error: nil}
	t.mu.Unlock()

	go func() {
		progress := func(p float64) {
			t.setJob(jobID, func(s FineTuneJobStatus) FineTuneJobStatus {
				s.JobID = jobID
				s.Progress = clampFloat(p, 0, 1)
				return s
			})
		}
		if err := t.trainer(ctx, baseModel, trainingDataPath, progress); err != nil {
			msg := err.Error()
			t.setJob(jobID, func(s FineTuneJobStatus) FineTuneJobStatus {
				s.JobID = jobID
				s.Error = &msg
				return s
			})
			return
		}
		t.setJob(jobID, func(s FineTuneJobStatus) FineTuneJobStatus {
			s.JobID = jobID
			s.Progress = 1.0
			s.Error = nil
			return s
		})
	}()
	return jobID, nil
}

// Status returns the current status of jobID, or an "unknown job" status.
func (t *InMemoryFederatedFineTuner) Status(_ context.Context, jobID string) (FineTuneJobStatus, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	s, ok := t.jobs[jobID]
	if !ok {
		msg := "unknown job"
		return FineTuneJobStatus{JobID: jobID, Progress: 0, Error: &msg}, nil
	}
	return s, nil
}

func (t *InMemoryFederatedFineTuner) defaultTrainer(ctx context.Context, _, _ string, progress FineTuneProgress) error {
	steps := t.defaultSteps
	if steps < 1 {
		steps = 1
	}
	step := 1.0 / float64(steps)
	for i := 0; i < steps; i++ {
		if ctx.Err() != nil {
			return nil
		}
		progress(float64(i) * step)
	}
	progress(1.0)
	return nil
}

var _ IFederatedFineTuner = (*InMemoryFederatedFineTuner)(nil)

// ===================================================================
// 21. FirstTokenOptimizer — sliding-window p50 latency tracker.
// ===================================================================

// SlidingP50FirstTokenOptimizer tracks the p50 first-token latency over a
// sliding window of samples. Ported from the C# SlidingP50FirstTokenOptimizer.
type SlidingP50FirstTokenOptimizer struct {
	mu         sync.Mutex
	samples    []int
	windowSize int
	targetMs   int
}

// NewSlidingP50FirstTokenOptimizer returns an optimizer with the given target
// (default 100ms) and window size (default 256). Non-positive values return an
// error (parity with the C# ArgumentOutOfRangeException).
func NewSlidingP50FirstTokenOptimizer(targetMs, windowSize int) (*SlidingP50FirstTokenOptimizer, error) {
	if targetMs <= 0 {
		return nil, fmt.Errorf("targetMs must be positive: %d", targetMs)
	}
	if windowSize <= 0 {
		return nil, fmt.Errorf("windowSize must be positive: %d", windowSize)
	}
	return &SlidingP50FirstTokenOptimizer{windowSize: windowSize, targetMs: targetMs}, nil
}

// NewSlidingP50FirstTokenOptimizerDefault returns an optimizer targeting 100ms
// over a 256-sample window.
func NewSlidingP50FirstTokenOptimizerDefault() *SlidingP50FirstTokenOptimizer {
	o, _ := NewSlidingP50FirstTokenOptimizer(100, 256)
	return o
}

// RecordFirstTokenLatency adds a latency sample (ms), evicting the oldest when
// the window is full.
func (o *SlidingP50FirstTokenOptimizer) RecordFirstTokenLatency(ms int) error {
	if ms < 0 {
		return fmt.Errorf("ms must be non-negative: %d", ms)
	}
	o.mu.Lock()
	o.samples = append(o.samples, ms)
	for len(o.samples) > o.windowSize {
		o.samples = o.samples[1:]
	}
	o.mu.Unlock()
	return nil
}

// Current returns the target vs current-p50 budget. p50 = sorted[len/2] (the
// C# upper-median convention), 0 when there are no samples.
func (o *SlidingP50FirstTokenOptimizer) Current(_ context.Context) (FirstTokenBudget, error) {
	o.mu.Lock()
	defer o.mu.Unlock()
	p50 := 0
	if len(o.samples) > 0 {
		sorted := make([]int, len(o.samples))
		copy(sorted, o.samples)
		sort.Ints(sorted)
		p50 = sorted[len(sorted)/2]
	}
	return FirstTokenBudget{TargetMs: o.targetMs, CurrentP50Ms: p50}, nil
}

var _ IFirstTokenOptimizer = (*SlidingP50FirstTokenOptimizer)(nil)

// ===================================================================
// 22. CryptoDelegation — ECDSA P-256 sign + verify.
//
// The C# EcdsaCryptoDelegation uses ECDsa over nistP256 with SHA-256. The Go
// port uses crypto/ecdsa over elliptic.P256() with the same canonical payload,
// so a credential issued and verified within one instance round-trips exactly.
// ===================================================================

// EcdsaCryptoDelegation issues and verifies ECDSA-P256-signed delegation
// credentials. Ported from the C# EcdsaCryptoDelegation.
type EcdsaCryptoDelegation struct {
	key    *ecdsa.PrivateKey
	issuer string
	now    func() time.Time
}

// NewEcdsaCryptoDelegation returns a delegation authority. A nil key generates a
// fresh P-256 key. An empty issuer returns an error (parity with C#).
func NewEcdsaCryptoDelegation(issuer string, key *ecdsa.PrivateKey) (*EcdsaCryptoDelegation, error) {
	if strings.TrimSpace(issuer) == "" {
		return nil, errors.New("issuer required")
	}
	if key == nil {
		k, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
		if err != nil {
			return nil, err
		}
		key = k
	}
	return &EcdsaCryptoDelegation{key: key, issuer: issuer, now: func() time.Time { return time.Now().UTC() }}, nil
}

// NewEcdsaCryptoDelegationDefault returns an authority issued as
// "circleai-companion" with a fresh key.
func NewEcdsaCryptoDelegationDefault() *EcdsaCryptoDelegation {
	d, _ := NewEcdsaCryptoDelegation("circleai-companion", nil)
	return d
}

// Issuer returns the issuer id stamped onto issued credentials.
func (d *EcdsaCryptoDelegation) Issuer() string { return d.issuer }

// Issue signs a delegation of scope to subjectID for lifetime.
func (d *EcdsaCryptoDelegation) Issue(subjectID, scope string, lifetime time.Duration) (DelegationCredential, error) {
	if strings.TrimSpace(subjectID) == "" {
		return DelegationCredential{}, errors.New("subjectId required")
	}
	if strings.TrimSpace(scope) == "" {
		return DelegationCredential{}, errors.New("scope required")
	}
	if lifetime <= 0 {
		return DelegationCredential{}, errors.New("lifetime must be positive")
	}
	expires := d.now().Add(lifetime)
	payload := d.canonical(subjectID, scope, expires)
	digest := sha256.Sum256([]byte(payload))
	sig, err := ecdsa.SignASN1(rand.Reader, d.key, digest[:])
	if err != nil {
		return DelegationCredential{}, err
	}
	return DelegationCredential{
		Issuer:       d.issuer,
		SubjectID:    subjectID,
		Scope:        scope,
		ExpiresAtUTC: expires,
		Signature:    base64.StdEncoding.EncodeToString(sig),
	}, nil
}

// Verify reports whether credential was issued by this authority, is unexpired,
// and carries a valid signature over its canonical payload.
func (d *EcdsaCryptoDelegation) Verify(credential DelegationCredential) bool {
	if credential.Issuer != d.issuer {
		return false
	}
	if !credential.ExpiresAtUTC.After(d.now()) {
		return false
	}
	if credential.Signature == "" {
		return false
	}
	sig, err := base64.StdEncoding.DecodeString(credential.Signature)
	if err != nil {
		return false
	}
	payload := d.canonical(credential.SubjectID, credential.Scope, credential.ExpiresAtUTC)
	digest := sha256.Sum256([]byte(payload))
	return ecdsa.VerifyASN1(&d.key.PublicKey, digest[:], sig)
}

// canonical reproduces the C# Canonical: issuer|subject|scope|expires("O").
func (d *EcdsaCryptoDelegation) canonical(subjectID, scope string, expiresAtUTC time.Time) string {
	return d.issuer + "|" + subjectID + "|" + scope + "|" + formatRoundTripUTC(expiresAtUTC)
}

var _ ICryptoDelegation = (*EcdsaCryptoDelegation)(nil)

// ===================================================================
// 23. CodeGenerationLoop — syntax-validates + runs registered tests.
//
// Generator, test runner, and deployment-hint are injected dependencies with
// deterministic defaults (echo generator, balance-check test runner). A host
// wires an LLM behind the generator and a real test harness behind the runner.
// ===================================================================

// CodeGenerator produces a code snippet for a prompt.
type CodeGenerator func(ctx context.Context, prompt string) (string, error)

// CodeTestRunner runs tests against a snippet, returning pass/fail.
type CodeTestRunner func(ctx context.Context, snippet string) (bool, error)

// DeploymentHinter returns a deployment hint for a snippet, or nil.
type DeploymentHinter func(snippet string) *string

// SyntaxCheckingCodeGenerationLoop generates, syntactically validates, tests,
// and hints deployment for a prompt. Ported from the C#
// SyntaxCheckingCodeGenerationLoop.
type SyntaxCheckingCodeGenerationLoop struct {
	generator      CodeGenerator
	testRunner     CodeTestRunner
	deploymentHint DeploymentHinter
}

// NewSyntaxCheckingCodeGenerationLoop wires the loop. Nil dependencies fall back
// to deterministic defaults.
func NewSyntaxCheckingCodeGenerationLoop(gen CodeGenerator, test CodeTestRunner, hint DeploymentHinter) *SyntaxCheckingCodeGenerationLoop {
	l := &SyntaxCheckingCodeGenerationLoop{generator: gen, testRunner: test, deploymentHint: hint}
	if l.generator == nil {
		l.generator = defaultCodeGenerator
	}
	if l.testRunner == nil {
		l.testRunner = defaultCodeTestRunner
	}
	if l.deploymentHint == nil {
		l.deploymentHint = defaultDeploymentHint
	}
	return l
}

// NewSyntaxCheckingCodeGenerationLoopDefault returns a loop with all defaults.
func NewSyntaxCheckingCodeGenerationLoopDefault() *SyntaxCheckingCodeGenerationLoop {
	return NewSyntaxCheckingCodeGenerationLoop(nil, nil, nil)
}

// Run generates a snippet for prompt, checks it, and returns the job. Tests only
// run when the snippet is syntactically balanced; a deploy hint is attached only
// when tests pass.
func (l *SyntaxCheckingCodeGenerationLoop) Run(ctx context.Context, prompt string) (CodeGenJob, error) {
	if strings.TrimSpace(prompt) == "" {
		return CodeGenJob{}, errors.New("prompt required")
	}
	id := strings.ReplaceAll(uuid.New().String(), "-", "")
	snippet, err := l.generator(ctx, prompt)
	if err != nil {
		return CodeGenJob{}, err
	}
	parses := isSyntacticallyBalanced(snippet)
	testsOk := false
	if parses {
		testsOk, err = l.testRunner(ctx, snippet)
		if err != nil {
			return CodeGenJob{}, err
		}
	}
	var hint *string
	if testsOk {
		hint = l.deploymentHint(snippet)
	}
	return CodeGenJob{ID: id, Prompt: prompt, OutputSnippet: snippet, TestsPass: testsOk, DeployHint: hint}, nil
}

func defaultCodeGenerator(_ context.Context, prompt string) (string, error) {
	return "// (3.3.0) generated from: " + strings.ReplaceAll(prompt, "\n", " ") + "\nreturn 0;", nil
}

func defaultCodeTestRunner(_ context.Context, snippet string) (bool, error) {
	return isSyntacticallyBalanced(snippet), nil
}

func defaultDeploymentHint(snippet string) *string {
	var h string
	if strings.Contains(snippet, "public class") {
		h = "stage as nuget"
	} else {
		h = "run inline"
	}
	return &h
}

func isSyntacticallyBalanced(snippet string) bool {
	if snippet == "" {
		return false
	}
	curly, paren, square := 0, 0, 0
	for _, c := range snippet {
		switch c {
		case '{':
			curly++
		case '}':
			curly--
		case '(':
			paren++
		case ')':
			paren--
		case '[':
			square++
		case ']':
			square--
		}
		if curly < 0 || paren < 0 || square < 0 {
			return false
		}
	}
	return curly == 0 && paren == 0 && square == 0
}

var _ ICodeGenerationLoop = (*SyntaxCheckingCodeGenerationLoop)(nil)

// ===================================================================
// 24. SelfImprovementLoop — tracks bench scores + applies improvements.
//
// runBench and proposeImprovement are injected; deterministic defaults keep the
// loop self-contained. A host wires a real bench harness (e.g. CircleAI.SelfBench)
// behind runBench. See SelfBenchSelfImprovementLoop (companion_selfbench.go) for
// the SelfBench-backed variant.
// ===================================================================

// BenchRunnerFunc runs a bench suite and returns its score in [0,1].
type BenchRunnerFunc func(ctx context.Context, benchSuiteID string) (float64, error)

// ImprovementProposerFunc proposes an improvement when a run regresses.
type ImprovementProposerFunc func(ctx context.Context, benchSuiteID string, current float64) (string, error)

// TrackingSelfImprovementLoop tracks the best bench score per suite and applies
// an improvement proposal on regression. Ported from the C#
// TrackingSelfImprovementLoop.
type TrackingSelfImprovementLoop struct {
	mu                 sync.Mutex
	bestScores         map[string]float64
	runBench           BenchRunnerFunc
	proposeImprovement ImprovementProposerFunc
}

// NewTrackingSelfImprovementLoop wires the loop. Nil dependencies fall back to
// deterministic defaults.
func NewTrackingSelfImprovementLoop(runBench BenchRunnerFunc, propose ImprovementProposerFunc) *TrackingSelfImprovementLoop {
	l := &TrackingSelfImprovementLoop{
		bestScores:         make(map[string]float64),
		runBench:           runBench,
		proposeImprovement: propose,
	}
	if l.runBench == nil {
		l.runBench = defaultRunBench
	}
	if l.proposeImprovement == nil {
		l.proposeImprovement = defaultProposeImprovement
	}
	return l
}

// NewTrackingSelfImprovementLoopDefault returns a loop with default deps.
func NewTrackingSelfImprovementLoopDefault() *TrackingSelfImprovementLoop {
	return NewTrackingSelfImprovementLoop(nil, nil)
}

// Cycle runs the suite, records a new best if it did not regress, else proposes
// an improvement. Returns the verdict and the current score.
func (l *TrackingSelfImprovementLoop) Cycle(ctx context.Context, benchSuiteID string) (SelfImprovementVerdict, error) {
	if strings.TrimSpace(benchSuiteID) == "" {
		return SelfImprovementVerdict{}, errors.New("benchSuiteId required")
	}
	l.mu.Lock()
	baseline := l.bestScores[benchSuiteID]
	l.mu.Unlock()

	current, err := l.runBench(ctx, benchSuiteID)
	if err != nil {
		return SelfImprovementVerdict{}, err
	}
	var applied string
	if current >= baseline {
		l.mu.Lock()
		l.bestScores[benchSuiteID] = current
		l.mu.Unlock()
		if current > baseline {
			applied = "new best"
		} else {
			applied = "no regression"
		}
	} else {
		applied, err = l.proposeImprovement(ctx, benchSuiteID, current)
		if err != nil {
			return SelfImprovementVerdict{}, err
		}
	}
	return SelfImprovementVerdict{ImprovementsApplied: applied, NewBenchScore: current}, nil
}

// BestScoreFor returns the best recorded score for benchSuiteID (0 if none).
func (l *TrackingSelfImprovementLoop) BestScoreFor(benchSuiteID string) float64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.bestScores[benchSuiteID]
}

func defaultRunBench(_ context.Context, id string) (float64, error) {
	// Deterministic pseudo-score in [0.5, 1.0] from a stable hash of the id
	// (mirrors the intent of the C# id.GetHashCode()-derived score, but stable).
	return 0.5 + float64(stableSeed(id)&0xFFFF)/65535.0*0.5, nil
}

func defaultProposeImprovement(_ context.Context, _ string, current float64) (string, error) {
	return fmt.Sprintf("retry-with-temperature-0 (score was %.3f)", current), nil
}

var _ ISelfImprovementLoop = (*TrackingSelfImprovementLoop)(nil)
