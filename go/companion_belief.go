// companion_belief.go
//
// Memory integrity: attribution + belief revision. Ported from
// CircleAI.Companion (PersonalBelief, HeuristicBeliefExtractor, SelfBeliefStore)
// — the C# reference — and mirrors the TypeScript pilot (companion/belief.ts) 1:1.
//
// Every belief carries WHOSE fact it is — the user's own (Self), someone else's
// (Other), or a general fact (World). The highest-harm rule in the whole system:
// a fact about a third party ("my mother is diabetic") must never be recorded as
// a fact about the user. Only Self beliefs become user facts; a newer self-belief
// on the same predicate supersedes the older one; a correction retracts a belief.

package circleai

import (
	"context"
	"strings"
	"sync"
	"time"
)

// Attribution is whose fact a belief is about.
type Attribution int

const (
	// AttributionSelf is a fact about the user themselves.
	AttributionSelf Attribution = iota
	// AttributionOther is a fact about a third party.
	AttributionOther
	// AttributionWorld is a general fact.
	AttributionWorld
)

// String returns the canonical name of the attribution.
func (a Attribution) String() string {
	switch a {
	case AttributionSelf:
		return "Self"
	case AttributionOther:
		return "Other"
	case AttributionWorld:
		return "World"
	default:
		return "Unknown"
	}
}

// PersonalBelief is a single attributed belief, with provenance and confidence.
type PersonalBelief struct {
	Attribution   Attribution
	Subject       string
	Predicate     string
	Object        string
	Confidence    float64
	Source        *string
	RecordedAtUTC time.Time
}

// IBeliefExtractor turns a sentence into attributed beliefs.
type IBeliefExtractor interface {
	Extract(ctx context.Context, text string, source *string) ([]PersonalBelief, error)
}

var beliefRelations = map[string]struct{}{
	"mother": {}, "father": {}, "mom": {}, "mum": {}, "dad": {}, "sister": {}, "brother": {}, "wife": {},
	"husband": {}, "son": {}, "daughter": {}, "aunt": {}, "uncle": {}, "grandmother": {}, "grandfather": {},
	"granny": {}, "grandpa": {}, "gran": {}, "nan": {}, "friend": {}, "colleague": {}, "boss": {},
	"neighbour": {}, "neighbor": {}, "cousin": {}, "partner": {}, "girlfriend": {}, "boyfriend": {},
}

var beliefPossessive = map[string]struct{}{
	"my": {}, "her": {}, "his": {}, "their": {}, "our": {},
}

var beliefStop = map[string]struct{}{
	"the": {}, "a": {}, "an": {}, "is": {}, "are": {}, "was": {}, "were": {}, "be": {}, "been": {}, "am": {},
	"to": {}, "of": {}, "in": {}, "on": {}, "at": {}, "and": {}, "or": {}, "but": {}, "with": {}, "has": {},
	"have": {}, "had": {}, "that": {}, "this": {}, "it": {}, "as": {}, "for": {}, "really": {}, "very": {},
	"just": {}, "now": {},
}

// HeuristicBeliefExtractor is a model-free belief extractor with attribution
// discipline. Coarse by design — the model-based extractor is far more precise —
// but it never collapses "my mother" into "me". Attribution is decided by the
// sentence's leading subject.
type HeuristicBeliefExtractor struct{}

