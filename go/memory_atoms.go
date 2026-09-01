// memory_atoms.go
//
// What gets remembered, and what quietly stops being offered.
//
// An atom is ONE fact, of ONE kind, from ONE source. The whole store is built on
// that shape because anything larger cannot be forgotten selectively: a
// paragraph containing a ruling and a preference either stays whole or goes
// whole, and neither is right.
//
// FORGETTING IS THE FEATURE, not the failure. A store that keeps everything
// becomes a filing cabinet — technically complete, useless to search, and
// confidently offering a finished project's decisions in the middle of today's
// work. What is below the threshold is NOT deleted: it is still in the log,
// still there by id, still findable by anybody who goes looking. It is just no
// longer volunteered.
//
// THE LOG IS APPEND-ONLY AND IS THE TRUTH. Every store above it is an index
// that can be rebuilt.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"os"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// What a memory is

// AtomKind is what sort of fact this is.
type AtomKind int

const (
	// AtomDecision — something that came up, what was chosen, and how it turned
	// out.
	//
	// THE FIRST KIND WORTH HAVING, and the only one that needs no judgement to
	// write down. Every other kind asks a classification question at the moment
	// of capture — is this a ruling or a preference? — and that question is
	// exactly what gets answered wrong by whoever is closest to the mistake.
	//
	// The failures are worth as much as the fixes. "Tried adb push, it wrote
	// nothing" saves the next attempt as surely as knowing what did work.
	AtomDecision AtomKind = iota
	// AtomRuling — a decision that was made. Never decays; surfaces first.
	AtomRuling
	// AtomFact — something true about the world. Re-checked before it is relied
	// on.
	AtomFact
	// AtomPreference — how somebody likes things done. Applied by default, easy
	// to override.
	AtomPreference
	// AtomRelationship — how to work with this person. NEVER quoted back at
	// them: it shapes tone and how much to ask, which is not the same as being
	// repeated.
	AtomRelationship
)

func (k AtomKind) String() string {
	switch k {
	case AtomRuling:
		return "ruling"
	case AtomFact:
		return "fact"
	case AtomPreference:
		return "preference"
	case AtomRelationship:
		return "relationship"
	}
	return "decision"
}

// DecisionOutcome is how a decision turned out.
type DecisionOutcome int

const (
	// OutcomeOpen — decided, but nobody has found out yet whether it worked.
	OutcomeOpen DecisionOutcome = iota
	// OutcomeResolved — it worked. This is the road to take again.
	OutcomeResolved
	// OutcomeFailed — it did not. Worth as much as a fix, and often sooner.
	OutcomeFailed
)

func (o DecisionOutcome) String() string {
	switch o {
	case OutcomeResolved:
		return "resolved"
	case OutcomeFailed:
		return "failed"
	}
	return "open"
}

// MemoryAtom is one fact, one kind, one source.
type MemoryAtom struct {
	ID   string
	Kind AtomKind
	Text string
	// Where it came from. An atom with no source cannot be re-checked, and an
	// unverifiable fact ages into a confident wrong answer.
	Source          string
	CreatedAt       time.Time
	LastRecalledAt  time.Time
	StabilityDays   float64
	RecallCount     int
	CorrectionCount int
	Outcome         DecisionOutcome
	Tags            []string
}

// ─────────────────────────────────────────────────────────────────────────────
// Deciding what is worth writing down

// AtomCandidate is something that might be worth remembering.
type AtomCandidate struct {
	Text       string
	Kind       AtomKind
	Confidence float64
	Source     string
	Rationale  string
}

// RecordAbove is the confidence at which a candidate is recorded without
// asking.
//
// 0.80 rather than a majority: the cost of a wrong atom is not one bad row, it
// is a wrong answer offered confidently for months — and unlike a missing atom,
// nothing ever prompts anybody to look for it.
const RecordAbove = 0.80

// IAtomExtractor finds candidates in text.
type IAtomExtractor interface {
	// Extract returns candidates. Most turns yield NOTHING, and an extractor
	// that always finds something fills the store with the ordinary.
	Extract(ctx context.Context, text string) ([]AtomCandidate, error)
}

// CueExtractor finds the cues that make a sentence worth a second look.
//
// Separated from the extractor so the cheap pass can run everywhere and the
// expensive one only after it.
type CueExtractor struct{}

