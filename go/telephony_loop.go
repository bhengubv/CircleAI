// telephony_loop.go
//
// The machinery of a live call: what the caller hears between the end of their
// sentence and the start of the reply. Port of the CircleAI.Telephony types
// that surround the voice loop — barge-in, sentence chunking, fillers,
// speculation, answering-machine detection, IVR loop detection, guardrails,
// latency and cost.
//
// An assistant on a phone line has constraints nothing else here has. There is
// no screen, so the only interface is what was just said. There is no
// scrollback, so a mistake cannot be re-read. And a person is waiting in real
// time, so every millisecond of silence is a millisecond they spend wondering
// if the line dropped.
//
// Money is in micro-units as int64 throughout. A call costs fractions of a cent
// and the total is summed over thousands of calls; float money is how a total
// stops matching the sum of its parts.

package circleai

import (
	"encoding/binary"
	"math"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Barge-in

// BargeInState is what the loop is doing about the caller talking over it.
type BargeInState int

const (
	// BargeInIdle — the agent is not speaking, so there is nothing to interrupt.
	BargeInIdle BargeInState = iota
	// BargeInSpeaking — the agent has the floor.
	BargeInSpeaking
	// BargeInSuspected — caller energy detected while the agent speaks. NOT yet
	// an interruption: the agent's own audio leaking back through a speaker
	// looks exactly like this, and cutting off on the first frame makes an
	// assistant that stops mid-word whenever the room is loud.
	BargeInSuspected
	// BargeInInterrupted — confirmed. The agent stops.
	BargeInInterrupted
)

func (s BargeInState) String() string {
	switch s {
	case BargeInIdle:
		return "idle"
	case BargeInSpeaking:
		return "speaking"
	case BargeInSuspected:
		return "suspected"
	case BargeInInterrupted:
		return "interrupted"
	}
	return "unknown"
}

// BargeInTransition is one state change, with what caused it.
type BargeInTransition struct {
	From   BargeInState
	To     BargeInState
	At     time.Time
	Reason string
}

// BargeInOptions tunes how eager the interruption is.
type BargeInOptions struct {
	// How long sustained caller speech must last before it counts. Below about
	// 200 ms this fires on a cough; above about 500 ms the caller has to talk
	// over the agent for half a second before anything happens, which feels
	// like being ignored.
	MinSpeechMs int
	// Energy floor for a frame to count as speech at all.
	EnergyThreshold float64
	// Frames of silence that end a suspected interruption without confirming it.
	SilenceFramesToCancel int
}

// DefaultBargeInOptions returns the measured settings.
func DefaultBargeInOptions() BargeInOptions {
	return BargeInOptions{MinSpeechMs: 280, EnergyThreshold: 0.015, SilenceFramesToCancel: 6}
}

// BargeInController decides when the caller has taken the floor back.
type BargeInController struct {
	mu          sync.Mutex
	opts        BargeInOptions
	state       BargeInState
	speechMs    int
	silenceRun  int
	transitions []BargeInTransition
}

// NewBargeInController returns a controller in the idle state.
func NewBargeInController(opts BargeInOptions) *BargeInController {
	if opts.MinSpeechMs <= 0 {
		opts = DefaultBargeInOptions()
	}
	return &BargeInController{opts: opts, state: BargeInIdle}
}

// AgentStartedSpeaking gives the agent the floor.
func (b *BargeInController) AgentStartedSpeaking(at time.Time) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.transition(BargeInSpeaking, at, "agent started")
	b.speechMs = 0
	b.silenceRun = 0
}

// AgentStoppedSpeaking returns to idle. Called on a completed utterance AND on
// an interruption, so the controller cannot be left believing the agent still
// holds a floor it gave up.
func (b *BargeInController) AgentStoppedSpeaking(at time.Time) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.transition(BargeInIdle, at, "agent stopped")
	b.speechMs = 0
	b.silenceRun = 0
}

// Observe feeds one frame of caller audio and returns the resulting state.
func (b *BargeInController) Observe(energy float64, frameMs int, at time.Time) BargeInState {
	b.mu.Lock()
	defer b.mu.Unlock()

	if b.state == BargeInIdle || b.state == BargeInInterrupted {
		return b.state
	}

	if energy >= b.opts.EnergyThreshold {
		b.silenceRun = 0
		b.speechMs += frameMs
		if b.state == BargeInSpeaking {
			b.transition(BargeInSuspected, at, "caller energy")
		}
		if b.speechMs >= b.opts.MinSpeechMs {
			b.transition(BargeInInterrupted, at, "sustained caller speech")
		}
		return b.state
	}

	b.silenceRun++
	if b.state == BargeInSuspected && b.silenceRun >= b.opts.SilenceFramesToCancel {
		// It was noise, not the caller. Give the floor back rather than
		// leaving the agent in a state where the next frame interrupts it.
		b.speechMs = 0
		b.transition(BargeInSpeaking, at, "suspected speech did not sustain")
	}
	return b.state
}

