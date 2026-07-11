// family_board_test.go
//
// Verifies the CircleAI.Family port (family_board.go): member add/get + name
// ordering, event schedule + EventsForMember (membership filter, earliest-first,
// defensive MemberIds copy), and expense record + TotalPaidBy / SpendByCategory
// (since filter; category case-insensitive).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestFamily_MembersAndEvents(t *testing.T) {
	b := circleai.NewInMemoryFamilyBoard()
	dob := time.Date(1990, 1, 1, 0, 0, 0, 0, time.UTC)
	b.Add(circleai.FamilyMember{MemberId: "m1", Name: "Thabo", Role: "Parent", DateOfBirth: dob})
	b.Add(circleai.FamilyMember{MemberId: "m2", Name: "Ama", Role: "Child", DateOfBirth: dob})

	if m, ok := b.GetMember("m1"); !ok || m.Name != "Thabo" {
		t.Fatalf("get member = %+v ok=%v", m, ok)
	}
	mem := b.Members()
	if len(mem) != 2 || mem[0].Name != "Ama" || mem[1].Name != "Thabo" {
		t.Fatalf("members name order wrong: %+v", mem)
	}

	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	ids := []string{"m1", "m2"}
	b.Schedule(circleai.FamilyEvent{EventId: "e1", Title: "Braai", AtUtc: t0.Add(48 * time.Hour), MemberIds: ids})
	b.Schedule(circleai.FamilyEvent{EventId: "e2", Title: "Dentist", AtUtc: t0.Add(24 * time.Hour), MemberIds: []string{"m2"}})
	ids[0] = "MUTATED" // must not affect stored event

	forM1 := b.EventsForMember("m1")
	if len(forM1) != 1 || forM1[0].EventId != "e1" {
		t.Fatalf("events for m1 wrong (defensive copy?): %+v", forM1)
	}
	forM2 := b.EventsForMember("m2")
	// e2 (t0+24h) then e1 (t0+48h), earliest first.
	if len(forM2) != 2 || forM2[0].EventId != "e2" || forM2[1].EventId != "e1" {
		t.Fatalf("events for m2 order wrong: %+v", forM2)
	}
}

func TestFamily_Expenses(t *testing.T) {
	b := circleai.NewInMemoryFamilyBoard()
	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	since := t0.Add(-time.Hour)
	old := t0.AddDate(0, 0, -30)
	b.Record(circleai.SharedExpense{ExpenseId: "x1", PaidById: "m1", Amount: circleai.DecimalFromInt(200), Currency: "ZAR", Category: "Groceries", AtUtc: t0})
	b.Record(circleai.SharedExpense{ExpenseId: "x2", PaidById: "m1", Amount: circleai.DecimalFromInt(50), Currency: "ZAR", Category: "groceries", AtUtc: t0.Add(time.Hour)})
	b.Record(circleai.SharedExpense{ExpenseId: "x3", PaidById: "m2", Amount: circleai.DecimalFromInt(500), Currency: "ZAR", Category: "Rent", AtUtc: t0})
	b.Record(circleai.SharedExpense{ExpenseId: "x4", PaidById: "m1", Amount: circleai.DecimalFromInt(999), Currency: "ZAR", Category: "Groceries", AtUtc: old}) // before since

	// m1 paid at/after since: 200 + 50 = 250 (x4 excluded).
	if got := b.TotalPaidBy("m1", since); !got.Equal(circleai.DecimalFromInt(250)) {
		t.Fatalf("total paid by m1 = %s, want 250", got)
	}
	// Groceries (case-insensitive) at/after since: 200 + 50 = 250.
	if got := b.SpendByCategory("GROCERIES", since); !got.Equal(circleai.DecimalFromInt(250)) {
		t.Fatalf("groceries spend = %s, want 250", got)
	}
}
