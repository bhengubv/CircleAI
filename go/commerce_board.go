// commerce_board.go
//
// Ports the CircleAI.Commerce primitive vertical (CommercePrimitives.cs):
//   CommerceCustomer / CommerceOrder / CommerceLineItem (records) -> value structs
//   ICommerceBoard        -> CommerceBoard interface (I-prefix dropped)
//   InMemoryCommerceBoard -> InMemoryCommerceBoard
//
// The CommerceDomainContext (static prompt strings) and CommerceCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: OrdersFor orders by AtUtc descending (id tiebreak added). LinesFor
// preserves insertion order over the backing list, exactly like the C# (a plain
// List filtered by OrderId). Money totals use the shared exact Decimal.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// CommerceCustomer is a commerce customer. Ports the CommerceCustomer record.
// Email is a pointer to mirror the nullable C# string? (nil == no email).
type CommerceCustomer struct {
	CustomerId string
	Name       string
	Email      *string
	CreatedUtc time.Time
}

// CommerceOrder is a customer order. Ports the CommerceOrder record.
type CommerceOrder struct {
	OrderId    string
	CustomerId string
	Total      Decimal
	Currency   string
	Status     string
	AtUtc      time.Time
}

// CommerceLineItem is a line on an order. Ports the CommerceLineItem record.
type CommerceLineItem struct {
	LineId    string
	OrderId   string
	Sku       string
	Quantity  int
	UnitPrice Decimal
}

// CommerceBoard is the customers/orders/lines board. Ports ICommerceBoard.
type CommerceBoard interface {
	AddCustomer(c CommerceCustomer)
	GetCustomer(id string) (CommerceCustomer, bool)
	Place(o CommerceOrder)
	AddLine(l CommerceLineItem)
	// UpdateStatus sets an order's status; errors if the id is unknown.
	UpdateStatus(orderId, status string) error
	// OrdersFor lists a customer's orders, most recent first.
	OrdersFor(customerId string) []CommerceOrder
	// LinesFor lists an order's line items in insertion order.
	LinesFor(orderId string) []CommerceLineItem
	// LifetimeValue is the sum of a customer's order totals.
	LifetimeValue(customerId string) Decimal
}

// InMemoryCommerceBoard is a concurrency-safe in-memory CommerceBoard. Ports
// InMemoryCommerceBoard. Line items live in an ordered slice (the C# List) guarded
// by the same mutex; customers/orders live in maps.
type InMemoryCommerceBoard struct {
	mu        sync.RWMutex
	customers map[string]CommerceCustomer
	orders    map[string]CommerceOrder
	lines     []CommerceLineItem
}

// NewInMemoryCommerceBoard constructs an empty board.
func NewInMemoryCommerceBoard() *InMemoryCommerceBoard {
	return &InMemoryCommerceBoard{
		customers: make(map[string]CommerceCustomer),
		orders:    make(map[string]CommerceOrder),
		lines:     make([]CommerceLineItem, 0),
	}
}

// AddCustomer stores (or replaces by CustomerId) a customer. Ports AddCustomer.
func (b *InMemoryCommerceBoard) AddCustomer(c CommerceCustomer) {
	b.mu.Lock()
	b.customers[c.CustomerId] = c
	b.mu.Unlock()
}

// GetCustomer returns the customer for id and true, or (zero, false) if absent.
func (b *InMemoryCommerceBoard) GetCustomer(id string) (CommerceCustomer, bool) {
	b.mu.RLock()
	c, ok := b.customers[id]
	b.mu.RUnlock()
	return c, ok
}

// Place stores (or replaces by OrderId) an order. Ports Place.
func (b *InMemoryCommerceBoard) Place(o CommerceOrder) {
	b.mu.Lock()
	b.orders[o.OrderId] = o
	b.mu.Unlock()
}

// AddLine appends a line item (insertion order preserved). Ports AddLine.
func (b *InMemoryCommerceBoard) AddLine(l CommerceLineItem) {
	b.mu.Lock()
	b.lines = append(b.lines, l)
	b.mu.Unlock()
}

// UpdateStatus mutates an order's status. Ports UpdateStatus (throws on unknown
// id -> error).
func (b *InMemoryCommerceBoard) UpdateStatus(orderId, status string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	o, ok := b.orders[orderId]
	if !ok {
		return errors.New("Unknown order " + orderId)
	}
	o.Status = status
	b.orders[orderId] = o
	return nil
}

// OrdersFor lists a customer's orders ordered by AtUtc descending. Ports OrdersFor.
func (b *InMemoryCommerceBoard) OrdersFor(customerId string) []CommerceOrder {
	b.mu.RLock()
	out := make([]CommerceOrder, 0)
	for _, o := range b.orders {
		if o.CustomerId == customerId {
			out = append(out, o)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].AtUtc.Equal(out[j].AtUtc) {
			return out[i].AtUtc.After(out[j].AtUtc)
		}
		return out[i].OrderId < out[j].OrderId
	})
	return out
}

// LinesFor lists an order's line items in insertion order. Ports LinesFor
// (Where over the backing list, preserving order).
func (b *InMemoryCommerceBoard) LinesFor(orderId string) []CommerceLineItem {
	b.mu.RLock()
	out := make([]CommerceLineItem, 0)
	for _, l := range b.lines {
		if l.OrderId == orderId {
			out = append(out, l)
		}
	}
	b.mu.RUnlock()
	return out
}

// LifetimeValue returns the sum of a customer's order totals. Ports LifetimeValue
// (OrdersFor(...).Sum(Total)).
func (b *InMemoryCommerceBoard) LifetimeValue(customerId string) Decimal {
	var total Decimal
	for _, o := range b.OrdersFor(customerId) {
		total = total.Add(o.Total)
	}
	return total
}

// Interface guard.
var _ CommerceBoard = (*InMemoryCommerceBoard)(nil)
