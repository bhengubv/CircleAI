// crm_board_test.go
//
// Verifies the CircleAI.CRM port (crm_board.go): contact upsert/get/search
// (name+email substring, case-insensitive order, topK cap, validation), deal
// upsert/get/list-by-stage (case-insensitive, value-descending), activity
// append/read (newest-first, limit cap, empty for unknown), and the Null
// fail-closed backends.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func strp(s string) *string { return &s }

func TestCRM_ContactSearch(t *testing.T) {
	ctx := context.Background()
	s := circleai.NewInMemoryContactStore()
	if s.BackendId() != "in-memory" {
		t.Fatalf("backend id = %q", s.BackendId())
	}
	_ = s.Upsert(ctx, circleai.Contact{ContactId: "c1", FullName: "Alice Zulu", Email: strp("alice@example.com")})
	_ = s.Upsert(ctx, circleai.Contact{ContactId: "c2", FullName: "bob ndlovu", Email: strp("BOB@corp.io")})
	_ = s.Upsert(ctx, circleai.Contact{ContactId: "c3", FullName: "Carol Khan"})

	// blank ContactId errors.
	if err := s.Upsert(ctx, circleai.Contact{ContactId: "  ", FullName: "x"}); err == nil {
		t.Fatalf("blank ContactId must error")
	}

	got, ok, err := s.Get(ctx, "c2")
	if err != nil || !ok || got.FullName != "bob ndlovu" {
		t.Fatalf("get c2 = %+v ok=%v err=%v", got, ok, err)
	}
	if _, _, err := s.Get(ctx, " "); err == nil {
		t.Fatalf("blank id get must error")
	}

	// Name substring, case-insensitive. Query "a" matches "Alice Zulu" (c1, name +
	// email) and "Carol Khan" (c3, name); "bob ndlovu" (c2) has no 'a' in name or
	// its email "BOB@corp.io".
	hits, err := s.Search(ctx, "a", 20)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(hits) != 2 {
		t.Fatalf("search 'a' = %d hits, want 2: %+v", len(hits), hits)
	}
	// Ordered by FullName OrdinalIgnoreCase: "Alice Zulu" < "Carol Khan".
	if hits[0].ContactId != "c1" || hits[1].ContactId != "c3" {
		t.Fatalf("search order wrong: %v, %v", hits[0].ContactId, hits[1].ContactId)
	}

	// Email substring match, case-insensitive.
	byEmail, _ := s.Search(ctx, "corp.io", 20)
	if len(byEmail) != 1 || byEmail[0].ContactId != "c2" {
		t.Fatalf("email search = %+v", byEmail)
	}

	// topK cap.
	capped, _ := s.Search(ctx, "", 1) // empty query matches all names
	if len(capped) != 1 {
		t.Fatalf("topK cap = %d, want 1", len(capped))
	}
	if _, err := s.Search(ctx, "x", 0); err == nil {
		t.Fatalf("topK<=0 must error")
	}
}

func TestCRM_DealPipeline(t *testing.T) {
	ctx := context.Background()
	p := circleai.NewInMemoryDealPipeline()
	_ = p.Upsert(ctx, circleai.Deal{DealId: "d1", CompanyId: "co1", Name: "Small", Value: circleai.DecimalFromInt(100), Currency: "ZAR", Stage: "Open"})
	_ = p.Upsert(ctx, circleai.Deal{DealId: "d2", CompanyId: "co1", Name: "Big", Value: circleai.DecimalFromInt(500), Currency: "ZAR", Stage: "open"})
	_ = p.Upsert(ctx, circleai.Deal{DealId: "d3", CompanyId: "co1", Name: "Won", Value: circleai.DecimalFromInt(300), Currency: "ZAR", Stage: "Closed"})

	if err := p.Upsert(ctx, circleai.Deal{DealId: " "}); err == nil {
		t.Fatalf("blank DealId must error")
	}
	if _, err := p.ListByStage(ctx, "  "); err == nil {
		t.Fatalf("blank stage must error")
	}

	open, err := p.ListByStage(ctx, "OPEN") // case-insensitive
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(open) != 2 {
		t.Fatalf("open deals = %d, want 2", len(open))
	}
	// Value descending: d2 (500) before d1 (100).
	if open[0].DealId != "d2" || open[1].DealId != "d1" {
		t.Fatalf("value order wrong: %v, %v", open[0].DealId, open[1].DealId)
	}
}

