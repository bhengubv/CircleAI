// security_redacted_evidence_test.go
//
// Verifies the RedactedEvidenceJsonConverter port (RedactedEvidenceJsonConverter.cs):
//   - RedactEvidenceValue = "sha256:" + lowercase hex of SHA-256(utf8(value));
//     empty value → "sha256:".
//   - MarshalRedactedEvidence emits a JSON object of redacted values (nil → null),
//     never the cleartext.
//   - UnmarshalRedactedEvidence reverses any object to an empty map and null to nil.

package circleai_test

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestRedactEvidenceValue_HashesUtf8(t *testing.T) {
	raw := "session-token-abc123"
	got := circleai.RedactEvidenceValue(raw)
	sum := sha256.Sum256([]byte(raw))
	want := "sha256:" + hex.EncodeToString(sum[:])
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
	// Lowercase hex only.
	if got != strings.ToLower(got) {
		t.Errorf("hex should be lowercase: %q", got)
	}
}

func TestRedactEvidenceValue_EmptyIsPrefixOnly(t *testing.T) {
	if got := circleai.RedactEvidenceValue(""); got != "sha256:" {
		t.Errorf("empty value: got %q, want 'sha256:'", got)
	}
}

func TestRedactEvidence_MapRedactsAllValues(t *testing.T) {
	in := map[string]string{"token": "secret", "empty": ""}
	out := circleai.RedactEvidence(in)
	if out["empty"] != "sha256:" {
		t.Errorf("empty entry: got %q", out["empty"])
	}
	if !strings.HasPrefix(out["token"], "sha256:") || strings.Contains(out["token"], "secret") {
		t.Errorf("token entry leaked or unredacted: %q", out["token"])
	}
	if circleai.RedactEvidence(nil) != nil {
		t.Error("nil map should redact to nil")
	}
}

func TestMarshalRedactedEvidence_NeverLeaksCleartext(t *testing.T) {
	in := map[string]string{"password": "hunter2", "ip": "10.0.0.1"}
	data, err := circleai.MarshalRedactedEvidence(in)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	s := string(data)
	if strings.Contains(s, "hunter2") || strings.Contains(s, "10.0.0.1") {
		t.Errorf("marshal leaked cleartext: %s", s)
	}
	// Must be valid JSON object with the original keys.
	var parsed map[string]string
	if err := json.Unmarshal(data, &parsed); err != nil {
		t.Fatalf("output not valid JSON: %v (%s)", err, s)
	}
	if _, ok := parsed["password"]; !ok {
		t.Error("key 'password' should be preserved")
	}
	if !strings.HasPrefix(parsed["password"], "sha256:") {
		t.Errorf("value should be redacted: %q", parsed["password"])
	}
}

func TestMarshalRedactedEvidence_NilIsJsonNull(t *testing.T) {
	data, err := circleai.MarshalRedactedEvidence(nil)
	if err != nil {
		t.Fatalf("marshal nil: %v", err)
	}
	if string(data) != "null" {
		t.Errorf("nil map: got %q, want null", string(data))
	}
}

func TestMarshalRedactedEvidence_DeterministicKeyOrder(t *testing.T) {
	in := map[string]string{"b": "2", "a": "1", "c": "3"}
	first, _ := circleai.MarshalRedactedEvidence(in)
	for i := 0; i < 5; i++ {
		again, _ := circleai.MarshalRedactedEvidence(in)
		if string(again) != string(first) {
			t.Fatalf("output not deterministic: %q vs %q", string(first), string(again))
		}
	}
}

func TestUnmarshalRedactedEvidence_ObjectBecomesEmptyMap(t *testing.T) {
	m, err := circleai.UnmarshalRedactedEvidence([]byte(`{"token":"sha256:deadbeef"}`))
	if err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if m == nil {
		t.Fatal("object should reverse to a non-nil empty map")
	}
	if len(m) != 0 {
		t.Errorf("object should reverse to EMPTY map, got %v", m)
	}
}

func TestUnmarshalRedactedEvidence_NullBecomesNil(t *testing.T) {
	m, err := circleai.UnmarshalRedactedEvidence([]byte(`null`))
	if err != nil {
		t.Fatalf("unmarshal null: %v", err)
	}
	if m != nil {
		t.Errorf("null should reverse to nil, got %v", m)
	}
}

func TestUnmarshalRedactedEvidence_MalformedErrors(t *testing.T) {
	if _, err := circleai.UnmarshalRedactedEvidence([]byte(`{not json`)); err == nil {
		t.Error("malformed JSON should error")
	}
}