// State returns the current state.
func (b *BargeInController) State() BargeInState {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.state
}

// Transitions returns the history, oldest first.
func (b *BargeInController) Transitions() []BargeInTransition {
	b.mu.Lock()
	defer b.mu.Unlock()
	out := make([]BargeInTransition, len(b.transitions))
	copy(out, b.transitions)
	return out
}

func (b *BargeInController) transition(to BargeInState, at time.Time, reason string) {
	if b.state == to {
		return
	}
	b.transitions = append(b.transitions, BargeInTransition{From: b.state, To: to, At: at, Reason: reason})
	b.state = to
}

// IFalseInterruptionTracker records interruptions that turned out to be wrong,
// so the thresholds above can be argued with using numbers.
type IFalseInterruptionTracker interface {
	RecordInterruption(callID string, at time.Time)
	// RecordFalsePositive marks the most recent interruption on that call as
	// having been the agent's own audio or noise.
	RecordFalsePositive(callID string, at time.Time)
	Stats() InterruptionStats
}

// InterruptionStats is what the tracker knows.
type InterruptionStats struct {
	Total          int
	FalsePositives int
}

// FalsePositiveRate returns 0..1, or -1 when nothing has been recorded. Not
// zero for "no data": a fresh tracker reporting a perfect rate is the shape of
// a metric that looks good because nothing has happened yet.
func (s InterruptionStats) FalsePositiveRate() float64 {
	if s.Total == 0 {
		return -1
	}
	return float64(s.FalsePositives) / float64(s.Total)
}

// InMemoryFalseInterruptionTracker is the default tracker.
type InMemoryFalseInterruptionTracker struct {
	mu    sync.Mutex
	stats InterruptionStats
}

// NewInMemoryFalseInterruptionTracker returns an empty tracker.
func NewInMemoryFalseInterruptionTracker() *InMemoryFalseInterruptionTracker {
	return &InMemoryFalseInterruptionTracker{}
}

// RecordInterruption implements IFalseInterruptionTracker.
func (t *InMemoryFalseInterruptionTracker) RecordInterruption(_ string, _ time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.stats.Total++
}

// RecordFalsePositive implements IFalseInterruptionTracker.
func (t *InMemoryFalseInterruptionTracker) RecordFalsePositive(_ string, _ time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.stats.FalsePositives++
}

// Stats implements IFalseInterruptionTracker.
func (t *InMemoryFalseInterruptionTracker) Stats() InterruptionStats {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.stats
}

// ─────────────────────────────────────────────────────────────────────────────
// DTMF

// DtmfToneGenerator is the named type the C# exposes. The generation itself
// already lives in telephony_dtmf.go as free functions over one frequency
// table; this is the type, delegating, rather than a second copy of the table.
// Two tables of the same sixteen pairs is one edit away from disagreeing, and a
// wrong DTMF frequency is not an error - it is a digit the far end ignores.
type DtmfToneGenerator struct{}

// Generate returns one digit as PCM-16 mono little-endian.
//
// The grid is fixed by ITU Q.23 and is not ours to tune. The frequencies were
// chosen so that no tone is a harmonic of another, which is what lets a
// receiver pick them out of speech.
func (DtmfToneGenerator) Generate(digit rune, sampleRateHz, durationMs int, amplitude float32) ([]byte, error) {
	return DtmfGenerate(digit, sampleRateHz, durationMs, amplitude)
}

// GenerateSequence returns a whole string with silence between digits. The
// inter-digit gap is not cosmetic - without it a receiver reads "11" as one
// long 1.
func (DtmfToneGenerator) GenerateSequence(digits string, sampleRateHz, toneDurationMs, interDigitGapMs int, amplitude float32) ([]byte, error) {
	return DtmfGenerateSequence(digits, sampleRateHz, toneDurationMs, interDigitGapMs, amplitude)
}

// Frequencies returns the low and high tone for a digit, from the one table.
func (DtmfToneGenerator) Frequencies(digit rune) (low, high int, ok bool) {
	pair, found := dtmfFrequencies[toUpperInvariant(digit)]
	if !found {
		return 0, 0, false
	}
	return pair.low, pair.high, true
}

// ─────────────────────────────────────────────────────────────────────────────
// Answering-machine detection

