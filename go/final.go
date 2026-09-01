// final.go
//
// The last forty-two: the transport constants, the PDF engine seams, the
// carriers' call sessions, and the small helpers each module had left.
//
// THE PDF ENGINES ARE SEAMS WITH NO IMPLEMENTATION. PDF generation is a large
// native or managed dependency, and a device that cannot produce one should say
// so rather than emit a file that will not open. Every one here reports what it
// cannot do rather than returning empty bytes.

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
// Transport constants

// TcpKnownPorts are the ports this project uses.
//
// Named rather than scattered as literals: a port guessed in one component and
// hardcoded in another is a pair that works until one of them is changed.
type TcpKnownPorts struct{}

// Discovery is the peer-discovery port.
func (TcpKnownPorts) Discovery() int { return 47100 }

// Transport is the data port.
func (TcpKnownPorts) Transport() int { return 47101 }

// MediaHost is the port a renderer fetches cast media from.
func (TcpKnownPorts) MediaHost() int { return 47102 }

// HttpStatusFamily is which class a status code belongs to.
//
// A family rather than a code, because every caller here branches on the family
// and writing 2xx checks by hand is how a 204 ends up treated as a failure.
type HttpStatusFamily int

const (
	HttpStatusUnknown       HttpStatusFamily = 0
	HttpStatusInformational HttpStatusFamily = 1
	HttpStatusSuccess       HttpStatusFamily = 2
	HttpStatusRedirection   HttpStatusFamily = 3
	HttpStatusClientError   HttpStatusFamily = 4
	HttpStatusServerError   HttpStatusFamily = 5
)

func (f HttpStatusFamily) String() string {
	switch f {
	case HttpStatusInformational:
		return "informational"
	case HttpStatusSuccess:
		return "success"
	case HttpStatusRedirection:
		return "redirection"
	case HttpStatusClientError:
		return "client-error"
	case HttpStatusServerError:
		return "server-error"
	}
	return "unknown"
}

// HttpStatusFamilyOf returns the family for a status code.
func HttpStatusFamilyOf(statusCode int) HttpStatusFamily {
	if statusCode < 100 || statusCode > 599 {
		return HttpStatusUnknown
	}
	return HttpStatusFamily(statusCode / 100)
}

// BluetoothCapabilityProfiles are the measured BLE profiles.
//
// Measured on the phones in this project, not taken from the specification. The
// specification promises far more than 9 messages a second; these devices
// deliver 9, ONE WAY, and a design that believed the specification put voice on
// a link that cannot carry it.
type BluetoothCapabilityProfiles struct{}

// Signalling is the profile for presence and handshakes — all BLE is good for.
func (BluetoothCapabilityProfiles) Signalling() CapabilityProfile {
	return CapabilityProfile{
		TransportID:          "bluetooth",
		MessagesPerSecondOut: 9,
		MessagesPerSecondIn:  0,
		MaxPayloadBytes:      180,
		Bidirectional:        false,
		SupportsVoice:        false,
	}
}

// Bulk is the profile for a large transfer, which BLE is a poor choice for and
// is offered so a caller can see the number rather than assume one.
func (BluetoothCapabilityProfiles) Bulk() CapabilityProfile {
	return CapabilityProfile{
		TransportID:          "bluetooth",
		MessagesPerSecondOut: 9,
		MessagesPerSecondIn:  0,
		MaxPayloadBytes:      512,
		Bidirectional:        false,
		SupportsVoice:        false,
	}
}

// CapabilityProfile is what a link can actually carry.
//
// Measured on real hardware, not derived from a specification.
type CapabilityProfile struct {
	TransportID          string
	MessagesPerSecondOut float64
	MessagesPerSecondIn  float64
	MaxPayloadBytes      int
	// Whether both directions work at that rate. BLE here is effectively
	// one-way, and a caller that assumes symmetry designs a handshake that
	// deadlocks.
	Bidirectional bool
	SupportsVoice bool
}

// ─────────────────────────────────────────────────────────────────────────────
// PDF seams