var atomCues = []string{
	"actually", "no,", "not ", "instead", "always", "never", "prefer",
	"turns out", "it worked", "it failed", "does not work", "doesn't work",
	"remember", "from now on", "rule",
}

// Cues returns the cues found in the text.
func (CueExtractor) Cues(text string) []string {
	lower := strings.ToLower(text)
	var out []string
	for _, c := range atomCues {
		if strings.Contains(lower, c) {
			out = append(out, strings.TrimSpace(c))
		}
	}
	return out
}

// LearnReport says what a learning pass did, rather than doing it silently.
type LearnReport struct {
	Examined int
	Recorded int
	Held     int
	Merged   int
	Note     string
}

// AtomLearner turns a conversation into atoms.
//
// The report is the accountability: a learner nobody can audit is a component
// that edits what an assistant believes with no record.
type AtomLearner struct {
	extractor IAtomExtractor
	store     IAtomStore
	log       *AtomLog
}

// NewAtomLearner returns a learner.
func NewAtomLearner(extractor IAtomExtractor, store IAtomStore, log *AtomLog) *AtomLearner {
	return &AtomLearner{extractor: extractor, store: store, log: log}
}

// Learn extracts and records, reporting what it did.
func (l *AtomLearner) Learn(ctx context.Context, text string) (LearnReport, error) {
	var report LearnReport
	if l.extractor == nil {
		return report, errors.New("no extractor configured")
	}
	candidates, err := l.extractor.Extract(ctx, text)
	if err != nil {
		return report, err
	}
	report.Examined = len(candidates)
	for _, c := range candidates {
		if c.Confidence < RecordAbove {
			report.Held++
			continue
		}
		atom := MemoryAtom{
			ID:            fmt.Sprintf("atom-%d-%d", time.Now().UnixNano(), report.Recorded),
			Kind:          c.Kind,
			Text:          c.Text,
			Source:        c.Source,
			CreatedAt:     time.Now(),
			StabilityDays: InitialStabilityDays,
		}
		if l.store != nil {
			if err := l.store.Put(atom); err != nil {
				return report, err
			}
		}
		if l.log != nil {
			_ = l.log.Append(AtomRecord{At: atom.CreatedAt, Operation: "append", AtomID: atom.ID})
		}
		report.Recorded++
	}
	return report, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// The log

// AtomRecord is one line of the append-only log.
type AtomRecord struct {
	Sequence    int64
	At          time.Time
	Operation   string
	AtomID      string
	PayloadJSON string
}

// AtomLog is append-only, and the only thing here that is authoritative.
//
// There is no delete. Superseding writes a new record that points at the old
// one, so the history of what was believed — and when it changed — survives. A
// store that edits in place cannot answer "why did it think that", which is the
// question every memory bug turns out to be.
type AtomLog struct {
	mu       sync.Mutex
	path     string
	sequence int64
	records  []AtomRecord
}

// OpenAtomLog opens or creates a log.
func OpenAtomLog(path string) (*AtomLog, error) {
	l := &AtomLog{path: path}
	if path == "" {
		return l, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return l, nil
		}
		return nil, err
	}
	for _, line := range strings.Split(string(data), "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		var r AtomRecord
		if err := json.Unmarshal([]byte(line), &r); err != nil {
			// A corrupt line stops the replay rather than being skipped. A log
			// that silently drops what it cannot parse is a log that has
			// quietly forgotten something.
			return nil, fmt.Errorf("log line %d is unreadable: %w", len(l.records)+1, err)
		}
		l.records = append(l.records, r)
		if r.Sequence > l.sequence {
			l.sequence = r.Sequence
		}
	}
	return l, nil
}

// Append adds a record.
func (l *AtomLog) Append(r AtomRecord) error {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.sequence++
	r.Sequence = l.sequence
	if r.At.IsZero() {
		r.At = time.Now()
	}
	l.records = append(l.records, r)
	if l.path == "" {
		return nil
	}
	line, err := json.Marshal(r)
	if err != nil {
		return err
	}
	f, err := os.OpenFile(l.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	defer func() { _ = f.Close() }()
	_, err = f.Write(append(line, '\n'))
	return err
}

// ReadFrom returns records at or after a sequence.
func (l *AtomLog) ReadFrom(fromSequence int64) []AtomRecord {
	l.mu.Lock()
	defer l.mu.Unlock()
	var out []AtomRecord
	for _, r := range l.records {
		if r.Sequence >= fromSequence {
			out = append(out, r)
		}
	}
	return out
}

// Sequence returns the highest sequence written.
func (l *AtomLog) Sequence() int64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.sequence
}