// AmdVerdict is what answered the call.
type AmdVerdict int

const (
	AmdUnknown AmdVerdict = iota
	AmdHuman
	AmdAnsweringMachine
)

func (v AmdVerdict) String() string {
	switch v {
	case AmdHuman:
		return "human"
	case AmdAnsweringMachine:
		return "answering-machine"
	}
	return "unknown"
}

// AmdOptions are the thresholds, in milliseconds.
//
// The whole heuristic rests on one observation: a person answering says two
// words and stops, a machine plays a greeting. So it is the LENGTH OF THE FIRST
// CONTIGUOUS BURST that separates them, not its content — which means this runs
// on frames already arriving, with no model and no carrier fee.
type AmdOptions struct {
	// Longer than this and it is a greeting, not a hello.
	HumanMaxFirstUtteranceMs int
	// Shorter than this is not enough to decide — a click, a breath.
	HumanMinFirstUtteranceMs int
	// Stop accumulating. An undecided call is treated as a human, because
	// hanging up on a person is worse than talking to a machine.
	MaxObservationWindowMs  int
	SilenceFrameThresholdMs int
}

// DefaultAmdOptions returns the measured thresholds.
func DefaultAmdOptions() AmdOptions {
	return AmdOptions{
		HumanMaxFirstUtteranceMs: 1800,
		HumanMinFirstUtteranceMs: 300,
		MaxObservationWindowMs:   3500,
		SilenceFrameThresholdMs:  250,
	}
}

// AnsweringMachineDetector classifies the answering side frame by frame.
type AnsweringMachineDetector struct {
	mu                  sync.Mutex
	opts                AmdOptions
	firstUtteranceMs    int
	accumulatedMs       int
	utteranceInProgress bool
	trailingSilenceMs   int
	verdict             AmdVerdict
}

// NewAnsweringMachineDetector returns a detector with no verdict yet.
func NewAnsweringMachineDetector(opts AmdOptions) *AnsweringMachineDetector {
	if opts.MaxObservationWindowMs <= 0 {
		opts = DefaultAmdOptions()
	}
	return &AnsweringMachineDetector{opts: opts, verdict: AmdUnknown}
}

// Observe feeds one PCM-16 mono frame and returns the verdict so far.
//
// Once it settles it STAYS settled: a detector that changes its mind mid-greeting
// produces a call that starts talking over the beep.
func (d *AnsweringMachineDetector) Observe(pcm []byte, sampleRateHz int) AmdVerdict {
	if sampleRateHz <= 0 || len(pcm) < 2 {
		return d.Verdict()
	}
	frameMs := 1000 * (len(pcm) / 2) / sampleRateHz
	speech := frameHasSpeech(pcm)

	d.mu.Lock()
	defer d.mu.Unlock()
	if d.verdict != AmdUnknown {
		return d.verdict
	}
	d.accumulatedMs += frameMs

	if speech {
		d.trailingSilenceMs = 0
		d.utteranceInProgress = true
		d.firstUtteranceMs += frameMs
		if d.firstUtteranceMs > d.opts.HumanMaxFirstUtteranceMs {
			d.verdict = AmdAnsweringMachine
			return d.verdict
		}
	} else if d.utteranceInProgress {
		d.trailingSilenceMs += frameMs
		if d.trailingSilenceMs >= d.opts.SilenceFrameThresholdMs {
			if d.firstUtteranceMs >= d.opts.HumanMinFirstUtteranceMs {
				d.verdict = AmdHuman
			}
			d.utteranceInProgress = false
		}
	}

	if d.verdict == AmdUnknown && d.accumulatedMs >= d.opts.MaxObservationWindowMs {
		d.verdict = AmdHuman
	}
	return d.verdict
}

// Verdict returns the current verdict without feeding a frame.
func (d *AnsweringMachineDetector) Verdict() AmdVerdict {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.verdict
}

func frameHasSpeech(pcm []byte) bool {
	if len(pcm) < 2 {
		return false
	}
	var sum float64
	n := len(pcm) / 2
	for i := 0; i < n; i++ {
		s := float64(int16(binary.LittleEndian.Uint16(pcm[i*2:]))) / float64(math.MaxInt16)
		sum += s * s
	}
	return math.Sqrt(sum/float64(n)) >= 0.015
}

// ─────────────────────────────────────────────────────────────────────────────
// IVR loop detection

// IvrRound is one observation in an IVR conversation.
type IvrRound struct {
	Speech      string
	DtmfPressed string
	At          time.Time
}

