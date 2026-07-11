// domain_sort.go
//
// A small culture-sensitive-ish string comparator used by the domain boards whose
// C# defines a string OrderBy: Personal.Finance IPersonalFinanceBoard.Budgets
// (OrderBy(Category)) and Personal.Health.ActiveMedications (OrderBy(Name)).
//
// .NET's LINQ OrderBy(string) uses Comparer<string>.Default, which is
// culture-sensitive (verified: on en-ZA, {"Food","Transport","food"} orders as
// food,Food,Transport — case-insensitive primary, then lower-before-upper — and
// "apple" sorts before "Apple"). Go's ordinal `<` gives a different result
// (Food,Transport,food). Reproducing full Unicode/ICU collation with variable
// weighting (e.g. the exact bb<bB<Bb<BB per-character case weighting, or
// space/hyphen being weighted below letters) requires the collation tables and is
// out of scope for a google/uuid-only port.
//
// cultureLess reproduces the observable ordering for the realistic ASCII domain
// data these boards hold (distinct category / medication names differing at most
// by case): primary key = case-folded comparison; tie-break = lower-case sorts
// before upper-case at the first differing position. This matches .NET's
// OrderBy(string) exactly for normally-cased ASCII names; pathological
// mixed-case-within-a-token or punctuation-weighting inputs (which do not occur
// for budget categories or medication names) may differ, which is documented on
// the two call sites.

package circleai

import "strings"

// cultureLess reports whether a should sort before b under the case-folded
// "dictionary" ordering described in the file header (a stand-in for .NET's
// culture-sensitive OrderBy(string) over ASCII domain names).
func cultureLess(a, b string) bool {
	la, lb := strings.ToLower(a), strings.ToLower(b)
	if la != lb {
		return la < lb // case-insensitive primary ordering
	}
	// Equal ignoring case: lower-case character sorts before upper-case at the
	// first position where the raw strings differ.
	n := len(a)
	if len(b) < n {
		n = len(b)
	}
	for i := 0; i < n; i++ {
		if a[i] == b[i] {
			continue
		}
		aLower := a[i] >= 'a' && a[i] <= 'z'
		bLower := b[i] >= 'a' && b[i] <= 'z'
		if aLower != bLower {
			return aLower // the lower-case one comes first
		}
		return a[i] < b[i]
	}
	// One is a prefix of the other: the shorter sorts first.
	return len(a) < len(b)
}