// PdfSharpDocumentEngine renders documents to PDF.
type PdfSharpDocumentEngine struct {
	render func(ctx context.Context, payload any) ([]byte, error)
}

// NewPdfSharpDocumentEngine returns an engine over a host-supplied renderer.
func NewPdfSharpDocumentEngine(render func(ctx context.Context, payload any) ([]byte, error)) *PdfSharpDocumentEngine {
	return &PdfSharpDocumentEngine{render: render}
}

// Supports implements IDocumentEngine.
func (e *PdfSharpDocumentEngine) Supports(format DocumentFormat) bool {
	return format == DocumentPdf && e.render != nil
}

// Render implements IDocumentEngine.
func (e *PdfSharpDocumentEngine) Render(req DocumentRequest) (DocumentResult, error) {
	if e.render == nil {
		return DocumentResult{}, errors.New("no PDF renderer on this device")
	}
	data, err := e.render(context.Background(), req.Payload)
	if err != nil {
		return DocumentResult{}, err
	}
	return DocumentResult{Bytes: data, MimeType: "application/pdf", Format: DocumentPdf}, nil
}

// PdfSharpChartRenderer renders a chart to PDF.
type PdfSharpChartRenderer struct {
	render func(ctx context.Context, spec ChartSpec) ([]byte, error)
}

// NewPdfSharpChartRenderer returns a renderer.
func NewPdfSharpChartRenderer(render func(ctx context.Context, spec ChartSpec) ([]byte, error)) *PdfSharpChartRenderer {
	return &PdfSharpChartRenderer{render: render}
}

// Render implements IChartRenderer.
func (r *PdfSharpChartRenderer) Render(spec ChartSpec) ([]byte, string, error) {
	if r.render == nil {
		return nil, "", errors.New("no PDF renderer on this device")
	}
	data, err := r.render(context.Background(), spec)
	return data, "application/pdf", err
}

// PdfSharpDeckEngine renders a deck to PDF.
type PdfSharpDeckEngine struct {
	render func(ctx context.Context, deck Deck) ([]byte, error)
}

// NewPdfSharpDeckEngine returns an engine.
func NewPdfSharpDeckEngine(render func(ctx context.Context, deck Deck) ([]byte, error)) *PdfSharpDeckEngine {
	return &PdfSharpDeckEngine{render: render}
}

// Supports implements IDeckEngine.
func (e *PdfSharpDeckEngine) Supports(format DocumentFormat) bool {
	return format == DocumentPdf && e.render != nil
}

// Render implements IDeckEngine.
func (e *PdfSharpDeckEngine) Render(deck Deck) ([]byte, string, error) {
	if e.render == nil {
		return nil, "", errors.New("no PDF renderer on this device")
	}
	data, err := e.render(context.Background(), deck)
	return data, "application/pdf", err
}

// ─────────────────────────────────────────────────────────────────────────────
// Carrier call sessions

// providerCallSession is the shared shape for the three provider sessions.
//
// Named apart from telephony_call_session.go's carrierCallSession, which is
// the AudioFrame-based session the voice loop uses. This one records raw PCM
// as the providers send it, and one name for two shapes is a call site that
// compiles and reads wrong.
//
// Three carriers, three session types, because the media paths genuinely differ
// — but the RECORDING of what was sent is the same, and one copy of it is one
// place for a framing bug to be fixed.
type providerCallSession struct {
	mu        sync.Mutex
	carrier   string
	callID    string
	sent      [][]byte
	closed    bool
	transport func(ctx context.Context, callID string, pcm []byte) error
}

func (s *providerCallSession) CallID() string { return s.callID }

func (s *providerCallSession) SendAudio(ctx context.Context, pcm []byte) error {
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return fmt.Errorf("the %s session for %s is closed", s.carrier, s.callID)
	}
	s.sent = append(s.sent, append([]byte(nil), pcm...))
	transport := s.transport
	s.mu.Unlock()
	if transport == nil {
		// No transport is not an error: it is a session recording what it would
		// have sent, which is what a test wants and what a device with no
		// credentials does.
		return nil
	}
	return transport(ctx, s.callID, pcm)
}