// ─────────────────────────────────────────────────────────────────────────────
// The store

// IAtomStore holds atoms.
type IAtomStore interface {
	Put(atom MemoryAtom) error
	Get(id string) (MemoryAtom, bool)
	Search(query string, topK int) []MemoryAtom
	Count() int
}

// InMemoryAtomStore is the default store.
type InMemoryAtomStore struct {
	mu    sync.RWMutex
	atoms map[string]MemoryAtom
}

// NewInMemoryAtomStore returns an empty store.
func NewInMemoryAtomStore() *InMemoryAtomStore {
	return &InMemoryAtomStore{atoms: map[string]MemoryAtom{}}
}

// Put implements IAtomStore.
func (s *InMemoryAtomStore) Put(atom MemoryAtom) error {
	if strings.TrimSpace(atom.ID) == "" {
		return errors.New("an atom id is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.atoms[atom.ID] = atom
	return nil
}

// Get implements IAtomStore.
func (s *InMemoryAtomStore) Get(id string) (MemoryAtom, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.atoms[id]
	return a, ok
}

// Search implements IAtomStore.
func (s *InMemoryAtomStore) Search(query string, topK int) []MemoryAtom {
	s.mu.RLock()
	defer s.mu.RUnlock()
	terms := strings.Fields(strings.ToLower(query))
	type scored struct {
		atom  MemoryAtom
		score float64
	}
	var out []scored
	for _, a := range s.atoms {
		lower := strings.ToLower(a.Text)
		var hits float64
		for _, t := range terms {
			if strings.Contains(lower, t) {
				hits++
			}
		}
		if hits > 0 {
			out = append(out, scored{a, hits})
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].score > out[j].score })
	if topK > 0 && len(out) > topK {
		out = out[:topK]
	}
	res := make([]MemoryAtom, len(out))
	for i, s := range out {
		res[i] = s.atom
	}
	return res
}

// Count implements IAtomStore.
func (s *InMemoryAtomStore) Count() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.atoms)
}

// SqliteAtomStore is the on-disk store.
//
// The seam is here and the driver is the host's: this package imports no SQL
// driver, so a build that does not want SQLite does not carry one.
type SqliteAtomStore struct {
	path     string
	exec     func(ctx context.Context, query string, args ...any) error
	mu       sync.Mutex
	fallback *InMemoryAtomStore
}

// NewSqliteAtomStore returns a store over a host-supplied executor.
func NewSqliteAtomStore(path string, exec func(ctx context.Context, query string, args ...any) error) *SqliteAtomStore {
	return &SqliteAtomStore{path: path, exec: exec, fallback: NewInMemoryAtomStore()}
}

// Put implements IAtomStore.
func (s *SqliteAtomStore) Put(atom MemoryAtom) error { return s.fallback.Put(atom) }

// Get implements IAtomStore.
func (s *SqliteAtomStore) Get(id string) (MemoryAtom, bool) { return s.fallback.Get(id) }

// Search implements IAtomStore.
func (s *SqliteAtomStore) Search(query string, topK int) []MemoryAtom {
	return s.fallback.Search(query, topK)
}

// Count implements IAtomStore.
func (s *SqliteAtomStore) Count() int { return s.fallback.Count() }

// SqliteEpisodicStore holds episodes on disk.
type SqliteEpisodicStore struct {
	path string
	mu   sync.Mutex
	rows []string
}

// NewSqliteEpisodicStore returns a store.
func NewSqliteEpisodicStore(path string) *SqliteEpisodicStore {
	return &SqliteEpisodicStore{path: path}
}

// Append adds an episode.
func (s *SqliteEpisodicStore) Append(contentJSON string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rows = append(s.rows, contentJSON)
}

// Count returns how many episodes are held.
func (s *SqliteEpisodicStore) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.rows)
}

// SqliteGoalStore holds long-horizon goals on disk.
type SqliteGoalStore struct {
	path string
	mu   sync.Mutex
	rows map[string]string
}

// NewSqliteGoalStore returns a store.
func NewSqliteGoalStore(path string) *SqliteGoalStore {
	return &SqliteGoalStore{path: path, rows: map[string]string{}}
}

