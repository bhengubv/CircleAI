// companion_theory_of_mind.go
//
// Ported from CircleAI.Companion (HerJarvisContracts.cs + HerJarvisRealImplementations.cs)
// — the C# reference:
//   - ITheoryOfMind                    (contract 10)
//   - OtherMindEstimate                (record)
//   - BeliefTrackerTheoryOfMind        (concrete: bag-of-belief with confidence decay)
//
// Theory of mind estimates another party's likely beliefs from an interaction
// history. In-memory, deterministic. C# ValueTask<OtherMindEstimate> becomes a
// synchronous (OtherMindEstimate, error) that honours ctx cancellation.
//
// Wire format: the LikelyBeliefJson field is produced by
// JsonSerializer.Serialize(Dictionary<string,double>) in the C# reference, which
// serialises the dictionary in INSERTION ORDER (not sorted) with .NET's shortest
// round-trip double formatting. Go's encoding/json sorts map keys and would break
// that, so LikelyBeliefJson is built by hand here to reproduce the C# bytes
// exactly: insertion-ordered keys, JSON-escaped, System.Text.Json number format.

package circleai

import (
	"context"
	"errors"
	"regexp"
	"strconv"
	"strings"
)

// OtherMindEstimate is an estimate of another party's beliefs, as a JSON belief
// bag plus an overall confidence. Ported from the C# record
// OtherMindEstimate(string TargetIdentifier, string LikelyBeliefJson, double Confidence).
type OtherMindEstimate struct {
	TargetIdentifier string
	LikelyBeliefJSON string
	Confidence       float64
}

// ITheoryOfMind is the theory-of-mind contract (C# ITheoryOfMind).
type ITheoryOfMind interface {
	Estimate(ctx context.Context, target, interactionHistoryJSON string) (OtherMindEstimate, error)
}

// beliefRx matches mental-state verbs and the claim that follows, mirroring the
// C# BeliefRx = @"\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)" with
// IgnoreCase. Go's regexp (RE2) supports these constructs directly.
var beliefRx = regexp.MustCompile(`(?i)\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)`)

// BeliefTrackerTheoryOfMind infers a bag of beliefs from an interaction history
// by scanning for "<verb> <claim>" patterns, weighting each by verb strength and
// a positional decay so earlier mentions count more. Ported from the C#
// BeliefTrackerTheoryOfMind.
type BeliefTrackerTheoryOfMind struct{}

// orderedBelief is one accumulated belief, preserving first-seen order so the
// serialised JSON matches C#'s insertion-ordered Dictionary.
type orderedBelief struct {
	key    string
	weight float64
}

// Estimate scans interactionHistoryJSON for belief expressions about the target
// and returns a JSON belief bag with a confidence. Mirrors the C# EstimateAsync:
//
//	decay  = 1 / (1 + idx*0.1)                 (idx = 0-based match index)
//	weight = verb starts with "believ" ? 1.0 : 0.7
//	key    = "<lowered verb>:<trimmed claim>"
//	bag[key] += weight*decay                    (accumulates on repeat)
//	conf   = bag empty ? 0 : min(1, Σweights / 5)
func (m *BeliefTrackerTheoryOfMind) Estimate(ctx context.Context, target, interactionHistoryJSON string) (OtherMindEstimate, error) {
	if err := ctx.Err(); err != nil {
		return OtherMindEstimate{}, err
	}
	if strings.TrimSpace(target) == "" {
		return OtherMindEstimate{}, errors.New("target required")
	}
	// C# guards interactionHistoryJson null; the Go empty string is non-null and
	// simply yields no matches.

	// Accumulate in insertion order (first appearance of a key fixes its slot),
	// exactly like a .NET Dictionary<string,double>.
	order := make([]*orderedBelief, 0)
	index := make(map[string]*orderedBelief)

	matches := beliefRx.FindAllStringSubmatch(interactionHistoryJSON, -1)
	for idx, mm := range matches {
		verb := strings.ToLower(mm[1])
		claim := strings.TrimSpace(mm[2])
		decay := 1.0 / (1.0 + float64(idx)*0.1)
		weight := 0.7
		if strings.HasPrefix(verb, "believ") {
			weight = 1.0
		}
		key := verb + ":" + claim
		if ob, ok := index[key]; ok {
			ob.weight += weight * decay
		} else {
			ob := &orderedBelief{key: key, weight: weight * decay}
			index[key] = ob
			order = append(order, ob)
		}
	}

	jsonBag := serializeBeliefBag(order)

	var sum float64
	for _, ob := range order {
		sum += ob.weight
	}
	conf := 0.0
	if len(order) > 0 {
		conf = sum / 5.0
		if conf > 1.0 {
			conf = 1.0
		}
	}

	return OtherMindEstimate{
		TargetIdentifier: target,
		LikelyBeliefJSON: jsonBag,
		Confidence:       conf,
	}, nil
}

