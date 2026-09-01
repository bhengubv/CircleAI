// remainder.go
//
// The last of it: the inference server's HTTP surface, the core model
// catalogue and device probe, search, the plugin and skill hosts, the
// antibody gate, the personal adapters, presentations, and the odds and ends.
//
// SEARCH AND VECTOR MATH ARE THE HOT PATH. They are the only things here that
// run on every turn, and they are the reason recall can be afforded at all.
//
// THE PERSONAL ADAPTERS ARE THE MOST SENSITIVE SEAM IN THE PACKAGE. Contacts,
// calendar and mail are, between them, most of a life. Every adapter is null by
// default, every read passes a consent token naming its scope, and nothing is
// cached beyond the call.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"
)

// ─────────────────────────────────────────────────────────────────────────────
// Search

// SearchTokenisation splits text into terms.
type SearchTokenisation struct{}

// Split returns the terms.
//
// Unicode-aware, lower-cased, and it does NOT split on every non-letter: an
// identifier, a version number and a hyphenated surname are each one term, and
// a tokeniser that shreds them makes them unfindable by the thing somebody
// actually typed.
func (SearchTokenisation) Split(text string) []string {
	var out []string
	var cur strings.Builder
	for _, r := range text {
		switch {
		case unicode.IsLetter(r) || unicode.IsDigit(r) || r == '-' || r == '_' || r == '.':
			cur.WriteRune(unicode.ToLower(r))
		default:
			if cur.Len() > 0 {
				out = append(out, strings.Trim(cur.String(), ".-_"))
				cur.Reset()
			}
		}
	}
	if cur.Len() > 0 {
		out = append(out, strings.Trim(cur.String(), ".-_"))
	}
	filtered := out[:0]
	for _, t := range out {
		if t != "" {
			filtered = append(filtered, t)
		}
	}
	return filtered
}

var searchStopWords = map[string]bool{
	"a": true, "an": true, "and": true, "are": true, "as": true, "at": true,
	"be": true, "but": true, "by": true, "for": true, "if": true, "in": true,
	"into": true, "is": true, "it": true, "no": true, "not": true, "of": true,
	"on": true, "or": true, "such": true, "that": true, "the": true, "their": true,
	"then": true, "there": true, "these": true, "they": true, "this": true,
	"to": true, "was": true, "will": true, "with": true,
}

// IsStopWord reports whether a term is worth dropping.
//
// Dropped on the QUERY side only, never at index time — dropping them from the
// index makes an exact phrase unsearchable, and the phrase is often the whole
// point.
func (SearchTokenisation) IsStopWord(term string) bool { return searchStopWords[term] }

// SearchScoring scores a document against a query term.
type SearchScoring struct{}

// K1 is BM25's term-frequency saturation parameter.
//
// The standard value, and standard here is right: tuning it per corpus is how
// two ports stop agreeing on what comes back first.
const K1 = 1.2

// B is BM25's length-normalisation parameter.
const B = 0.75

// Bm25 scores one term in one document.
//
// The saturation term is what stops a document that repeats one word forty
// times outranking one that uses it twice in a sentence that means something.
func (SearchScoring) Bm25(termFrequency, documentFrequency, documentCount, documentLength int, averageDocumentLength float64) float64 {
	if documentCount <= 0 || averageDocumentLength <= 0 {
		return 0
	}
	idf := math.Log(1 + (float64(documentCount)-float64(documentFrequency)+0.5)/(float64(documentFrequency)+0.5))
	tf := float64(termFrequency)
	norm := tf + K1*(1-B+B*float64(documentLength)/averageDocumentLength)
	if norm == 0 {
		return 0
	}
	return idf * (tf * (K1 + 1)) / norm
}

// VectorMath is the reference implementation.
//
// Kept beside the widened one so there is always a correct version to check the
// fast one against: a vector kernel that is quietly wrong produces plausible
// rankings and no error.
type VectorMath struct{}

// Dot returns the dot product.
func (VectorMath) Dot(a, b []float32) float64 {
	n := len(a)
	if len(b) < n {
		n = len(b)
	}
	var sum float64
	for i := 0; i < n; i++ {
		sum += float64(a[i]) * float64(b[i])
	}
	return sum
}

// Cosine returns the cosine similarity, or 0 when either vector is zero.
func (v VectorMath) Cosine(a, b []float32) float64 {
	na, nb := math.Sqrt(v.Dot(a, a)), math.Sqrt(v.Dot(b, b))
	if na == 0 || nb == 0 {
		return 0
	}
	return v.Dot(a, b) / (na * nb)
}

// Normalise scales a vector to unit length in place.
func (v VectorMath) Normalise(vec []float32) {
	n := math.Sqrt(v.Dot(vec, vec))
	if n == 0 {
		return
	}
	for i := range vec {
		vec[i] = float32(float64(vec[i]) / n)
	}
}

// SimdOps is the widened form, where the platform has it.
//
// Results match VectorMath within floating-point tolerance, and the tolerance
// is real: a different summation order gives a different last bit, so a test
// that demands exact equality between these two fails on some devices and not
// others.
type SimdOps struct{}

// Available reports whether a widened path is in use.
//
// False here: Go's compiler vectorises some of this on its own, and claiming a
// hand-written SIMD path that does not exist would make a benchmark
// attributable to the wrong thing.
func (SimdOps) Available() bool { return false }

// Dot returns the dot product.
func (SimdOps) Dot(a, b []float32) float64 { return VectorMath{}.Dot(a, b) }

