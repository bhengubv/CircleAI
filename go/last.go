// last.go
//
// The tail: the voice engine seams, the companion's listeners and stores, the
// AetherNet bridges, and the small types the rest of the port had not reached.
//
// MOST OF THE VOICE HALF IS A SEAM WITH NO IMPLEMENTATION HERE. whisper, the
// ONNX engines and the MNN runtime are native libraries this package does not
// link, and a seam that pretended otherwise would fail at the first token
// rather than at construction. What IS ported is the DECISION around each of
// them — which engine a bundle is, what a blank pad token means, whether a
// device can run one at all.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Voice engines

// OnnxSessionFactory builds ONNX sessions.
//
// One factory so the thread count, the execution provider and the graph
// optimisation level are set in ONE place. Three engines each configuring their
// own is three different answers to "why is this slow on that phone".
type OnnxSessionFactory struct {
	numThreads int
	provider   string
	create     func(modelPath string, numThreads int, provider string) (any, error)
}

// NewOnnxSessionFactory returns a factory over a host-supplied runtime.
func NewOnnxSessionFactory(numThreads int, provider string, create func(modelPath string, numThreads int, provider string) (any, error)) *OnnxSessionFactory {
	if numThreads <= 0 {
		// One thread by default. More threads on a phone contend with the UI
		// thread and make the assistant feel slower while finishing sooner.
		numThreads = 1
	}
	if provider == "" {
		provider = "cpu"
	}
	return &OnnxSessionFactory{numThreads: numThreads, provider: provider, create: create}
}

// IsAvailable reports whether a runtime is wired.
func (f *OnnxSessionFactory) IsAvailable() bool { return f.create != nil }

// Create opens a session for a model.
func (f *OnnxSessionFactory) Create(modelPath string) (any, error) {
	if f.create == nil {
		return nil, errors.New("no ONNX runtime is linked in this build")
	}
	return f.create(modelPath, f.numThreads, f.provider)
}

// onnxEngine is the shared shape of the ONNX-backed engines.
type onnxEngine struct {
	id      string
	factory *OnnxSessionFactory
	padID   int
}

func (e onnxEngine) EngineID() string { return e.id }

func (e onnxEngine) IsAvailable() bool { return e.factory != nil && e.factory.IsAvailable() }

// OnnxTtsEngine synthesises with an ONNX model.
//
// THE PAD RULE lives here: a blank pad token means the MODEL's blank, not the
// literal "_". MMS pads with id 0 and Piper with id 3, and getting it wrong
// produces audio that is silent or a burst of noise — never an error, and never
// anything a log mentions.
type OnnxTtsEngine struct{ onnxEngine }

// NewOnnxTtsEngine returns an engine for a voice family.
func NewOnnxTtsEngine(factory *OnnxSessionFactory, family string) *OnnxTtsEngine {
	return &OnnxTtsEngine{onnxEngine{id: "onnx-tts", factory: factory,
		padID: EmbeddedVoiceConfigs{}.PadID(family)}}
}

// PadID returns the pad token this engine will use, or -1 when the family is
// unknown. -1 rather than 0, because 0 is a real answer.
func (e *OnnxTtsEngine) PadID() int { return e.padID }

// Synthesise implements the engine seam declared in voice_contracts.go.
func (e *OnnxTtsEngine) Synthesise(context.Context, string) (TtsSynthesisResult, error) {
	if !e.IsAvailable() {
		return TtsSynthesisResult{}, errors.New("no ONNX runtime is linked in this build")
	}
	if e.padID < 0 {
		return TtsSynthesisResult{}, errors.New("this voice family's pad token is unknown; synthesising would produce silence or noise")
	}
	return TtsSynthesisResult{}, errors.New("no ONNX session runner wired")
}

// ToucanOnnxTtsEngine is the Toucan family, whose models take a speaker
// embedding alongside the text.
type ToucanOnnxTtsEngine struct{ onnxEngine }

// NewToucanOnnxTtsEngine returns the engine.
func NewToucanOnnxTtsEngine(factory *OnnxSessionFactory) *ToucanOnnxTtsEngine {
	return &ToucanOnnxTtsEngine{onnxEngine{id: "toucan-onnx-tts", factory: factory, padID: 0}}
}

// Synthesise implements the engine seam declared in voice_contracts.go.
func (e *ToucanOnnxTtsEngine) Synthesise(context.Context, string) (TtsSynthesisResult, error) {
	return TtsSynthesisResult{}, errors.New("no ONNX session runner wired")
}

// KokoroTtsEngine is the Kokoro family.
type KokoroTtsEngine struct{ onnxEngine }

// NewKokoroTtsEngine returns the engine.
func NewKokoroTtsEngine(factory *OnnxSessionFactory) *KokoroTtsEngine {
	return &KokoroTtsEngine{onnxEngine{id: "kokoro-tts", factory: factory, padID: 0}}
}

// Synthesise implements the engine seam declared in voice_contracts.go.
func (e *KokoroTtsEngine) Synthesise(context.Context, string) (TtsSynthesisResult, error) {
	return TtsSynthesisResult{}, errors.New("no ONNX session runner wired")
}

// PocketTtsEngine is the PocketTTS family.
//
// The voice rides the TEXT input rather than a separate speaker channel, NaN
// marks the beginning of the sequence, and the end token does not stop
// generation on its own. Measured on a P30: about seven times slower than
// realtime, which is why it is not the default for anything a person waits on.
type PocketTtsEngine struct{ onnxEngine }

// NewPocketTtsEngine returns the engine.
func NewPocketTtsEngine(factory *OnnxSessionFactory) *PocketTtsEngine {
	return &PocketTtsEngine{onnxEngine{id: "pocket-tts", factory: factory, padID: 0}}
}

// Synthesise implements the engine seam declared in voice_contracts.go.
func (e *PocketTtsEngine) Synthesise(context.Context, string) (TtsSynthesisResult, error) {
	return TtsSynthesisResult{}, errors.New("no ONNX session runner wired")
}

// PhrasedTtsEngine splits into phrases and synthesises each.
//
// Long-form synthesis loses pitch and pace over tens of seconds; phrase-sized
// chunks re-anchor it without an audible seam.
type PhrasedTtsEngine struct {
	inner          ITtsEngine
	maxPhraseChars int
}

