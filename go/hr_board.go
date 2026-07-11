// hr_board.go
//
// Ports the CircleAI.HR primitive vertical (HRPrimitives.cs):
//   Employee / LeaveRequest / PerformanceReview (records) -> value structs
//   IHRBoard        -> HRBoard interface (I-prefix dropped)
//   InMemoryHRBoard -> InMemoryHRBoard
//
// The HRDomainContext (static prompt strings) and HRCompanionAdapter (LLM-prompt
// wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: Employees orders by Name (C# OrderBy(Name), culture-sensitive
// default comparer -> cultureLess over the ASCII names). PendingLeaves keeps no
// defined C# order (ConcurrentDictionary values); this port sorts by RequestId
// for stable output. AvgRatingFor reproduces DefaultIfEmpty(0).Average() -> 0.0
// when the employee has no reviews (NOT NaN). Salary uses the shared exact
// Decimal (C# decimal).

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// Employee is an employee. Ports the Employee record. Salary uses exact Decimal.
type Employee struct {
	EmployeeId string
	Name       string
	Role       string
	HiredOn    time.Time
	Salary     Decimal
	Currency   string
}

// LeaveRequest is a leave request. Ports the LeaveRequest record.
type LeaveRequest struct {
	RequestId  string
	EmployeeId string
	Kind       string
	From       time.Time
	To         time.Time
	Status     string
}

// PerformanceReview is a performance review. Ports the PerformanceReview record.
type PerformanceReview struct {
	ReviewId     string
	EmployeeId   string
	ReviewedOn   time.Time
	RatingOutOf5 int
	Notes        string
}

// HRBoard is the employees/leave/reviews board. Ports IHRBoard. Employees is
// exposed as a method.
type HRBoard interface {
	Hire(e Employee)
	GetEmployee(id string) (Employee, bool)
	// Employees lists all employees ordered by Name ascending.
	Employees() []Employee
	Request(r LeaveRequest)
	// DecideLeave sets a leave request's status; errors if the id is unknown.
	DecideLeave(requestId, decision string) error
	// PendingLeaves lists leave requests whose Status is "Pending" (case-insensitive).
	PendingLeaves() []LeaveRequest
	Review(r PerformanceReview)
	// AvgRatingFor is the mean RatingOutOf5 across an employee's reviews (0 if none).
	AvgRatingFor(employeeId string) float64
}

// InMemoryHRBoard is a concurrency-safe in-memory HRBoard. Ports InMemoryHRBoard
// (employees + leaves in maps; reviews in an ordered list guarded by the mutex).
type InMemoryHRBoard struct {
	mu        sync.RWMutex
	employees map[string]Employee
	leaves    map[string]LeaveRequest
	reviews   []PerformanceReview
}

// NewInMemoryHRBoard constructs an empty board.
func NewInMemoryHRBoard() *InMemoryHRBoard {
	return &InMemoryHRBoard{
		employees: make(map[string]Employee),
		leaves:    make(map[string]LeaveRequest),
		reviews:   make([]PerformanceReview, 0),
	}
}

// Hire stores (or replaces by EmployeeId) an employee. Ports Hire.
func (b *InMemoryHRBoard) Hire(e Employee) {
	b.mu.Lock()
	b.employees[e.EmployeeId] = e
	b.mu.Unlock()
}

// GetEmployee returns the employee for id and true, or (zero, false) if absent.
// Ports GetEmployee.
func (b *InMemoryHRBoard) GetEmployee(id string) (Employee, bool) {
	b.mu.RLock()
	e, ok := b.employees[id]
	b.mu.RUnlock()
	return e, ok
}

// Employees lists all employees ordered by Name ascending. Ports the Employees
// property (OrderBy(Name)).
func (b *InMemoryHRBoard) Employees() []Employee {
	b.mu.RLock()
	out := make([]Employee, 0, len(b.employees))
	for _, e := range b.employees {
		out = append(out, e)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out
}

// Request stores (or replaces by RequestId) a leave request. Ports Request.
func (b *InMemoryHRBoard) Request(r LeaveRequest) {
	b.mu.Lock()
	b.leaves[r.RequestId] = r
	b.mu.Unlock()
}

// DecideLeave mutates a leave request's status. Ports DecideLeave (throws on
// unknown id -> error).
func (b *InMemoryHRBoard) DecideLeave(requestId, decision string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	r, ok := b.leaves[requestId]
	if !ok {
		return errors.New("Unknown leave request " + requestId)
	}
	r.Status = decision
	b.leaves[requestId] = r
	return nil
}

// PendingLeaves lists leave requests whose Status is "Pending" (case-insensitive),
// sorted by RequestId for determinism. Ports PendingLeaves.
func (b *InMemoryHRBoard) PendingLeaves() []LeaveRequest {
	b.mu.RLock()
	out := make([]LeaveRequest, 0)
	for _, r := range b.leaves {
		if strings.EqualFold(r.Status, "Pending") {
			out = append(out, r)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].RequestId < out[j].RequestId })
	return out
}

// Review appends a performance review. Ports Review.
func (b *InMemoryHRBoard) Review(r PerformanceReview) {
	b.mu.Lock()
	b.reviews = append(b.reviews, r)
	b.mu.Unlock()
}

// AvgRatingFor returns the mean RatingOutOf5 over an employee's reviews, or 0.0
// when there are none. Ports AvgRatingFor (DefaultIfEmpty(0).Average()).
func (b *InMemoryHRBoard) AvgRatingFor(employeeId string) float64 {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var sum, n int
	for _, r := range b.reviews {
		if r.EmployeeId == employeeId {
			sum += r.RatingOutOf5
			n++
		}
	}
	if n == 0 {
		return 0.0
	}
	return float64(sum) / float64(n)
}

// Interface guard.
var _ HRBoard = (*InMemoryHRBoard)(nil)
