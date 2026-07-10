// telephony_carrier_util.go
//
// Shared wire helpers for the three real carrier bindings (Twilio/Telnyx/Plivo).
// These reproduce the exact behaviours the C# carriers get from BCL types:
//   parseJSONObject / tjArray / tjString / tjDecimal — System.Text.Json
//     JsonDocument element access (GetProperty / TryGetProperty / EnumerateArray
//     / GetString / GetDecimal with the string-fallback decimal parse).
//   formEncode  — FormUrlEncodedContent (application/x-www-form-urlencoded, keys
//     sorted for a deterministic body; space -> '+').
//   escapeDataString — Uri.EscapeDataString (RFC 3986 unreserved kept, space ->
//     %20, everything else percent-encoded).
//   htmlEncode  — System.Net.WebUtility.HtmlEncode (& < > " ' → entities), used
//     for TwiML.
//   statusError — the InvalidOperationException an EnsureSuccessStatusCode raises
//     on a non-2xx.

package circleai

import (
	"encoding/json"
	"errors"
	"sort"
	"strconv"
	"strings"
)

// parseJSONObject parses body into a JSON object map. Mirrors
// JsonDocument.Parse(...).RootElement for an object payload. Numbers are decoded
// as json.Number (UseNumber) so tjDecimal recovers the exact base-10 value from
// the original text rather than a lossy float64 round-trip.
func parseJSONObject(body []byte) (map[string]interface{}, error) {
	dec := json.NewDecoder(strings.NewReader(string(body)))
	dec.UseNumber()
	var root map[string]interface{}
	if err := dec.Decode(&root); err != nil {
		return nil, err
	}
	return root, nil
}

// tjArray returns the array at key and whether it is present-and-an-array.
// Mirrors TryGetProperty(name, out arr) && arr.ValueKind == Array.
func tjArray(obj map[string]interface{}, key string) ([]interface{}, bool) {
	if obj == nil {
		return nil, false
	}
	v, ok := obj[key]
	if !ok {
		return nil, false
	}
	arr, ok := v.([]interface{})
	return arr, ok
}

// tjString returns the string at key. Mirrors GetProperty(name).GetString().
// Missing/non-string yields ("", false).
func tjString(obj map[string]interface{}, key string) (string, bool) {
	if obj == nil {
		return "", false
	}
	v, ok := obj[key]
	if !ok {
		return "", false
	}
	s, ok := v.(string)
	return s, ok
}

// tjObject returns the nested object at key. Mirrors
// GetProperty(name)/TryGetProperty for an object value.
func tjObject(obj map[string]interface{}, key string) (map[string]interface{}, bool) {
	if obj == nil {
		return nil, false
	}
	v, ok := obj[key]
	if !ok {
		return nil, false
	}
	m, ok := v.(map[string]interface{})
	return m, ok
}

// tjDecimal returns the decimal at key and whether it parsed. Mirrors the
// carriers' ParseDecimal: a JSON number → its decimal value; a JSON string →
// decimal.TryParse(NumberStyles.Any, InvariantCulture); anything else → false.
//
// encoding/json decodes bare numbers into float64. To keep the value EXACT (a
// float64 round-trip of e.g. 1.15 is lossy), the number is re-parsed from its
// original textual form via json.Number when available; parseJSONObject uses the
// default decoder so numbers arrive as float64 — tjDecimalRaw handles both.
func tjDecimal(obj map[string]interface{}, key string) (Decimal, bool) {
	if obj == nil {
		return ZeroDecimal, false
	}
	v, ok := obj[key]
	if !ok {
		return ZeroDecimal, false
	}
	return tjDecimalRaw(v)
}

// tjDecimalRaw converts a decoded JSON value to a Decimal.
func tjDecimalRaw(v interface{}) (Decimal, bool) {
	switch t := v.(type) {
	case json.Number:
		if d, ok := ParseDecimalInvariant(t.String()); ok {
			return d, true
		}
		return ZeroDecimal, false
	case float64:
		// Render without exponent/trailing zeros, then parse via the exact
		// base-10 path so cents are not corrupted by binary-float rounding.
		s := strconv.FormatFloat(t, 'f', -1, 64)
		if d, ok := ParseDecimalInvariant(s); ok {
			return d, true
		}
		return ZeroDecimal, false
	case string:
		return ParseDecimalInvariant(t)
	default:
		return ZeroDecimal, false
	}
}

// formEncode builds an application/x-www-form-urlencoded body from fields, with
// keys sorted for a deterministic result. Ports FormUrlEncodedContent: keys and
// values are form-escaped (space -> '+', reserved chars percent-encoded).
func formEncode(fields map[string]string) string {
	keys := make([]string, 0, len(fields))
	for k := range fields {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var sb strings.Builder
	for i, k := range keys {
		if i > 0 {
			sb.WriteByte('&')
		}
		sb.WriteString(formEscape(k))
		sb.WriteByte('=')
		sb.WriteString(formEscape(fields[k]))
	}
	return sb.String()
}

// formEscape percent-encodes for application/x-www-form-urlencoded (space -> '+').
// Matches the encoding FormUrlEncodedContent uses.
func formEscape(s string) string {
	var sb strings.Builder
	for i := 0; i < len(s); i++ {
		ch := s[i]
		switch {
		case ch == ' ':
			sb.WriteByte('+')
		case isUnreserved(ch):
			sb.WriteByte(ch)
		default:
			sb.WriteByte('%')
			sb.WriteByte(hexUpper(ch >> 4))
			sb.WriteByte(hexUpper(ch & 0xF))
		}
	}
	return sb.String()
}

// escapeDataString ports Uri.EscapeDataString: RFC 3986 unreserved characters
// (A-Z a-z 0-9 - _ . ~) are kept; everything else (including space) is
// percent-encoded as %XX with upper-case hex.
func escapeDataString(s string) string {
	var sb strings.Builder
	for i := 0; i < len(s); i++ {
		ch := s[i]
		if isUnreserved(ch) {
			sb.WriteByte(ch)
		} else {
			sb.WriteByte('%')
			sb.WriteByte(hexUpper(ch >> 4))
			sb.WriteByte(hexUpper(ch & 0xF))
		}
	}
	return sb.String()
}

// isUnreserved reports whether ch is an RFC 3986 unreserved character.
func isUnreserved(ch byte) bool {
	return (ch >= 'A' && ch <= 'Z') ||
		(ch >= 'a' && ch <= 'z') ||
		(ch >= '0' && ch <= '9') ||
		ch == '-' || ch == '_' || ch == '.' || ch == '~'
}

// hexUpper returns the upper-case hex digit for the low nibble of b.
func hexUpper(b byte) byte {
	const digits = "0123456789ABCDEF"
	return digits[b&0xF]
}

// htmlEncode ports System.Net.WebUtility.HtmlEncode for the characters that
// appear in TwiML values: & < > " '. C# encodes ' as &#39; and " as &quot;.
func htmlEncode(s string) string {
	var sb strings.Builder
	for _, r := range s {
		switch r {
		case '&':
			sb.WriteString("&amp;")
		case '<':
			sb.WriteString("&lt;")
		case '>':
			sb.WriteString("&gt;")
		case '"':
			sb.WriteString("&quot;")
		case '\'':
			sb.WriteString("&#39;")
		default:
			sb.WriteRune(r)
		}
	}
	return sb.String()
}

// statusError builds the error an EnsureSuccessStatusCode raises on a non-2xx.
func statusError(op string, code int) error {
	return errors.New(op + " returned HTTP " + itoaSmall(code))
}
