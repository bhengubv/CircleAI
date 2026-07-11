// retail_board_test.go
//
// Verifies the CircleAI.Retail port (retail_board.go): product add/get, stock
// set/get, RecordSale (append + stock decrement, unknown-SKU error), RevenueToday
// (same-day sum of UnitPrice*Quantity), and TopSellersSince (units-descending,
// topK cap, first-seen tie order).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestRetail_ProductsStockAndSale(t *testing.T) {
	b := circleai.NewInMemoryRetailBoard()
	b.AddProduct(circleai.Product{Sku: "A", Name: "Widget", Price: circleai.DecimalFromInt(10), Currency: "ZAR"})
	b.SetStock(circleai.StockLevel{Sku: "A", Quantity: 100})

	if p, ok := b.GetProduct("A"); !ok || p.Name != "Widget" {
		t.Fatalf("get product = %+v ok=%v", p, ok)
	}
	if b.Stock("A") != 100 {
		t.Fatalf("stock = %d, want 100", b.Stock("A"))
	}
	if b.Stock("Z") != 0 {
		t.Fatalf("unknown stock must be 0")
	}

	now := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	if err := b.RecordSale(circleai.Sale{SaleId: "s1", Sku: "A", Quantity: 3, UnitPrice: circleai.DecimalFromInt(10), AtUtc: now}); err != nil {
		t.Fatalf("record sale: %v", err)
	}
	if b.Stock("A") != 97 {
		t.Fatalf("stock after sale = %d, want 97", b.Stock("A"))
	}
	// Unknown SKU errors.
	if err := b.RecordSale(circleai.Sale{SaleId: "s2", Sku: "GHOST", Quantity: 1, UnitPrice: circleai.DecimalFromInt(1), AtUtc: now}); err == nil {
		t.Fatalf("unknown SKU sale must error")
	}
}

func TestRetail_RevenueToday(t *testing.T) {
	b := circleai.NewInMemoryRetailBoard()
	b.AddProduct(circleai.Product{Sku: "A", Name: "A", Price: circleai.DecimalFromInt(10), Currency: "ZAR"})
	now := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	yesterday := now.AddDate(0, 0, -1)
	_ = b.RecordSale(circleai.Sale{SaleId: "s1", Sku: "A", Quantity: 2, UnitPrice: circleai.DecimalFromInt(10), AtUtc: now})
	_ = b.RecordSale(circleai.Sale{SaleId: "s2", Sku: "A", Quantity: 5, UnitPrice: circleai.DecimalFromInt(10), AtUtc: now.Add(2 * time.Hour)})
	_ = b.RecordSale(circleai.Sale{SaleId: "s3", Sku: "A", Quantity: 9, UnitPrice: circleai.DecimalFromInt(10), AtUtc: yesterday})

	// Today: (2 + 5) * 10 = 70. Yesterday's 90 excluded.
	if rev := b.RevenueToday(now); !rev.Equal(circleai.DecimalFromInt(70)) {
		t.Fatalf("revenue today = %s, want 70", rev)
	}
}

func TestRetail_TopSellers(t *testing.T) {
	b := circleai.NewInMemoryRetailBoard()
	for _, sku := range []string{"A", "B", "C"} {
		b.AddProduct(circleai.Product{Sku: sku, Name: sku, Price: circleai.DecimalFromInt(1), Currency: "ZAR"})
	}
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	since := base.Add(-time.Hour)
	_ = b.RecordSale(circleai.Sale{SaleId: "s1", Sku: "A", Quantity: 5, UnitPrice: circleai.DecimalFromInt(1), AtUtc: base})
	_ = b.RecordSale(circleai.Sale{SaleId: "s2", Sku: "B", Quantity: 8, UnitPrice: circleai.DecimalFromInt(1), AtUtc: base})
	_ = b.RecordSale(circleai.Sale{SaleId: "s3", Sku: "A", Quantity: 4, UnitPrice: circleai.DecimalFromInt(1), AtUtc: base})                   // A -> 9 total
	_ = b.RecordSale(circleai.Sale{SaleId: "s4", Sku: "C", Quantity: 1, UnitPrice: circleai.DecimalFromInt(1), AtUtc: base.AddDate(0, 0, -5)}) // before `since`, excluded

	top, err := b.TopSellersSince(since, 5)
	if err != nil {
		t.Fatalf("top sellers: %v", err)
	}
	// A=9, B=8 (C excluded by time). Descending: A then B.
	if len(top) != 2 || top[0].Sku != "A" || top[0].Sold != 9 || top[1].Sku != "B" || top[1].Sold != 8 {
		t.Fatalf("top sellers wrong: %+v", top)
	}
	// topK cap.
	if one, _ := b.TopSellersSince(since, 1); len(one) != 1 || one[0].Sku != "A" {
		t.Fatalf("topK cap wrong: %+v", one)
	}
	if _, err := b.TopSellersSince(since, 0); err == nil {
		t.Fatalf("topK<=0 must error")
	}
}