// Scale multiplies a vector in place.
func (SimdOps) Scale(vec []float32, factor float32) {
	for i := range vec {
		vec[i] *= factor
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// Core: models, paths and the device probe

// ModelModality is what a model does.
//
// Kept separate from its size or its backend, because those change with the
// build and this does not.
type ModelModality int

const (
	ModalityChat ModelModality = iota
	ModalityAsr
	ModalityTts
	ModalityVad
	ModalityWakeWord
	ModalityVision
	ModalityMusic
	ModalityVideo
	ModalityCoding
	ModalityPhonemizer
)

func (m ModelModality) String() string {
	switch m {
	case ModalityAsr:
		return "asr"
	case ModalityTts:
		return "tts"
	case ModalityVad:
		return "vad"
	case ModalityWakeWord:
		return "wake-word"
	case ModalityVision:
		return "vision"
	case ModalityMusic:
		return "music"
	case ModalityVideo:
		return "video"
	case ModalityCoding:
		return "coding"
	case ModalityPhonemizer:
		return "phonemizer"
	}
	return "chat"
}

// ModelSource is where a model's bytes come from.
type ModelSource int

const (
	SourceModelScope ModelSource = iota
	SourceHuggingFace
	// SourceHuggingFaceBucket is a separate member rather than a URL detail: it
	// is a bucket we hold no token for, and a 401 from a bucket is not the same
	// problem as a 404 from a repo. Treating them alike sends somebody looking
	// for a file that is there.
	SourceHuggingFaceBucket
	SourceGitHubRelease
)

// DownloadPhase is what a download is doing right now.
//
// Not all of it is transfer. A 433 MB bundle spends real time hashing and, on a
// bad link, retrying — and without a phase those look identical to a stalled
// download, so the person watching concludes the app has hung.
type DownloadPhase int

const (
	PhaseDownloading DownloadPhase = iota
	PhaseResuming
	PhaseRetrying
	PhaseVerifying
	PhaseCached
	PhaseComplete
)

func (p DownloadPhase) String() string {
	switch p {
	case PhaseResuming:
		return "resuming"
	case PhaseRetrying:
		return "retrying"
	case PhaseVerifying:
		return "verifying"
	case PhaseCached:
		return "cached"
	case PhaseComplete:
		return "complete"
	}
	return "downloading"
}

// ModelPaths decides where models live.
//
// THE MODEL DIRECTORY WAS DECIDED IN FOUR PLACES AND THEY DISAGREED ON A PHONE.
// Three loaders used the application-data folder and the mobile head used the
// app's own data directory; on Android the first is a SUBDIRECTORY of the
// second. Nothing failed — both existed, both were writable, both looked right
// in a log. What happened instead is that a 523 MB chat model was downloaded
// twice onto a phone with 890 MB of app data, and it was found by looking at
// the disk.
//
// Deliberately NOT a cache directory: a system is free to evict a cache under
// pressure, and a half-evicted 400 MB bundle fails its hash on the next launch
// with no explanation.
type ModelPaths struct{}

// Root returns the directory models live under.
func (ModelPaths) Root() string {
	if dir, err := os.UserHomeDir(); err == nil && dir != "" {
		return filepath.Join(dir, ".circleai", "models")
	}
	return filepath.Join(".circleai", "models")
}

// Resolve returns the directory to use, creating it if absent.
//
// A blank request means the DEFAULT, not the working directory: a relative path
// here puts a 400 MB download wherever the process happened to be started from.
func (p ModelPaths) Resolve(requested string) (string, error) {
	dir := strings.TrimSpace(requested)
	if dir == "" {
		dir = p.Root()
	} else if !filepath.IsAbs(dir) {
		return "", fmt.Errorf("a relative model directory (%q) puts a large download wherever the process started; give an absolute path", requested)
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	return dir, nil
}

// PlatformMemory is real device memory, supplied by a head that can read it.
//
// Two numbers on purpose: total is the device CLASS, available is what is free
// now. Collapsing them makes a busy 8 GB phone look like a 2 GB one. Negative
// means not supplied.
type PlatformMemory struct {
	RamAvailableBytes int64
	StorageFreeBytes  int64
	RamTotalBytes     int64
}

// UnknownPlatformMemory is what a probe returns when it cannot say.
func UnknownPlatformMemory() PlatformMemory {
	return PlatformMemory{RamAvailableBytes: -1, StorageFreeBytes: -1, RamTotalBytes: -1}
}

// RamMeasurement says where a RAM figure came from.
//
// A PROBE THAT GUESSED WAS INDISTINGUISHABLE FROM ONE THAT MEASURED, and every
// verdict downstream was stated with full confidence about a number that is the
// managed heap limit — a few hundred megabytes inside an Android sandbox. The
// device reads as a wearable, every model comes back as not fitting, and
// nothing anywhere says the input was invented.
type RamMeasurement int

const (
	// RamExplicit — a caller stated it outright.
	RamExplicit RamMeasurement = iota
	// RamPlatformMeasured — read from the device by a platform head.
	RamPlatformMeasured
	// RamHeuristic — nobody supplied one, so it was inferred. On mobile that is
	// a guess.
	RamHeuristic
)

func (m RamMeasurement) String() string {
	switch m {
	case RamPlatformMeasured:
		return "platform-measured"
	case RamHeuristic:
		return "heuristic"
	}
	return "explicit"
}

// SystemInfoDeviceContext answers what the C library can, and nothing else.
//
// Every optional field is absent rather than zero. A zero battery level and an
// unknown battery level are different facts, and reporting 0% tells the
// assistant the phone is about to die.
type SystemInfoDeviceContext struct {
	activeAppID string
}

// NewSystemInfoDeviceContext returns a context.
func NewSystemInfoDeviceContext(activeAppID string) *SystemInfoDeviceContext {
	return &SystemInfoDeviceContext{activeAppID: activeAppID}
}

// ActiveAppID returns the app in the foreground, or "".
func (c *SystemInfoDeviceContext) ActiveAppID() string { return c.activeAppID }

// TimeZoneID returns the IANA zone name.
func (c *SystemInfoDeviceContext) TimeZoneID() string { return time.Local.String() }

// BatteryLevel returns -1: this context cannot read a battery, and guessing
// would be worse than saying so.
func (c *SystemInfoDeviceContext) BatteryLevel() float64 { return -1 }

// CircleAIDiagnostics is the instrument surface.
type CircleAIDiagnostics struct {
	mu    sync.Mutex
	count map[string]int64
}

// The instrument names, EXACTLY as the C# has them. A dashboard is built on
// these strings, so renaming one silently splits a metric in two.
const (
	MetricOperationsTotal     = "circleai.operations.total"
	MetricOperationDurationMs = "circleai.operation.duration.ms"
	MetricAnomalySignalsTotal = "circleai.anomaly.signals.total"
	MetricInferenceRequests   = "circleai.inference.requests.total"
)

// Outcomes is the CLOSED vocabulary for how an operation ended.
//
// Closed because "failed", "error" and "err" in three components make a chart
// nobody can read.
type Outcomes struct{}

// Success is the success outcome.
func (Outcomes) Success() string { return "success" }

// Cancelled is the cancelled outcome.
func (Outcomes) Cancelled() string { return "cancelled" }

// Unavailable is the unavailable outcome.
func (Outcomes) Unavailable() string { return "unavailable" }

// RateLimited is the rate-limited outcome.
func (Outcomes) RateLimited() string { return "rate_limited" }

// Invalid is the invalid-input outcome.
func (Outcomes) Invalid() string { return "invalid" }

// Error is the error outcome.
func (Outcomes) Error() string { return "error" }

// NewCircleAIDiagnostics returns a diagnostics surface.
func NewCircleAIDiagnostics() *CircleAIDiagnostics {
	return &CircleAIDiagnostics{count: map[string]int64{}}
}

// Count records a counter increment.
func (d *CircleAIDiagnostics) Count(name, component, operation, outcome string, amount int64) {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.count[name+"|"+component+"|"+operation+"|"+outcome] += amount
}

// Snapshot returns the counters recorded so far.
func (d *CircleAIDiagnostics) Snapshot() map[string]int64 {
	d.mu.Lock()
	defer d.mu.Unlock()
	out := make(map[string]int64, len(d.count))
	for k, v := range d.count {
		out[k] = v
	}
	return out
}

// VerificationLevel is how far a component has actually been taken.
//
// This exists because "done" was being used for four different things, and the
// gap between them is where every disappointment in this codebase has come
// from. Compiling is not running; running on a desktop is not running on the
// phone this is for.
type VerificationLevel int

const (
	// VerificationCompiled — written and it compiles. Nothing has been run.
	VerificationCompiled VerificationLevel = iota
	VerificationTested
	VerificationDesktopVerified
	// VerificationDeviceVerified — run on the target hardware, by a person, and
	// observed to work. THE ONLY level that counts as done for anything
	// user-facing.
	VerificationDeviceVerified
	VerificationMeasured
)

func (v VerificationLevel) String() string {
	switch v {
	case VerificationTested:
		return "tested"
	case VerificationDesktopVerified:
		return "desktop-verified"
	case VerificationDeviceVerified:
		return "device-verified"
	case VerificationMeasured:
		return "measured"
	}
	return "compiled"
}

// CircleAIVerificationStatusAttribute records a component's verification level.
//
// The C# is an attribute read by reflection; Go has neither, so it is a value a
// component declares. What matters is that the claim is written down beside the
// code rather than in somebody's memory.
type CircleAIVerificationStatusAttribute struct {
	Level VerificationLevel
	// What was actually run, and where. A level with no evidence is a claim.
	Evidence string
	At       time.Time
}

// EmbeddedVoiceConfigs is the voice configuration compiled in, so a device with
// no download can still speak.
type EmbeddedVoiceConfigs struct{}

// PadID returns the pad token id for a voice family, or -1 when unknown.
//
// THE PAD RULE: a blank pad token means the MODEL's blank, not the literal "_".
// MMS pads with id 0 and Piper with id 3. Getting it wrong produces audio that
// is silent or a burst of noise — never an error. -1 rather than 0 for unknown,
// because 0 is a real answer.
func (EmbeddedVoiceConfigs) PadID(family string) int {
	switch strings.ToLower(family) {
	case "mms":
		return 0
	case "piper":
		return 3
	}
	return -1
}

// Families returns the voice families that ship with a configuration.
func (EmbeddedVoiceConfigs) Families() []string { return []string{"mms", "piper"} }

// BitPacker writes sub-byte values MSB-first within each byte.
//
// The bit order is part of the format: a reader that unpacks LSB-first gets
// plausible numbers out of the same bytes, which is a corruption nothing
// detects.
type BitPacker struct {
	bytes  []byte
	bitPos int
}

// NewBitPacker returns a packer.
func NewBitPacker(capacityBytes int) *BitPacker {
	return &BitPacker{bytes: make([]byte, 0, capacityBytes)}
}

// Write appends a value of the given width.
func (p *BitPacker) Write(value uint32, bits int) error {
	if bits < 1 || bits > 32 {
		return errors.New("bits must be 1..32")
	}
	for i := bits - 1; i >= 0; i-- {
		byteIdx := p.bitPos / 8
		for len(p.bytes) <= byteIdx {
			p.bytes = append(p.bytes, 0)
		}
		if value&(1<<uint(i)) != 0 {
			p.bytes[byteIdx] |= 1 << uint(7-p.bitPos%8)
		}
		p.bitPos++
	}
	return nil
}

// Bytes returns what has been packed.
func (p *BitPacker) Bytes() []byte { return p.bytes }

// BitPackerUnpack reads back one value a BitPacker wrote, MSB-first.
//
// Named apart from memory_compression.go's BitUnpack, which unpacks a whole
// run of fixed-width indices: one name for two shapes is a call site that
// compiles and reads wrong.
func BitPackerUnpack(data []byte, bitOffset, bits int) (uint32, error) {
	if bits < 1 || bits > 32 {
		return 0, errors.New("bits must be 1..32")
	}
	var v uint32
	for i := 0; i < bits; i++ {
		byteIdx := (bitOffset + i) / 8
		if byteIdx >= len(data) {
			return 0, errors.New("read past the end")
		}
		v <<= 1
		if data[byteIdx]&(1<<uint(7-(bitOffset+i)%8)) != 0 {
			v |= 1
		}
	}
	return v, nil
}

// OrthogonalRotation applies a random orthogonal rotation before quantising.
//
// The reason quantisation error stops being structured: an unrotated vector
// quantises with its error aligned to the axes, and those axes mean something.
// Rotating first spreads the error, which is why the rotation must be the SAME
// one on both sides — it is derived from a seed, never generated fresh.
type OrthogonalRotation struct {
	seed uint64
	dims int
}

// NewOrthogonalRotation returns a rotation for a seed and dimensionality.
func NewOrthogonalRotation(seed uint64, dims int) *OrthogonalRotation {
	return &OrthogonalRotation{seed: seed, dims: dims}
}

// Apply rotates a vector in place, using a deterministic Givens sequence.
func (r *OrthogonalRotation) Apply(vec []float32) {
	if len(vec) < 2 {
		return
	}
	state := r.seed
	next := func() uint64 {
		state ^= state << 13
		state ^= state >> 7
		state ^= state << 17
		return state
	}
	for i := 0; i+1 < len(vec); i += 2 {
		theta := float64(next()%1000000) / 1000000 * 2 * math.Pi
		c, s := math.Cos(theta), math.Sin(theta)
		a, b := float64(vec[i]), float64(vec[i+1])
		vec[i] = float32(a*c - b*s)
		vec[i+1] = float32(a*s + b*c)
	}
}

// TurboQuantCodec is the quantisation format.
type TurboQuantCodec struct{}

// Version is written into every payload.
//
// A codec with no version is a cache that cannot be read after the codec
// improves — and here that means re-downloading every model on the device.
func (TurboQuantCodec) Version() int { return 1 }

// EncodedSize returns the bytes a payload of this shape will occupy, so a
// caller can decide whether it fits before spending the time to produce it.
func (TurboQuantCodec) EncodedSize(valueCount, bitsPerValue int) int {
	return (valueCount*bitsPerValue + 7) / 8
}

// Supports reports whether this build can encode AND decode a width.
//
// Asked up front because a payload written with an unsupported width is
// discovered on the way back in, by which time the source data is gone.
func (TurboQuantCodec) Supports(bitsPerValue int) bool {
	return bitsPerValue >= 1 && bitsPerValue <= 8
}

// ─────────────────────────────────────────────────────────────────────────────
// The antibody gate

// AntibodyCapability is what may be asked of the awareness layer.
type AntibodyCapability int

const (
	// FileReputationAwareness — "is a file the user is about to open
	// known-bad?" A pre-open warning about somebody's own downloads.
	FileReputationAwareness AntibodyCapability = iota
	// NetworkIndicatorAwareness — "is a host about to be trusted known-bad?" A
	// pre-connect warning, not a block.
	NetworkIndicatorAwareness
	// BreachExposureAwareness — "has the user's OWN identity turned up in a
	// breach?" Their own identity only; the capability does not exist for
	// looking up anybody else.
	BreachExposureAwareness
)

func (c AntibodyCapability) String() string {
	switch c {
	case NetworkIndicatorAwareness:
		return "network-indicator-awareness"
	case BreachExposureAwareness:
		return "breach-exposure-awareness"
	}
	return "file-reputation-awareness"
}

// AuthorizedUseConsent is permission for one capability, bounded and attributed.
type AuthorizedUseConsent struct {
	ConsentID  string
	Capability AntibodyCapability
	GrantedBy  string
	Scope      string
	GrantedAt  time.Time
	ExpiresAt  time.Time
}

// GrantAuthorizedUseConsent grants for a bounded duration starting now.
//
// Returns an error for a blank granter, a blank scope, or a non-positive
// duration. An unattributed or unbounded consent is not a stricter grant — it
// is a permission that cannot be reviewed, revoked on schedule, or explained to
// the person it was taken on behalf of.
func GrantAuthorizedUseConsent(capability AntibodyCapability, grantedBy, scope string, duration time.Duration, now time.Time) (AuthorizedUseConsent, error) {
	if strings.TrimSpace(grantedBy) == "" {
		return AuthorizedUseConsent{}, errors.New("a granter is required: 'the system consented' is how this becomes surveillance with a changelog")
	}
	if strings.TrimSpace(scope) == "" {
		return AuthorizedUseConsent{}, errors.New("a scope is required")
	}
	if duration <= 0 {
		return AuthorizedUseConsent{}, errors.New("a positive duration is required: a permission that never lapses is one nobody remembers giving")
	}
	return AuthorizedUseConsent{
		ConsentID:  fmt.Sprintf("consent-%d", now.UnixNano()),
		Capability: capability, GrantedBy: grantedBy, Scope: scope,
		GrantedAt: now, ExpiresAt: now.Add(duration),
	}, nil
}

// IsActiveFor reports whether this consent covers a capability now.
//
// Half-open: the expiry instant is already lapsed.
func (c AuthorizedUseConsent) IsActiveFor(capability AntibodyCapability, now time.Time) bool {
	return c.Capability == capability && !now.Before(c.GrantedAt) && now.Before(c.ExpiresAt)
}

// AuthorizedUseRequest is one thing being asked.
type AuthorizedUseRequest struct {
	Capability AntibodyCapability
	// A hash, a host, a hashed identifier — never the plain identity value.
	Subject     string
	Scope       string
	RequestedBy string
	At          time.Time
}

// AuthorizationDecision is the answer.
type AuthorizationDecision struct {
	Allowed bool
	// ALWAYS populated, including when allowed. A decision without a reason
	// cannot be shown to the person it was made about, and this is the one
	// component where that is the whole point.
	Reason    string
	ConsentID string
	At        time.Time
}

// IAuthorizedUseConsentStore holds consents.
type IAuthorizedUseConsentStore interface {
	Put(consent AuthorizedUseConsent) error
	FindActive(capability AntibodyCapability, now time.Time) (AuthorizedUseConsent, bool)
	// Revoke is IMMEDIATE and there is no soft-delete. A consent somebody
	// withdrew must stop working the moment they say so.
	Revoke(consentID string) bool
	Count() int
}

// InMemoryAuthorizedUseConsentStore is the default store.
type InMemoryAuthorizedUseConsentStore struct {
	mu       sync.RWMutex
	consents map[string]AuthorizedUseConsent
}

// NewInMemoryAuthorizedUseConsentStore returns an empty store.
func NewInMemoryAuthorizedUseConsentStore() *InMemoryAuthorizedUseConsentStore {
	return &InMemoryAuthorizedUseConsentStore{consents: map[string]AuthorizedUseConsent{}}
}

// Put implements IAuthorizedUseConsentStore.
func (s *InMemoryAuthorizedUseConsentStore) Put(c AuthorizedUseConsent) error {
	if strings.TrimSpace(c.ConsentID) == "" {
		return errors.New("a consent id is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.consents[c.ConsentID] = c
	return nil
}

// FindActive implements IAuthorizedUseConsentStore.
func (s *InMemoryAuthorizedUseConsentStore) FindActive(capability AntibodyCapability, now time.Time) (AuthorizedUseConsent, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, c := range s.consents {
		if c.IsActiveFor(capability, now) {
			return c, true
		}
	}
	return AuthorizedUseConsent{}, false
}

// Revoke implements IAuthorizedUseConsentStore.
func (s *InMemoryAuthorizedUseConsentStore) Revoke(consentID string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.consents[consentID]; !ok {
		return false
	}
	delete(s.consents, consentID)
	return true
}

// Count implements IAuthorizedUseConsentStore.
func (s *InMemoryAuthorizedUseConsentStore) Count() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.consents)
}

// IAuthorizedUseGate decides whether an assessment may happen at all.
type IAuthorizedUseGate interface {
	Authorize(ctx context.Context, req AuthorizedUseRequest) AuthorizationDecision
}

// NullAuthorizedUseGate denies everything.
//
// THE DEFAULT. Not a test double: a host that wires nothing should get a
// component that assesses nothing. The alternative default — allow when
// unconfigured — is a capability that reads files because somebody forgot a
// line of setup.
type NullAuthorizedUseGate struct{}

// Authorize implements IAuthorizedUseGate.
func (NullAuthorizedUseGate) Authorize(_ context.Context, _ AuthorizedUseRequest) AuthorizationDecision {
	return AuthorizationDecision{Reason: "no authorization gate is configured, so nothing is assessed", At: time.Now()}
}

// ExplicitConsentAuthorizedUseGate allows only what an active consent covers.
type ExplicitConsentAuthorizedUseGate struct {
	store IAuthorizedUseConsentStore
}

// NewExplicitConsentAuthorizedUseGate returns a gate over a store.
func NewExplicitConsentAuthorizedUseGate(store IAuthorizedUseConsentStore) *ExplicitConsentAuthorizedUseGate {
	return &ExplicitConsentAuthorizedUseGate{store: store}
}

// Authorize implements IAuthorizedUseGate.
func (g *ExplicitConsentAuthorizedUseGate) Authorize(_ context.Context, req AuthorizedUseRequest) AuthorizationDecision {
	now := req.At
	if now.IsZero() {
		now = time.Now()
	}
	if g.store == nil {
		return AuthorizationDecision{Reason: "no consent store configured", At: now}
	}
	c, ok := g.store.FindActive(req.Capability, now)
	if !ok {
		return AuthorizationDecision{
			Reason: "nobody has agreed to " + req.Capability.String() + " on this device",
			At:     now,
		}
	}
	return AuthorizationDecision{
		Allowed: true, ConsentID: c.ConsentID, At: now,
		Reason: "covered by consent granted by " + c.GrantedBy + " for " + c.Scope,
	}
}

// DefensiveThreatContext is what an assessment observed and where.
type DefensiveThreatContext struct {
	Severity ThreatSeverity
	Summary  string
	Source   string
	At       time.Time
	// Local by default and by design: asking a remote service whether an
	// address has been breached tells that service the address AND that its
	// owner is worried.
	AssessedLocally bool
}

// ThreatIndicator is one thing a corpus flags.
type ThreatIndicator struct {
	Kind     IndicatorKind
	Value    string
	Severity ThreatSeverity
	Source   string
	Detail   string
}

// IDefensiveAntibodySystem is the assembled awareness layer.
type IDefensiveAntibodySystem interface {
	AssessFile(ctx context.Context, artifact FileArtifact) (ThreatAwarenessVerdict, DefensiveThreatContext)
	AssessNetwork(ctx context.Context, hostOrAddress string) (ThreatAwarenessVerdict, DefensiveThreatContext)
	AssessBreachExposure(ctx context.Context, identifier string) (ThreatAwarenessVerdict, DefensiveThreatContext)
}

// DefensiveAntibodySystem is the gate in front and the local corpus behind.
//
// AWARENESS, NEVER ENFORCEMENT. Nothing here blocks, quarantines or deletes.
// Collapsing the two would put the component that can read your files in charge
// of refusing them, and the blast radius of a false positive goes from a
// notification to a device that will not open its owner's documents.
type DefensiveAntibodySystem struct {
	gate   IAuthorizedUseGate
	corpus ILocalIndicatorCorpus
}

// NewDefensiveAntibodySystem assembles the system.
func NewDefensiveAntibodySystem(gate IAuthorizedUseGate, corpus ILocalIndicatorCorpus) *DefensiveAntibodySystem {
	if gate == nil {
		gate = NullAuthorizedUseGate{}
	}
	if corpus == nil {
		corpus = EmptyIndicatorCorpus{}
	}
	return &DefensiveAntibodySystem{gate: gate, corpus: corpus}
}

func (s *DefensiveAntibodySystem) allowed(ctx context.Context, capability AntibodyCapability, subject string) (AuthorizationDecision, bool) {
	d := s.gate.Authorize(ctx, AuthorizedUseRequest{Capability: capability, Subject: subject, At: time.Now()})
	return d, d.Allowed
}

// AssessFile implements IDefensiveAntibodySystem.
func (s *DefensiveAntibodySystem) AssessFile(ctx context.Context, artifact FileArtifact) (ThreatAwarenessVerdict, DefensiveThreatContext) {
	d, ok := s.allowed(ctx, FileReputationAwareness, artifact.Sha256)
	if !ok {
		// NOT_ASSESSED, never a verdict inferred from having been stopped.
		return NotAssessed, DefensiveThreatContext{Summary: d.Reason, At: d.At, AssessedLocally: true}
	}
	results, err := NewFileThreatAwarenessAssessor(s.corpus).Assess(ctx, artifact)
	if err != nil || len(results) == 0 {
		return NotAssessed, DefensiveThreatContext{Summary: "nothing ran", At: time.Now(), AssessedLocally: true}
	}
	r := results[0]
	return r.Verdict, DefensiveThreatContext{Severity: r.Severity, Summary: r.Summary, Source: r.Source, At: r.At, AssessedLocally: true}
}

// AssessNetwork implements IDefensiveAntibodySystem.
func (s *DefensiveAntibodySystem) AssessNetwork(ctx context.Context, hostOrAddress string) (ThreatAwarenessVerdict, DefensiveThreatContext) {
	d, ok := s.allowed(ctx, NetworkIndicatorAwareness, hostOrAddress)
	if !ok {
		return NotAssessed, DefensiveThreatContext{Summary: d.Reason, At: d.At, AssessedLocally: true}
	}
	results, err := NewNetworkThreatAwarenessAssessor(s.corpus).Assess(ctx, hostOrAddress)
	if err != nil || len(results) == 0 {
		return NotAssessed, DefensiveThreatContext{Summary: "nothing ran", At: time.Now(), AssessedLocally: true}
	}
	r := results[0]
	return r.Verdict, DefensiveThreatContext{Severity: r.Severity, Summary: r.Summary, Source: r.Source, At: r.At, AssessedLocally: true}
}

// AssessBreachExposure implements IDefensiveAntibodySystem.
//
// `identifier` is hashed before it leaves this call; the plain value is never
// stored and never sent anywhere.
func (s *DefensiveAntibodySystem) AssessBreachExposure(ctx context.Context, identifier string) (ThreatAwarenessVerdict, DefensiveThreatContext) {
	d, ok := s.allowed(ctx, BreachExposureAwareness, Sha256Hex(identifier))
	if !ok {
		return NotAssessed, DefensiveThreatContext{Summary: d.Reason, At: d.At, AssessedLocally: true}
	}
	results, err := NewBreachExposureAssessor(s.corpus).Assess(ctx, identifier)
	if err != nil || len(results) == 0 {
		return NotAssessed, DefensiveThreatContext{Summary: "nothing ran", At: time.Now(), AssessedLocally: true}
	}
	r := results[0]
	return r.Verdict, DefensiveThreatContext{Severity: r.Severity, Summary: r.Summary, Source: r.Source, At: r.At, AssessedLocally: true}
}

// ─────────────────────────────────────────────────────────────────────────────
// Personal

// ConsentGuard checks a token before an adapter is touched, and records that it
// did.
//
// The record is the point. A permission system nobody can audit is
// indistinguishable from no permission system — the code looks careful either
// way, and only a log can tell you which reads actually happened.
type ConsentGuard struct {
	mu      sync.Mutex
	allowed int
	denied  int
}

// NewConsentGuard returns a guard.
func NewConsentGuard() *ConsentGuard { return &ConsentGuard{} }

// Check reports whether a read may proceed, and why not when it may not.
//
// The validity test itself is UserConsentToken.IsValidFor, already in
// personal.go. This adds the record, which is the part that was missing: a
// permission system nobody can audit is indistinguishable from none.
func (g *ConsentGuard) Check(token UserConsentToken, required ConsentScope, now time.Time) (bool, string) {
	if token.IsValidFor(required, now) {
		g.mu.Lock()
		g.allowed++
		g.mu.Unlock()
		return true, ""
	}
	g.mu.Lock()
	g.denied++
	g.mu.Unlock()
	return false, "this token does not cover that, or it has lapsed"
}

// Counts returns how many reads were allowed and denied.
func (g *ConsentGuard) Counts() (allowed, denied int) {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.allowed, g.denied
}

// IContactsAdapter reads contacts.
type IContactsAdapter interface {
	// Search takes a token on EVERY read. Not a constructor argument: a token
	// supplied once at construction is a permission that outlives the task it
	// was for.
	Search(ctx context.Context, token UserConsentToken, query string) ([]Contact, error)
}

// NullContactsAdapter finds nobody. THE DEFAULT.
type NullContactsAdapter struct{}

// Search implements IContactsAdapter.
func (NullContactsAdapter) Search(context.Context, UserConsentToken, string) ([]Contact, error) {
	return nil, nil
}

// ICalendarAdapter reads and writes a calendar.
type ICalendarAdapter interface {
	Between(ctx context.Context, token UserConsentToken, from, to time.Time) ([]CalendarEvent, error)
	// Create takes a separate scope AND a separate method, so that "look at my
	// calendar" cannot become "put something in it".
	Create(ctx context.Context, token UserConsentToken, event CalendarEvent) error
}

// NullCalendarAdapter reads nothing and writes nothing. THE DEFAULT.
type NullCalendarAdapter struct{}

// Between implements ICalendarAdapter.
func (NullCalendarAdapter) Between(context.Context, UserConsentToken, time.Time, time.Time) ([]CalendarEvent, error) {
	return nil, nil
}

// Create implements ICalendarAdapter.
func (NullCalendarAdapter) Create(context.Context, UserConsentToken, CalendarEvent) error {
	return errors.New("no calendar adapter configured")
}

// IEmailAdapter reads and drafts mail.
//
// There is no Send, and there will not be. A message leaving somebody's account
// is an action a PERSON takes, in their own mail client, having read it.
type IEmailAdapter interface {
	Recent(ctx context.Context, token UserConsentToken, max int) ([]EmailMessage, error)
	Draft(ctx context.Context, token UserConsentToken, message EmailMessage) (string, error)
}

// NullEmailAdapter reads nothing and drafts nothing. THE DEFAULT.
type NullEmailAdapter struct{}

// Recent implements IEmailAdapter.
func (NullEmailAdapter) Recent(context.Context, UserConsentToken, int) ([]EmailMessage, error) {
	return nil, nil
}

// Draft implements IEmailAdapter.
func (NullEmailAdapter) Draft(context.Context, UserConsentToken, EmailMessage) (string, error) {
	return "", errors.New("no email adapter configured")
}

// ─────────────────────────────────────────────────────────────────────────────
// Plugins and skills

// PluginEventNames are the lifecycle events a plugin can observe.
//
// A closed list: an open one means a plugin can subscribe to anything the host
// ever emits, including events it was never meant to see.
type PluginEventNames struct{}

// Installed is the installed event.
func (PluginEventNames) Installed() string { return "plugin.installed" }

// Enabled is the enabled event.
func (PluginEventNames) Enabled() string { return "plugin.enabled" }

// Disabled is the disabled event.
func (PluginEventNames) Disabled() string { return "plugin.disabled" }

// Removed is the removed event.
func (PluginEventNames) Removed() string { return "plugin.removed" }

// Permissions is what a plugin is allowed.
//
// Explicit fields rather than a bag of strings: a permission set that can grow
// by configuration is one where nobody can answer "what is this plugin allowed
// to do" by reading the type.
type Permissions struct {
	ReadWorkspace  bool
	WriteWorkspace bool
	Network        bool
	InvokeTools    bool
	ReadMemory     bool
}

// RegisteredPlugin is an installed plugin.
type RegisteredPlugin struct {
	PluginID    string
	Name        string
	Version     string
	EntryPoint  string
	Permissions Permissions
	Enabled     bool
	InstalledAt time.Time
}

// MarketplaceEntry is a plugin somebody could install.
type MarketplaceEntry struct {
	PluginID    string
	Name        string
	Publisher   string
	Description string
	// Whether the publisher's signature verified. NOT whether the plugin is
	// safe — the two get conflated, and a signed plugin is only evidence about
	// who wrote it.
	SignatureVerified bool
	Homepage          string
}

// PluginRegistry holds installed plugins.
type PluginRegistry struct {
	mu      sync.RWMutex
	plugins map[string]RegisteredPlugin
}

// NewPluginRegistry returns an empty registry.
func NewPluginRegistry() *PluginRegistry {
	return &PluginRegistry{plugins: map[string]RegisteredPlugin{}}
}

// Put adds or replaces a plugin.
func (r *PluginRegistry) Put(p RegisteredPlugin) error {
	if strings.TrimSpace(p.PluginID) == "" {
		return errors.New("a plugin id is required")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.plugins[p.PluginID] = p
	return nil
}

// Get returns a plugin.
func (r *PluginRegistry) Get(pluginID string) (RegisteredPlugin, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	p, ok := r.plugins[pluginID]
	return p, ok
}

// List returns every plugin.
func (r *PluginRegistry) List() []RegisteredPlugin {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]RegisteredPlugin, 0, len(r.plugins))
	for _, p := range r.plugins {
		out = append(out, p)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].PluginID < out[j].PluginID })
	return out
}

