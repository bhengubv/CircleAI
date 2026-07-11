// legal_board.go
//
// Ports the CircleAI.Legal primitive vertical (LegalPrimitives.cs):
//   Matter / Contract / LegalDeadline / Clause (records) -> value structs
//   ILegalBoard        -> LegalBoard interface (I-prefix dropped)
//   InMemoryLegalBoard -> InMemoryLegalBoard
//
// The LegalDomainContext (static prompt/compliance strings) and
// LegalCompanionAdapter (LLM-prompt wrapper over ICompanionSession) are out of
// scope for the deterministic in-memory board and are not ported.
//
// DETERMINISM: the C# orders active matters by OpenedAtUtc descending, contracts
// by ExpiryDate ascending, and deadlines by DueOn ascending over
// ConcurrentDictionary values (unspecified order); this port keeps those primary
// orders and adds a stable id tiebreak for equal keys. ClausesByTag/ActiveMatters
// ties likewise resolve deterministically.

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// Matter is a legal matter. Ports the Matter record. Open reports whether the
// matter is still active.
type Matter struct {
	MatterId     string
	Title        string
	Jurisdiction string
	Client       string
	OpenedAtUtc  time.Time
	Open         bool
}

// Contract is a contract attached to a matter. Ports the Contract record.
// ExpiryDate is a pointer to mirror the nullable C# DateTime? (nil == no expiry).
// Counterparties is copied defensively on store.
type Contract struct {
	ContractId     string
	MatterId       string
	Title          string
	EffectiveDate  time.Time
	ExpiryDate     *time.Time
	Counterparties []string
}

// LegalDeadline is a dated obligation on a matter. Ports the LegalDeadline record.
type LegalDeadline struct {
	DeadlineId  string
	MatterId    string
	Description string
	DueOn       time.Time
}

// Clause is a reusable clause-library entry. Ports the Clause record.
type Clause struct {
	ClauseId string
	Title    string
	Body     string
	Tags     []string
}

// LegalBoard is the legal matters/contracts/deadlines/clauses board. Ports
// ILegalBoard. ActiveMatters is exposed as a method (Go has no property getters).
type LegalBoard interface {
	Open(m Matter)
	// Close marks a matter closed; errors if the id is unknown.
	Close(matterId string) error
	GetMatter(id string) (Matter, bool)
	// ActiveMatters lists open matters, most recently opened first.
	ActiveMatters() []Matter
	AddContract(c Contract)
	// ContractsExpiringBefore lists contracts with an expiry on or before date,
	// soonest expiry first.
	ContractsExpiringBefore(date time.Time) []Contract
	Add(d LegalDeadline)
	// UpcomingDeadlines lists deadlines due on or after now, soonest first.
	UpcomingDeadlines(now time.Time) []LegalDeadline
	AddClause(c Clause)
	// ClausesByTag lists clauses carrying tag (case-insensitive); errors on blank tag.
	ClausesByTag(tag string) ([]Clause, error)
}

// InMemoryLegalBoard is a concurrency-safe in-memory LegalBoard. Ports
// InMemoryLegalBoard.
type InMemoryLegalBoard struct {
	mu        sync.RWMutex
	matters   map[string]Matter
	contracts map[string]Contract
	deadlines map[string]LegalDeadline
	clauses   map[string]Clause
}

// NewInMemoryLegalBoard constructs an empty board.
func NewInMemoryLegalBoard() *InMemoryLegalBoard {
	return &InMemoryLegalBoard{
		matters:   make(map[string]Matter),
		contracts: make(map[string]Contract),
		deadlines: make(map[string]LegalDeadline),
		clauses:   make(map[string]Clause),
	}
}

// Open stores (or replaces by MatterId) a matter. Ports Open.
func (b *InMemoryLegalBoard) Open(m Matter) {
	b.mu.Lock()
	b.matters[m.MatterId] = m
	b.mu.Unlock()
}

// Close sets a matter's Open flag to false. Ports Close (throws on unknown id).
func (b *InMemoryLegalBoard) Close(matterId string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	m, ok := b.matters[matterId]
	if !ok {
		return errors.New("Unknown matter " + matterId)
	}
	m.Open = false
	b.matters[matterId] = m
	return nil
}

