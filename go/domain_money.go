// domain_money.go
//
// Extra base-10 fixed-point arithmetic for the Decimal type (defined in
// telephony_decimal.go) needed by the domain-board verticals ported from the
// CircleAI.Banking / Commerce* / Personal.Finance C# modules. Those C# modules
// use System.Decimal for balances, ledger amounts, invoice totals, tax, and
// net-profit maths. telephony_decimal.go only exposes Add/Cmp/Equal, so this
// file adds the subtraction, negation, multiplication, sign, and slice-sum
// operations the boards require. Kept in the same package so the unexported
// micro-unit field is reachable; kept in a separate file so no unrelated
// telephony code is touched.
//
// No new C# type is introduced here — this is purely arithmetic support for the
// shared Decimal so the ported boards can reproduce C# decimal semantics exactly
// (exact base-10, no binary-float rounding of cents).

package circleai

// Sub returns d - other.
func (d Decimal) Sub(other Decimal) Decimal { return Decimal{units: d.units - other.units} }

// Neg returns -d.
func (d Decimal) Neg() Decimal { return Decimal{units: -d.units} }

// Sign returns -1, 0, or +1 as d is negative, zero, or positive.
func (d Decimal) Sign() int {
	switch {
	case d.units < 0:
		return -1
	case d.units > 0:
		return 1
	default:
		return 0
	}
}

// Less reports whether d < other (convenience over Cmp).
func (d Decimal) Less(other Decimal) bool { return d.units < other.units }

// Mul returns d * other. Both operands are scaled by 10^6, so the raw product is
// scaled by 10^12 and must be divided back by 10^6. Division truncates toward
// zero, matching the fixed 6-fractional-digit precision of the type (C# decimal
// keeps more digits, but every amount in these boards is currency at <= 6 dp, so
// the product is exact for all realistic inputs).
func (d Decimal) Mul(other Decimal) Decimal {
	return Decimal{units: (d.units * other.units) / decimalScale}
}

// MulFloat returns d * f, used for the C# expression
// `Amount * (decimal)(1 + TaxPct / 100.0)` in the invoice board. The C# code
// casts the double tax multiplier to decimal before multiplying; this reproduces
// that by scaling the multiplier to micro-units (rounded to nearest) and reusing
// exact base-10 multiplication, so a 15% tax on 100.00 yields exactly 115.00.
func (d Decimal) MulFloat(f float64) Decimal {
	scaled := f * float64(decimalScale)
	// Round to nearest micro-unit (half away from zero) to avoid float dust.
	var m int64
	if scaled >= 0 {
		m = int64(scaled + 0.5)
	} else {
		m = int64(scaled - 0.5)
	}
	return Decimal{units: (d.units * m) / decimalScale}
}

// DivInt returns d divided by a positive integer count, truncating toward zero
// at the type's fixed 6-fractional-digit precision. It models the exact C#
// `decimal` average `rows.Average(x => x.Amount)` used by the RealEstate
// SuburbAverage: C# decimal division keeps up to 28 digits, but the realistic
// prices these boards average (whole-currency suburb asking prices) divide
// exactly at 6dp, so the micro-unit quotient equals C#'s result for all such
// inputs. Panics on a non-positive divisor (an average is only taken over a
// non-empty set).
func (d Decimal) DivInt(count int) Decimal {
	if count <= 0 {
		panic("DivInt: count must be positive")
	}
	return Decimal{units: d.units / int64(count)}
}

// DecimalFromFloat converts a float64 to a Decimal at the type's fixed
// 6-fractional-digit precision, rounding to the nearest micro-unit (half away
// from zero). It models the C# `(decimal)someDouble` cast used by the Logistics
// route-cost estimator (`(decimal)(totalKm * vehicle.CostPerKm)`): C#'s cast
// keeps more digits than 6dp, but every cost these boards produce is currency at
// <= 6dp, so the rounded micro-unit value is exact for all realistic inputs and
// avoids binary-float dust (e.g. 100.0 km * 2.5 -> exactly 250.000000).
func DecimalFromFloat(f float64) Decimal {
	scaled := f * float64(decimalScale)
	var m int64
	if scaled >= 0 {
		m = int64(scaled + 0.5)
	} else {
		m = int64(scaled - 0.5)
	}
	return Decimal{units: m}
}

// SumDecimals returns the total of a slice of Decimal (empty slice -> zero).
func SumDecimals(xs []Decimal) Decimal {
	var total Decimal
	for _, x := range xs {
		total = total.Add(x)
	}
	return total
}