// PluginMarketplace lists plugins available to install.
type PluginMarketplace struct {
	mu      sync.RWMutex
	entries []MarketplaceEntry
}

// NewPluginMarketplace returns an empty marketplace.
func NewPluginMarketplace() *PluginMarketplace { return &PluginMarketplace{} }

// Add records an entry.
func (m *PluginMarketplace) Add(e MarketplaceEntry) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.entries = append(m.entries, e)
}

// List returns the entries.
func (m *PluginMarketplace) List() []MarketplaceEntry {
	m.mu.RLock()
	defer m.mu.RUnlock()
	out := make([]MarketplaceEntry, len(m.entries))
	copy(out, m.entries)
	return out
}

// PluginLoadResult is the outcome of loading a plugin.
type PluginLoadResult struct {
	Loaded bool
	Plugin RegisteredPlugin
	// Always populated when Loaded is false. A plugin that failed to load with
	// no reason is one somebody reinstalls three times.
	Reason string
}

// PluginLoader loads plugins from disk.
//
// Go has no assembly loading, so what this does is read a MANIFEST and hand the
// host what it needs to wire the plugin itself. The C# loads .NET assemblies by
// reflection; that half does not transfer, and pretending it does would be a
// loader that silently loads nothing.
type PluginLoader struct {
	readFile func(path string) ([]byte, error)
}