// GetMatter returns the matter for id and true, or (zero, false) if absent.
func (b *InMemoryLegalBoard) GetMatter(id string) (Matter, bool) {
	b.mu.RLock()
	m, ok := b.matters[id]
	b.mu.RUnlock()
	return m, ok
}

// ActiveMatters lists open matters ordered by OpenedAtUtc descending. Ports the
// ActiveMatters property.
func (b *InMemoryLegalBoard) ActiveMatters() []Matter {
	b.mu.RLock()
	out := make([]Matter, 0)
	for _, m := range b.matters {
		if m.Open {
			out = append(out, m)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].OpenedAtUtc.Equal(out[j].OpenedAtUtc) {
			return out[i].OpenedAtUtc.After(out[j].OpenedAtUtc)
		}
		return out[i].MatterId < out[j].MatterId
	})
	return out
}

// AddContract stores (or replaces by ContractId) a contract, copying its
// counterparties defensively. Ports AddContract.
func (b *InMemoryLegalBoard) AddContract(c Contract) {
	c.Counterparties = append([]string(nil), c.Counterparties...)
	b.mu.Lock()
	b.contracts[c.ContractId] = c
	b.mu.Unlock()
}

// ContractsExpiringBefore lists contracts whose (non-nil) expiry is on or before
// date, ordered by expiry ascending. Ports ContractsExpiringBefore.
func (b *InMemoryLegalBoard) ContractsExpiringBefore(date time.Time) []Contract {
	b.mu.RLock()
	out := make([]Contract, 0)
	for _, c := range b.contracts {
		if c.ExpiryDate != nil && !c.ExpiryDate.After(date) {
			out = append(out, c)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].ExpiryDate.Equal(*out[j].ExpiryDate) {
			return out[i].ExpiryDate.Before(*out[j].ExpiryDate)
		}
		return out[i].ContractId < out[j].ContractId
	})
	return out
}

// Add stores (or replaces by DeadlineId) a deadline. Ports Add(LegalDeadline).
func (b *InMemoryLegalBoard) Add(d LegalDeadline) {
	b.mu.Lock()
	b.deadlines[d.DeadlineId] = d
	b.mu.Unlock()
}

// UpcomingDeadlines lists deadlines due on or after now, ordered by DueOn
// ascending. Ports UpcomingDeadlines.
func (b *InMemoryLegalBoard) UpcomingDeadlines(now time.Time) []LegalDeadline {
	b.mu.RLock()
	out := make([]LegalDeadline, 0)
	for _, d := range b.deadlines {
		if !d.DueOn.Before(now) {
			out = append(out, d)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].DueOn.Equal(out[j].DueOn) {
			return out[i].DueOn.Before(out[j].DueOn)
		}
		return out[i].DeadlineId < out[j].DeadlineId
	})
	return out
}

// AddClause stores (or replaces by ClauseId) a clause, copying its tags. Ports
// AddClause.
func (b *InMemoryLegalBoard) AddClause(c Clause) {
	c.Tags = append([]string(nil), c.Tags...)
	b.mu.Lock()
	b.clauses[c.ClauseId] = c
	b.mu.Unlock()
}

// ClausesByTag lists clauses carrying tag (case-insensitive match). Ports
// ClausesByTag (throws ArgumentException on blank tag -> error). Result order is
// unspecified in C# (ConcurrentDictionary values); sorted by ClauseId here for
// determinism.
func (b *InMemoryLegalBoard) ClausesByTag(tag string) ([]Clause, error) {
	if strings.TrimSpace(tag) == "" {
		return nil, errors.New("tag required")
	}
	b.mu.RLock()
	out := make([]Clause, 0)
	for _, c := range b.clauses {
		for _, t := range c.Tags {
			if strings.EqualFold(t, tag) {
				out = append(out, c)
				break
			}
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].ClauseId < out[j].ClauseId })
	return out, nil
}

// Interface guard.
var _ LegalBoard = (*InMemoryLegalBoard)(nil)