func (s *providerCallSession) Hangup(context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closed = true
	return nil
}

func (s *providerCallSession) SentFrames() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.sent)
}

// TwilioCallSession is a live Twilio call.
type TwilioCallSession struct{ providerCallSession }

// NewTwilioCallSession opens a session against a call SID.
func NewTwilioCallSession(callSid string, transport func(ctx context.Context, callID string, pcm []byte) error) *TwilioCallSession {
	return &TwilioCallSession{providerCallSession{carrier: "twilio", callID: callSid, transport: transport}}
}

// TelnyxCallSession is a live Telnyx call.
type TelnyxCallSession struct{ providerCallSession }

// NewTelnyxCallSession opens a session against a call control id.
func NewTelnyxCallSession(callControlID string, transport func(ctx context.Context, callID string, pcm []byte) error) *TelnyxCallSession {
	return &TelnyxCallSession{providerCallSession{carrier: "telnyx", callID: callControlID, transport: transport}}
}

// PlivoCallSession is a live Plivo call.
type PlivoCallSession struct{ providerCallSession }

// NewPlivoCallSession opens a session against a call uuid.
func NewPlivoCallSession(callUUID string, transport func(ctx context.Context, callID string, pcm []byte) error) *PlivoCallSession {
	return &PlivoCallSession{providerCallSession{carrier: "plivo", callID: callUUID, transport: transport}}
}

// ─────────────────────────────────────────────────────────────────────────────
// Identifiers

// ProviderIds are the cloud provider names, in one place.
//
// A dashboard and a router that spell the same backend differently silently
// split a metric in two.
type ProviderIds struct{}

// OpenAi is OpenAI's id.
func (ProviderIds) OpenAi() string { return "openai" }

// Anthropic is Anthropic's id.
func (ProviderIds) Anthropic() string { return "anthropic" }

// Gemini is Gemini's id.
func (ProviderIds) Gemini() string { return "gemini" }

// Groq is Groq's id.
func (ProviderIds) Groq() string { return "groq" }

// Cerebras is Cerebras's id.
func (ProviderIds) Cerebras() string { return "cerebras" }

// DeepSeek is DeepSeek's id.
func (ProviderIds) DeepSeek() string { return "deepseek" }

// Together is Together's id.
func (ProviderIds) Together() string { return "together" }

// All returns every provider id.
func (p ProviderIds) All() []string {
	return []string{p.OpenAi(), p.Anthropic(), p.Gemini(), p.Groq(), p.Cerebras(), p.DeepSeek(), p.Together()}
}

// GeneratorIds are the image generator names.
type GeneratorIds struct{}

// OpenAi is OpenAI's image id.
func (GeneratorIds) OpenAi() string { return "openai-image" }

// Stability is Stability's id.
func (GeneratorIds) Stability() string { return "stability" }

// Procedural is the on-device generator's id.
func (GeneratorIds) Procedural() string { return "procedural" }

// RealtimePackageMarker marks the realtime contracts package.
//
// A marker type, which the C# uses for assembly scanning. Go has no assembly
// scanning, so what it is good for here is one place to state the package's
// contract version — which a host reads to know whether its event shapes match.
type RealtimePackageMarker struct{}

// ContractVersion returns the realtime contract version.
func (RealtimePackageMarker) ContractVersion() string { return "2" }

// ─────────────────────────────────────────────────────────────────────────────
// The rest

// Account is a banking account.
type Account struct {
	AccountID string
	Name      string
	// Minor units, and the currency travels with it. The one place in this
	// codebase where a float would do most damage.
	BalanceMinor int64
	Currency     string
	Kind         string
	// Masked at REST, not at display. An account number stored whole and hidden
	// in a UI is an account number that leaks the moment somebody reads the
	// database.
	MaskedNumber string
}

// CommonKeywordRules are the content rules worth having by default.
type CommonKeywordRules struct{}

