// retail_board.go
//
// Ports the CircleAI.Retail primitive vertical (RetailPrimitives.cs):
//   Product / StockLevel / Sale (records)             -> value structs
//   (string Sku, int Sold) tuple                       -> SellerCount struct
//   IRetailBoard        -> RetailBoard interface (I-prefix dropped)
//   InMemoryRetailBoard -> InMemoryRetailBoard
//
// The RetailDomainContext (static prompt strings) and RetailCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// MONEY: Price / UnitPrice / revenue use the shared exact Decimal (C# decimal).
// RevenueToday sums UnitPrice*Quantity over sales whose calendar date equals
// now's date (both interpreted in their own location, as C# DateTimeOffset.Date).
// TopSellersSince reproduces GroupBy(Sku).Sum(Quantity).OrderByDescending(sold):
// C# groups in first-seen order over the sales list and OrderByDescending is
// stable, so equal totals keep first-seen order — this port does the same.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Product is a retail product. Ports the Product record. Category is a pointer to
// mirror the nullable C# string?. Price uses exact Decimal.
type Product struct {
	Sku      string
	Name     string
	Price    Decimal
	Currency string
	Category *string
}

// StockLevel is a SKU's on-hand quantity. Ports the StockLevel record.
type StockLevel struct {
	Sku      string
	Quantity int
}

// Sale is a recorded sale. Ports the Sale record. UnitPrice uses exact Decimal.
type Sale struct {
	SaleId    string
	Sku       string
	Quantity  int
	UnitPrice Decimal
	AtUtc     time.Time
}

// SellerCount is one (Sku, Sold) row of a top-sellers ranking. Ports the C#
// (string Sku, int Sold) value tuple.
type SellerCount struct {
	Sku  string
	Sold int
}

// DefaultTopSellersTopK is the C# default `topK = 5` for TopSellersSince.
const DefaultTopSellersTopK = 5

// RetailBoard is the products/stock/sales board. Ports IRetailBoard.
type RetailBoard interface {
	AddProduct(p Product)
	GetProduct(sku string) (Product, bool)
	SetStock(l StockLevel)
	// Stock returns the on-hand quantity for sku (0 if unknown).
	Stock(sku string) int
	// RecordSale appends a sale and decrements stock; errors on an unknown SKU.
	RecordSale(s Sale) error
	// RevenueToday sums UnitPrice*Quantity for sales dated the same day as now.
	RevenueToday(now time.Time) Decimal
	// TopSellersSince ranks SKUs by units sold at or after since, top topK.
	TopSellersSince(since time.Time, topK int) ([]SellerCount, error)
}

// InMemoryRetailBoard is a concurrency-safe in-memory RetailBoard. Ports
// InMemoryRetailBoard (products + stock in maps; sales in an ordered list guarded
// by the mutex so a RecordSale's append+decrement is atomic).
type InMemoryRetailBoard struct {
	mu       sync.RWMutex
	products map[string]Product
	stock    map[string]int
	sales    []Sale
}

// NewInMemoryRetailBoard constructs an empty board.
func NewInMemoryRetailBoard() *InMemoryRetailBoard {
	return &InMemoryRetailBoard{
		products: make(map[string]Product),
		stock:    make(map[string]int),
		sales:    make([]Sale, 0),
	}
}

// AddProduct stores (or replaces by Sku) a product. Ports AddProduct.
func (b *InMemoryRetailBoard) AddProduct(p Product) {
	b.mu.Lock()
	b.products[p.Sku] = p
	b.mu.Unlock()
}

// GetProduct returns the product for sku and true, or (zero, false) if absent.
// Ports GetProduct.
func (b *InMemoryRetailBoard) GetProduct(sku string) (Product, bool) {
	b.mu.RLock()
	p, ok := b.products[sku]
	b.mu.RUnlock()
	return p, ok
}

// SetStock sets the on-hand quantity for a SKU. Ports SetStock.
func (b *InMemoryRetailBoard) SetStock(l StockLevel) {
	b.mu.Lock()
	b.stock[l.Sku] = l.Quantity
	b.mu.Unlock()
}

// Stock returns the on-hand quantity for sku, or 0 if unknown. Ports Stock.
func (b *InMemoryRetailBoard) Stock(sku string) int {
	b.mu.RLock()
	q := b.stock[sku]
	b.mu.RUnlock()
	return q
}

// RecordSale appends a sale and decrements the SKU's stock by the sold quantity.
// Ports RecordSale (throws on an unknown SKU -> error). The append + decrement
// happen under one lock so they are atomic.
func (b *InMemoryRetailBoard) RecordSale(s Sale) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if _, ok := b.products[s.Sku]; !ok {
		return errors.New("Unknown SKU " + s.Sku)
	}
	b.sales = append(b.sales, s)
	b.stock[s.Sku] = b.stock[s.Sku] - s.Quantity
	return nil
}

// RevenueToday sums UnitPrice*Quantity over sales whose calendar date equals
// now's date. Ports RevenueToday (Where(AtUtc.Date == now.Date).Sum(UnitPrice*Quantity)).
func (b *InMemoryRetailBoard) RevenueToday(now time.Time) Decimal {
	ny, nm, nd := now.Date()
	b.mu.RLock()
	defer b.mu.RUnlock()
	var total Decimal
	for _, s := range b.sales {
		sy, sm, sd := s.AtUtc.Date()
		if sy == ny && sm == nm && sd == nd {
			total = total.Add(s.UnitPrice.Mul(DecimalFromInt(int64(s.Quantity))))
		}
	}
	return total
}

// TopSellersSince ranks SKUs by total units sold at or after since, returning the
// top topK by units sold (descending). Ports TopSellersSince (ArgumentOutOfRange
// on topK <= 0 -> error). Equal totals keep first-seen order (stable, mirroring
// GroupBy first-seen order + stable OrderByDescending).
func (b *InMemoryRetailBoard) TopSellersSince(since time.Time, topK int) ([]SellerCount, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	b.mu.RLock()
	order := make([]string, 0) // first-seen SKU order
	totals := make(map[string]int)
	for _, s := range b.sales {
		if s.AtUtc.Before(since) {
			continue
		}
		if _, seen := totals[s.Sku]; !seen {
			order = append(order, s.Sku)
		}
		totals[s.Sku] += s.Quantity
	}
	b.mu.RUnlock()

	out := make([]SellerCount, 0, len(order))
	for _, sku := range order {
		out = append(out, SellerCount{Sku: sku, Sold: totals[sku]})
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Sold > out[j].Sold })
	if len(out) > topK {
		out = out[:topK]
	}
	return out, nil
}

// Interface guard.
var _ RetailBoard = (*InMemoryRetailBoard)(nil)