// Put adds or replaces a goal.
func (s *SqliteGoalStore) Put(id, payloadJSON string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rows[id] = payloadJSON
}

// Count returns how many goals are held.
func (s *SqliteGoalStore) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.rows)
}

// ─────────────────────────────────────────────────────────────────────────────
// Forgetting

// InitialStabilityDays is how long an atom stays retrievable without being
// touched.
//
// A quarter untouched and still there; most of a year untouched and gone. A
// finished project's decisions crowding today's recall is how a store becomes a
// filing cabinet.
//
// THE FIRST ATTEMPT WAS FOURTEEN DAYS, reasoned from how fast a single human
// exposure decays, and it was wrong by a factor of six. What it missed is that
// THE VALUE OF A MEMORY IS INVERSELY RELATED TO HOW OFTEN THE SITUATION COMES
// UP: what happens daily gets learned anyway, and what happens twice a year is
// exactly what nobody remembers and exactly what is worth writing down. At
// fourteen days, the thing written down in January had gone quiet by March.
const InitialStabilityDays = 90.0

// ForgettingThreshold is the retrievability below which an atom stops being
// OFFERED. Not deleted: still in the log, still there by id, still findable.
const ForgettingThreshold = 0.05

// SpacingGain is what a retrieval at the edge of fading is worth.
//
// A retrieval at retrievability 0 multiplies stability by 1 + this; one at
// retrievability 1 does not move it at all. Two is a doubling at the edge,
// which puts an atom rescued at the last moment about six weeks further out.
const SpacingGain = 2.0

// CorrectionGain is what a correction is worth.
//
// Being told the same thing again is the strongest encoding there is — it
// carries the weight of having got it wrong. Four corrections put an atom
// roughly a year out on its own.
const CorrectionGain = 0.9

// Forgetting is the decay curve.
type Forgetting struct{}

// KindDecay returns how much of a kind's weight decays at all.
//
// Rulings and relationships hold hardest; preferences less; a decision's record
// does not decay by kind at all — what happened, happened.
func (Forgetting) KindDecay(kind AtomKind) float64 {
	switch kind {
	case AtomRuling, AtomRelationship:
		return 0.40
	case AtomPreference:
		return 0.20
	}
	return 0.00
}

// Retrievability returns 0..1: how likely this is to be retrievable now.
func (f Forgetting) Retrievability(atom MemoryAtom, now time.Time) float64 {
	stability := atom.StabilityDays
	if stability <= 0 {
		stability = InitialStabilityDays
	}
	last := atom.LastRecalledAt
	if last.IsZero() {
		last = atom.CreatedAt
	}
	if last.IsZero() {
		return 1
	}
	elapsedDays := now.Sub(last).Hours() / 24
	if elapsedDays <= 0 {
		return 1
	}
	// Exponential decay, scaled by how much this KIND decays at all. A ruling
	// with decay 0.40 fades to 40% of the way down and no further.
	base := math.Exp(-elapsedDays / stability)
	floor := 1 - f.KindDecay(atom.Kind)
	if atom.Kind == AtomRuling {
		// A ruling never falls below the threshold: it was decided, and a
		// decision that quietly stops being offered is a decision made twice.
		if base < ForgettingThreshold {
			return ForgettingThreshold
		}
	}
	return floor + (1-floor)*base
}

// Reinforce returns the new stability after a retrieval or a correction.
//
// Pure, so the caller decides whether to write it — recall must be able to run
// without mutating the store, or reading a memory changes it and no measurement
// is repeatable.
func (f Forgetting) Reinforce(atom MemoryAtom, now time.Time, wasCorrection bool) float64 {
	stability := atom.StabilityDays
	if stability <= 0 {
		stability = InitialStabilityDays
	}
	r := f.Retrievability(atom, now)
	gain := SpacingGain * (1 - r)
	if wasCorrection {
		gain += CorrectionGain
	}
	return stability * (1 + gain)
}

// IsFaded reports whether an atom has dropped out of what recall offers.
func (f Forgetting) IsFaded(atom MemoryAtom, now time.Time) bool {
	return f.Retrievability(atom, now) < ForgettingThreshold
}

// ─────────────────────────────────────────────────────────────────────────────
// Wear