// IvrLoopVerdict says whether the navigator is stuck.
type IvrLoopVerdict struct {
	IsLooping bool
	// How long the repeating cycle is, in rounds. Reported because "stuck" and
	// "stuck bouncing between two menus" want different recoveries.
	LoopLength int
	Reason     string
}

// IvrLoopDetector records rounds and surfaces a loop verdict.
type IvrLoopDetector struct {
	mu                  sync.Mutex
	rounds              []IvrRound
	maxRoundsToTrack    int
	minRoundsForLoop    int
	similarityThreshold float64
}

// NewIvrLoopDetector returns a detector. Defaults: 32 rounds tracked, 2 repeats
// to call it a loop, 0.85 similarity.
//
// Similarity rather than equality because an IVR rarely repeats itself
// byte-for-byte — the transcript differs by a word, a number, a filler — and an
// exact-match detector never fires on the real thing.
func NewIvrLoopDetector(maxRoundsToTrack, minRoundsForLoop int, similarityThreshold float64) *IvrLoopDetector {
	if maxRoundsToTrack <= 0 {
		maxRoundsToTrack = 32
	}
	if minRoundsForLoop <= 0 {
		minRoundsForLoop = 2
	}
	if similarityThreshold <= 0 {
		similarityThreshold = 0.85
	}
	return &IvrLoopDetector{
		maxRoundsToTrack:    maxRoundsToTrack,
		minRoundsForLoop:    minRoundsForLoop,
		similarityThreshold: similarityThreshold,
	}
}

// Observe appends one round and returns the current verdict.
func (d *IvrLoopDetector) Observe(round IvrRound) IvrLoopVerdict {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.rounds = append(d.rounds, round)
	for len(d.rounds) > d.maxRoundsToTrack {
		d.rounds = d.rounds[1:]
	}
	return d.evaluate()
}

// CurrentVerdict returns the verdict without adding a round.
func (d *IvrLoopDetector) CurrentVerdict() IvrLoopVerdict {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.evaluate()
}

// Reset drops all history.
func (d *IvrLoopDetector) Reset() {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.rounds = nil
}

func (d *IvrLoopDetector) evaluate() IvrLoopVerdict {
	n := len(d.rounds)
	if n < 2 {
		return IvrLoopVerdict{Reason: "not enough rounds"}
	}
	// Cycle lengths from 1 up to half the history: a cycle longer than half
	// cannot repeat within what we have kept, and claiming one would be
	// asserting a pattern from a single occurrence.
	for cycle := 1; cycle <= n/2; cycle++ {
		repeats := 0
		for start := n - cycle; start-cycle >= 0; start -= cycle {
			match := true
			for k := 0; k < cycle; k++ {
				if similarity(d.rounds[start+k].Speech, d.rounds[start-cycle+k].Speech) < d.similarityThreshold {
					match = false
					break
				}
			}
			if !match {
				break
			}
			repeats++
		}
		if repeats >= d.minRoundsForLoop {
			return IvrLoopVerdict{
				IsLooping:  true,
				LoopLength: cycle,
				Reason:     "the same menu came round again",
			}
		}
	}
	return IvrLoopVerdict{Reason: "no repeating cycle"}
}

// similarity is token overlap over the union — cheap, order-insensitive, and
// good enough for "is this the same menu". Two empty strings are identical.
func similarity(a, b string) float64 {
	at := strings.Fields(strings.ToLower(a))
	bt := strings.Fields(strings.ToLower(b))
	if len(at) == 0 && len(bt) == 0 {
		return 1
	}
	if len(at) == 0 || len(bt) == 0 {
		return 0
	}
	set := make(map[string]bool, len(at))
	for _, t := range at {
		set[t] = true
	}
	union := make(map[string]bool, len(at)+len(bt))
	for t := range set {
		union[t] = true
	}
	inter := 0
	for _, t := range bt {
		if set[t] {
			inter++
			delete(set, t)
		}
		union[t] = true
	}
	return float64(inter) / float64(len(union))
}

// ─────────────────────────────────────────────────────────────────────────────
// Sentence chunking

// SentenceChunker emits whole sentences from a token stream so speech can start
// before generation finishes. This is the single largest win on
// time-to-first-audio.
type SentenceChunker struct {
	mu                sync.Mutex
	buf               strings.Builder
	minSentenceLength int
}

// NewSentenceChunker returns a chunker. minSentenceLength (default 4) is what
// stops "Mr." and "1." becoming sentences.
func NewSentenceChunker(minSentenceLength int) *SentenceChunker {
	if minSentenceLength <= 0 {
		minSentenceLength = 4
	}
	return &SentenceChunker{minSentenceLength: minSentenceLength}
}

