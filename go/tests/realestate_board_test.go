// realestate_board_test.go
//
// Verifies the CircleAI.RealEstate port (realestate_board.go): PropertyKind enum
// ordinals/names, register/list/close, ActiveInSuburb (active + suburb filter,
// newest-first, blank error), and SuburbAverage (exact decimal mean; absent when
// no active listings).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestRealEstate_EnumOrdinals(t *testing.T) {
	if circleai.PropertyKindApartment != 0 || circleai.PropertyKindLand != 4 {
		t.Fatalf("PropertyKind ordinals wrong")
	}
	if circleai.PropertyKindTownhouse.String() != "Townhouse" {
		t.Fatalf("PropertyKind name wrong")
	}
}

func TestRealEstate_ActiveInSuburbAndAverage(t *testing.T) {
	b := circleai.NewInMemoryRealEstateBoard()
	b.RegisterProperty(circleai.Property{PropertyId: "p1", Suburb: "Sandton", Kind: circleai.PropertyKindApartment, Beds: 2, Baths: 1, FloorAreaM2: 80})
	b.RegisterProperty(circleai.Property{PropertyId: "p2", Suburb: "Sandton", Kind: circleai.PropertyKindHouse, Beds: 4, Baths: 3, FloorAreaM2: 250})
	b.RegisterProperty(circleai.Property{PropertyId: "p3", Suburb: "Rosebank", Kind: circleai.PropertyKindApartment, Beds: 1, Baths: 1, FloorAreaM2: 55})

	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.List(circleai.Listing{ListingId: "L1", PropertyId: "p1", AskingPrice: circleai.DecimalFromInt(1000000), Currency: "ZAR", ListedUtc: t0, IsActive: true})
	b.List(circleai.Listing{ListingId: "L2", PropertyId: "p2", AskingPrice: circleai.DecimalFromInt(3000000), Currency: "ZAR", ListedUtc: t0.Add(24 * time.Hour), IsActive: true})
	b.List(circleai.Listing{ListingId: "L3", PropertyId: "p3", AskingPrice: circleai.DecimalFromInt(900000), Currency: "ZAR", ListedUtc: t0, IsActive: true})

	active, err := b.ActiveInSuburb("sandton") // case-insensitive
	if err != nil {
		t.Fatalf("active in suburb: %v", err)
	}
	// L1 + L2 active in Sandton; newest first -> L2 (t0+24h) then L1.
	if len(active) != 2 || active[0].ListingId != "L2" || active[1].ListingId != "L1" {
		t.Fatalf("active listings wrong: %+v", active)
	}

	// Average of 1,000,000 and 3,000,000 = 2,000,000.
	avg, ok, err := b.SuburbAverage("Sandton")
	if err != nil || !ok || !avg.Equal(circleai.DecimalFromInt(2000000)) {
		t.Fatalf("suburb average = %s ok=%v err=%v, want 2000000", avg, ok, err)
	}

	// Close a listing -> excluded.
	if err := b.Close("L1"); err != nil {
		t.Fatalf("close: %v", err)
	}
	if err := b.Close("ghost"); err == nil {
		t.Fatalf("unknown listing close must error")
	}
	active2, _ := b.ActiveInSuburb("Sandton")
	if len(active2) != 1 || active2[0].ListingId != "L2" {
		t.Fatalf("after close active wrong: %+v", active2)
	}

	// No active listings in a suburb -> (zero, false).
	if _, ok, _ := b.SuburbAverage("Nowhere"); ok {
		t.Fatalf("empty suburb average must be absent")
	}
	if _, err := b.ActiveInSuburb(" "); err == nil {
		t.Fatalf("blank suburb must error")
	}
}