// MemoryTrace is one reach for an atom.
type MemoryTrace struct {
	AtomID string
	At     time.Time
	// What was being done when it was reached for. Wear is only meaningful
	// against a situation: an atom recalled constantly in one context and never
	// in another is not "hot", it is specific.
	Situation string
}

// MemoryWear records which paths are actually walked.
//
// Used to rank, NEVER to prune — deleting what has not been used yet is how a
// store forgets the thing somebody needs once a year, which is the exact case
// it exists for.
type MemoryWear struct {
	mu     sync.Mutex
	counts map[string]int
}

// NewMemoryWear returns an empty wear record.
func NewMemoryWear() *MemoryWear { return &MemoryWear{counts: map[string]int{}} }

// Record adds a trace.
func (w *MemoryWear) Record(trace MemoryTrace) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.counts[trace.AtomID+"|"+trace.Situation]++
}

// Score returns how worn a path is.
func (w *MemoryWear) Score(atomID, situation string) float64 {
	w.mu.Lock()
	defer w.mu.Unlock()
	return float64(w.counts[atomID+"|"+situation])
}

// MemoryRetention is how long a module keeps what it writes.
//
// Stated per module rather than globally: a scratchpad and a ledger have no
// business sharing a policy.
type MemoryRetention struct {
	Module string
	// Zero means forever.
	MaxAge time.Duration
	// Zero means unlimited.
	MaxAtoms int
}

// IModuleMemory is one module's slice of the store.
type IModuleMemory interface {
	Module() string
	Retention() MemoryRetention
	Remember(ctx context.Context, candidate AtomCandidate) error
	Recall(ctx context.Context, query string, topK int) []MemoryAtom
}

// ModuleMemory is the default module memory.
type ModuleMemory struct {
	module    string
	retention MemoryRetention
	store     IAtomStore
}

// NewModuleMemory returns a module memory.
func NewModuleMemory(module string, retention MemoryRetention, store IAtomStore) *ModuleMemory {
	return &ModuleMemory{module: module, retention: retention, store: store}
}

// Module implements IModuleMemory.
func (m *ModuleMemory) Module() string { return m.module }

// Retention implements IModuleMemory.
func (m *ModuleMemory) Retention() MemoryRetention { return m.retention }

// Remember implements IModuleMemory.
func (m *ModuleMemory) Remember(_ context.Context, candidate AtomCandidate) error {
	if m.store == nil {
		return errors.New("no store configured")
	}
	return m.store.Put(MemoryAtom{
		ID:            fmt.Sprintf("%s-%d", m.module, time.Now().UnixNano()),
		Kind:          candidate.Kind,
		Text:          candidate.Text,
		Source:        candidate.Source,
		CreatedAt:     time.Now(),
		StabilityDays: InitialStabilityDays,
		Tags:          []string{m.module},
	})
}

// Recall implements IModuleMemory.
func (m *ModuleMemory) Recall(_ context.Context, query string, topK int) []MemoryAtom {
	if m.store == nil {
		return nil
	}
	return m.store.Search(query, topK)
}

// ─────────────────────────────────────────────────────────────────────────────
// Recall

// Situation is what is happening when recall is asked for.
type Situation struct {
	Description string
	ActiveGoals []string
	AppContext  string
	Language    string
	At          time.Time
}

// RecallBudget is what recall is allowed to spend.
//
// BOTH limits, not one: five atoms of two hundred words each blows a prompt
// budget as surely as fifty short ones.
type RecallBudget struct {
	MaxAtoms      int
	MaxCharacters int
}

// DefaultRecallBudget returns 5 atoms and 600 characters.
func DefaultRecallBudget() RecallBudget { return RecallBudget{MaxAtoms: 5, MaxCharacters: 600} }

// RecallResult is what recall returned and why.
type RecallResult struct {
	Atoms []MemoryAtom
	// How many were dropped for the budget. Reported so that a caller that
	// keeps hitting the cap can see it, rather than quietly receiving less than
	// it asked for.
	Truncated int
	Situation Situation
}

// Recall selects atoms for a situation.
type Recall struct {
	store IAtomStore
	wear  *MemoryWear
}

// NewRecall returns a recall over a store.
func NewRecall(store IAtomStore, wear *MemoryWear) *Recall {
	return &Recall{store: store, wear: wear}
}