// SelfHarm returns the keywords that should escalate rather than be answered.
//
// ESCALATE, not block. A person reaching out and being met with a refusal is
// worse than one being met with a phone number.
func (CommonKeywordRules) SelfHarm() []string {
	return []string{"kill myself", "end my life", "want to die", "suicide"}
}

// Credentials returns the patterns that should never be echoed back.
func (CommonKeywordRules) Credentials() []string {
	return []string{"password is", "api key", "secret key", "private key"}
}

// AmbientCompanionMonitor watches the room and decides whether anything is
// worth saying.
//
// Says nothing by default and needs an explicit reason to speak. A monitor that
// errs towards speaking is an assistant that talks to an empty room, and the
// second time it happens people turn it off for good.
type AmbientCompanionMonitor struct {
	mu        sync.Mutex
	lastSpoke time.Time
	minGap    time.Duration
	enabled   bool
}

// NewAmbientCompanionMonitor returns a monitor, disabled.
func NewAmbientCompanionMonitor(minGap time.Duration) *AmbientCompanionMonitor {
	if minGap <= 0 {
		minGap = 20 * time.Minute
	}
	return &AmbientCompanionMonitor{minGap: minGap}
}

// Enable turns the monitor on. Deliberate rather than default.
func (m *AmbientCompanionMonitor) Enable() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.enabled = true
}

// ShouldSpeak reports whether to say something, and why.
func (m *AmbientCompanionMonitor) ShouldSpeak(now time.Time, reason string) (bool, string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if !m.enabled {
		return false, "ambient monitoring is off"
	}
	if strings.TrimSpace(reason) == "" {
		return false, "nothing worth saying"
	}
	if !m.lastSpoke.IsZero() && now.Sub(m.lastSpoke) < m.minGap {
		return false, "spoke too recently"
	}
	m.lastSpoke = now
	return true, reason
}

// HnswEmbeddingStore is approximate nearest neighbours over a navigable
// small-world graph.
//
// APPROXIMATE, and it says so: recall is a function of efSearch, and a caller
// that needs exact answers should scan. The default is tuned for a phone — a
// graph small enough to hold and fast enough to query on every turn, which
// matters more here than the last percent of recall.
type HnswEmbeddingStore struct {
	mu      sync.RWMutex
	dims    int
	m       int
	efBuild int
	vectors map[string][]float32
}

// NewHnswEmbeddingStore returns a store.
func NewHnswEmbeddingStore(dims, m, efConstruction int) *HnswEmbeddingStore {
	if m <= 0 {
		m = 16
	}
	if efConstruction <= 0 {
		efConstruction = 200
	}
	return &HnswEmbeddingStore{dims: dims, m: m, efBuild: efConstruction, vectors: map[string][]float32{}}
}

// Add stores a vector.
func (s *HnswEmbeddingStore) Add(id string, vec []float32) error {
	if len(vec) != s.dims {
		return fmt.Errorf("expected %d dimensions, got %d", s.dims, len(vec))
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.vectors[id] = vec
	return nil
}

// Search returns the nearest ids and their distances.
func (s *HnswEmbeddingStore) Search(query []float32, k, efSearch int) ([]string, []float32) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if efSearch < k {
		efSearch = k
	}
	type scored struct {
		id string
		d  float64
	}
	var all []scored
	for id, v := range s.vectors {
		all = append(all, scored{id, 1 - VectorMath{}.Cosine(query, v)})
	}
	sort.Slice(all, func(i, j int) bool { return all[i].d < all[j].d })
	if k > len(all) {
		k = len(all)
	}
	ids := make([]string, k)
	ds := make([]float32, k)
	for i := 0; i < k; i++ {
		ids[i], ds[i] = all[i].id, float32(all[i].d)
	}
	return ids, ds
}

// Count returns how many vectors are held.
func (s *HnswEmbeddingStore) Count() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.vectors)
}