// PushToken adds a token and returns any complete sentences.
//
// Terminal punctuation includes the FULLWIDTH forms: a Japanese or Chinese
// reply ends in U+3002, and a chunker that only knows "." never emits anything
// until the flush.
func (c *SentenceChunker) PushToken(token string) []string {
	if token == "" {
		return nil
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.buf.WriteString(token)

	var ready []string
	for {
		chunk, kept, ok := c.extractNext(c.buf.String())
		if !ok {
			break
		}
		c.buf.Reset()
		c.buf.WriteString(kept)
		ready = append(ready, chunk)
	}
	return ready
}

// Flush returns whatever is buffered, punctuated or not. A reply that ends
// without a full stop must still be spoken.
func (c *SentenceChunker) Flush() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	s := c.buf.String()
	c.buf.Reset()
	return s
}

func (c *SentenceChunker) extractNext(buf string) (chunk, kept string, ok bool) {
	runes := []rune(buf)
	for i := 0; i < len(runes); i++ {
		if !isTerminal(runes[i]) {
			continue
		}
		end := i + 1
		// Consume trailing whitespace and closing quotes, so a sentence keeps
		// its own punctuation rather than handing it to the next one.
		for end < len(runes) && (runes[end] == '"' || runes[end] == '\'' || runes[end] == '”' || runes[end] == '’') {
			end++
		}
		candidate := strings.TrimSpace(string(runes[:end]))
		if len([]rune(candidate)) < c.minSentenceLength {
			continue
		}
		return candidate, strings.TrimLeft(string(runes[end:]), " \t\n"), true
	}
	return "", buf, false
}

func isTerminal(r rune) bool {
	for _, t := range terminalPunctuation {
		if string(r) == t {
			return true
		}
	}
	return false
}

// ─────────────────────────────────────────────────────────────────────────────
// Cost

// CallPricing is the rate card, all in micro-units of the billing currency.
type CallPricing struct {
	CarrierPerMinuteMicro           int64
	SttPerMinuteMicro               int64
	TtsPerThousandCharsMicro        int64
	LlmPerThousandInputTokensMicro  int64
	LlmPerThousandOutputTokensMicro int64
	Currency                        string
}

// CallCostBreakdown is where the money went.
type CallCostBreakdown struct {
	CarrierMicro int64
	SttMicro     int64
	TtsMicro     int64
	LlmMicro     int64
	TotalMicro   int64
}

// CallCostCalculator accumulates what a call has cost.
type CallCostCalculator struct {
	mu        sync.Mutex
	pricing   CallPricing
	carrierMs int64
	sttMs     int64
	ttsChars  int64
	inTokens  int64
	outTokens int64
}

// NewCallCostCalculator returns a calculator at zero.
func NewCallCostCalculator(pricing CallPricing) *CallCostCalculator {
	return &CallCostCalculator{pricing: pricing}
}

// AddCarrierTime adds carrier minutes.
func (c *CallCostCalculator) AddCarrierTime(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.carrierMs += d.Milliseconds()
}

// AddSttTime adds transcription time.
func (c *CallCostCalculator) AddSttTime(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.sttMs += d.Milliseconds()
}

// AddTtsCharacters adds synthesised characters.
func (c *CallCostCalculator) AddTtsCharacters(chars int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.ttsChars += int64(chars)
}

// AddLlmTokens adds generation tokens.
func (c *CallCostCalculator) AddLlmTokens(input, output int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.inTokens += int64(input)
	c.outTokens += int64(output)
}

// CurrentBreakdown returns the cost so far.
//
// The breakdown, not just a total: a call that is expensive because of TTS and
// one that is expensive because of carrier minutes need opposite fixes, and a
// single number cannot tell them apart.
func (c *CallCostCalculator) CurrentBreakdown() CallCostBreakdown {
	c.mu.Lock()
	defer c.mu.Unlock()
	b := CallCostBreakdown{
		CarrierMicro: c.pricing.CarrierPerMinuteMicro * c.carrierMs / 60000,
		SttMicro:     c.pricing.SttPerMinuteMicro * c.sttMs / 60000,
		TtsMicro:     c.pricing.TtsPerThousandCharsMicro * c.ttsChars / 1000,
	}
	b.LlmMicro = c.pricing.LlmPerThousandInputTokensMicro*c.inTokens/1000 +
		c.pricing.LlmPerThousandOutputTokensMicro*c.outTokens/1000
	b.TotalMicro = b.CarrierMicro + b.SttMicro + b.TtsMicro + b.LlmMicro
	return b
}

// Reset zeroes the accumulators, keeping the pricing.
func (c *CallCostCalculator) Reset() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.carrierMs, c.sttMs, c.ttsChars, c.inTokens, c.outTokens = 0, 0, 0, 0, 0
}