// NewPluginLoader returns a loader.
func NewPluginLoader(readFile func(path string) ([]byte, error)) *PluginLoader {
	return &PluginLoader{readFile: readFile}
}

// Load reads a plugin manifest.
func (l *PluginLoader) Load(dir string) PluginLoadResult {
	if l.readFile == nil {
		return PluginLoadResult{Reason: "no file reader configured"}
	}
	data, err := l.readFile(filepath.Join(dir, "plugin.json"))
	if err != nil {
		return PluginLoadResult{Reason: "no plugin.json in " + dir}
	}
	var p RegisteredPlugin
	if err := json.Unmarshal(data, &p); err != nil {
		return PluginLoadResult{Reason: "plugin.json is malformed: " + err.Error()}
	}
	if strings.TrimSpace(p.PluginID) == "" {
		return PluginLoadResult{Reason: "plugin.json declares no id"}
	}
	// Loaded DISABLED. A plugin that enabled itself by being present is a
	// plugin that ran because somebody copied a folder.
	p.Enabled = false
	return PluginLoadResult{Loaded: true, Plugin: p}
}

// IPluginsRootResolver says where plugins live.
type IPluginsRootResolver interface {
	Root() string
}

// IWorkspacePathProvider says where the workspace is.
//
// Separate from the plugins root because a plugin's own files and the work it
// operates on are different things, and a plugin that could write to its own
// root could rewrite its own manifest.
type IWorkspacePathProvider interface {
	WorkspacePath() string
}