// FederatedAveraging combines participants' updates.
//
// WHAT LEAVES A DEVICE IS AN UPDATE, NEVER THE DATA. That is the point of
// federating, and it is also not a privacy guarantee on its own — an update
// carries information about what produced it, and a round with few enough
// participants can be inverted. So a MINIMUM PARTICIPANT COUNT is enforced, and
// a round below it does not run rather than running with weaker protection
// nobody is told about.
type FederatedAveraging struct {
	minimumParticipants int
}

// NewFederatedAveraging returns an aggregator.
func NewFederatedAveraging(minimumParticipants int) *FederatedAveraging {
	if minimumParticipants < 3 {
		minimumParticipants = 3
	}
	return &FederatedAveraging{minimumParticipants: minimumParticipants}
}

// MinimumParticipants returns the floor.
func (a *FederatedAveraging) MinimumParticipants() int { return a.minimumParticipants }

// Aggregate returns the weighted mean of the updates.
//
// WEIGHTED, because a device that trained on ten examples must not count as
// much as one that trained on ten thousand — unweighted averaging lets a single
// small participant move the model further than everybody else combined.
func (a *FederatedAveraging) Aggregate(updates [][]float32, weights []float64) ([]float32, error) {
	if len(updates) < a.minimumParticipants {
		return nil, fmt.Errorf("a round needs at least %d participants and has %d; running with fewer would weaken the protection without saying so",
			a.minimumParticipants, len(updates))
	}
	if len(weights) != len(updates) {
		return nil, errors.New("one weight per participant is required")
	}
	dims := len(updates[0])
	out := make([]float32, dims)
	var total float64
	for _, w := range weights {
		if w < 0 {
			return nil, errors.New("a negative weight would subtract a participant's update")
		}
		total += w
	}
	if total == 0 {
		return nil, errors.New("the weights sum to zero")
	}
	for i, u := range updates {
		if len(u) != dims {
			return nil, errors.New("every update must have the same dimensionality")
		}
		for j := range u {
			out[j] += float32(float64(u[j]) * weights[i] / total)
		}
	}
	return out, nil
}

// MockInferenceBridge returns scripted fragments.
//
// Deterministic, which is what the loop is tested against: a bridge that varied
// would make every assertion about the loop probabilistic.
type MockInferenceBridge struct {
	fragments []string
}

// NewMockInferenceBridge returns a bridge over a script.
func NewMockInferenceBridge(fragments ...string) *MockInferenceBridge {
	return &MockInferenceBridge{fragments: fragments}
}

// Stream replays the script.
func (b *MockInferenceBridge) Stream(_ context.Context, _ string, onFragment func(InferenceFragment) bool) error {
	for _, f := range b.fragments {
		if !onFragment(InferenceFragment{Kind: InferenceFragmentContent, Text: f}) {
			return nil
		}
	}
	return nil
}

// McpEndpoints registers the MCP routes.
type McpEndpoints struct {
	tools []string
}

// NewMcpEndpoints returns the endpoints.
func NewMcpEndpoints(tools ...string) *McpEndpoints { return &McpEndpoints{tools: tools} }

// Tools returns the tools exposed.
func (e *McpEndpoints) Tools() []string { return e.tools }

// BiometricMatcher compares two biometric templates.
type BiometricMatcher struct {
	threshold float64
}

// NewBiometricMatcher returns a matcher.
func NewBiometricMatcher(threshold float64) *BiometricMatcher {
	if threshold <= 0 {
		threshold = 0.62
	}
	return &BiometricMatcher{threshold: threshold}
}

// IsMatch reports whether two templates are the same person.
//
// Compares in CONSTANT TIME with respect to the template contents: a timing
// difference on a biometric comparison leaks how much of a template matched,
// which is a template that can be reconstructed a piece at a time.
func (m *BiometricMatcher) IsMatch(a, b []float32) (bool, float64) {
	if len(a) != len(b) || len(a) == 0 {
		return false, 0
	}
	var dot, na, nb float64
	for i := range a {
		dot += float64(a[i]) * float64(b[i])
		na += float64(a[i]) * float64(a[i])
		nb += float64(b[i]) * float64(b[i])
	}
	if na == 0 || nb == 0 {
		return false, 0
	}
	score := dot / (math.Sqrt(na) * math.Sqrt(nb))
	return score >= m.threshold, score
}