func TestCRM_ActivityLog(t *testing.T) {
	ctx := context.Background()
	l := circleai.NewInMemoryActivityLog()
	base := time.Date(2026, 7, 1, 8, 0, 0, 0, time.UTC)
	_ = l.Append(ctx, circleai.Activity{ActivityId: "a1", ContactId: "c1", Kind: "call", Body: "first", AtUtc: base})
	_ = l.Append(ctx, circleai.Activity{ActivityId: "a2", ContactId: "c1", Kind: "email", Body: "second", AtUtc: base.Add(2 * time.Hour)})
	_ = l.Append(ctx, circleai.Activity{ActivityId: "a3", ContactId: "c2", Kind: "note", Body: "other", AtUtc: base})

	if err := l.Append(ctx, circleai.Activity{ActivityId: "x", ContactId: " "}); err == nil {
		t.Fatalf("blank ContactId must error")
	}

	got, err := l.ReadForContact(ctx, "c1", 100)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if len(got) != 2 {
		t.Fatalf("c1 activities = %d, want 2", len(got))
	}
	// Newest first: a2 (base+2h) before a1 (base).
	if got[0].ActivityId != "a2" || got[1].ActivityId != "a1" {
		t.Fatalf("newest-first order wrong: %v, %v", got[0].ActivityId, got[1].ActivityId)
	}
	// Limit cap.
	if one, _ := l.ReadForContact(ctx, "c1", 1); len(one) != 1 || one[0].ActivityId != "a2" {
		t.Fatalf("limit cap wrong: %+v", one)
	}
	// Unknown contact -> empty.
	if empty, _ := l.ReadForContact(ctx, "ghost", 10); len(empty) != 0 {
		t.Fatalf("unknown contact should be empty, got %d", len(empty))
	}
	if _, err := l.ReadForContact(ctx, " ", 10); err == nil {
		t.Fatalf("blank contactId must error")
	}
}

func TestCRM_NullBackends(t *testing.T) {
	ctx := context.Background()
	if circleai.NullContactStoreInstance.BackendId() != "null" {
		t.Fatalf("null contact backend id wrong")
	}
	_ = circleai.NullContactStoreInstance.Upsert(ctx, circleai.Contact{ContactId: "c1", FullName: "x"})
	if _, ok, _ := circleai.NullContactStoreInstance.Get(ctx, "c1"); ok {
		t.Fatalf("null contact store must report absent")
	}
	if hits, _ := circleai.NullContactStoreInstance.Search(ctx, "x", 5); len(hits) != 0 {
		t.Fatalf("null search must be empty")
	}

	_ = circleai.NullDealPipelineInstance.Upsert(ctx, circleai.Deal{DealId: "d1"})
	if _, ok, _ := circleai.NullDealPipelineInstance.Get(ctx, "d1"); ok {
		t.Fatalf("null deal pipeline must report absent")
	}
	if ds, _ := circleai.NullDealPipelineInstance.ListByStage(ctx, "Open"); len(ds) != 0 {
		t.Fatalf("null list-by-stage must be empty")
	}

	_ = circleai.NullActivityLogInstance.Append(ctx, circleai.Activity{ActivityId: "a1", ContactId: "c1"})
	if as, _ := circleai.NullActivityLogInstance.ReadForContact(ctx, "c1", 10); len(as) != 0 {
		t.Fatalf("null activity read must be empty")
	}
}