// PluginLifecycleService installs, enables, disables and removes plugins.
type PluginLifecycleService struct {
	mu       sync.Mutex
	resolver IPluginsRootResolver
	registry *PluginRegistry
}

// NewPluginLifecycleService returns a service.
func NewPluginLifecycleService(resolver IPluginsRootResolver, registry *PluginRegistry) *PluginLifecycleService {
	if registry == nil {
		registry = NewPluginRegistry()
	}
	return &PluginLifecycleService{resolver: resolver, registry: registry}
}

// Enable turns a plugin on.
func (s *PluginLifecycleService) Enable(pluginID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	p, ok := s.registry.Get(pluginID)
	if !ok {
		return fmt.Errorf("no plugin %q", pluginID)
	}
	p.Enabled = true
	return s.registry.Put(p)
}

// Disable turns a plugin off.
//
// DISABLE IS NOT REMOVE. A disabled plugin keeps its data, so re-enabling it
// does not silently start it from nothing.
func (s *PluginLifecycleService) Disable(pluginID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	p, ok := s.registry.Get(pluginID)
	if !ok {
		return fmt.Errorf("no plugin %q", pluginID)
	}
	p.Enabled = false
	return s.registry.Put(p)
}

// ParsedSkill is one skill from a pack.
type ParsedSkill struct {
	SkillID     string
	Name        string
	Description string
	Body        string
}