// Extract returns at most one attributed belief for the sentence. The split set
// (no apostrophe, no hyphen) mirrors the TS/C# reference so "i'm" stays one token.
func (e *HeuristicBeliefExtractor) Extract(_ context.Context, text string, source *string) ([]PersonalBelief, error) {
	if strings.TrimSpace(text) == "" {
		return []PersonalBelief{}, nil
	}

	tokens := strings.FieldsFunc(strings.ToLower(text), isBeliefSeparator)
	if len(tokens) == 0 {
		return []PersonalBelief{}, nil
	}

	var attribution Attribution
	var subject string
	skip := make(map[int]struct{}) // subject tokens, excluded from the object

	switch {
	case len(tokens) >= 2 && contains(beliefPossessive, tokens[0]) && contains(beliefRelations, tokens[1]):
		// "my mother ..." → someone else
		attribution = AttributionOther
		subject = tokens[1]
		skip[0] = struct{}{}
		skip[1] = struct{}{}
	case contains(beliefRelations, tokens[0]):
		attribution = AttributionOther
		subject = tokens[0]
		skip[0] = struct{}{}
	case tokens[0] == "i" || tokens[0] == "i'm" || tokens[0] == "im" || tokens[0] == "me" || tokens[0] == "my":
		// "I ..." or "my <non-relation> ..." → the user
		attribution = AttributionSelf
		subject = "user"
		skip[0] = struct{}{}
	default:
		attribution = AttributionWorld
		subject = tokens[0]
	}

	var objectParts []string
	for i, t := range tokens {
		if _, skipped := skip[i]; skipped {
			continue
		}
		if len(t) < 3 {
			continue
		}
		if _, stop := beliefStop[t]; stop {
			continue
		}
		if _, rel := beliefRelations[t]; rel {
			continue
		}
		objectParts = append(objectParts, t)
	}
	obj := strings.Join(objectParts, " ")
	if strings.TrimSpace(obj) == "" {
		return []PersonalBelief{}, nil
	}

	return []PersonalBelief{{
		Attribution:   attribution,
		Subject:       subject,
		Predicate:     "isAbout",
		Object:        obj,
		Confidence:    0.6,
		Source:        source,
		RecordedAtUTC: time.Now().UTC(),
	}}, nil
}

// isBeliefSeparator matches the belief split set: whitespace and . , ? ! ; : " ( ).
// Note: NO apostrophe, so "i'm" survives as a single token.
func isBeliefSeparator(r rune) bool {
	switch r {
	case ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '"', '(', ')':
		return true
	}
	return false
}

func contains(set map[string]struct{}, key string) bool {
	_, ok := set[key]
	return ok
}

// SelfBeliefStore holds the user's own facts, with attribution filtering,
// revision, and correction. Thread-safe: the encoder writes from its background
// drain while the session reads facts for the prompt.
type SelfBeliefStore struct {
	mu    sync.Mutex
	self  []PersonalBelief
	audit []PersonalBelief // other/world — remembered, never a user fact
}

// NewSelfBeliefStore returns an empty store.
func NewSelfBeliefStore() *SelfBeliefStore {
	return &SelfBeliefStore{}
}

// Record records a belief. Only Self beliefs become user facts; the rest are
// audited. A newer Self belief on the same (subject, predicate) supersedes the
// older one.
func (s *SelfBeliefStore) Record(belief PersonalBelief) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	if belief.Attribution != AttributionSelf {
		s.audit = append(s.audit, belief)
		return nil
	}
	// Supersede an existing self-belief on the same (subject, predicate): a
	// functional fact holds one current value. The prior value drops out.
	kept := s.self[:0]
	for _, b := range s.self {
		if eqCi(b.Subject, belief.Subject) && eqCi(b.Predicate, belief.Predicate) {
			continue
		}
		kept = append(kept, b)
	}
	s.self = kept
	s.self = append(s.self, belief)
	return nil
}

// SelfFacts returns the user's own current facts.
func (s *SelfBeliefStore) SelfFacts() []PersonalBelief {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]PersonalBelief, len(s.self))
	copy(out, s.self)
	return out
}

// NonSelf returns beliefs remembered but never treated as user facts (audit trail).
func (s *SelfBeliefStore) NonSelf() []PersonalBelief {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]PersonalBelief, len(s.audit))
	copy(out, s.audit)
	return out
}

// Retract drops any user fact whose object contains the given text
// (case-insensitive) and returns the number removed.
func (s *SelfBeliefStore) Retract(objectContains string) int {
	if strings.TrimSpace(objectContains) == "" {
		return 0
	}
	needle := strings.ToLower(objectContains)
	s.mu.Lock()
	defer s.mu.Unlock()
	kept := s.self[:0]
	removed := 0
	for _, b := range s.self {
		if strings.Contains(strings.ToLower(b.Object), needle) {
			removed++
			continue
		}
		kept = append(kept, b)
	}
	s.self = kept
	return removed
}

// Provenance returns the distinct source turns behind the user's facts.
func (s *SelfBeliefStore) Provenance() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	seen := make(map[string]struct{})
	var out []string
	for _, b := range s.self {
		if b.Source != nil {
			if _, ok := seen[*b.Source]; !ok {
				seen[*b.Source] = struct{}{}
				out = append(out, *b.Source)
			}
		}
	}
	return out
}

func eqCi(a, b string) bool {
	return strings.EqualFold(a, b)
}

// Compile-time assertion.
var _ IBeliefExtractor = (*HeuristicBeliefExtractor)(nil)