// serializeBeliefBag renders the insertion-ordered belief bag as JSON, matching
// System.Text.Json's Serialize(Dictionary<string,double>): {"k":v,...} with keys
// JSON-escaped and values in .NET shortest round-trip form. An empty bag yields
// "{}".
func serializeBeliefBag(order []*orderedBelief) string {
	var b strings.Builder
	b.WriteByte('{')
	for i, ob := range order {
		if i > 0 {
			b.WriteByte(',')
		}
		writeJSONString(&b, ob.key)
		b.WriteByte(':')
		b.WriteString(formatDotNetDouble(ob.weight))
	}
	b.WriteByte('}')
	return b.String()
}

// writeJSONString writes s as a JSON string literal reproducing System.Text.Json's
// DEFAULT (non-relaxed) encoder byte-for-byte — verified against
// JsonSerializer.Serialize(Dictionary<string,double>) on .NET 10:
//
//	"  -> "        (the quote is escaped as ", not \")
//	\  -> \\
//	/  -> /             (solidus is NOT escaped)
//	\b \t \n \f \r      short forms are used for these control chars
//	other C0 controls   -> \uXXXX (upper-case hex)
//	< > & ' +           -> < > & ' + (HTML-sensitive set)
//	every non-ASCII rune (>= 0x80) -> \uXXXX, using UTF-16 surrogate pairs for
//	                       astral runes (> 0xFFFF), all hex digits upper-case.
//
// This exactness matters because LikelyBeliefJson is a wire field the Go and C#
// implementations must agree on.
func writeJSONString(b *strings.Builder, s string) {
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case '"':
			b.WriteString(`"`)
		case '\\':
			b.WriteString(`\\`)
		case '\b':
			b.WriteString(`\b`)
		case '\f':
			b.WriteString(`\f`)
		case '\n':
			b.WriteString(`\n`)
		case '\r':
			b.WriteString(`\r`)
		case '\t':
			b.WriteString(`\t`)
		default:
			if r < 0x20 || r >= 0x7f {
				// C0 controls (except the short forms above) and everything
				// non-ASCII are \uXXXX escaped, as surrogate pairs when needed.
				if r > 0xFFFF {
					r -= 0x10000
					hi := 0xD800 + (r >> 10)
					lo := 0xDC00 + (r & 0x3FF)
					writeU4(b, hi)
					writeU4(b, lo)
				} else {
					writeU4(b, r)
				}
			} else {
				switch r {
				// HTML-sensitive set: System.Text.Json's default encoder escapes
				// these as < > & ' + (upper-case hex).
				case '<', '>', '&', '\'', '+':
					writeU4(b, r)
				default:
					b.WriteByte(byte(r))
				}
			}
		}
	}
	b.WriteByte('"')
}

// writeU4 appends a single \uXXXX escape with upper-case hex digits, matching
// System.Text.Json.
func writeU4(b *strings.Builder, r rune) {
	const hex = "0123456789ABCDEF"
	b.WriteString(`\u`)
	b.WriteByte(hex[(r>>12)&0xF])
	b.WriteByte(hex[(r>>8)&0xF])
	b.WriteByte(hex[(r>>4)&0xF])
	b.WriteByte(hex[r&0xF])
}

// formatDotNetDouble renders a float64 the way System.Text.Json writes a double:
// the shortest string that round-trips, with integral values printed without a
// trailing ".0" (e.g. 1.0 -> "1"), and exponents (rare here) upper-cased with an
// explicit sign to match .NET. For the theory-of-mind value domain (small
// positive sums of 0.7 and 1.0 scaled by 1/(1+0.1k)) this yields plain decimals.
func formatDotNetDouble(v float64) string {
	s := strconv.FormatFloat(v, 'g', -1, 64)
	// Go emits lowercase 'e' and may omit the '+' sign; .NET uses 'E' with a sign.
	if i := strings.IndexAny(s, "eE"); i >= 0 {
		mantissa := s[:i]
		exp := s[i+1:]
		sign := "+"
		if len(exp) > 0 && (exp[0] == '+' || exp[0] == '-') {
			if exp[0] == '-' {
				sign = "-"
			}
			exp = exp[1:]
		}
		// .NET pads the exponent to at least two digits (E+09), matching
		// System.Text.Json's number writer.
		if len(exp) < 2 {
			exp = strings.Repeat("0", 2-len(exp)) + exp
		}
		return mantissa + "E" + sign + exp
	}
	return s
}

// Compile-time assertion.
var _ ITheoryOfMind = (*BeliefTrackerTheoryOfMind)(nil)
