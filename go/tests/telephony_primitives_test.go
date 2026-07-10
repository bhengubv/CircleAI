// telephony_primitives_test.go
//
// Verifies CircleAI.Telephony/Primitives.cs ports: enum ordinals + names, and
// the Decimal money type standing in for C# decimal (parse/format/arithmetic).

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestTelephonyEnumOrdinals(t *testing.T) {
	// CallDirection: Inbound=0, Outbound=1.
	if int(circleai.CallDirectionInbound) != 0 || int(circleai.CallDirectionOutbound) != 1 {
		t.Errorf("CallDirection ordinals wrong: %d %d", circleai.CallDirectionInbound, circleai.CallDirectionOutbound)
	}
	// CallStatus: Ringing=0 .. Transferred=7.
	wantStatus := []struct {
		s    circleai.CallStatus
		ord  int
		name string
	}{
		{circleai.CallStatusRinging, 0, "Ringing"},
		{circleai.CallStatusActive, 1, "Active"},
		{circleai.CallStatusEndedByCaller, 2, "EndedByCaller"},
		{circleai.CallStatusEndedByCallee, 3, "EndedByCallee"},
		{circleai.CallStatusEndedByAgent, 4, "EndedByAgent"},
		{circleai.CallStatusVoicemail, 5, "Voicemail"},
		{circleai.CallStatusFailed, 6, "Failed"},
		{circleai.CallStatusTransferred, 7, "Transferred"},
	}
	for _, w := range wantStatus {
		if int(w.s) != w.ord {
			t.Errorf("CallStatus %s ordinal = %d, want %d", w.name, int(w.s), w.ord)
		}
		if w.s.String() != w.name {
			t.Errorf("CallStatus.String() = %q, want %q", w.s.String(), w.name)
		}
	}
	// CallMediaFormat: Mulaw8000=0, Alaw8000=1, Pcm16000=2, Pcm24000=3.
	if int(circleai.CallMediaFormatMulaw8000) != 0 || int(circleai.CallMediaFormatAlaw8000) != 1 ||
		int(circleai.CallMediaFormatPcm16000) != 2 || int(circleai.CallMediaFormatPcm24000) != 3 {
		t.Error("CallMediaFormat ordinals wrong")
	}
	// TransferMode: Cold=0, Warm=1.
	if int(circleai.TransferModeCold) != 0 || int(circleai.TransferModeWarm) != 1 {
		t.Error("TransferMode ordinals wrong")
	}
	if circleai.CallMediaFormatPcm24000.String() != "Pcm24000" || circleai.TransferModeWarm.String() != "Warm" {
		t.Error("enum String() wrong")
	}
}

func TestDecimalParseFormat(t *testing.T) {
	cases := []struct {
		in   string
		ok   bool
		want string
	}{
		{"0", true, "0"},
		{"1.15", true, "1.15"},
		{"1.1500", true, "1.15"}, // trailing zeros trimmed
		{"-2.5", true, "-2.5"},
		{"+3", true, "3"},
		{"100", true, "100"},
		{"0.000001", true, "0.000001"},
		{" 4.25 ", true, "4.25"}, // NumberStyles.Any tolerates surrounding space
		{"", false, "0"},
		{"abc", false, "0"},
		{"1.2.3", false, "0"},
	}
	for _, c := range cases {
		d, ok := circleai.ParseDecimalInvariant(c.in)
		if ok != c.ok {
			t.Errorf("ParseDecimalInvariant(%q) ok = %v, want %v", c.in, ok, c.ok)
			continue
		}
		if ok && d.String() != c.want {
			t.Errorf("ParseDecimalInvariant(%q) = %q, want %q", c.in, d.String(), c.want)
		}
	}
}

func TestDecimalArithmeticAndCompare(t *testing.T) {
	a, _ := circleai.ParseDecimalInvariant("1.15")
	b, _ := circleai.ParseDecimalInvariant("2.85")
	sum := a.Add(b)
	if sum.String() != "4" {
		t.Errorf("1.15 + 2.85 = %q, want 4", sum.String())
	}
	if !circleai.DecimalFromInt(4).Equal(sum) {
		t.Error("DecimalFromInt(4) != 4")
	}
	if a.Cmp(b) != -1 || b.Cmp(a) != 1 || a.Cmp(a) != 0 {
		t.Error("Decimal.Cmp wrong")
	}
	if !circleai.ZeroDecimal.IsZero() {
		t.Error("ZeroDecimal.IsZero() should be true")
	}
	if circleai.ZeroDecimal.String() != "0" {
		t.Errorf("ZeroDecimal.String() = %q, want 0", circleai.ZeroDecimal.String())
	}
}