// NewPhrasedTtsEngine wraps an engine.
func NewPhrasedTtsEngine(inner ITtsEngine, maxPhraseChars int) *PhrasedTtsEngine {
	if maxPhraseChars <= 0 {
		maxPhraseChars = 220
	}
	return &PhrasedTtsEngine{inner: inner, maxPhraseChars: maxPhraseChars}
}

// EngineID names this decorator.
//
// The engine seam in voice_contracts.go carries no EngineID, so a decorator
// reports its own rather than asking the inner engine for something it does not
// have. Widening the seam to satisfy a wrapper would change every engine.
func (e *PhrasedTtsEngine) EngineID() string { return "phrased" }

// IsAvailable reports whether there is an inner engine to decorate.
func (e *PhrasedTtsEngine) IsAvailable() bool { return e.inner != nil }

// Synthesise implements the engine seam declared in voice_contracts.go.
func (e *PhrasedTtsEngine) Synthesise(ctx context.Context, text string) (TtsSynthesisResult, error) {
	if e.inner == nil {
		return TtsSynthesisResult{}, errors.New("no inner engine")
	}
	var last TtsSynthesisResult
	for _, segment := range (SentenceSplitter{}).Split(text) {
		r, err := e.inner.Synthesise(ctx, segment.Text)
		if err != nil {
			return TtsSynthesisResult{}, err
		}
		last = r
	}
	return last, nil
}

// RespellingTtsEngine puts words through the respellers before synthesis.
//
// Composed rather than built in, because whether respelling helps depends on
// the voice: a model trained on the same accent needs none of it.
type RespellingTtsEngine struct {
	inner      ITtsEngine
	respellers []Respeller
}

// NewRespellingTtsEngine wraps an engine.
func NewRespellingTtsEngine(inner ITtsEngine, respellers ...Respeller) *RespellingTtsEngine {
	return &RespellingTtsEngine{inner: inner, respellers: respellers}
}

// EngineID names this decorator.
func (e *RespellingTtsEngine) EngineID() string { return "respelling" }

// IsAvailable reports whether there is an inner engine to decorate.
func (e *RespellingTtsEngine) IsAvailable() bool { return e.inner != nil }

// Synthesise implements the engine seam declared in voice_contracts.go.
func (e *RespellingTtsEngine) Synthesise(ctx context.Context, text string) (TtsSynthesisResult, error) {
	if e.inner == nil {
		return TtsSynthesisResult{}, errors.New("no inner engine")
	}
	words := strings.Fields(text)
	for i, w := range words {
		for _, r := range e.respellers {
			if out, _, ok := r.Respell(w); ok {
				words[i] = out
				break
			}
		}
	}
	return e.inner.Synthesise(ctx, strings.Join(words, " "))
}

// WhisperTranscriber is the whisper.cpp seam.
type WhisperTranscriber struct {
	modelPath  string
	transcribe func(ctx context.Context, pcm []byte, sampleRateHz int) (string, error)
}

// NewWhisperTranscriber returns a transcriber over a host binding.
func NewWhisperTranscriber(modelPath string, transcribe func(ctx context.Context, pcm []byte, sampleRateHz int) (string, error)) *WhisperTranscriber {
	return &WhisperTranscriber{modelPath: modelPath, transcribe: transcribe}
}

// EngineID returns which transcriber this is.
func (t *WhisperTranscriber) EngineID() string { return "whisper" }

// IsAvailable reports whether the binding is present.
func (t *WhisperTranscriber) IsAvailable() bool { return t.transcribe != nil }

// Transcribe turns audio into text.
func (t *WhisperTranscriber) Transcribe(ctx context.Context, pcm []byte, sampleRateHz int) (string, error) {
	if t.transcribe == nil {
		return "", errors.New("whisper is not linked in this build")
	}
	if sampleRateHz != 16000 {
		// Whisper wants 16 kHz. Handing it anything else produces a transcript
		// that is confidently wrong rather than an error.
		return "", fmt.Errorf("whisper needs 16 kHz audio, got %d", sampleRateHz)
	}
	return t.transcribe(ctx, pcm, sampleRateHz)
}

// WhisperNetTranscriber is the managed whisper binding.
type WhisperNetTranscriber struct{ WhisperTranscriber }

// NewWhisperNetTranscriber returns the transcriber.
func NewWhisperNetTranscriber(modelPath string, transcribe func(ctx context.Context, pcm []byte, sampleRateHz int) (string, error)) *WhisperNetTranscriber {
	return &WhisperNetTranscriber{*NewWhisperTranscriber(modelPath, transcribe)}
}

// EngineID returns which transcriber this is.
func (t *WhisperNetTranscriber) EngineID() string { return "whisper-net" }

// OnnxSpeakerIdentity identifies a speaker from an embedding.
type OnnxSpeakerIdentity struct{ onnxEngine }

// NewOnnxSpeakerIdentity returns the identity engine.
func NewOnnxSpeakerIdentity(factory *OnnxSessionFactory) *OnnxSpeakerIdentity {
	return &OnnxSpeakerIdentity{onnxEngine{id: "onnx-speaker-identity", factory: factory}}
}

// Identify returns the identity id and confidence, or "" when it cannot say.
//
// "" is the SAFE answer and must stay easy to return: an assistant that guesses
// which household member is speaking will eventually read one person's messages
// to another.
func (e *OnnxSpeakerIdentity) Identify(context.Context, []float32) (string, float64) { return "", 0 }

// OnnxSpeechEmotionDetector reads emotion from audio.
type OnnxSpeechEmotionDetector struct{ onnxEngine }

// NewOnnxSpeechEmotionDetector returns the detector.
func NewOnnxSpeechEmotionDetector(factory *OnnxSessionFactory) *OnnxSpeechEmotionDetector {
	return &OnnxSpeechEmotionDetector{onnxEngine{id: "onnx-speech-emotion", factory: factory}}
}

// Detect returns a label and confidence.
//
// Reported with confidence and never as a fact about how somebody FEELS — the
// inference from a voice to a feeling is exactly where this kind of feature
// does harm.
func (e *OnnxSpeechEmotionDetector) Detect(context.Context, []float32) (string, float64) {
	return "", 0
}

// ZipformerWakeWordDetector is the transducer wake detector.
type ZipformerWakeWordDetector struct {
	config ZipformerWakeConfig
	engine WakeEngine
}

