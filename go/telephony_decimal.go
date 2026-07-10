// telephony_decimal.go
//
// A small base-10 fixed-point money type standing in for the C# `decimal` used
// by the telephony surface (ProvisionedNumber.MonthlyRecurringCost,
// CallSnapshot.CostSoFar, and the carriers' price parsing). C# `decimal` is an
// exact base-10 type; Go has no built-in equivalent and the "only dependency is
// google/uuid" rule forbids shopspring/decimal. The telephony values are plain
// currency amounts, so a fixed 6-decimal-place int64 (micro-units) reproduces
// the exact-arithmetic behaviour that matters here (no binary-float rounding on
// cents) while staying dependency-free.
//
// Ported behaviour: decimal.TryParse(s, NumberStyles.Any, InvariantCulture) as
// used by TwilioCarrier/TelnyxCarrier/PlivoCarrier price parsing, and the `0m`
// literal (ZeroDecimal). Parsing accepts an optional leading sign, integer part,
// and up to 6 fractional digits (extra digits are truncated toward zero, which
// never occurs for real carrier pricing but keeps parsing total).

package circleai

import (
	"errors"
	"strings"
)

// decimalScale is 10^6 — six fractional digits of precision.
const decimalScale = 1_000_000

// Decimal is an exact base-10 fixed-point value with 6 fractional digits,
// standing in for C# `decimal` on the telephony surface. The zero value is 0.
type Decimal struct {
	units int64 // value * 10^6
}

// ZeroDecimal is the 0m literal.
var ZeroDecimal = Decimal{}

// DecimalFromInt builds a Decimal from a whole number.
func DecimalFromInt(v int64) Decimal { return Decimal{units: v * decimalScale} }

// NewDecimal builds a Decimal from an integer part and a fractional numerator in
// micro-units (0..999999). Negative amounts pass a negative integer part; frac
// is always applied with the integer part's sign.
func NewDecimal(intPart int64, microFrac int64) Decimal {
	u := intPart * decimalScale
	if intPart < 0 {
		u -= microFrac
	} else {
		u += microFrac
	}
	return Decimal{units: u}
}

// IsZero reports whether the value is exactly zero.
func (d Decimal) IsZero() bool { return d.units == 0 }

// Micro returns the raw micro-unit representation (value * 10^6).
func (d Decimal) Micro() int64 { return d.units }

// Add returns d + other.
func (d Decimal) Add(other Decimal) Decimal { return Decimal{units: d.units + other.units} }

// Equal reports exact equality.
func (d Decimal) Equal(other Decimal) bool { return d.units == other.units }

// Cmp returns -1, 0, or +1 as d is <, ==, or > other.
func (d Decimal) Cmp(other Decimal) int {
	switch {
	case d.units < other.units:
		return -1
	case d.units > other.units:
		return 1
	default:
		return 0
	}
}

// String renders the value in invariant (dot-decimal) form, trimming trailing
// fractional zeros but always keeping at least the integer digit (e.g. "0",
// "1.15", "-2.5").
func (d Decimal) String() string {
	neg := d.units < 0
	u := d.units
	if neg {
		u = -u
	}
	intPart := u / decimalScale
	frac := u % decimalScale

	var sb strings.Builder
	if neg {
		sb.WriteByte('-')
	}
	sb.WriteString(itoaSmall(int64ToInt(intPart)))
	if frac != 0 {
		// Six-digit fractional, trailing zeros trimmed.
		var fb [6]byte
		for i := 5; i >= 0; i-- {
			fb[i] = byte('0' + frac%10)
			frac /= 10
		}
		end := 6
		for end > 0 && fb[end-1] == '0' {
			end--
		}
		sb.WriteByte('.')
		sb.Write(fb[:end])
	}
	return sb.String()
}

// errDecimalParse is returned when a string is not a valid decimal.
var errDecimalParse = errors.New("invalid decimal")

// ParseDecimalInvariant parses s the way C# decimal.TryParse(NumberStyles.Any,
// InvariantCulture) does for the shapes carrier pricing actually emits: optional
// sign, digits, optional '.' with fractional digits. Leading/trailing spaces are
// tolerated. Returns the value and true on success; (ZeroDecimal, false) on any
// malformed input (mirroring TryParse's bool contract — the callers treat false
// as "no price → 0m").
func ParseDecimalInvariant(s string) (Decimal, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return ZeroDecimal, false
	}
	neg := false
	switch s[0] {
	case '+':
		s = s[1:]
	case '-':
		neg = true
		s = s[1:]
	}
	if s == "" {
		return ZeroDecimal, false
	}

	intStr := s
	fracStr := ""
	if dot := strings.IndexByte(s, '.'); dot >= 0 {
		intStr = s[:dot]
		fracStr = s[dot+1:]
	}
	// Both sides digits-only; at least one digit total.
	if intStr == "" && fracStr == "" {
		return ZeroDecimal, false
	}
	var intVal int64
	for i := 0; i < len(intStr); i++ {
		c := intStr[i]
		if c < '0' || c > '9' {
			return ZeroDecimal, false
		}
		intVal = intVal*10 + int64(c-'0')
	}
	// Fractional: consume up to 6 digits, truncate the rest; validate all digits.
	var frac int64
	digitsUsed := 0
	for i := 0; i < len(fracStr); i++ {
		c := fracStr[i]
		if c < '0' || c > '9' {
			return ZeroDecimal, false
		}
		if digitsUsed < 6 {
			frac = frac*10 + int64(c-'0')
			digitsUsed++
		}
	}
	for digitsUsed < 6 {
		frac *= 10
		digitsUsed++
	}
	units := intVal*decimalScale + frac
	if neg {
		units = -units
	}
	return Decimal{units: units}, true
}

// int64ToInt narrows for itoaSmall (values here are small currency integer
// parts; the conversion is safe for all realistic amounts).
func int64ToInt(v int64) int { return int(v) }