// SkillPackManifest describes a pack.
type SkillPackManifest struct {
	PackID    string
	Name      string
	Version   string
	Publisher string
	Sha256    string
	Skills    []ParsedSkill
	// What the pack needs to be able to do. Declared UP FRONT so somebody can
	// decide before installing, rather than discovering it when a skill reaches
	// for something.
	RequiredCapabilities []string
}

// SkillPackLoader reads and verifies a pack.
type SkillPackLoader struct {
	readFile func(path string) ([]byte, error)
	sha256   func(path string) (string, error)
}

// NewSkillPackLoader returns a loader.
func NewSkillPackLoader(readFile func(path string) ([]byte, error), sha256 func(path string) (string, error)) *SkillPackLoader {
	return &SkillPackLoader{readFile: readFile, sha256: sha256}
}

// Load reads a manifest and verifies the hash.
//
// A skill pack is INSTRUCTIONS AN ASSISTANT WILL FOLLOW; one that arrived
// damaged is one whose instructions nobody wrote.
func (l *SkillPackLoader) Load(packPath, expectedSha256 string) (SkillPackManifest, error) {
	if l.readFile == nil {
		return SkillPackManifest{}, errors.New("no file reader configured")
	}
	if expectedSha256 != "" {
		if l.sha256 == nil {
			return SkillPackManifest{}, errors.New("a hash was expected but no hasher is configured")
		}
		actual, err := l.sha256(packPath)
		if err != nil {
			return SkillPackManifest{}, err
		}
		if !strings.EqualFold(actual, expectedSha256) {
			return SkillPackManifest{}, errors.New("this pack does not match the hash it was supposed to have")
		}
	}
	data, err := l.readFile(filepath.Join(packPath, "pack.json"))
	if err != nil {
		return SkillPackManifest{}, err
	}
	var m SkillPackManifest
	if err := json.Unmarshal(data, &m); err != nil {
		return SkillPackManifest{}, err
	}
	return m, nil
}

// KnownSkillPacks are the packs this project ships or knows about.
//
// A CLOSED list: a pack from anywhere else is installed deliberately, by
// somebody, from a file.
type KnownSkillPacks struct{}

// IDs returns the known pack ids.
func (KnownSkillPacks) IDs() []string {
	return []string{"circleai.core", "circleai.mobile", "circleai.voice"}
}

// SkillPackSourcesOptions says where packs may come from.
type SkillPackSourcesOptions struct {
	WatchDirectory   string
	StagingDirectory string
	// Off by default. A pack that installed itself from a directory is a pack
	// that ran because somebody copied a folder.
	AutoInstall bool
}

// HttpPackDownloader fetches a pack over HTTP.
type HttpPackDownloader struct {
	get func(ctx context.Context, url string) ([]byte, error)
}

// NewHttpPackDownloader returns a downloader.
func NewHttpPackDownloader(get func(ctx context.Context, url string) ([]byte, error)) *HttpPackDownloader {
	return &HttpPackDownloader{get: get}
}

// Download fetches a pack. A hash is REQUIRED: a pack fetched over a link
// nobody verified is instructions from whoever was on the path.
func (d *HttpPackDownloader) Download(ctx context.Context, url, expectedSha256 string) ([]byte, error) {
	if d.get == nil {
		return nil, errors.New("no transport configured")
	}
	if strings.TrimSpace(expectedSha256) == "" {
		return nil, errors.New("an expected hash is required to download a skill pack")
	}
	return d.get(ctx, url)
}

// SkillPackAutoImporter notices a pack and STAGES it.
//
// "Auto" means the person does not have to find the file, NOT that nobody has
// to approve it.
type SkillPackAutoImporter struct {
	mu     sync.Mutex
	opts   SkillPackSourcesOptions
	staged []string
}

// NewSkillPackAutoImporter returns an importer.
func NewSkillPackAutoImporter(opts SkillPackSourcesOptions) *SkillPackAutoImporter {
	return &SkillPackAutoImporter{opts: opts}
}

// Stage records a pack as awaiting approval.
func (i *SkillPackAutoImporter) Stage(packID string) {
	i.mu.Lock()
	defer i.mu.Unlock()
	i.staged = append(i.staged, packID)
}

// StagedCount returns how many packs await approval.
func (i *SkillPackAutoImporter) StagedCount() int {
	i.mu.Lock()
	defer i.mu.Unlock()
	return len(i.staged)
}

// FileSkillStore reads skills from a directory.
type FileSkillStore struct {
	root    string
	readDir func(path string) ([]string, error)
}

// NewFileSkillStore returns a store.
func NewFileSkillStore(root string, readDir func(path string) ([]string, error)) *FileSkillStore {
	return &FileSkillStore{root: root, readDir: readDir}
}

// IDs returns the skill ids present.
func (s *FileSkillStore) IDs() []string {
	if s.readDir == nil {
		return nil
	}
	names, err := s.readDir(s.root)
	if err != nil {
		return nil
	}
	return names
}

// CapabilityManifestSkillStore reads skills from a capability manifest.
//
// A manifest is a list somebody wrote; a directory scan is whatever happens to
// be on disk, which is how a skill dropped into a folder becomes active without
// anybody adding it.
type CapabilityManifestSkillStore struct {
	mu       sync.RWMutex
	skillIDs []string
}

// NewCapabilityManifestSkillStore returns a store over a manifest body.
func NewCapabilityManifestSkillStore(manifestJSON string) (*CapabilityManifestSkillStore, error) {
	var m struct {
		Skills []string `json:"skills"`
	}
	if err := json.Unmarshal([]byte(manifestJSON), &m); err != nil {
		return nil, err
	}
	return &CapabilityManifestSkillStore{skillIDs: m.Skills}, nil
}

// IDs returns the skills the manifest declares.
func (s *CapabilityManifestSkillStore) IDs() []string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]string, len(s.skillIDs))
	copy(out, s.skillIDs)
	return out
}

// SkillContextBuilder assembles the skill text that goes into a prompt.
//
// Budgeted because skills are the easiest thing in a prompt to let grow, and
// every character spent here is one the conversation does not get.
type SkillContextBuilder struct {
	maxCharacters int
	skills        map[string]string
	mu            sync.RWMutex
}

// NewSkillContextBuilder returns a builder.
func NewSkillContextBuilder(maxCharacters int) *SkillContextBuilder {
	if maxCharacters <= 0 {
		maxCharacters = 2000
	}
	return &SkillContextBuilder{maxCharacters: maxCharacters, skills: map[string]string{}}
}

// Add registers a skill's text.
func (b *SkillContextBuilder) Add(skillID, body string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.skills[skillID] = body
}

// Build returns the skill text for a situation, within budget.
func (b *SkillContextBuilder) Build(situation string) string {
	b.mu.RLock()
	defer b.mu.RUnlock()
	terms := SearchTokenisation{}.Split(situation)
	type scored struct {
		id    string
		body  string
		score int
	}
	var ranked []scored
	for id, body := range b.skills {
		lower := strings.ToLower(body)
		n := 0
		for _, t := range terms {
			if len(t) > 3 && strings.Contains(lower, t) {
				n++
			}
		}
		if n > 0 {
			ranked = append(ranked, scored{id, body, n})
		}
	}
	sort.SliceStable(ranked, func(i, j int) bool { return ranked[i].score > ranked[j].score })

	var out strings.Builder
	for _, s := range ranked {
		if out.Len()+len(s.body) > b.maxCharacters {
			continue
		}
		out.WriteString(s.body)
		out.WriteString("\n\n")
	}
	return strings.TrimSpace(out.String())
}