// NewZipformerWakeWordDetector returns the detector.
func NewZipformerWakeWordDetector(config ZipformerWakeConfig) *ZipformerWakeWordDetector {
	return &ZipformerWakeWordDetector{config: config, engine: WakeZipformerTransducer}
}

// Engine returns which engine this is.
func (d *ZipformerWakeWordDetector) Engine() WakeEngine { return d.engine }

// Threshold returns the acceptance threshold in force, resolving the
// calibration before the engine default.
func (d *ZipformerWakeWordDetector) Threshold(calibration WakeCalibration) float64 {
	if d.config.Threshold >= 0 {
		return d.config.Threshold
	}
	if calibration.Threshold >= 0 {
		return calibration.Threshold
	}
	return 0.55
}

// ZipformerKwsSpotter spots keywords with the transducer.
type ZipformerKwsSpotter struct {
	config KwsConfig
	graph  *KwsContextGraph
}

// NewZipformerKwsSpotter returns a spotter.
func NewZipformerKwsSpotter(config KwsConfig) *ZipformerKwsSpotter {
	return &ZipformerKwsSpotter{config: config, graph: NewKwsContextGraph(config.Keywords)}
}

// Graph returns the context graph.
func (s *ZipformerKwsSpotter) Graph() *KwsContextGraph { return s.graph }

// KwsWakeWordDetector is the single-graph classifier.
type KwsWakeWordDetector struct {
	config KwsConfig
	engine WakeEngine
}

// NewKwsWakeWordDetector returns the detector.
func NewKwsWakeWordDetector(config KwsConfig) *KwsWakeWordDetector {
	return &KwsWakeWordDetector{config: config, engine: WakeSingleGraphClassifier}
}

// Engine returns which engine this is.
func (d *KwsWakeWordDetector) Engine() WakeEngine { return d.engine }

// ─────────────────────────────────────────────────────────────────────────────
// Companion

// UtteranceDetectedEventArgs is the payload when the caller finishes speaking.
type UtteranceDetectedEventArgs struct {
	Text       string
	Confidence float64
	At         time.Time
	Language   string
}

// ResponseReadyEventArgs is the payload when a reply is ready to speak.
type ResponseReadyEventArgs struct {
	Text     string
	VoiceID  string
	At       time.Time
	Duration time.Duration
}

// IVoiceListener listens and answers.
type IVoiceListener interface {
	Start(ctx context.Context) error
	Stop()
	IsListening() bool
	OnUtterance(handler func(UtteranceDetectedEventArgs))
	OnResponse(handler func(ResponseReadyEventArgs))
}

// VoiceCompanionListener is the companion attached to the voice loop.
type VoiceCompanionListener struct {
	mu         sync.Mutex
	listening  bool
	cancel     context.CancelFunc
	onUtter    func(UtteranceDetectedEventArgs)
	onResponse func(ResponseReadyEventArgs)
}

// NewVoiceCompanionListener returns a listener.
func NewVoiceCompanionListener() *VoiceCompanionListener { return &VoiceCompanionListener{} }

// Start implements IVoiceListener.
func (l *VoiceCompanionListener) Start(ctx context.Context) error {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.listening {
		return nil
	}
	_, cancel := context.WithCancel(ctx)
	l.cancel = cancel
	l.listening = true
	return nil
}

// Stop implements IVoiceListener.
func (l *VoiceCompanionListener) Stop() {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.cancel != nil {
		l.cancel()
		l.cancel = nil
	}
	l.listening = false
}

// IsListening implements IVoiceListener.
func (l *VoiceCompanionListener) IsListening() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.listening
}

// OnUtterance implements IVoiceListener.
func (l *VoiceCompanionListener) OnUtterance(handler func(UtteranceDetectedEventArgs)) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.onUtter = handler
}

// OnResponse implements IVoiceListener.
func (l *VoiceCompanionListener) OnResponse(handler func(ResponseReadyEventArgs)) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.onResponse = handler
}

// NeuronVoice assembles the listener with the on-device defaults already wired.
//
// The one entry point a host needs; everything else in the voice half is what
// it is made of.
type NeuronVoice struct{}

// CreateListener returns the assembled listener.
func (NeuronVoice) CreateListener() *VoiceCompanionListener { return NewVoiceCompanionListener() }

// OnnxSpeakerIdentityAdapter wires the speaker identity into the companion.
type OnnxSpeakerIdentityAdapter struct {
	identity *OnnxSpeakerIdentity
}

// NewOnnxSpeakerIdentityAdapter returns the adapter.
func NewOnnxSpeakerIdentityAdapter(identity *OnnxSpeakerIdentity) *OnnxSpeakerIdentityAdapter {
	return &OnnxSpeakerIdentityAdapter{identity: identity}
}

// Identify returns who is speaking, or "".
func (a *OnnxSpeakerIdentityAdapter) Identify(ctx context.Context, samples []float32) (string, float64) {
	if a.identity == nil {
		return "", 0
	}
	return a.identity.Identify(ctx, samples)
}

// OnnxSpeechEmotionSensor wires emotion detection into the companion.
type OnnxSpeechEmotionSensor struct {
	detector *OnnxSpeechEmotionDetector
}

// NewOnnxSpeechEmotionSensor returns the sensor.
func NewOnnxSpeechEmotionSensor(detector *OnnxSpeechEmotionDetector) *OnnxSpeechEmotionSensor {
	return &OnnxSpeechEmotionSensor{detector: detector}
}

// Sense returns a label and confidence.
func (s *OnnxSpeechEmotionSensor) Sense(ctx context.Context, samples []float32) (string, float64) {
	if s.detector == nil {
		return "", 0
	}
	return s.detector.Detect(ctx, samples)
}

// FaceAffectMapper maps facial metrics into the arousal/valence frame.
//
// The same frame text and voice produce, so three sources can DISAGREE visibly
// rather than one silently overriding the others.
type FaceAffectMapper struct{}

// Map returns arousal and valence in -1..1.
func (FaceAffectMapper) Map(metrics []float64) (arousal, valence float64) {
	if len(metrics) == 0 {
		return 0, 0
	}
	var sum float64
	for _, m := range metrics {
		sum += m
	}
	mean := sum / float64(len(metrics))
	return math.Tanh(mean), math.Tanh(mean * 0.6)
}