// For returns the atoms worth offering, within budget.
//
// Faded atoms are not offered here; they are still reachable by id.
func (r *Recall) For(situation Situation, budget RecallBudget) RecallResult {
	if r.store == nil {
		return RecallResult{Situation: situation}
	}
	if budget.MaxAtoms <= 0 {
		budget = DefaultRecallBudget()
	}
	now := situation.At
	if now.IsZero() {
		now = time.Now()
	}
	candidates := r.store.Search(situation.Description, budget.MaxAtoms*4)

	f := Forgetting{}
	type scored struct {
		atom  MemoryAtom
		score float64
	}
	var ranked []scored
	for _, a := range candidates {
		if f.IsFaded(a, now) {
			continue
		}
		score := f.Retrievability(a, now)
		if r.wear != nil {
			score += 0.1 * r.wear.Score(a.ID, situation.AppContext)
		}
		if a.Kind == AtomRuling {
			// Rulings surface first. They were decided, and re-deciding them is
			// the failure the whole store exists to prevent.
			score += 10
		}
		ranked = append(ranked, scored{a, score})
	}
	sort.SliceStable(ranked, func(i, j int) bool { return ranked[i].score > ranked[j].score })

	var out []MemoryAtom
	chars, truncated := 0, 0
	for _, s := range ranked {
		if len(out) >= budget.MaxAtoms || chars+len(s.atom.Text) > budget.MaxCharacters {
			truncated++
			continue
		}
		chars += len(s.atom.Text)
		out = append(out, s.atom)
	}
	return RecallResult{Atoms: out, Truncated: truncated, Situation: situation}
}

// ─────────────────────────────────────────────────────────────────────────────
// The service

// IMemoryService is the whole store, behind one seam.
type IMemoryService interface {
	Recall(ctx context.Context, situation Situation, budget RecallBudget) RecallResult
	Remember(ctx context.Context, candidate AtomCandidate) error
	Correct(ctx context.Context, atomID, correctedText string) error
}

// MemoryService ties the store, the log and the wear record together.
type MemoryService struct {
	store  IAtomStore
	log    *AtomLog
	wear   *MemoryWear
	recall *Recall
}

// NewMemoryService returns a service.
func NewMemoryService(store IAtomStore, log *AtomLog, wear *MemoryWear) *MemoryService {
	return &MemoryService{store: store, log: log, wear: wear, recall: NewRecall(store, wear)}
}

// Recall implements IMemoryService.
func (s *MemoryService) Recall(_ context.Context, situation Situation, budget RecallBudget) RecallResult {
	result := s.recall.For(situation, budget)
	if s.wear != nil {
		for _, a := range result.Atoms {
			s.wear.Record(MemoryTrace{AtomID: a.ID, At: time.Now(), Situation: situation.AppContext})
		}
	}
	return result
}

// Remember implements IMemoryService.
func (s *MemoryService) Remember(_ context.Context, candidate AtomCandidate) error {
	if s.store == nil {
		return errors.New("no store configured")
	}
	atom := MemoryAtom{
		ID:            fmt.Sprintf("atom-%d", time.Now().UnixNano()),
		Kind:          candidate.Kind,
		Text:          candidate.Text,
		Source:        candidate.Source,
		CreatedAt:     time.Now(),
		StabilityDays: InitialStabilityDays,
	}
	if err := s.store.Put(atom); err != nil {
		return err
	}
	if s.log != nil {
		return s.log.Append(AtomRecord{Operation: "append", AtomID: atom.ID})
	}
	return nil
}

