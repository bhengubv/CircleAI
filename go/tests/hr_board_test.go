// hr_board_test.go
//
// Verifies the CircleAI.HR port (hr_board.go): hire/get, Employees name-ordering,
// leave request + decide (unknown-id error) + pending filter, and AvgRatingFor
// (mean over reviews; 0.0 when none).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestHR_HireAndEmployeesOrdered(t *testing.T) {
	b := circleai.NewInMemoryHRBoard()
	b.Hire(circleai.Employee{EmployeeId: "e1", Name: "Zola", Role: "Dev", Salary: circleai.DecimalFromInt(50000), Currency: "ZAR"})
	b.Hire(circleai.Employee{EmployeeId: "e2", Name: "Ada", Role: "PM", Salary: circleai.DecimalFromInt(60000), Currency: "ZAR"})

	if e, ok := b.GetEmployee("e1"); !ok || e.Name != "Zola" {
		t.Fatalf("get e1 = %+v ok=%v", e, ok)
	}
	if _, ok := b.GetEmployee("ghost"); ok {
		t.Fatalf("unknown employee must be absent")
	}
	emps := b.Employees()
	if len(emps) != 2 || emps[0].Name != "Ada" || emps[1].Name != "Zola" {
		t.Fatalf("employees name order wrong: %+v", emps)
	}
}

func TestHR_LeaveDecisionAndPending(t *testing.T) {
	b := circleai.NewInMemoryHRBoard()
	from := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Request(circleai.LeaveRequest{RequestId: "r1", EmployeeId: "e1", Kind: "Annual", From: from, To: from.AddDate(0, 0, 5), Status: "Pending"})
	b.Request(circleai.LeaveRequest{RequestId: "r2", EmployeeId: "e2", Kind: "Sick", From: from, To: from, Status: "pending"})

	if len(b.PendingLeaves()) != 2 {
		t.Fatalf("pending should be 2")
	}
	if err := b.DecideLeave("r1", "Approved"); err != nil {
		t.Fatalf("decide: %v", err)
	}
	if err := b.DecideLeave("ghost", "Approved"); err == nil {
		t.Fatalf("unknown leave request must error")
	}
	pend := b.PendingLeaves()
	if len(pend) != 1 || pend[0].RequestId != "r2" {
		t.Fatalf("pending after decision wrong: %+v", pend)
	}
}

func TestHR_AvgRating(t *testing.T) {
	b := circleai.NewInMemoryHRBoard()
	when := time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC)
	b.Review(circleai.PerformanceReview{ReviewId: "v1", EmployeeId: "e1", ReviewedOn: when, RatingOutOf5: 4})
	b.Review(circleai.PerformanceReview{ReviewId: "v2", EmployeeId: "e1", ReviewedOn: when, RatingOutOf5: 2})
	if got := b.AvgRatingFor("e1"); got != 3.0 {
		t.Fatalf("avg rating = %v, want 3.0", got)
	}
	// No reviews -> 0.0 (DefaultIfEmpty(0).Average()).
	if got := b.AvgRatingFor("nobody"); got != 0.0 {
		t.Fatalf("avg rating no-reviews = %v, want 0.0", got)
	}
}