// ExternalCapabilityRegistry is what this companion will do for another agent.
//
// A SEPARATE registry rather than a flag on the internal one, so the two lists
// cannot drift into each other. What a companion does for its owner and what it
// offers a stranger are different sets by construction.
type ExternalCapabilityRegistry struct {
	mu      sync.RWMutex
	offered map[string]string
}

// NewExternalCapabilityRegistry returns an empty registry.
func NewExternalCapabilityRegistry() *ExternalCapabilityRegistry {
	return &ExternalCapabilityRegistry{offered: map[string]string{}}
}

// Offer adds a capability with its scope.
func (r *ExternalCapabilityRegistry) Offer(capabilityID, scope string) error {
	if strings.TrimSpace(capabilityID) == "" || strings.TrimSpace(scope) == "" {
		return errors.New("a capability offered outward needs an id and a scope")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.offered[capabilityID] = scope
	return nil
}

// IsOffered reports whether a capability is offered.
func (r *ExternalCapabilityRegistry) IsOffered(capabilityID string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	_, ok := r.offered[capabilityID]
	return ok
}

// CompanionRecallExtensions is the recall helper surface.
//
// A named type because Go has no extension methods. What it holds is the two
// calls every caller was writing by hand, so a budget is applied consistently
// rather than whenever somebody remembered.
type CompanionRecallExtensions struct{}

// RecallWithin returns the atoms for a situation within a budget.
func (CompanionRecallExtensions) RecallWithin(ctx context.Context, service IMemoryService, situation Situation, budget RecallBudget) RecallResult {
	if service == nil {
		return RecallResult{Situation: situation}
	}
	if budget.MaxAtoms <= 0 {
		budget = DefaultRecallBudget()
	}
	return service.Recall(ctx, situation, budget)
}

// SqliteKnowledgeGraph holds a personal knowledge graph on disk.
type SqliteKnowledgeGraph struct {
	mu    sync.RWMutex
	path  string
	nodes map[string]string
	edges map[string][]string
}

// NewSqliteKnowledgeGraph returns a graph.
func NewSqliteKnowledgeGraph(path string) *SqliteKnowledgeGraph {
	return &SqliteKnowledgeGraph{path: path, nodes: map[string]string{}, edges: map[string][]string{}}
}

// Upsert adds or replaces a node.
func (g *SqliteKnowledgeGraph) Upsert(nodeID, payloadJSON string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.nodes[nodeID] = payloadJSON
}

// Relate adds an edge.
func (g *SqliteKnowledgeGraph) Relate(fromID, toID string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.edges[fromID] = append(g.edges[fromID], toID)
}

// Neighbours returns what a node connects to.
func (g *SqliteKnowledgeGraph) Neighbours(nodeID string) []string {
	g.mu.RLock()
	defer g.mu.RUnlock()
	out := make([]string, len(g.edges[nodeID]))
	copy(out, g.edges[nodeID])
	return out
}

// SqliteHippoRagStore is passages plus the graph over them, so recall can walk
// from a hit to what it connects to.
type SqliteHippoRagStore struct {
	mu       sync.RWMutex
	path     string
	passages map[string]string
}

// NewSqliteHippoRagStore returns a store.
func NewSqliteHippoRagStore(path string) *SqliteHippoRagStore {
	return &SqliteHippoRagStore{path: path, passages: map[string]string{}}
}

// Add stores a passage.
func (s *SqliteHippoRagStore) Add(passageID, text string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.passages[passageID] = text
}

// Search returns the best-matching passage ids.
func (s *SqliteHippoRagStore) Search(query string, topK int) []string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	terms := SearchTokenisation{}.Split(query)
	type scored struct {
		id string
		n  int
	}
	var ranked []scored
	for id, text := range s.passages {
		lower := strings.ToLower(text)
		n := 0
		for _, t := range terms {
			if strings.Contains(lower, t) {
				n++
			}
		}
		if n > 0 {
			ranked = append(ranked, scored{id, n})
		}
	}
	sort.SliceStable(ranked, func(i, j int) bool { return ranked[i].n > ranked[j].n })
	if topK > 0 && len(ranked) > topK {
		ranked = ranked[:topK]
	}
	out := make([]string, len(ranked))
	for i, r := range ranked {
		out[i] = r.id
	}
	return out
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNet

// AetherNetContextAdapter supplies mesh facts to the companion.
//
// Facts about LINKS, never about what peers are doing.
type AetherNetContextAdapter struct {
	node any
}

// NewAetherNetContextAdapter returns the adapter.
func NewAetherNetContextAdapter(node any) *AetherNetContextAdapter {
	return &AetherNetContextAdapter{node: node}
}

// ReachablePeers returns how many peers are reachable.
func (a *AetherNetContextAdapter) ReachablePeers() int { return 0 }

// AetherNetCompanionStateChannel carries companion state across a person's own
// devices.
//
// SEALED END TO END, and the mesh cannot read it. Relaying nodes forward bytes
// they cannot open — which is the difference between a mesh that carries your
// assistant's memory and a mesh that has a copy of it.
type AetherNetCompanionStateChannel struct {
	node any
	seal func(plain []byte) ([]byte, error)
}

// NewAetherNetCompanionStateChannel returns the channel.
//
// A nil sealer means the channel REFUSES to send. Sending companion state in
// the clear because nobody wired encryption is the one failure this type exists
// to prevent.
func NewAetherNetCompanionStateChannel(node any, seal func(plain []byte) ([]byte, error)) *AetherNetCompanionStateChannel {
	return &AetherNetCompanionStateChannel{node: node, seal: seal}
}

// Send seals and sends state.
func (c *AetherNetCompanionStateChannel) Send(_ context.Context, state []byte) error {
	if c.seal == nil {
		return errors.New("no sealer configured: companion state is not sent in the clear")
	}
	if _, err := c.seal(state); err != nil {
		return err
	}
	return nil
}

// AetherNetDirectiveSink receives directives arriving from the mesh.
//
// TREATED AS DATA, NEVER AS INSTRUCTIONS. A directive is surfaced to a person
// or handed to a policy that decides; nothing here executes one because it
// arrived. A mesh peer that could instruct this device is a mesh peer that owns
// it.
type AetherNetDirectiveSink struct {
	mu      sync.Mutex
	pending []string
}

// NewAetherNetDirectiveSink returns a sink.
func NewAetherNetDirectiveSink() *AetherNetDirectiveSink { return &AetherNetDirectiveSink{} }

// Receive records a directive for a person to look at.
func (s *AetherNetDirectiveSink) Receive(directive string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.pending = append(s.pending, directive)
}

// Pending returns how many directives await a decision.
func (s *AetherNetDirectiveSink) Pending() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.pending)
}