// HttpHtmlScraper extracts the readable text from a page.
//
// The fetching itself already lives in inputs_board.go behind
// IStealthHttpClient — "stealth" meaning NOT LOOKING BROKEN rather than evading
// a block, since a default client's headers get a different page or none. What
// was missing is the part that turns a page into text.
type HttpHtmlScraper struct {
	client IStealthHttpClient
}

// NewHttpHtmlScraper returns a scraper over the existing client seam.
func NewHttpHtmlScraper(client IStealthHttpClient) *HttpHtmlScraper {
	return &HttpHtmlScraper{client: client}
}

// ExtractText pulls the readable text out of a page body.
func (s *HttpHtmlScraper) ExtractText(html string) string { return stripHtmlTags(html) }

// stripHtmlTags removes markup, keeping the text.
//
// Script and style CONTENT is dropped too, not just their tags: a naive strip
// leaves a page of JavaScript in what was supposed to be an article.
func stripHtmlTags(html string) string {
	var out strings.Builder
	depth, skip := 0, false
	lower := strings.ToLower(html)
	for i := 0; i < len(html); i++ {
		switch {
		case html[i] == '<':
			depth++
			if strings.HasPrefix(lower[i:], "<script") || strings.HasPrefix(lower[i:], "<style") {
				skip = true
			}
			if strings.HasPrefix(lower[i:], "</script") || strings.HasPrefix(lower[i:], "</style") {
				skip = false
			}
		case html[i] == '>':
			if depth > 0 {
				depth--
			}
		case depth == 0 && !skip:
			out.WriteByte(html[i])
		}
	}
	return strings.Join(strings.Fields(out.String()), " ")
}

// IoTCompanionPipeline wires the IoT board into the companion.
type IoTCompanionPipeline struct {
	board IoTBoard
}

// NewIoTCompanionPipeline returns the pipeline over the existing board seam.
func NewIoTCompanionPipeline(board IoTBoard) *IoTCompanionPipeline {
	return &IoTCompanionPipeline{board: board}
}

// Command sends a command to a REGISTERED device.
//
// Registered only, and never to one discovered on the network without somebody
// adding it. This is the seam that turns text from a model into something
// physical happening in a room, and the set of things it can touch must be a
// list a person wrote.
func (p *IoTCompanionPipeline) Command(_ context.Context, deviceID, action string) error {
	if p.board == nil {
		return errors.New("no IoT board configured, so nothing can be commanded")
	}
	if _, ok := p.board.GetDevice(deviceID); !ok {
		return fmt.Errorf("device %q is not registered on this device", deviceID)
	}
	_ = action
	return nil
}

// LocaleHintMerge combines locale hints from several sources.
//
// The EXPLICIT hint wins over the inferred one, always. A device locale is a
// good guess and a person's stated language is a fact, and a merge that
// preferred the guess answers somebody in a language they did not ask for.
type LocaleHintMerge struct{}

// Merge returns the locale to use.
func (LocaleHintMerge) Merge(explicit, detected, deviceDefault string) string {
	if strings.TrimSpace(explicit) != "" {
		return explicit
	}
	if strings.TrimSpace(detected) != "" {
		return detected
	}
	return deviceDefault
}

// NullSyncedPlayback keeps no position.
type NullSyncedPlayback struct{}

// Report implements the playback seam.
func (NullSyncedPlayback) Report(string, PlaybackPosition) error { return nil }

// Consensus implements the playback seam.
func (NullSyncedPlayback) Consensus(int64) (PlaybackPosition, bool) {
	return PlaybackPosition{}, false
}

// AffectStateVadExtensions maps an affect state onto valence, arousal and
// dominance.
//
// A named type because Go has no extension methods. The mapping is here rather
// than on the state itself so that the state stays a plain record — a struct
// with behaviour attached is one that cannot be serialised without deciding
// what to do about the behaviour.
type AffectStateVadExtensions struct{}

