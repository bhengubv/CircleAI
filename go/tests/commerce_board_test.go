// commerce_board_test.go
//
// Verifies the CircleAI.Commerce port (commerce_board.go): customer add/get,
// order place/status, line items (insertion order), orders-for ordering (desc),
// and lifetime value summation.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCommerce_CustomerAddGet(t *testing.T) {
	b := circleai.NewInMemoryCommerceBoard()
	b.AddCustomer(circleai.CommerceCustomer{CustomerId: "cust1", Name: "Ada", Email: strptr("ada@x.io"), CreatedUtc: time.Now().UTC()})
	got, ok := b.GetCustomer("cust1")
	if !ok || got.Name != "Ada" || got.Email == nil || *got.Email != "ada@x.io" {
		t.Fatalf("get customer = %+v ok=%v", got, ok)
	}
	// Nil email preserved.
	b.AddCustomer(circleai.CommerceCustomer{CustomerId: "cust2", Name: "NoMail", Email: nil})
	g2, _ := b.GetCustomer("cust2")
	if g2.Email != nil {
		t.Fatalf("nil email should stay nil")
	}
}

func TestCommerce_OrdersLinesAndStatus(t *testing.T) {
	b := circleai.NewInMemoryCommerceBoard()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Place(circleai.CommerceOrder{OrderId: "o1", CustomerId: "c1", Total: circleai.DecimalFromInt(100), Currency: "ZAR", Status: "New", AtUtc: base})
	b.Place(circleai.CommerceOrder{OrderId: "o2", CustomerId: "c1", Total: circleai.DecimalFromInt(250), Currency: "ZAR", Status: "New", AtUtc: base.Add(48 * time.Hour)})
	b.Place(circleai.CommerceOrder{OrderId: "o3", CustomerId: "c1", Total: circleai.DecimalFromInt(50), Currency: "ZAR", Status: "New", AtUtc: base.Add(24 * time.Hour)})

	orders := b.OrdersFor("c1")
	if len(orders) != 3 || orders[0].OrderId != "o2" || orders[1].OrderId != "o3" || orders[2].OrderId != "o1" {
		t.Fatalf("orders desc failed: %+v", orders)
	}
	if err := b.UpdateStatus("o1", "Shipped"); err != nil {
		t.Fatalf("update status: %v", err)
	}
	if err := b.UpdateStatus("ghost", "X"); err == nil {
		t.Fatalf("unknown order status must error")
	}

	// Lines preserve insertion order and filter by order.
	b.AddLine(circleai.CommerceLineItem{LineId: "L1", OrderId: "o1", Sku: "A", Quantity: 2, UnitPrice: circleai.DecimalFromInt(10)})
	b.AddLine(circleai.CommerceLineItem{LineId: "L2", OrderId: "o2", Sku: "B", Quantity: 1, UnitPrice: circleai.DecimalFromInt(5)})
	b.AddLine(circleai.CommerceLineItem{LineId: "L3", OrderId: "o1", Sku: "C", Quantity: 3, UnitPrice: circleai.DecimalFromInt(7)})
	lines := b.LinesFor("o1")
	if len(lines) != 2 || lines[0].LineId != "L1" || lines[1].LineId != "L3" {
		t.Fatalf("lines insertion order failed: %+v", lines)
	}

	// Lifetime value = 100 + 250 + 50 = 400.
	if ltv := b.LifetimeValue("c1"); !ltv.Equal(circleai.DecimalFromInt(400)) {
		t.Fatalf("ltv = %s, want 400", ltv)
	}
	if ltv := b.LifetimeValue("nobody"); !ltv.IsZero() {
		t.Fatalf("ltv for unknown customer = %s, want 0", ltv)
	}
}