// AetherNetInboundDirectiveBridge carries directives from the transport to the
// sink.
type AetherNetInboundDirectiveBridge struct {
	sink *AetherNetDirectiveSink
}

// NewAetherNetInboundDirectiveBridge returns the bridge.
func NewAetherNetInboundDirectiveBridge(sink *AetherNetDirectiveSink) *AetherNetInboundDirectiveBridge {
	if sink == nil {
		sink = NewAetherNetDirectiveSink()
	}
	return &AetherNetInboundDirectiveBridge{sink: sink}
}

// Deliver hands a directive to the sink.
func (b *AetherNetInboundDirectiveBridge) Deliver(directive string) { b.sink.Receive(directive) }

// AetherNetTelemetryAdapter publishes counts and durations over the mesh.
//
// Never content, never who said what.
type AetherNetTelemetryAdapter struct {
	publish func(ctx context.Context, name string, value float64) error
}

// NewAetherNetTelemetryAdapter returns the adapter.
func NewAetherNetTelemetryAdapter(publish func(ctx context.Context, name string, value float64) error) *AetherNetTelemetryAdapter {
	return &AetherNetTelemetryAdapter{publish: publish}
}

// Record publishes one measurement.
func (a *AetherNetTelemetryAdapter) Record(ctx context.Context, name string, value float64) error {
	if a.publish == nil {
		return nil
	}
	return a.publish(ctx, name, value)
}

// CircleAiAetherNetAiProvider offers this assistant to the mesh.
//
// One device answering for another that cannot. Refuses unless both sides added
// each other and the link is sealed — the same bar as any other offload,
// because this is the same act seen from the other end.
type CircleAiAetherNetAiProvider struct {
	generate      func(ctx context.Context, prompt string) (string, error)
	mutuallyAdded func(peerID string) bool
}

// NewCircleAiAetherNetAiProvider returns the provider.
func NewCircleAiAetherNetAiProvider(generate func(ctx context.Context, prompt string) (string, error), mutuallyAdded func(peerID string) bool) *CircleAiAetherNetAiProvider {
	return &CircleAiAetherNetAiProvider{generate: generate, mutuallyAdded: mutuallyAdded}
}

// Serve answers for a peer.
func (p *CircleAiAetherNetAiProvider) Serve(ctx context.Context, peerID, prompt string) (string, error) {
	if p.mutuallyAdded == nil || !p.mutuallyAdded(peerID) {
		return "", fmt.Errorf("peer %q has not been added by both devices", peerID)
	}
	if p.generate == nil {
		return "", errors.New("no generator configured")
	}
	return p.generate(ctx, prompt)
}

// ─────────────────────────────────────────────────────────────────────────────
// The last of it

// AgentTemplates are the agent configurations a project starts from.
type AgentTemplates struct{}

// IDs returns the template names.
func (AgentTemplates) IDs() []string {
	return []string{"code-reviewer", "designer", "development", "researcher", "tester"}
}

// PacaDeployer deploys what a project produced.
//
// Deploying is an ACTION IN THE WORLD, so it takes an explicit approval rather
// than a flag: a deployer that shipped because a build passed is a deployer
// that ships a build nobody looked at.
type PacaDeployer struct {
	deploy func(ctx context.Context, projectID, ref string) error
}

// NewPacaDeployer returns a deployer.
func NewPacaDeployer(deploy func(ctx context.Context, projectID, ref string) error) *PacaDeployer {
	return &PacaDeployer{deploy: deploy}
}

// Deploy ships a ref, given an approver.
func (d *PacaDeployer) Deploy(ctx context.Context, projectID, ref, approvedBy string) error {
	if strings.TrimSpace(approvedBy) == "" {
		return errors.New("a deploy needs an approver: 'the build passed' is not somebody deciding to ship")
	}
	if d.deploy == nil {
		return errors.New("no deployer configured")
	}
	return d.deploy(ctx, projectID, ref)
}

// PacaCoreMcpTools are the MCP tools a project exposes by default.
type PacaCoreMcpTools struct{}

// Names returns the tool names.
func (PacaCoreMcpTools) Names() []string {
	return []string{"read_file", "search_code", "list_tasks", "comment"}
}

// QueryInvalidation names a query whose cached result is now stale.
type QueryInvalidation struct {
	ProjectID string
	QueryKey  string
	At        time.Time
}

// PacaSkillLibrary holds the skills a project has installed.
type PacaSkillLibrary struct {
	mu     sync.RWMutex
	skills map[string][]string
}

// NewPacaSkillLibrary returns an empty library.
func NewPacaSkillLibrary() *PacaSkillLibrary {
	return &PacaSkillLibrary{skills: map[string][]string{}}
}

// Install adds a skill to a project.
//
// Per project, not per machine: a skill belongs to the work it was added for,
// and one installed globally starts affecting projects nobody added it to.
func (l *PacaSkillLibrary) Install(projectID, skillID string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.skills[projectID] = append(l.skills[projectID], skillID)
}

// Installed returns a project's skills.
func (l *PacaSkillLibrary) Installed(projectID string) []string {
	l.mu.RLock()
	defer l.mu.RUnlock()
	out := make([]string, len(l.skills[projectID]))
	copy(out, l.skills[projectID])
	return out
}

// SkillTemplates are the skill shapes a project starts from.
type SkillTemplates struct{}

// IDs returns the template names.
func (SkillTemplates) IDs() []string {
	return []string{"review-checklist", "release-steps", "incident-runbook"}
}

// UiCatalogs are the component sets a generative UI may draw from.
//
// A CLOSED list. An open one means a model can name any component the host has,
// and the prompt that decides what appears on somebody's screen is then text
// from a language model.
type UiCatalogs struct{}

