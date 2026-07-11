// domain_money_test.go
//
// Verifies the extra Decimal arithmetic in domain_money.go (Sub/Neg/Sign/Less/
// Mul/MulFloat/SumDecimals) used by the domain-board money maths.

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestDecimal_SubNegSign(t *testing.T) {
	a := circleai.DecimalFromInt(10)
	b := circleai.DecimalFromInt(3)
	if got := a.Sub(b); !got.Equal(circleai.DecimalFromInt(7)) {
		t.Fatalf("10-3 = %s, want 7", got)
	}
	if got := b.Sub(a); !got.Equal(circleai.DecimalFromInt(-7)) {
		t.Fatalf("3-10 = %s, want -7", got)
	}
	if got := a.Neg(); !got.Equal(circleai.DecimalFromInt(-10)) {
		t.Fatalf("-(10) = %s, want -10", got)
	}
	if circleai.DecimalFromInt(-5).Sign() != -1 || circleai.ZeroDecimal.Sign() != 0 || circleai.DecimalFromInt(5).Sign() != 1 {
		t.Fatalf("sign wrong")
	}
	if !circleai.DecimalFromInt(3).Less(circleai.DecimalFromInt(4)) || circleai.DecimalFromInt(4).Less(circleai.DecimalFromInt(4)) {
		t.Fatalf("Less wrong")
	}
}

func TestDecimal_Mul(t *testing.T) {
	// 2.5 * 4 = 10
	a := circleai.NewDecimal(2, 500_000) // 2.5
	b := circleai.DecimalFromInt(4)
	if got := a.Mul(b); !got.Equal(circleai.DecimalFromInt(10)) {
		t.Fatalf("2.5*4 = %s, want 10", got)
	}
	// 1.5 * 1.5 = 2.25
	c := circleai.NewDecimal(1, 500_000)
	want := circleai.NewDecimal(2, 250_000)
	if got := c.Mul(c); !got.Equal(want) {
		t.Fatalf("1.5*1.5 = %s, want 2.25", got)
	}
}

func TestDecimal_MulFloat_TaxMultiplier(t *testing.T) {
	// 100.00 * (1 + 15/100) = 115.00 exactly.
	amount := circleai.DecimalFromInt(100)
	got := amount.MulFloat(1 + 15.0/100.0)
	if !got.Equal(circleai.DecimalFromInt(115)) {
		t.Fatalf("100 * 1.15 = %s, want 115", got)
	}
	if got.String() != "115" {
		t.Fatalf("string = %q, want 115", got.String())
	}
	// 200.00 * 1.14 (14% VAT) = 228.00
	got2 := circleai.DecimalFromInt(200).MulFloat(1 + 14.0/100.0)
	if !got2.Equal(circleai.DecimalFromInt(228)) {
		t.Fatalf("200 * 1.14 = %s, want 228", got2)
	}
	// Zero tax leaves the amount unchanged.
	got3 := circleai.NewDecimal(49, 990_000).MulFloat(1.0) // 49.99
	if !got3.Equal(circleai.NewDecimal(49, 990_000)) {
		t.Fatalf("49.99 * 1.0 = %s, want 49.99", got3)
	}
}

func TestSumDecimals(t *testing.T) {
	if got := circleai.SumDecimals(nil); !got.IsZero() {
		t.Fatalf("sum of nil = %s, want 0", got)
	}
	xs := []circleai.Decimal{
		circleai.NewDecimal(1, 100_000), // 1.1
		circleai.NewDecimal(2, 200_000), // 2.2
		circleai.NewDecimal(3, 300_000), // 3.3
	}
	if got := circleai.SumDecimals(xs); !got.Equal(circleai.NewDecimal(6, 600_000)) {
		t.Fatalf("sum = %s, want 6.6", got)
	}
}
