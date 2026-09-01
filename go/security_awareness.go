// security_awareness.go
//
// Telling somebody what has happened to them: an address in a breach, a file
// that looks dangerous, a network that is not what it claims.
//
// AWARENESS, NOT ENFORCEMENT. Everything here reports what it SEES and nothing
// acts on it. Collapsing the two would put the component that can read your
// files in charge of blocking them, and the blast radius of a false positive
// goes from a notification to a device that will not open its owner's
// documents.
//
// THE CORPUS IS LOCAL AND SO IS THE MATCHING. A device does not ask a remote
// service "has this address been breached", because that question tells the
// service the address AND that its owner is worried.
//
// NOTHING HERE IS A VERDICT. An assessment says what was observed and how
// confident it is. "This file is safe" is a promise no local check can keep,
// and a UI that renders one is lying on the product's behalf.

package circleai

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Severity and verdicts

// ThreatSeverity is how bad, on ONE scale, so three different sources can be
// compared at all.
type ThreatSeverity int

const (
	ThreatInformational ThreatSeverity = iota
	ThreatLow
	ThreatMedium
	ThreatHigh
	ThreatCritical
)

func (s ThreatSeverity) String() string {
	switch s {
	case ThreatLow:
		return "low"
	case ThreatMedium:
		return "medium"
	case ThreatHigh:
		return "high"
	case ThreatCritical:
		return "critical"
	}
	return "informational"
}

// ThreatAwarenessVerdict is what an assessment concluded.
type ThreatAwarenessVerdict int

const (
	// NotAssessed — NO ASSESSMENT WAS PERFORMED. The gate denied it, or nothing
	// ran. The ZERO VALUE on purpose, so an unset result reads as "nothing was
	// checked" rather than as a pass.
	NotAssessed ThreatAwarenessVerdict = iota
	// NoKnownThreat — did not match anything known-bad in the local corpus.
	// NOT a clean bill of health: it means "no known threat", nothing stronger,
	// and a UI that renders it as "safe" is lying on the product's behalf.
	NoKnownThreat
	Suspicious
	KnownBad
)

func (v ThreatAwarenessVerdict) String() string {
	switch v {
	case NoKnownThreat:
		return "no-known-threat"
	case Suspicious:
		return "suspicious"
	case KnownBad:
		return "known-bad"
	}
	return "not-assessed"
}

// ThreatAwarenessResult is one observation, said to a PERSON.
type ThreatAwarenessResult struct {
	Verdict  ThreatAwarenessVerdict
	Severity ThreatSeverity
	// The line that appears in a notification, so it names the thing rather
	// than the rule that fired.
	Summary string
	Detail  string
	// Which corpus or check produced it. "Flagged" is not actionable without
	// "by whom" — one source's false positive is another's deliberate policy.
	Source string
	// 0..1. Reported rather than thresholded here, because what counts as
	// enough differs per surface: a banking screen and a photo gallery should
	// not share one cutoff.
	Confidence float64
	At         time.Time
}

// ─────────────────────────────────────────────────────────────────────────────
// Indicators

// IndicatorKind is what an indicator describes.
type IndicatorKind int

const (
	IndicatorIdentity IndicatorKind = iota
	IndicatorNetwork
	IndicatorFile
)

// IdentityIndicator is one breach record.
type IdentityIndicator struct {
	// An email address, phone number or handle — HASHED, never the value. The
	// corpus never needs the original, and holding one turns a protective
	// feature into a second copy of the thing being protected.
	IdentifierSha256 string
	BreachName       string
	BreachAt         time.Time
	// What was exposed: "password", "id number", "address". The part people
	// actually need in order to decide what to change.
	ExposedFields []string
}

// NetworkIndicator is one bad host or address.
type NetworkIndicator struct {
	Value    string
	Category string
	Severity ThreatSeverity
	Source   string
}

// FileArtifact is a file being assessed.
type FileArtifact struct {
	Path         string
	Sha256       string
	SizeBytes    int64
	DeclaredMime string
}

// IndicatorMatch is a corpus hit.
type IndicatorMatch struct {
	Kind     IndicatorKind
	Value    string
	Source   string
	Severity ThreatSeverity
	Detail   string
}

// ─────────────────────────────────────────────────────────────────────────────
// The corpus

// ILocalIndicatorCorpus is a local set of indicators.
type ILocalIndicatorCorpus interface {
	Name() string
	// FindIdentity looks up a HASHED identifier.
	FindIdentity(identifierSha256 string) (IdentityIndicator, bool)
	FindNetwork(value string) (NetworkIndicator, bool)
	FindFile(sha256 string) (IndicatorMatch, bool)
	Count() int
}