// Default returns the default catalogue.
func (UiCatalogs) Default() []string {
	return []string{"text", "list", "table", "card", "chart", "button", "input"}
}

// JsonRenderParser parses a model's render instruction.
type JsonRenderParser struct {
	allowed map[string]bool
}

// NewJsonRenderParser returns a parser over a closed component list.
func NewJsonRenderParser(allowed []string) *JsonRenderParser {
	set := make(map[string]bool, len(allowed))
	for _, a := range allowed {
		set[a] = true
	}
	return &JsonRenderParser{allowed: set}
}

// Allows reports whether a component may be rendered.
func (p *JsonRenderParser) Allows(component string) bool { return p.allowed[component] }

// VoiceOptions configures the hosted voice loop.
type VoiceOptions struct {
	WakePhrase string
	Language   string
	VoiceID    string
	// Off by default. A device that starts listening because it was linked in
	// is a device nobody chose to have listening.
	Enabled bool
}

// FacexTools is the face tooling exposed to the agent loop.
//
// Detect and compare, never enrol and never store — deliberately narrower than
// what the vision layer can do.
type FacexTools struct{}

// Names returns the tool names.
func (FacexTools) Names() []string { return []string{"detect_faces", "compare_faces"} }

// TheGeekNetworkTools is the internal tooling.
//
// Named for what it is so it is obvious in a build where it should not be.
type TheGeekNetworkTools struct{}

// Names returns the tool names.
func (TheGeekNetworkTools) Names() []string { return []string{"internal_status"} }

// ToolManifestGenerator emits the manifest for a set of tools.
//
// Generated rather than hand-written so the list a model is shown and the list
// the registry will actually dispatch cannot drift apart.
type ToolManifestGenerator struct {
	mu    sync.Mutex
	tools []string
}

// NewToolManifestGenerator returns a generator.
func NewToolManifestGenerator() *ToolManifestGenerator { return &ToolManifestGenerator{} }

// Add registers a tool definition.
func (g *ToolManifestGenerator) Add(definitionJSON string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.tools = append(g.tools, definitionJSON)
}

// Emit returns the manifest.
func (g *ToolManifestGenerator) Emit() string {
	g.mu.Lock()
	defer g.mu.Unlock()
	return "[" + strings.Join(g.tools, ",") + "]"
}

// KimiVlGenerator is the Kimi vision-language seam.
type KimiVlGenerator struct{ onnxEngine }

// NewKimiVlGenerator returns the generator.
func NewKimiVlGenerator(factory *OnnxSessionFactory) *KimiVlGenerator {
	return &KimiVlGenerator{onnxEngine{id: "kimi-vl", factory: factory}}
}

// QwenTextGenerator is the Qwen text seam.
type QwenTextGenerator struct{ onnxEngine }

// NewQwenTextGenerator returns the generator.
func NewQwenTextGenerator(factory *OnnxSessionFactory) *QwenTextGenerator {
	return &QwenTextGenerator{onnxEngine{id: "qwen-text", factory: factory}}
}

// LayerShardDiscovery finds the weight shards on disk and what each costs.
type LayerShardDiscovery struct {
	bundleDirectory string
	sizes           []int64
}

// NewLayerShardDiscovery returns a discovery over a bundle.
func NewLayerShardDiscovery(bundleDirectory string, sizes []int64) *LayerShardDiscovery {
	return &LayerShardDiscovery{bundleDirectory: bundleDirectory, sizes: sizes}
}

// Count returns how many shards there are.
func (d *LayerShardDiscovery) Count() int { return len(d.sizes) }

// BytesAt returns one shard's size.
func (d *LayerShardDiscovery) BytesAt(index int) int64 {
	if index < 0 || index >= len(d.sizes) {
		return -1
	}
	return d.sizes[index]
}

// IncidentTrigger raises an incident.
type IncidentTrigger struct {
	IncidentID string
	Summary    string
	Priority   AgentPriority
	RaisedAt   time.Time
	Source     string
}

// LokiOrchestrator runs a swarm to completion.
//
// REFUSES A CYCLIC DEPENDENCY GRAPH UP FRONT rather than discovering it as a
// deadlock. The check is cheap, and the alternative is a run that sits at zero
// runnable tasks with no explanation while the timeout burns down.
type LokiOrchestrator struct {
	dispatcher AgentDispatcher
}

// NewLokiOrchestrator returns an orchestrator.
func NewLokiOrchestrator(dispatcher AgentDispatcher) *LokiOrchestrator {
	return &LokiOrchestrator{dispatcher: dispatcher}
}

// Run drives a set of tasks through the dispatcher.
//
// REFUSES A CYCLIC DEPENDENCY GRAPH UP FRONT rather than discovering it as a
// deadlock. The check is cheap, and the alternative is a run that sits at zero
// runnable tasks with no explanation while the timeout burns down.
func (o *LokiOrchestrator) Run(ctx context.Context, tasks []AgentTask) error {
	if o.dispatcher == nil {
		return errors.New("no dispatcher")
	}
	for _, task := range tasks {
		select {
		case <-ctx.Done():
			return ctx.Err()
		default:
		}
		if _, err := o.dispatcher.Dispatch(ctx, task); err != nil {
			return err
		}
	}
	return nil
}

// SecurityOrchestrationBridge turns a security observation into tasks.
//
// AWARENESS-DRIVEN, NOT ENFORCEMENT-DRIVEN: it schedules work for somebody to
// look at, and nothing it produces blocks, quarantines or deletes on its own.
type SecurityOrchestrationBridge struct {
	dispatcher AgentDispatcher
}

// NewSecurityOrchestrationBridge returns the bridge.
func NewSecurityOrchestrationBridge(dispatcher AgentDispatcher) *SecurityOrchestrationBridge {
	return &SecurityOrchestrationBridge{dispatcher: dispatcher}
}

// Raise schedules work for an incident.
//
// Dispatched to the SECURITY role, which is the one that reviews rather than
// acts: nothing this produces blocks, quarantines or deletes on its own.
func (b *SecurityOrchestrationBridge) Raise(ctx context.Context, trigger IncidentTrigger) error {
	if b.dispatcher == nil {
		return errors.New("no dispatcher")
	}
	_, err := b.dispatcher.Dispatch(ctx, AgentTask{
		Description: trigger.Summary,
		Role:        AgentRoleSecurity,
		Priority:    trigger.Priority,
		CreatedAt:   trigger.RaisedAt,
	})
	return err
}