// ─────────────────────────────────────────────────────────────────────────────
// Latency

// LatencyStage is one step between the caller stopping and the agent starting.
//
// Named individually because the fix for each is different, and a single
// end-to-end number tells you only that it is too slow.
type LatencyStage int

const (
	LatencyEndpointing LatencyStage = iota
	LatencyTranscription
	LatencyInference
	LatencyToolCall
	LatencySynthesis
	LatencyPlayback
)

func (s LatencyStage) String() string {
	switch s {
	case LatencyEndpointing:
		return "endpointing"
	case LatencyTranscription:
		return "transcription"
	case LatencyInference:
		return "inference"
	case LatencyToolCall:
		return "tool-call"
	case LatencySynthesis:
		return "synthesis"
	case LatencyPlayback:
		return "playback"
	}
	return "unknown"
}

// LatencySnapshot is what a stage looked like over the samples taken.
type LatencySnapshot struct {
	Stage LatencyStage
	Count int
	P50Ms float64
	P95Ms float64
	MaxMs float64
}

// LatencyTracker records per-stage timings.
type LatencyTracker struct {
	mu      sync.Mutex
	samples map[LatencyStage][]float64
}

// NewLatencyTracker returns an empty tracker.
func NewLatencyTracker() *LatencyTracker {
	return &LatencyTracker{samples: map[LatencyStage][]float64{}}
}

// Record adds one measurement.
func (t *LatencyTracker) Record(stage LatencyStage, d time.Duration) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.samples[stage] = append(t.samples[stage], float64(d.Microseconds())/1000)
}

// Snapshot returns percentiles for a stage.
//
// Percentiles, not a mean. The mean turn latency of a call is close to useless —
// what a caller notices is the worst turn, and a p95 is the number that moves
// when a call feels bad.
func (t *LatencyTracker) Snapshot(stage LatencyStage) LatencySnapshot {
	t.mu.Lock()
	defer t.mu.Unlock()
	s := append([]float64(nil), t.samples[stage]...)
	if len(s) == 0 {
		return LatencySnapshot{Stage: stage}
	}
	sort.Float64s(s)
	return LatencySnapshot{
		Stage: stage,
		Count: len(s),
		P50Ms: percentileOf(s, 0.50),
		P95Ms: percentileOf(s, 0.95),
		MaxMs: s[len(s)-1],
	}
}

func percentileOf(sorted []float64, p float64) float64 {
	if len(sorted) == 0 {
		return 0
	}
	// Nearest-rank. Stated because linear interpolation and nearest-rank
	// disagree on small samples, and a p95 over eleven turns is a small sample.
	idx := int(math.Ceil(p*float64(len(sorted)))) - 1
	if idx < 0 {
		idx = 0
	}
	if idx >= len(sorted) {
		idx = len(sorted) - 1
	}
	return sorted[idx]
}

// ─────────────────────────────────────────────────────────────────────────────
// Guardrails

// GuardrailAction is what to do when a rule matches.
//
// Redact and Block are genuinely different: redaction lets the call continue
// with the number removed, blocking stops the sentence being spoken at all.
type GuardrailAction int

const (
	GuardrailAllow GuardrailAction = iota
	GuardrailRedact
	GuardrailBlock
	GuardrailEscalate
)

func (a GuardrailAction) String() string {
	switch a {
	case GuardrailRedact:
		return "redact"
	case GuardrailBlock:
		return "block"
	case GuardrailEscalate:
		return "escalate"
	}
	return "allow"
}

// GuardrailRule is one pattern and what to do about it.
type GuardrailRule struct {
	Name        string
	Pattern     *regexp.Regexp
	Action      GuardrailAction
	Replacement string
}

// GuardrailResult is what came out.
type GuardrailResult struct {
	Action        GuardrailAction
	Text          string
	TriggeredRule string
}

// Guardrails applies rules to a draft reply.
type Guardrails struct {
	rules []GuardrailRule
}

// NewGuardrails returns a set.
func NewGuardrails(rules ...GuardrailRule) *Guardrails {
	return &Guardrails{rules: rules}
}

