// domain_ordinal_sort.go
//
// ordinalIgnoreCaseLess reproduces .NET's StringComparer.OrdinalIgnoreCase
// ordering, which several domain boards pass explicitly to LINQ OrderBy:
//
//	CircleAI.CRM   InMemoryContactStore.SearchAsync  (OrderBy(FullName, OrdinalIgnoreCase))
//	CircleAI.Markets InMemoryInstrumentCatalog.SearchAsync (Symbol is already
//	                 Ordinal-keyed; kept here for the OrdinalIgnoreCase call sites)
//
// Unlike cultureLess (domain_sort.go, which models the *culture-sensitive*
// default Comparer<string>), OrdinalIgnoreCase is a pure per-rune upper-invariant
// ordinal comparison: uppercase each side (invariant/ASCII-fold for the ASCII
// names these boards hold) and compare code points. It is case-insensitive with
// no lower-before-upper tie-break, so equal-ignoring-case strings compare equal
// and a stable sort preserves their original (insertion) order — exactly matching
// .NET's stable OrderBy. Full Unicode case-folding is out of scope for a
// google/uuid-only port; the domain data here (contact names, symbols) is ASCII.
package circleai

import "strings"

// ordinalIgnoreCaseLess reports whether a should sort strictly before b under
// .NET's StringComparer.OrdinalIgnoreCase ordering (case-folded ordinal compare).
func ordinalIgnoreCaseLess(a, b string) bool {
	return strings.ToUpper(a) < strings.ToUpper(b)
}