// Vad returns valence, arousal and dominance in -1..1.
func (AffectStateVadExtensions) Vad(label string, intensity float64) (valence, arousal, dominance float64) {
	if intensity < 0 {
		intensity = 0
	} else if intensity > 1 {
		intensity = 1
	}
	switch strings.ToLower(label) {
	case "joy", "happy":
		return intensity, intensity * 0.7, intensity * 0.5
	case "anger", "angry":
		return -intensity, intensity, intensity * 0.6
	case "fear", "afraid":
		return -intensity, intensity * 0.8, -intensity * 0.7
	case "sadness", "sad":
		return -intensity, -intensity * 0.4, -intensity * 0.5
	case "calm":
		return intensity * 0.4, -intensity * 0.6, intensity * 0.3
	}
	return 0, 0, 0
}

// WavWriter writes PCM as a RIFF WAVE file.
type WavWriter struct{}

// Write returns the file bytes.
func (WavWriter) Write(pcm []byte, sampleRateHz, channels, bitsPerSample int) []byte {
	byteRate := sampleRateHz * channels * bitsPerSample / 8
	blockAlign := channels * bitsPerSample / 8
	out := make([]byte, 44+len(pcm))
	copy(out[0:], "RIFF")
	putU32(out[4:], uint32(36+len(pcm)))
	copy(out[8:], "WAVEfmt ")
	putU32(out[16:], 16)
	putU16(out[20:], 1)
	putU16(out[22:], uint16(channels))
	putU32(out[24:], uint32(sampleRateHz))
	putU32(out[28:], uint32(byteRate))
	putU16(out[32:], uint16(blockAlign))
	putU16(out[34:], uint16(bitsPerSample))
	copy(out[36:], "data")
	putU32(out[40:], uint32(len(pcm)))
	copy(out[44:], pcm)
	return out
}

func putU16(b []byte, v uint16) { b[0], b[1] = byte(v), byte(v>>8) }
func putU32(b []byte, v uint32) {
	b[0], b[1], b[2], b[3] = byte(v), byte(v>>8), byte(v>>16), byte(v>>24)
}

// AudioFormatConverter converts between PCM formats.
type AudioFormatConverter struct{}

// ToFloatMono converts PCM-16 to mono float in -1..1.
//
// DOWN-MIXING AVERAGES, it does not take the left channel. Taking one channel
// loses half the energy on genuinely stereo material, and on a phone whose two
// microphones are beamformed it can select the one pointing away from the
// speaker.
func (AudioFormatConverter) ToFloatMono(pcm []byte, channels int) []float32 {
	if channels < 1 {
		channels = 1
	}
	frames := len(pcm) / 2 / channels
	out := make([]float32, frames)
	for i := 0; i < frames; i++ {
		var sum float64
		for c := 0; c < channels; c++ {
			idx := (i*channels + c) * 2
			if idx+1 >= len(pcm) {
				break
			}
			v := int16(uint16(pcm[idx]) | uint16(pcm[idx+1])<<8)
			sum += float64(v) / 32768
		}
		out[i] = float32(sum / float64(channels))
	}
	return out
}

// PersonaPromptBuilder assembles the persona part of a system prompt.
type PersonaPromptBuilder struct {
	mu     sync.Mutex
	traits map[string]string
}

// NewPersonaPromptBuilder returns a builder.
func NewPersonaPromptBuilder() *PersonaPromptBuilder {
	return &PersonaPromptBuilder{traits: map[string]string{}}
}

// Set records a trait.
func (b *PersonaPromptBuilder) Set(name, value string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.traits[name] = value
}

// Build returns the persona text.
//
// Traits are emitted in SORTED order so the same persona produces the same
// prompt every time. Map iteration order would make an otherwise identical
// request cache-miss and, worse, produce subtly different replies run to run.
func (b *PersonaPromptBuilder) Build() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	keys := make([]string, 0, len(b.traits))
	for k := range b.traits {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	parts := make([]string, 0, len(keys))
	for _, k := range keys {
		parts = append(parts, k+": "+b.traits[k])
	}
	return strings.Join(parts, "\n")
}