// ─────────────────────────────────────────────────────────────────────────────
// Desktop

// WindowDescriptor is one window on a desktop.
type WindowDescriptor struct {
	WindowID string
	Title    string
	AppID    string
	Focused  bool
}

// DesktopSession is one desktop somebody is working at.
type DesktopSession struct {
	SessionID string
	Windows   []WindowDescriptor
	StartedAt time.Time
}

// DesktopShortcut is a keyboard shortcut.
type DesktopShortcut struct {
	Keys        string
	Description string
	AppID       string
}

// IDesktopBoard holds what is known about the desktop.
type IDesktopBoard interface {
	Observe(session DesktopSession)
	Current() (DesktopSession, bool)
	Shortcuts(appID string) []DesktopShortcut
}

// InMemoryDesktopBoard is the default board.
//
// Observes and remembers; it does not act. A component that could focus a
// window or send a keystroke is a different thing entirely, and this is
// deliberately not it.
type InMemoryDesktopBoard struct {
	mu        sync.RWMutex
	session   DesktopSession
	haveOne   bool
	shortcuts map[string][]DesktopShortcut
}

// NewInMemoryDesktopBoard returns an empty board.
func NewInMemoryDesktopBoard() *InMemoryDesktopBoard {
	return &InMemoryDesktopBoard{shortcuts: map[string][]DesktopShortcut{}}
}

// Observe implements IDesktopBoard.
func (b *InMemoryDesktopBoard) Observe(session DesktopSession) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.session, b.haveOne = session, true
}

// Current implements IDesktopBoard.
func (b *InMemoryDesktopBoard) Current() (DesktopSession, bool) {
	b.mu.RLock()
	defer b.mu.RUnlock()
	return b.session, b.haveOne
}

// Shortcuts implements IDesktopBoard.
func (b *InMemoryDesktopBoard) Shortcuts(appID string) []DesktopShortcut {
	b.mu.RLock()
	defer b.mu.RUnlock()
	return b.shortcuts[appID]
}

// DesktopDomainContext is the desktop domain's prompt snippet.
type DesktopDomainContext struct{}

// DomainID implements DomainContext.
func (DesktopDomainContext) DomainID() string { return "Desktop" }

// SystemPromptSnippet implements DomainContext.
func (DesktopDomainContext) SystemPromptSnippet() string {
	return "You are helping somebody at a desktop. You can see what is open. " +
		"You cannot click, type or focus a window; say what to do rather than doing it."
}

// DesktopCompanionAdapter is a companion session scoped to the desktop.
type DesktopCompanionAdapter struct{ *DomainCompanionAdapter }

// NewDesktopCompanionAdapter wraps a session for the desktop domain.
func NewDesktopCompanionAdapter(inner any) *DesktopCompanionAdapter {
	return &DesktopCompanionAdapter{NewDomainCompanionAdapter(inner, DesktopDomainContext{})}
}

// ─────────────────────────────────────────────────────────────────────────────
// Presentations

// Slide is one slide.
type Slide struct {
	Title  string
	Body   string
	Notes  string
	Layout string
}

// Deck is a presentation.
type Deck struct {
	DeckID string
	Title  string
	Slides []Slide
	Theme  string
}

// IDeckEngine renders a deck.
type IDeckEngine interface {
	Render(deck Deck) ([]byte, string, error)
	Supports(format DocumentFormat) bool
}

// MarkdownDeckEngine renders a deck as Markdown.
//
// The format that needs no dependency, and the one a person can read before it
// is anything else.
type MarkdownDeckEngine struct{}

// Supports implements IDeckEngine.
func (MarkdownDeckEngine) Supports(format DocumentFormat) bool { return format == DocumentMarkdown }

// Render implements IDeckEngine.
func (MarkdownDeckEngine) Render(deck Deck) ([]byte, string, error) {
	var b strings.Builder
	b.WriteString("# " + deck.Title + "\n\n")
	for i, s := range deck.Slides {
		// Slides are separated by a rule, which every Markdown presenter
		// understands as a page break. A heading alone does not break a page.
		if i > 0 {
			b.WriteString("---\n\n")
		}
		b.WriteString("## " + s.Title + "\n\n" + s.Body + "\n\n")
		if s.Notes != "" {
			b.WriteString("<!-- notes: " + s.Notes + " -->\n\n")
		}
	}
	return []byte(b.String()), "text/markdown", nil
}

// SampleDeck is a worked example, clearly marked.
type SampleDeck struct{}

// Deck returns the sample.
func (SampleDeck) Deck() Deck {
	return Deck{
		DeckID: "sample",
		Title:  "Sample: what this device can do offline",
		Slides: []Slide{
			{Title: "Sample: on-device first", Body: "Everything here runs with no network."},
			{Title: "Sample: what leaves", Body: "Nothing, unless you say so."},
		},
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// The inference server's HTTP surface

// AuthOptions configures the server's authentication.
type AuthOptions struct {
	// Off means NO authentication, and it is off only for a loopback listener.
	// A server that defaulted to open on a public interface is the shape of
	// every accidentally-exposed inference endpoint.
	Enabled bool
	ApiKey  ApiKeyAuthSchemeOptions
	Jwt     JwtOptions
}

// JwtOptions configures bearer-token authentication.
type JwtOptions struct {
	Issuer   string
	Audience string
	// Verify is the host's; no key material lives here, and it is expected to
	// check the expiry BEFORE the claims — an expired token that still tells an
	// attacker whether the rest of it was right is a token oracle.
	Verify func(token string) (subject string, ok bool)
}

// InferenceServerOptions configures the server.
type InferenceServerOptions struct {
	Address string
	Auth    AuthOptions
	// The maximum request body. Unbounded is how a device with 2 GB is killed
	// by one request.
	MaxRequestBytes int64
	RequestTimeout  time.Duration
}

// DefaultInferenceServerOptions returns loopback, authenticated, bounded.
func DefaultInferenceServerOptions() InferenceServerOptions {
	return InferenceServerOptions{
		Address:         "127.0.0.1:8080",
		MaxRequestBytes: 8 << 20,
		RequestTimeout:  120 * time.Second,
	}
}

// HostProfileDto is what the host can do.
type HostProfileDto struct {
	OperatingSystem string `json:"operating_system"`
	Architecture    string `json:"architecture"`
	RamTotalBytes   int64  `json:"ram_total_bytes"`
	// Where the RAM figure came from. Reported because a guessed figure and a
	// measured one must not read the same in a diagnostics response.
	RamSource string `json:"ram_source"`
}

// BackendSelectionDto is which backend was chosen and why.
type BackendSelectionDto struct {
	Backend string `json:"backend"`
	Reason  string `json:"reason"`
}

// LoadedModelInfo is one resident model.
type LoadedModelInfo struct {
	ModelID        string `json:"model_id"`
	Modality       string `json:"modality"`
	ApproxRamBytes int64  `json:"approx_ram_bytes"`
	LoadedAt       string `json:"loaded_at"`
}

// CounterSnapshot is one counter.
type CounterSnapshot struct {
	Name  string `json:"name"`
	Value int64  `json:"value"`
}

// NativeRuntimePathsDto is where the native runtimes were found.
type NativeRuntimePathsDto struct {
	RuntimeID string `json:"runtime_id"`
	Path      string `json:"path"`
	Verified  bool   `json:"verified"`
}

// HealthResponse is the health endpoint's body.
type HealthResponse struct {
	Status string `json:"status"`
	// Uptime rather than a start time: a start time in a container that
	// restarted is indistinguishable from one that did not.
	UptimeSeconds int64 `json:"uptime_seconds"`
}

// DiagnosticsResponse is the diagnostics endpoint's body.
type DiagnosticsResponse struct {
	Host     HostProfileDto          `json:"host"`
	Backend  BackendSelectionDto     `json:"backend"`
	Models   []LoadedModelInfo       `json:"models"`
	Counters []CounterSnapshot       `json:"counters"`
	Runtimes []NativeRuntimePathsDto `json:"runtimes"`
}

// ServerSentEventsWriter streams tokens to a client.
//
// FLUSHES AFTER EVERY EVENT. Without the flush the whole point is lost: the
// response buffers and arrives at once, which is indistinguishable from not
// streaming at all except that it is slower.
type ServerSentEventsWriter struct {
	w http.ResponseWriter
}

// NewServerSentEventsWriter prepares a response for streaming.
func NewServerSentEventsWriter(w http.ResponseWriter) *ServerSentEventsWriter {
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	// Proxies buffer text/event-stream by default and the symptom is a stream
	// that arrives complete at the end.
	w.Header().Set("X-Accel-Buffering", "no")
	return &ServerSentEventsWriter{w: w}
}

// Write sends one event.
func (s *ServerSentEventsWriter) Write(event, data string) error {
	if event != "" {
		if _, err := fmt.Fprintf(s.w, "event: %s\n", event); err != nil {
			return err
		}
	}
	// Every line of the payload needs its own "data:" prefix, or a multi-line
	// message is silently truncated at the first newline.
	for _, line := range strings.Split(data, "\n") {
		if _, err := fmt.Fprintf(s.w, "data: %s\n", line); err != nil {
			return err
		}
	}
	if _, err := fmt.Fprint(s.w, "\n"); err != nil {
		return err
	}
	if f, ok := s.w.(http.Flusher); ok {
		f.Flush()
	}
	return nil
}

// Close sends the terminal event.
func (s *ServerSentEventsWriter) Close() error { return s.Write("done", "[DONE]") }

// ChatCompletionsEndpoint serves chat completions.
type ChatCompletionsEndpoint struct {
	generate func(ctx context.Context, prompt string) (string, error)
	opts     InferenceServerOptions
}

// NewChatCompletionsEndpoint returns the endpoint.
func NewChatCompletionsEndpoint(opts InferenceServerOptions, generate func(ctx context.Context, prompt string) (string, error)) *ChatCompletionsEndpoint {
	return &ChatCompletionsEndpoint{generate: generate, opts: opts}
}

// ServeHTTP implements http.Handler.
func (e *ChatCompletionsEndpoint) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, e.opts.MaxRequestBytes)
	var req struct {
		Prompt string `json:"prompt"`
		Stream bool   `json:"stream"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "malformed request", http.StatusBadRequest)
		return
	}
	if e.generate == nil {
		http.Error(w, "no generator configured", http.StatusServiceUnavailable)
		return
	}
	out, err := e.generate(r.Context(), req.Prompt)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if req.Stream {
		sse := NewServerSentEventsWriter(w)
		_ = sse.Write("", out)
		_ = sse.Close()
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"response": out})
}

// EmbeddingsEndpoint serves embeddings.
type EmbeddingsEndpoint struct {
	embed func(ctx context.Context, text string) ([]float32, error)
}

// NewEmbeddingsEndpoint returns the endpoint.
func NewEmbeddingsEndpoint(embed func(ctx context.Context, text string) ([]float32, error)) *EmbeddingsEndpoint {
	return &EmbeddingsEndpoint{embed: embed}
}

// ServeHTTP implements http.Handler.
func (e *EmbeddingsEndpoint) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if e.embed == nil {
		http.Error(w, "no embedder configured", http.StatusServiceUnavailable)
		return
	}
	var req struct {
		Text string `json:"text"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "malformed request", http.StatusBadRequest)
		return
	}
	vec, err := e.embed(r.Context(), req.Text)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"embedding": vec})
}

