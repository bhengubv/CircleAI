// security_redacted_evidence.go
//
// Ports CircleAI.Security.RedactedEvidenceJsonConverter
// (RedactedEvidenceJsonConverter.cs).
//
// The C# type is a System.Text.Json converter for AnomalySignal.Evidence. Go has
// no attribute-driven converter mechanism, so the behaviour is ported as
// explicit marshal/unmarshal helpers that produce byte-identical output:
//
//   Write side — every value is replaced by the SHA-256 hex of its UTF-8 bytes,
//   prefixed "sha256:". Keys (evidence labels) are preserved so structured log
//   sinks can still join by evidence shape, but raw values — which may carry
//   session tokens, payload fragments, or PII — never leave the process in clear
//   text. Empty / missing value → "sha256:".
//
//   Read side — intentionally reverses to an empty map: incoming JSON cannot be
//   trusted to carry the original cleartext, and round-tripping hashes back into
//   the map would mask whether the source-of-record is the in-process signal or
//   a serialised copy. A JSON null reverses to nil.

package circleai

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"sort"
)

// RedactEvidenceValue returns the redacted form of a single evidence value:
// "sha256:" for an empty string, otherwise "sha256:" + lowercase hex of the
// SHA-256 of the value's UTF-8 bytes. Mirrors RedactedEvidenceJsonConverter.
// HashRedacted.
func RedactEvidenceValue(raw string) string {
	if raw == "" {
		return "sha256:"
	}
	sum := sha256.Sum256([]byte(raw))
	return "sha256:" + hex.EncodeToString(sum[:])
}

// RedactEvidence returns a new map with every value replaced by its redacted
// form (keys preserved). A nil input yields a nil map. This is the map-level
// analogue of the converter's Write side.
func RedactEvidence(evidence map[string]string) map[string]string {
	if evidence == nil {
		return nil
	}
	out := make(map[string]string, len(evidence))
	for k, v := range evidence {
		out[k] = RedactEvidenceValue(v)
	}
	return out
}

// MarshalRedactedEvidence serialises evidence to a JSON object with each value
// redacted, matching what System.Text.Json emits through the converter's Write
// method. A nil map marshals to the JSON null literal (mirrors WriteNullValue).
// Keys are emitted in sorted order for deterministic, byte-stable output.
func MarshalRedactedEvidence(evidence map[string]string) ([]byte, error) {
	if evidence == nil {
		return []byte("null"), nil
	}

	keys := make([]string, 0, len(evidence))
	for k := range evidence {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	// Build an ordered object manually so key order is deterministic (Go maps
	// have no stable iteration order, and json.Marshal of a map sorts keys but
	// we redact values first).
	obj := make([]byte, 0, 2+len(keys)*48)
	obj = append(obj, '{')
	for i, k := range keys {
		if i > 0 {
			obj = append(obj, ',')
		}
		keyJSON, err := json.Marshal(k)
		if err != nil {
			return nil, err
		}
		valJSON, err := json.Marshal(RedactEvidenceValue(evidence[k]))
		if err != nil {
			return nil, err
		}
		obj = append(obj, keyJSON...)
		obj = append(obj, ':')
		obj = append(obj, valJSON...)
	}
	obj = append(obj, '}')
	return obj, nil
}

// UnmarshalRedactedEvidence mirrors the converter's Read side: a JSON null
// yields nil; any other value is tolerated but never trusted — it reverses to an
// empty (non-nil) map. Ports RedactedEvidenceJsonConverter.Read.
func UnmarshalRedactedEvidence(data []byte) (map[string]string, error) {
	trimmed := trimJSONSpace(data)
	if len(trimmed) == 4 && string(trimmed) == "null" {
		return nil, nil
	}
	// Validate that the payload is well-formed JSON (Skip in C# would throw on
	// malformed input); we do the same so callers can distinguish garbage.
	var discard json.RawMessage
	if err := json.Unmarshal(data, &discard); err != nil {
		return nil, err
	}
	return map[string]string{}, nil
}

func trimJSONSpace(data []byte) []byte {
	start := 0
	for start < len(data) {
		switch data[start] {
		case ' ', '\t', '\n', '\r':
			start++
			continue
		}
		break
	}
	end := len(data)
	for end > start {
		switch data[end-1] {
		case ' ', '\t', '\n', '\r':
			end--
			continue
		}
		break
	}
	return data[start:end]
}