// EmptyIndicatorCorpus has nothing in it.
//
// THE DEFAULT, deliberately. Shipping a populated corpus would mean shipping
// somebody else's list and its politics; a host loads one it chose. Empty means
// every assessment comes back "nothing known", which is honest, rather than
// "clean", which is not.
type EmptyIndicatorCorpus struct{}

// Name implements ILocalIndicatorCorpus.
func (EmptyIndicatorCorpus) Name() string { return "empty" }

// FindIdentity implements ILocalIndicatorCorpus.
func (EmptyIndicatorCorpus) FindIdentity(string) (IdentityIndicator, bool) {
	return IdentityIndicator{}, false
}

// FindNetwork implements ILocalIndicatorCorpus.
func (EmptyIndicatorCorpus) FindNetwork(string) (NetworkIndicator, bool) {
	return NetworkIndicator{}, false
}

// FindFile implements ILocalIndicatorCorpus.
func (EmptyIndicatorCorpus) FindFile(string) (IndicatorMatch, bool) { return IndicatorMatch{}, false }

// Count implements ILocalIndicatorCorpus.
func (EmptyIndicatorCorpus) Count() int { return 0 }

// InMemoryIndicatorCorpus is a corpus a host loaded.
type InMemoryIndicatorCorpus struct {
	mu         sync.RWMutex
	name       string
	identities map[string]IdentityIndicator
	networks   map[string]NetworkIndicator
	files      map[string]IndicatorMatch
}

// NewInMemoryIndicatorCorpus returns a corpus over the given indicators.
func NewInMemoryIndicatorCorpus(name string, identities []IdentityIndicator, networks []NetworkIndicator) *InMemoryIndicatorCorpus {
	c := &InMemoryIndicatorCorpus{
		name:       name,
		identities: map[string]IdentityIndicator{},
		networks:   map[string]NetworkIndicator{},
		files:      map[string]IndicatorMatch{},
	}
	for _, i := range identities {
		c.identities[strings.ToLower(i.IdentifierSha256)] = i
	}
	for _, n := range networks {
		c.networks[strings.ToLower(n.Value)] = n
	}
	return c
}

// Name implements ILocalIndicatorCorpus.
func (c *InMemoryIndicatorCorpus) Name() string { return c.name }

// FindIdentity implements ILocalIndicatorCorpus.
func (c *InMemoryIndicatorCorpus) FindIdentity(identifierSha256 string) (IdentityIndicator, bool) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	i, ok := c.identities[strings.ToLower(identifierSha256)]
	return i, ok
}

// FindNetwork implements ILocalIndicatorCorpus.
//
// Checks the host and then each parent domain: a corpus listing "bad.example"
// should match "tracker.bad.example", and one that only matches exactly is a
// corpus that never fires on anything real.
func (c *InMemoryIndicatorCorpus) FindNetwork(value string) (NetworkIndicator, bool) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	host := strings.ToLower(strings.TrimSpace(value))
	for {
		if n, ok := c.networks[host]; ok {
			return n, true
		}
		dot := strings.Index(host, ".")
		if dot < 0 {
			return NetworkIndicator{}, false
		}
		host = host[dot+1:]
		if !strings.Contains(host, ".") {
			return NetworkIndicator{}, false
		}
	}
}

// FindFile implements ILocalIndicatorCorpus.
func (c *InMemoryIndicatorCorpus) FindFile(sha string) (IndicatorMatch, bool) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	m, ok := c.files[strings.ToLower(sha)]
	return m, ok
}

// Count implements ILocalIndicatorCorpus.
func (c *InMemoryIndicatorCorpus) Count() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return len(c.identities) + len(c.networks) + len(c.files)
}

// Sha256Hex returns the lower-case hex SHA-256 of a string.
//
// Exposed because a caller holding an identifier should hash it ONCE and pass
// the hash around, rather than passing the plain value to three components.
func Sha256Hex(text string) string {
	sum := sha256.Sum256([]byte(strings.ToLower(strings.TrimSpace(text))))
	return hex.EncodeToString(sum[:])
}

// ─────────────────────────────────────────────────────────────────────────────
// Breach exposure