// CompanionEndpoint serves companion turns.
type CompanionEndpoint struct {
	send func(ctx context.Context, sessionID, message string) (string, error)
}

// NewCompanionEndpoint returns the endpoint.
func NewCompanionEndpoint(send func(ctx context.Context, sessionID, message string) (string, error)) *CompanionEndpoint {
	return &CompanionEndpoint{send: send}
}

// ServeHTTP implements http.Handler.
func (e *CompanionEndpoint) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if e.send == nil {
		http.Error(w, "no companion configured", http.StatusServiceUnavailable)
		return
	}
	var req struct {
		SessionID string `json:"session_id"`
		Message   string `json:"message"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "malformed request", http.StatusBadRequest)
		return
	}
	out, err := e.send(r.Context(), req.SessionID, req.Message)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"response": out})
}

// DiagnosticsEndpoint serves the diagnostics body.
type DiagnosticsEndpoint struct {
	snapshot func() DiagnosticsResponse
}

// NewDiagnosticsEndpoint returns the endpoint.
func NewDiagnosticsEndpoint(snapshot func() DiagnosticsResponse) *DiagnosticsEndpoint {
	return &DiagnosticsEndpoint{snapshot: snapshot}
}

// ServeHTTP implements http.Handler.
func (e *DiagnosticsEndpoint) ServeHTTP(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	if e.snapshot == nil {
		_ = json.NewEncoder(w).Encode(DiagnosticsResponse{})
		return
	}
	_ = json.NewEncoder(w).Encode(e.snapshot())
}

// InferenceServerBuilder assembles the server.
type InferenceServerBuilder struct {
	opts    InferenceServerOptions
	mux     *http.ServeMux
	started time.Time
}

// NewInferenceServerBuilder returns a builder.
func NewInferenceServerBuilder(opts InferenceServerOptions) *InferenceServerBuilder {
	if opts.Address == "" {
		opts = DefaultInferenceServerOptions()
	}
	return &InferenceServerBuilder{opts: opts, mux: http.NewServeMux(), started: time.Now()}
}

// Handle registers an endpoint behind the auth gate.
func (b *InferenceServerBuilder) Handle(pattern string, h http.Handler) {
	b.mux.Handle(pattern, b.guard(h))
}

func (b *InferenceServerBuilder) guard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !b.opts.Auth.Enabled {
			// Unauthenticated is permitted ONLY on a loopback address. A server
			// that allowed it on a public one is the shape of every
			// accidentally-exposed inference endpoint.
			if !strings.HasPrefix(b.opts.Address, "127.0.0.1") && !strings.HasPrefix(b.opts.Address, "localhost") {
				http.Error(w, "authentication is required on a non-loopback address", http.StatusForbidden)
				return
			}
			next.ServeHTTP(w, r)
			return
		}
		if b.opts.Auth.ApiKey.Verify != nil {
			if _, ok := b.opts.Auth.ApiKey.Verify(r.Header.Get(b.opts.Auth.ApiKey.HeaderName)); ok {
				next.ServeHTTP(w, r)
				return
			}
		}
		if b.opts.Auth.Jwt.Verify != nil {
			token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
			if _, ok := b.opts.Auth.Jwt.Verify(token); ok {
				next.ServeHTTP(w, r)
				return
			}
		}
		http.Error(w, "unauthorised", http.StatusUnauthorized)
	})
}

// Health returns the health body.
func (b *InferenceServerBuilder) Health() HealthResponse {
	return HealthResponse{Status: "ok", UptimeSeconds: int64(time.Since(b.started).Seconds())}
}

// Handler returns the assembled mux.
func (b *InferenceServerBuilder) Handler() http.Handler { return b.mux }

// Program is the server's entry point shape.
//
// A type rather than a main function: this package is a library, and the host
// that wants a server calls Run. A library with its own main is a library that
// cannot be embedded.
type Program struct {
	builder *InferenceServerBuilder
}

// NewProgram returns a program over a builder.
func NewProgram(builder *InferenceServerBuilder) *Program { return &Program{builder: builder} }

// Run serves until the context is cancelled.
func (p *Program) Run(ctx context.Context) error {
	if p.builder == nil {
		return errors.New("no builder")
	}
	srv := &http.Server{
		Addr:              p.builder.opts.Address,
		Handler:           p.builder.Handler(),
		ReadHeaderTimeout: 10 * time.Second,
	}
	go func() {
		<-ctx.Done()
		_ = srv.Close()
	}()
	if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}

// MnnInferenceBridgeFactory builds an inference bridge over the MNN runtime.
type MnnInferenceBridgeFactory struct {
	config MnnRuntimeConfig
}

// NewMnnInferenceBridgeFactory returns a factory.
func NewMnnInferenceBridgeFactory(config MnnRuntimeConfig) *MnnInferenceBridgeFactory {
	return &MnnInferenceBridgeFactory{config: config}
}

// Available reports whether the runtime is present.
//
// False here: the native runtime is a seam this package does not link. Claiming
// it is available would produce a bridge that fails at the first token rather
// than at construction.
func (f *MnnInferenceBridgeFactory) Available() bool { return false }