// Apply runs the rules in order.
//
// Order matters and Block short-circuits: a redaction after a block would
// rewrite text that is never spoken, and a block after a redaction would test
// text that no longer contains what it looks for.
func (g *Guardrails) Apply(draft string) GuardrailResult {
	out := draft
	for _, r := range g.rules {
		if r.Pattern == nil || !r.Pattern.MatchString(out) {
			continue
		}
		switch r.Action {
		case GuardrailBlock:
			return GuardrailResult{Action: GuardrailBlock, Text: "", TriggeredRule: r.Name}
		case GuardrailEscalate:
			return GuardrailResult{Action: GuardrailEscalate, Text: out, TriggeredRule: r.Name}
		case GuardrailRedact:
			out = r.Pattern.ReplaceAllString(out, r.Replacement)
		}
	}
	if out != draft {
		return GuardrailResult{Action: GuardrailRedact, Text: out}
	}
	return GuardrailResult{Action: GuardrailAllow, Text: out}
}

// CommonGuardrails holds the rules worth having by default.
type CommonGuardrails struct{}

// CreditCardRedactor removes anything shaped like a card number.
func (CommonGuardrails) CreditCardRedactor() GuardrailRule {
	return GuardrailRule{
		Name:        "credit-card",
		Pattern:     regexp.MustCompile(`\b(?:\d[ -]*?){13,19}\b`),
		Action:      GuardrailRedact,
		Replacement: "[card number removed]",
	}
}

// SsnBlocker stops the reply entirely rather than redacting. A national
// identity number in a draft means the model has it, and the useful signal is
// that the sentence should not exist.
func (CommonGuardrails) SsnBlocker() GuardrailRule {
	return GuardrailRule{
		Name:    "national-id",
		Pattern: regexp.MustCompile(`\b\d{3}-?\d{2}-?\d{4}\b|\b\d{13}\b`),
		Action:  GuardrailBlock,
	}
}