// IBreachExposureAwareness answers "has my own identity turned up in a breach".
type IBreachExposureAwareness interface {
	// Assess takes the PLAIN identifier and hashes it here; the plain form
	// never leaves this call.
	Assess(ctx context.Context, identifier string) ([]ThreatAwarenessResult, error)
}

// BreachExposureAssessor is the default assessor.
type BreachExposureAssessor struct {
	corpus ILocalIndicatorCorpus
}

// NewBreachExposureAssessor returns an assessor over a corpus.
func NewBreachExposureAssessor(corpus ILocalIndicatorCorpus) *BreachExposureAssessor {
	if corpus == nil {
		corpus = EmptyIndicatorCorpus{}
	}
	return &BreachExposureAssessor{corpus: corpus}
}

// Assess implements IBreachExposureAwareness.
func (a *BreachExposureAssessor) Assess(_ context.Context, identifier string) ([]ThreatAwarenessResult, error) {
	if strings.TrimSpace(identifier) == "" {
		return nil, errors.New("an identifier is required")
	}
	hash := Sha256Hex(identifier)
	ind, ok := a.corpus.FindIdentity(hash)
	if !ok {
		return []ThreatAwarenessResult{{
			Verdict:    NoKnownThreat,
			Severity:   ThreatInformational,
			Summary:    "not in any breach set on this device",
			Detail:     "this checks only what is stored locally, so it is not proof of anything",
			Source:     a.corpus.Name(),
			Confidence: 1,
			At:         time.Now(),
		}}, nil
	}
	// Severity follows what was exposed, not the age of the breach. A password
	// exposed five years ago that somebody still uses is a live problem; an
	// email address exposed yesterday mostly is not.
	sev := ThreatMedium
	for _, f := range ind.ExposedFields {
		switch strings.ToLower(f) {
		case "password", "id number", "passport", "card number":
			sev = ThreatCritical
		}
	}
	return []ThreatAwarenessResult{{
		Verdict:  KnownBad,
		Severity: sev,
		Summary:  "this address appears in " + ind.BreachName,
		Detail:   "exposed: " + strings.Join(ind.ExposedFields, ", "),
		Source:   a.corpus.Name(),
		// Not 1.0. A hash match is strong evidence the address was in the set,
		// and no evidence at all that the set is accurate.
		Confidence: 0.9,
		At:         time.Now(),
	}}, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// File threats

// IFileThreatAwareness answers "is a file I am about to open known-bad".
type IFileThreatAwareness interface {
	Assess(ctx context.Context, artifact FileArtifact) ([]ThreatAwarenessResult, error)
}

// FileThreatAwarenessAssessor hashes the file and asks the corpus, and
// separately notices shapes that are suspicious regardless of any list.
type FileThreatAwarenessAssessor struct {
	corpus ILocalIndicatorCorpus
}

// NewFileThreatAwarenessAssessor returns an assessor.
func NewFileThreatAwarenessAssessor(corpus ILocalIndicatorCorpus) *FileThreatAwarenessAssessor {
	if corpus == nil {
		corpus = EmptyIndicatorCorpus{}
	}
	return &FileThreatAwarenessAssessor{corpus: corpus}
}

var executableExtensions = map[string]bool{
	".exe": true, ".scr": true, ".bat": true, ".cmd": true, ".com": true,
	".msi": true, ".apk": true, ".jar": true, ".sh": true, ".ps1": true,
}

// Assess implements IFileThreatAwareness.
//
// Empty is not a certificate: "no observations" and "clean" are the same answer
// here, and pretending to certify a file as safe is a promise no local check
// can keep.
func (a *FileThreatAwarenessAssessor) Assess(_ context.Context, artifact FileArtifact) ([]ThreatAwarenessResult, error) {
	var out []ThreatAwarenessResult
	now := time.Now()

	if m, ok := a.corpus.FindFile(artifact.Sha256); ok {
		out = append(out, ThreatAwarenessResult{
			Verdict: KnownBad, Severity: m.Severity,
			Summary: "this file matches something the local list flags",
			Detail:  m.Detail, Source: m.Source, Confidence: 0.95, At: now,
		})
	}

	name := filepath.Base(artifact.Path)

	// The right-to-left override trick, which matters more than it looks: it is
	// what makes "photo_annexe.exe" render as "photo_exe.ennexa", and no hash
	// list catches a file nobody has seen before.
	if strings.ContainsRune(name, '‮') || strings.ContainsRune(name, '‫') {
		out = append(out, ThreatAwarenessResult{
			Verdict: Suspicious, Severity: ThreatHigh,
			Summary: "this file name is written to display backwards",
			Detail:  "it contains a right-to-left override, which hides the real extension",
			Source:  "shape", Confidence: 0.95, At: now,
		})
	}

	lower := strings.ToLower(name)
	ext := filepath.Ext(lower)
	if executableExtensions[ext] {
		// A double extension: the visible one is not the real one.
		stem := strings.TrimSuffix(lower, ext)
		if inner := filepath.Ext(stem); inner != "" && !executableExtensions[inner] {
			out = append(out, ThreatAwarenessResult{
				Verdict: Suspicious, Severity: ThreatHigh,
				Summary: "this looks like a document but will run as a program",
				Detail:  "the name ends " + inner + ext,
				Source:  "shape", Confidence: 0.9, At: now,
			})
		}
	}

	// A declared type that disagrees with the extension.
	if artifact.DeclaredMime != "" && executableExtensions[ext] &&
		!strings.Contains(artifact.DeclaredMime, "application/") {
		out = append(out, ThreatAwarenessResult{
			Verdict: Suspicious, Severity: ThreatMedium,
			Summary: "this file says it is one thing and is named as another",
			Detail:  "declared " + artifact.DeclaredMime + ", named " + ext,
			Source:  "shape", Confidence: 0.7, At: now,
		})
	}

	if len(out) == 0 {
		out = append(out, ThreatAwarenessResult{
			Verdict: NoKnownThreat, Severity: ThreatInformational,
			Summary: "nothing known about this file",
			Detail:  "that is not the same as safe",
			Source:  a.corpus.Name(), Confidence: 1, At: now,
		})
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Severity > out[j].Severity })
	return out, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Network threats

// INetworkThreatAwareness answers "is this host known-bad".
type INetworkThreatAwareness interface {
	Assess(ctx context.Context, hostOrAddress string) ([]ThreatAwarenessResult, error)
}

// NetworkThreatAwarenessAssessor checks a host against the local corpus.
type NetworkThreatAwarenessAssessor struct {
	corpus ILocalIndicatorCorpus
}

// NewNetworkThreatAwarenessAssessor returns an assessor.
func NewNetworkThreatAwarenessAssessor(corpus ILocalIndicatorCorpus) *NetworkThreatAwarenessAssessor {
	if corpus == nil {
		corpus = EmptyIndicatorCorpus{}
	}
	return &NetworkThreatAwarenessAssessor{corpus: corpus}
}

// Assess implements INetworkThreatAwareness.
func (a *NetworkThreatAwarenessAssessor) Assess(_ context.Context, hostOrAddress string) ([]ThreatAwarenessResult, error) {
	if strings.TrimSpace(hostOrAddress) == "" {
		return nil, errors.New("a host or address is required")
	}
	now := time.Now()
	if n, ok := a.corpus.FindNetwork(hostOrAddress); ok {
		return []ThreatAwarenessResult{{
			Verdict: KnownBad, Severity: n.Severity,
			Summary: hostOrAddress + " is on a " + n.Category + " list",
			Source:  n.Source, Confidence: 0.9, At: now,
		}}, nil
	}
	return []ThreatAwarenessResult{{
		Verdict: NoKnownThreat, Severity: ThreatInformational,
		Summary: "nothing known about " + hostOrAddress,
		Detail:  "the local lists have no entry; that is not the same as safe",
		Source:  a.corpus.Name(), Confidence: 1, At: now,
	}}, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Network observation and defence

// ThreatDirection is which way the traffic went.
type ThreatDirection int

const (
	ThreatInbound ThreatDirection = iota
	// ThreatOutbound is the one that matters most on a personal device:
	// something on this phone talking to somewhere it should not is a
	// compromised app, and it is the case a defence aimed at servers is not
	// looking for.
	ThreatOutbound
	ThreatLateral
)

func (d ThreatDirection) String() string {
	switch d {
	case ThreatOutbound:
		return "outbound"
	case ThreatLateral:
		return "lateral"
	}
	return "inbound"
}

// ThreatCategory is what kind of behaviour was seen.
type ThreatCategory int

const (
	ThreatScanning ThreatCategory = iota
	ThreatExfiltration
	ThreatCommandAndControl
	ThreatCredentialAccess
	ThreatDenialOfService
	ThreatAnomaly
)

func (c ThreatCategory) String() string {
	switch c {
	case ThreatExfiltration:
		return "exfiltration"
	case ThreatCommandAndControl:
		return "command-and-control"
	case ThreatCredentialAccess:
		return "credential-access"
	case ThreatDenialOfService:
		return "denial-of-service"
	case ThreatAnomaly:
		return "anomaly"
	}
	return "scanning"
}

// NetworkObservation is one connection seen.
type NetworkObservation struct {
	At             time.Time
	LocalEndpoint  string
	RemoteEndpoint string
	Protocol       string
	BytesOut       int64
	BytesIn        int64
	Direction      ThreatDirection
}

// INetworkObservationFeed supplies observations.
type INetworkObservationFeed interface {
	Drain() []NetworkObservation
}

// ThreatSignal is something worth telling a person about.
type ThreatSignal struct {
	Category   ThreatCategory
	Direction  ThreatDirection
	Severity   ThreatSeverity
	Summary    string
	Evidence   string
	Confidence float64
	At         time.Time
}

// ─────────────────────────────────────────────────────────────────────────────
// Escalation

// ISosEscalation reaches a PERSON.
type ISosEscalation interface {
	// Escalate returns false when it could not reach anybody, which the caller
	// must handle rather than assume: an escalation nobody received is the
	// failure this whole path exists to prevent.
	Escalate(ctx context.Context, signal ThreatSignal) bool
}

// NullSosEscalation escalates nowhere and says so by returning false.
//
// THE DEFAULT, and false rather than true on purpose. A null escalation that
// reported success would make a device look protected while every alert went
// into nothing — which is worse than no defence at all, because somebody
// believes in it.
type NullSosEscalation struct{}

// Escalate implements ISosEscalation.
func (NullSosEscalation) Escalate(_ context.Context, _ ThreatSignal) bool { return false }

// DelegateSosEscalation calls the host's function.
//
// The whole seam: what "reach a person" means — a notification, an SMS, a call
// to a neighbour — is the host's to decide.
type DelegateSosEscalation struct {
	deliver func(ctx context.Context, signal ThreatSignal) bool
}

// NewDelegateSosEscalation returns an escalation over a delivery function.
func NewDelegateSosEscalation(deliver func(ctx context.Context, signal ThreatSignal) bool) *DelegateSosEscalation {
	return &DelegateSosEscalation{deliver: deliver}
}

// Escalate implements ISosEscalation.
func (e *DelegateSosEscalation) Escalate(ctx context.Context, signal ThreatSignal) bool {
	if e.deliver == nil {
		return false
	}
	return e.deliver(ctx, signal)
}

// SosThreatSink collects signals and escalates the ones that warrant it.
type SosThreatSink struct {
	mu              sync.Mutex
	escalation      ISosEscalation
	minimumSeverity ThreatSeverity
	dedupeWindow    time.Duration
	lastSeen        map[string]time.Time
	escalated       int
	suppressed      int
	now             func() time.Time
}

// NewSosThreatSink returns a sink.
//
// De-duplicates within a window: the same finding arriving forty times is one
// situation, and forty alerts is how somebody learns to ignore all of them.
func NewSosThreatSink(escalation ISosEscalation, minimumSeverity ThreatSeverity, dedupeWindow time.Duration) *SosThreatSink {
	if escalation == nil {
		escalation = NullSosEscalation{}
	}
	if dedupeWindow <= 0 {
		dedupeWindow = 10 * time.Minute
	}
	return &SosThreatSink{
		escalation:      escalation,
		minimumSeverity: minimumSeverity,
		dedupeWindow:    dedupeWindow,
		lastSeen:        map[string]time.Time{},
		now:             time.Now,
	}
}

// Submit offers a signal, returning whether it was escalated.
func (s *SosThreatSink) Submit(ctx context.Context, signal ThreatSignal) bool {
	if signal.Severity < s.minimumSeverity {
		s.mu.Lock()
		s.suppressed++
		s.mu.Unlock()
		return false
	}
	key := signal.Category.String() + "|" + signal.Summary
	now := s.now()

	s.mu.Lock()
	if last, ok := s.lastSeen[key]; ok && now.Sub(last) < s.dedupeWindow {
		s.suppressed++
		s.mu.Unlock()
		return false
	}
	s.lastSeen[key] = now
	s.mu.Unlock()

	ok := s.escalation.Escalate(ctx, signal)
	s.mu.Lock()
	if ok {
		s.escalated++
	} else {
		s.suppressed++
	}
	s.mu.Unlock()
	return ok
}

// Counts returns how many signals were escalated and suppressed.
func (s *SosThreatSink) Counts() (escalated, suppressed int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.escalated, s.suppressed
}