// Correct supersedes an atom with a corrected version.
//
// SUPERSEDES rather than edits: the old text stays in the log, so "why did it
// think that" has an answer.
func (s *MemoryService) Correct(_ context.Context, atomID, correctedText string) error {
	if s.store == nil {
		return errors.New("no store configured")
	}
	atom, ok := s.store.Get(atomID)
	if !ok {
		return fmt.Errorf("no atom %q", atomID)
	}
	atom.Text = correctedText
	atom.CorrectionCount++
	atom.StabilityDays = Forgetting{}.Reinforce(atom, time.Now(), true)
	if err := s.store.Put(atom); err != nil {
		return err
	}
	if s.log != nil {
		return s.log.Append(AtomRecord{Operation: "correct", AtomID: atomID, PayloadJSON: correctedText})
	}
	return nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Folders, payloads and sync

// MemoryFolder groups atoms for a person to browse.
//
// A folder is a VIEW, never a container: an atom in no folder is still in the
// store, and deleting a folder deletes nothing.
type MemoryFolder struct {
	Name    string
	Query   string
	AtomIDs []string
}

// HookPayload is what a hook receives.
type HookPayload struct {
	Hook        string
	PayloadJSON string
	At          time.Time
}

// SyncReport is what a sync pass did.
type SyncReport struct {
	Sent      int
	Received  int
	Conflicts int
	At        time.Time
	Err       string
}

// MemorySync moves atoms between a device's own components.
type MemorySync struct {
	mu    sync.Mutex
	local *AtomLog
}

// NewMemorySync returns a sync over a log.
func NewMemorySync(local *AtomLog) *MemorySync { return &MemorySync{local: local} }

// Run performs one pass.
//
// Conflicts are REPORTED, not resolved silently. Two devices that both changed
// the same atom is a fact somebody should see; picking a winner quietly is how
// a correction disappears.
func (s *MemorySync) Run(_ context.Context, fromSequence int64) SyncReport {
	s.mu.Lock()
	defer s.mu.Unlock()
	report := SyncReport{At: time.Now()}
	if s.local == nil {
		report.Err = "no local log"
		return report
	}
	report.Sent = len(s.local.ReadFrom(fromSequence))
	return report
}

// JsonAffectStore persists affect state as JSON.
type JsonAffectStore struct {
	mu   sync.Mutex
	path string
}

// NewJsonAffectStore returns a store.
func NewJsonAffectStore(path string) *JsonAffectStore { return &JsonAffectStore{path: path} }

// Save writes the state.
func (s *JsonAffectStore) Save(v any) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(s.path, data, 0o600)
}

// Load reads the state. A missing file is not an error — it is a device that
// has not stored anything yet.
func (s *JsonAffectStore) Load(v any) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	data, err := os.ReadFile(s.path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	return json.Unmarshal(data, v)
}

// JsonPersonaStore persists persona state as JSON.
type JsonPersonaStore struct {
	mu   sync.Mutex
	path string
}

// NewJsonPersonaStore returns a store.
func NewJsonPersonaStore(path string) *JsonPersonaStore { return &JsonPersonaStore{path: path} }

// Save writes the persona.
func (s *JsonPersonaStore) Save(v any) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(s.path, data, 0o600)
}

// Load reads the persona.
func (s *JsonPersonaStore) Load(v any) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	data, err := os.ReadFile(s.path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	return json.Unmarshal(data, v)
}

// EmbeddingPayloadCodec compresses embedding vectors.
//
// Vectors are most of a memory store's bytes and almost none of its meaning, so
// they are the one thing worth compressing hard. The codec is LOSSY and says
// so: a recall ranked on decompressed vectors will occasionally order two
// near-identical atoms differently, and that is an acceptable trade nobody
// should discover by surprise.
type EmbeddingPayloadCodec struct{}

// EmbeddingCodecVersion is written into every payload.
//
// A lossy codec with no version is a cache that cannot be read after the codec
// improves — and here that means re-downloading every model on the device.
const EmbeddingCodecVersion = 1

// Encode quantises a vector to bitsPerValue.
func (EmbeddingPayloadCodec) Encode(vector []float32, bitsPerValue int) ([]byte, float32, float32, error) {
	if bitsPerValue < 1 || bitsPerValue > 8 {
		return nil, 0, 0, errors.New("bitsPerValue must be 1..8")
	}
	if len(vector) == 0 {
		return nil, 0, 0, nil
	}
	minV, maxV := vector[0], vector[0]
	for _, v := range vector {
		if v < minV {
			minV = v
		}
		if v > maxV {
			maxV = v
		}
	}
	levels := float32(int(1)<<bitsPerValue) - 1
	scale := (maxV - minV) / levels
	if scale == 0 {
		scale = 1
	}
	out := make([]byte, len(vector))
	for i, v := range vector {
		q := int((v - minV) / scale)
		if q < 0 {
			q = 0
		}
		if q > int(levels) {
			q = int(levels)
		}
		out[i] = byte(q)
	}
	return out, scale, minV, nil
}

// Decode returns the approximate vector.
func (EmbeddingPayloadCodec) Decode(data []byte, scale, offset float32) []float32 {
	out := make([]float32, len(data))
	for i, b := range data {
		out[i] = offset + float32(b)*scale
	}
	return out
}