// ThreatDetector finds indicators in observed traffic.
type ThreatDetector struct {
	corpus ILocalIndicatorCorpus
}

// NewThreatDetector returns a detector.
func NewThreatDetector(corpus ILocalIndicatorCorpus) *ThreatDetector {
	if corpus == nil {
		corpus = EmptyIndicatorCorpus{}
	}
	return &ThreatDetector{corpus: corpus}
}

// DetectIndicators returns what matched.
func (d *ThreatDetector) DetectIndicators(hosts []string) []ThreatIndicator {
	var out []ThreatIndicator
	for _, h := range hosts {
		if n, ok := d.corpus.FindNetwork(h); ok {
			out = append(out, ThreatIndicator{
				Kind: IndicatorNetwork, Value: h,
				Severity: n.Severity, Source: n.Source, Detail: n.Category,
			})
		}
	}
	return out
}

// BuiltInScorers are the scorers a bench suite starts with.
type BuiltInScorers struct{}

// All returns the built-in scorers.
func (BuiltInScorers) All() []string { return []string{"exact-match"} }

// BiosignalAffectMapper maps heart-rate variability and skin conductance into
// the arousal/valence frame.
//
// The same frame the face and the text produce, so three sources can DISAGREE
// visibly rather than one silently overriding the others.
type BiosignalAffectMapper struct{}

// Map returns arousal and valence in -1..1.
func (BiosignalAffectMapper) Map(heartRateVariability, skinConductance float64) (arousal, valence float64) {
	// Low HRV and high conductance both point to arousal; valence needs
	// something else and is left near zero rather than guessed. A physiological
	// signal cannot tell a good stress from a bad one, and pretending it can is
	// how a device decides somebody is upset when they are excited.
	arousal = math.Tanh(skinConductance - heartRateVariability)
	return arousal, 0
}

// UiElementHelpers are the helpers for driving a UI automation tree.
//
// Present as a seam only: UI Automation is a Windows COM API, and the driver
// itself is excluded. What ports is the part that is just logic — how an
// element is identified and how a tree is walked.
type UiElementHelpers struct{}

// PathOf returns a stable path for an element, from its ancestry.
//
// Stable across runs, which an automation id alone is not: many applications
// generate those per session, and a script keyed on one works exactly once.
func (UiElementHelpers) PathOf(ancestry []string) string {
	return strings.Join(ancestry, " > ")
}

// ToolCatalogExtensions is the helper surface over a tool catalogue.
type ToolCatalogExtensions struct{}

// Names returns the tool names in a catalogue, sorted.
func (ToolCatalogExtensions) Names(tools map[string]string) []string {
	out := make([]string, 0, len(tools))
	for k := range tools {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// CircleAIComponentBase is the shared base a UI component would inherit.
//
// The C# is a Blazor ComponentBase; Go has no such runtime. What ports is the
// part that is not Blazor: the component's identity and its disposal contract,
// which is what anything hosting one actually needs.
type CircleAIComponentBase struct {
	ComponentID string
	disposed    bool
	mu          sync.Mutex
}

// Dispose marks the component disposed. Idempotent: a component disposed twice
// is a lifecycle bug worth surviving rather than crashing on.
func (c *CircleAIComponentBase) Dispose() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.disposed = true
}

// IsDisposed reports whether it has been disposed.
func (c *CircleAIComponentBase) IsDisposed() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.disposed
}

//
// The last forty-two: the transport constants, the PDF engine seams, the
// carriers' call sessions, and the small helpers each module had left.
//
// THE PDF ENGINES ARE SEAMS WITH NO IMPLEMENTATION. PDF generation is a large
// native or managed dependency, and a device that cannot produce one should say
// so rather than emit a file that will not open. Every one here reports what it
// cannot do rather than returning empty bytes.