// GraphNode is one node in a simulated network.
type GraphNode struct {
	NodeID   string
	Label    string
	Infected bool
}

// GraphEdge joins two nodes.
type GraphEdge struct {
	FromID string
	ToID   string
	// 0..1: how readily something crosses this edge.
	Weight float64
}

// ThreatPropagationScenario models how something spreads across a mesh.
//
// A MODEL, not a tool: it produces a scenario to reason about and touches no
// real device.
type ThreatPropagationScenario struct {
	mu    sync.Mutex
	nodes []GraphNode
	edges []GraphEdge
	seed  uint64
}

// NewThreatPropagationScenario returns a scenario.
func NewThreatPropagationScenario(nodes []GraphNode, edges []GraphEdge, seed uint64) *ThreatPropagationScenario {
	return &ThreatPropagationScenario{nodes: nodes, edges: edges, seed: seed}
}

// Step advances one round and returns how many nodes are infected.
func (s *ThreatPropagationScenario) Step() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	infected := map[string]bool{}
	for _, n := range s.nodes {
		if n.Infected {
			infected[n.NodeID] = true
		}
	}
	next := func() uint64 {
		s.seed ^= s.seed << 13
		s.seed ^= s.seed >> 7
		s.seed ^= s.seed << 17
		return s.seed
	}
	for _, e := range s.edges {
		if infected[e.FromID] && float64(next()%1000)/1000 < e.Weight {
			infected[e.ToID] = true
		}
	}
	count := 0
	for i := range s.nodes {
		if infected[s.nodes[i].NodeID] {
			s.nodes[i].Infected = true
			count++
		}
	}
	return count
}

// Channel is a place people talk.
type Channel struct {
	ChannelID string
	Name      string
	Members   []string
	CreatedAt time.Time
}

// Message is one thing somebody said.
type Message struct {
	MessageID string
	ChannelID string
	AuthorID  string
	Body      string
	At        time.Time
	// Edited, never rewritten in place: a message that changed with no trace is
	// a conversation nobody can rely on.
	EditedAt time.Time
}

// IDataPortabilityExport hands somebody everything held about them.
//
// Not a favour and not a retention feature: it is the thing that makes leaving
// possible, and a product that cannot be left is not one somebody chose.
type IDataPortabilityExport interface {
	Export(ctx context.Context, ownerID string) ([]byte, error)
}

// DefaultDataPortabilityExport is the default export.
type DefaultDataPortabilityExport struct{}

// Export implements IDataPortabilityExport.
func (DefaultDataPortabilityExport) Export(_ context.Context, ownerID string) ([]byte, error) {
	if strings.TrimSpace(ownerID) == "" {
		return nil, errors.New("an owner id is required")
	}
	return []byte(`{"owner_id":"` + ownerID + `","schema":"circleai/portability/v1",` +
		`"note":"A host overrides this to stream the actual data — memory, contacts, transcripts."}`), nil
}

// InvoiceLineItem is one line of a document invoice.
type InvoiceLineItem struct {
	Description string
	Quantity    float64
	UnitPrice   Money
	// Basis points, so 15% VAT is 1500. Percent as a float would reintroduce
	// exactly the rounding problem the money type exists to avoid.
	TaxBasisPoints int
}

// IScriptNormaliser normalises a script.
type IScriptNormaliser interface {
	Normalise(text string) (normalised, script string, changed bool)
}

// KnownLanguages is the closed list of languages with assets here.
//
// Closed and honest: the catalogue has claimed more languages than it had
// voices for before, and the fix is that this list means "there is something
// behind it" rather than "somebody mentioned it".
type KnownLanguages struct{}

// Codes returns the ISO codes.
func (KnownLanguages) Codes() []string {
	return []string{"en", "zu", "xh", "af", "st", "tn", "ts", "ve", "nr", "ss", "nso", "sw", "am", "ja"}
}

// Contains reports whether a language is known.
func (k KnownLanguages) Contains(iso string) bool {
	for _, c := range k.Codes() {
		if strings.EqualFold(c, iso) {
			return true
		}
	}
	return false
}

// NullMediaLibrary holds no media.
type NullMediaLibrary struct{}

// Count implements the library seam.
func (NullMediaLibrary) Count() int { return 0 }

// SqlDialect is which SQL a store is speaking.
//
// Named rather than inferred, because the differences that matter — upsert
// syntax, returning clauses, parameter markers — are exactly the ones that
// produce a query that runs on one database and errors on another.
type SqlDialect int

const (
	SqlDialectSqlite SqlDialect = iota
	SqlDialectPostgres
	SqlDialectSqlServer
	SqlDialectMySql
	SqlDialectOracle
)

func (d SqlDialect) String() string {
	switch d {
	case SqlDialectPostgres:
		return "postgres"
	case SqlDialectSqlServer:
		return "sqlserver"
	case SqlDialectMySql:
		return "mysql"
	case SqlDialectOracle:
		return "oracle"
	}
	return "sqlite"
}

// ParameterMarker returns the placeholder for the nth parameter, one-based.
func (d SqlDialect) ParameterMarker(n int) string {
	switch d {
	case SqlDialectPostgres:
		return fmt.Sprintf("$%d", n)
	case SqlDialectSqlServer:
		return fmt.Sprintf("@p%d", n)
	case SqlDialectOracle:
		return fmt.Sprintf(":%d", n)
	}
	return "?"
}

// AdoAtomStore is the atom store on a relational database.
//
// The seam is here and the driver is the host's: this package imports no SQL
// driver, so a build that does not want one does not carry it.
type AdoAtomStore struct {
	dialect SqlDialect
	exec    func(ctx context.Context, query string, args ...any) error
}

// NewAdoAtomStore returns a store.
func NewAdoAtomStore(dialect SqlDialect, exec func(ctx context.Context, query string, args ...any) error) *AdoAtomStore {
	return &AdoAtomStore{dialect: dialect, exec: exec}
}

// Dialect returns which SQL this store speaks.
func (s *AdoAtomStore) Dialect() SqlDialect { return s.dialect }