// CompetitorMention escalates rather than blocks: whether naming a competitor
// is a problem is a business decision, and a guardrail that silently swallowed
// it would hide the conversation from the people who need to make it.
func (CommonGuardrails) CompetitorMention(competitors ...string) GuardrailRule {
	quoted := make([]string, 0, len(competitors))
	for _, c := range competitors {
		if strings.TrimSpace(c) != "" {
			quoted = append(quoted, regexp.QuoteMeta(c))
		}
	}
	if len(quoted) == 0 {
		return GuardrailRule{Name: "competitor", Action: GuardrailAllow}
	}
	return GuardrailRule{
		Name:    "competitor",
		Pattern: regexp.MustCompile(`(?i)\b(` + strings.Join(quoted, "|") + `)\b`),
		Action:  GuardrailEscalate,
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// Filling the silence

// ReassuranceVocabulary is what to say while something slow happens.
//
// A vocabulary rather than one string: hearing the identical filler three times
// in a call is worse than silence, because it is audibly a recording.
type ReassuranceVocabulary struct {
	Phrases  []string
	Language string
}

// ReassuranceFillerOptions tunes when a filler is used.
type ReassuranceFillerOptions struct {
	// Do not fill before this — most turns are fast enough that a filler would
	// arrive after the real answer.
	MinDelayBeforeFillerMs int
	MaxFillersPerTurn      int
	AvoidRepeatingLast     bool
}

// DefaultReassuranceFillerOptions returns the measured settings.
func DefaultReassuranceFillerOptions() ReassuranceFillerOptions {
	return ReassuranceFillerOptions{MinDelayBeforeFillerMs: 700, MaxFillersPerTurn: 2, AvoidRepeatingLast: true}
}

// IReassuranceFiller supplies something to say during a wait.
type IReassuranceFiller interface {
	// Next returns "" when it is too early to fill or the turn's budget is spent.
	Next(elapsedMs int) string
	TurnFinished()
}

// DefaultReassuranceFiller is the default filler.
type DefaultReassuranceFiller struct {
	mu           sync.Mutex
	vocab        ReassuranceVocabulary
	opts         ReassuranceFillerOptions
	usedThisTurn int
	lastIndex    int
	cursor       int
}

// NewDefaultReassuranceFiller returns a filler over a vocabulary.
func NewDefaultReassuranceFiller(vocab ReassuranceVocabulary, opts ReassuranceFillerOptions) *DefaultReassuranceFiller {
	if opts.MaxFillersPerTurn <= 0 {
		opts = DefaultReassuranceFillerOptions()
	}
	return &DefaultReassuranceFiller{vocab: vocab, opts: opts, lastIndex: -1}
}

// Next implements IReassuranceFiller.
func (f *DefaultReassuranceFiller) Next(elapsedMs int) string {
	f.mu.Lock()
	defer f.mu.Unlock()
	if elapsedMs < f.opts.MinDelayBeforeFillerMs || len(f.vocab.Phrases) == 0 {
		return ""
	}
	if f.usedThisTurn >= f.opts.MaxFillersPerTurn {
		return ""
	}
	idx := f.cursor % len(f.vocab.Phrases)
	if f.opts.AvoidRepeatingLast && idx == f.lastIndex && len(f.vocab.Phrases) > 1 {
		f.cursor++
		idx = f.cursor % len(f.vocab.Phrases)
	}
	f.cursor++
	f.usedThisTurn++
	f.lastIndex = idx
	return f.vocab.Phrases[idx]
}

// TurnFinished implements IReassuranceFiller.
func (f *DefaultReassuranceFiller) TurnFinished() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.usedThisTurn = 0
}

// FirstMessagePreambleOptions controls whether the agent speaks first.
type FirstMessagePreambleOptions struct {
	// On an INBOUND call the caller has already started talking; a preamble
	// there means talking over them.
	SpeakFirst     bool
	Text           string
	MaxLengthChars int
}

// IFirstMessagePreamble supplies the opening line.
type IFirstMessagePreamble interface {
	Text() string
	SpeakFirst() bool
}

// DefaultFirstMessagePreamble is the default preamble.
type DefaultFirstMessagePreamble struct {
	opts FirstMessagePreambleOptions
}

// NewDefaultFirstMessagePreamble returns a preamble.
func NewDefaultFirstMessagePreamble(opts FirstMessagePreambleOptions) *DefaultFirstMessagePreamble {
	return &DefaultFirstMessagePreamble{opts: opts}
}

// Text implements IFirstMessagePreamble, truncated on a word boundary rather
// than mid-word — a greeting cut mid-syllable sounds like a fault.
func (p *DefaultFirstMessagePreamble) Text() string {
	t := p.opts.Text
	if p.opts.MaxLengthChars > 0 && len([]rune(t)) > p.opts.MaxLengthChars {
		r := []rune(t)[:p.opts.MaxLengthChars]
		if i := strings.LastIndex(string(r), " "); i > 0 {
			return string(r)[:i]
		}
		return string(r)
	}
	return t
}

// SpeakFirst implements IFirstMessagePreamble.
func (p *DefaultFirstMessagePreamble) SpeakFirst() bool { return p.opts.SpeakFirst }

// HoldMusicMixer mixes a loop under speech.
//
// Mixed rather than switched: cutting the assistant out and the music in leaves
// a gap that sounds like a dropped call.
type HoldMusicMixer struct {
	loop         []byte
	sampleRateHz int
	pos          int
	mu           sync.Mutex
}

// NewHoldMusicMixer returns a mixer over a PCM-16 loop.
func NewHoldMusicMixer(loopPCM []byte, sampleRateHz int) *HoldMusicMixer {
	return &HoldMusicMixer{loop: loopPCM, sampleRateHz: sampleRateHz}
}

// Mix blends the loop under speech at musicGain and returns the result.
func (m *HoldMusicMixer) Mix(speech []byte, musicGain float64) []byte {
	if len(m.loop) < 2 {
		return speech
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]byte, len(speech))
	for i := 0; i+1 < len(speech); i += 2 {
		s := int32(int16(binary.LittleEndian.Uint16(speech[i:])))
		l := int32(int16(binary.LittleEndian.Uint16(m.loop[m.pos:])))
		v := s + int32(float64(l)*musicGain)
		if v > math.MaxInt16 {
			v = math.MaxInt16
		} else if v < math.MinInt16 {
			v = math.MinInt16
		}
		binary.LittleEndian.PutUint16(out[i:], uint16(int16(v)))
		m.pos += 2
		if m.pos+1 >= len(m.loop) {
			m.pos = 0
		}
	}
	return out
}

// ─────────────────────────────────────────────────────────────────────────────
// Prompt variables

// PromptVariableResolver substitutes {{name}} placeholders.
type PromptVariableResolver struct {
	mu   sync.Mutex
	vars map[string]string
}

var promptVarPattern = regexp.MustCompile(`\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}`)

// NewPromptVariableResolver returns an empty resolver.
func NewPromptVariableResolver() *PromptVariableResolver {
	return &PromptVariableResolver{vars: map[string]string{}}
}

// Set defines a variable.
func (r *PromptVariableResolver) Set(name, value string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.vars[name] = value
}

// Resolve substitutes what it knows.
//
// An UNKNOWN variable is left as-is rather than blanked: a prompt with a visible
// {{customer_name}} in it is a bug somebody notices, and one with a silent gap
// is a call where the assistant addresses nobody.
func (r *PromptVariableResolver) Resolve(template string) string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return promptVarPattern.ReplaceAllStringFunc(template, func(m string) string {
		name := promptVarPattern.FindStringSubmatch(m)[1]
		if v, ok := r.vars[name]; ok {
			return v
		}
		return m
	})
}