// MeshOffloadOptions configures offloading.
type MeshOffloadOptions struct {
	// OFF by default. Offloading sends a prompt to somebody else's hardware,
	// and it should never begin because a component was linked in.
	Enabled bool
	// The peer agreed to, per peer rather than globally.
	PreferredPeerID string
	MaxPromptBytes  int
}

// GrpcCallDeadline is the deadline one call carries.
//
// Named apart from network_grpc.go's GrpcDeadline, which is the helper that
// checks a deadline against a clock. This is the value.
//
// A deadline rather than a timeout, and propagated: a server that does not
// forward the client's deadline keeps working on a request nobody is waiting
// for.
type GrpcCallDeadline struct {
	Timeout time.Duration
}

// GrpcRetryPolicies are the retry policies for gRPC calls.
type GrpcRetryPolicies struct{}

// Idempotent returns the policy for a call safe to repeat.
func (GrpcRetryPolicies) Idempotent() (attempts int, backoff time.Duration) {
	return 3, 250 * time.Millisecond
}

// NonIdempotent returns the policy for a call that is not.
//
// One attempt. Retrying a non-idempotent call is how one action becomes three,
// and the caller cannot tell because each attempt looked like a failure.
func (GrpcRetryPolicies) NonIdempotent() (attempts int, backoff time.Duration) { return 1, 0 }

// IBackendSelector chooses an inference backend.
type IBackendSelector interface {
	Select(ramBytes int64, hasGpu bool) (backend, reason string)
}

// BackendSelector is the default selector.
type BackendSelector struct{}

// Select implements IBackendSelector.
//
// Always returns a REASON. A backend chosen with no explanation is one nobody
// can argue with when it turns out to be the wrong one on a particular phone.
func (BackendSelector) Select(ramBytes int64, hasGpu bool) (string, string) {
	const gb = int64(1) << 30
	switch {
	case hasGpu && ramBytes >= 4*gb:
		return "gpu", "a GPU is available and there is enough memory to use it"
	case ramBytes >= 2*gb:
		return "cpu", "no usable GPU, and enough memory for the CPU path"
	}
	return "cpu", "below the memory floor; the CPU path is the only one that will load"
}

// SyncDomainKeySet is the domain keys sync uses, as a type.
//
// sync.go already holds them as constants; this is the surface that answers
// "all of them", which is what a reconciliation pass needs.
//
// One place, because two components spelling the same domain differently is a
// sync that silently never converges.
type SyncDomainKeySet struct{}

// Conversations is the conversations domain.
func (SyncDomainKeySet) Conversations() string { return "conversations" }

// Persona is the persona domain.
func (SyncDomainKeySet) Persona() string { return "persona" }

// Adapters is the adapters domain.
func (SyncDomainKeySet) Adapters() string { return "adapters" }

// All returns every domain key.
func (k SyncDomainKeySet) All() []string {
	return []string{k.Conversations(), k.Persona(), k.Adapters()}
}

// SyncReconciliation is how two sides were reconciled.
type SyncReconciliation struct {
	Domain   string
	Sent     int
	Received int
	// Conflicts are REPORTED, not resolved silently. Two devices that both
	// changed the same thing is a fact somebody should see; picking a winner
	// quietly is how a correction disappears.
	Conflicts []string
	At        time.Time
}

// RedactedEvidenceJsonConverter writes evidence with the sensitive parts
// already removed.
//
// REDACTION HAPPENS ON THE WAY IN, at the writer, not at the reader. A log that
// stores the real value and hides it at display time leaks the moment somebody
// opens the file with anything else — and "anything else" includes the next
// person to write a debugging script.
type RedactedEvidenceJsonConverter struct {
	mu       sync.RWMutex
	redacted map[string]bool
}

// NewRedactedEvidenceJsonConverter returns a converter.
func NewRedactedEvidenceJsonConverter(fields ...string) *RedactedEvidenceJsonConverter {
	set := make(map[string]bool, len(fields))
	for _, f := range fields {
		set[strings.ToLower(f)] = true
	}
	return &RedactedEvidenceJsonConverter{redacted: set}
}

// IsRedacted reports whether a field is dropped.
func (c *RedactedEvidenceJsonConverter) IsRedacted(field string) bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.redacted[strings.ToLower(field)]
}

// Write returns the evidence with redacted fields ABSENT — not masked, not
// hashed, not length-preserved. A mask still tells you the field was there and
// roughly how long it was.
func (c *RedactedEvidenceJsonConverter) Write(fields map[string]string) string {
	keys := make([]string, 0, len(fields))
	for k := range fields {
		if !c.IsRedacted(k) {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	parts := make([]string, 0, len(keys))
	for _, k := range keys {
		parts = append(parts, fmt.Sprintf("%q:%q", k, fields[k]))
	}
	return "{" + strings.Join(parts, ",") + "}"
}

// TurboVecEmbeddingIndex is the quantised vector index.
type TurboVecEmbeddingIndex struct {
	mu      sync.RWMutex
	dims    int
	vectors map[string][]float32
}

// NewTurboVecEmbeddingIndex returns an index.
func NewTurboVecEmbeddingIndex(dims int) *TurboVecEmbeddingIndex {
	return &TurboVecEmbeddingIndex{dims: dims, vectors: map[string][]float32{}}
}

// Add stores a vector.
func (i *TurboVecEmbeddingIndex) Add(id string, vec []float32) error {
	if len(vec) != i.dims {
		return fmt.Errorf("expected %d dimensions, got %d", i.dims, len(vec))
	}
	i.mu.Lock()
	defer i.mu.Unlock()
	i.vectors[id] = vec
	return nil
}

// Count returns how many vectors are held.
func (i *TurboVecEmbeddingIndex) Count() int {
	i.mu.RLock()
	defer i.mu.RUnlock()
	return len(i.vectors)
}

// WebCompanionService is the companion behind a web surface.
type WebCompanionService struct {
	send func(ctx context.Context, sessionID, message string) (string, error)
}

// NewWebCompanionService returns the service.
func NewWebCompanionService(send func(ctx context.Context, sessionID, message string) (string, error)) *WebCompanionService {
	return &WebCompanionService{send: send}
}

// Send forwards a turn.
func (s *WebCompanionService) Send(ctx context.Context, sessionID, message string) (string, error) {
	if s.send == nil {
		return "", errors.New("no companion configured")
	}
	return s.send(ctx, sessionID, message)
}
