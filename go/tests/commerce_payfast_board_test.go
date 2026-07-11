// commerce_payfast_board_test.go
//
// Verifies the CircleAI.Commerce.Integration.PayFast port
// (commerce_payfast_board.go). The expected MD5 signatures are computed from the
// exact C# SignatureFor logic (WebUtility.UrlEncode(value).Replace("%20","+") +
// MD5 + lower-hex) run against the .NET 10 runtime, so this asserts byte-for-byte
// signature parity, plus ITN verify and reverse-chronological webhook recall.

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestPayFast_SignatureParityWithPassphrase(t *testing.T) {
	board := circleai.NewInMemoryPayFastBoard(circleai.PayFastConfig{
		MerchantId: "10000100", MerchantKey: "46f0cd694581a", Passphrase: "myPassPhrase123", Sandbox: true,
	})
	fields := []circleai.PayFastField{
		{Key: "merchant_id", Value: "10000100"},
		{Key: "merchant_key", Value: "46f0cd694581a"},
		{Key: "return_url", Value: "https://example.com/return"},
		{Key: "item_name", Value: "Test Item & Co"},
		{Key: "amount", Value: "100.00"},
	}
	got := board.SignatureFor(fields)
	const want = "6bc713fc147ec994075f544b45b81999" // from C# reference
	if got != want {
		t.Fatalf("signature = %s, want %s", got, want)
	}
}

func TestPayFast_SignatureParityNoPassphrase(t *testing.T) {
	board := circleai.NewInMemoryPayFastBoard(circleai.PayFastConfig{MerchantId: "10000100"})
	fields := []circleai.PayFastField{
		{Key: "merchant_id", Value: "10000100"},
		{Key: "amount", Value: "55.50"},
		{Key: "item_name", Value: "Widget"},
	}
	if got, want := board.SignatureFor(fields), "46e389f8c122ba2946049c402fa54bb1"; got != want {
		t.Fatalf("no-pass signature = %s, want %s", got, want)
	}
}

func TestPayFast_SignatureSingleFieldTrailingAmpTrim(t *testing.T) {
	board := circleai.NewInMemoryPayFastBoard(circleai.PayFastConfig{MerchantId: "10000100"})
	fields := []circleai.PayFastField{{Key: "merchant_id", Value: "10000100"}}
	if got, want := board.SignatureFor(fields), "036c31e640eea59940d54b803a3473c6"; got != want {
		t.Fatalf("single-field signature = %s, want %s", got, want)
	}
}

func TestPayFast_SignatureTildeEncoding(t *testing.T) {
	// '~' must encode as %7E (WebUtility.UrlEncode), not stay literal (RFC 3986).
	board := circleai.NewInMemoryPayFastBoard(circleai.PayFastConfig{Passphrase: "pp~1"})
	fields := []circleai.PayFastField{{Key: "note", Value: "a~b c"}}
	if got, want := board.SignatureFor(fields), "1a0678443c390a08a9ab652127f409c2"; got != want {
		t.Fatalf("tilde signature = %s, want %s", got, want)
	}
}

func TestPayFast_VerifyItnAndConfig(t *testing.T) {
	cfg := circleai.PayFastConfig{MerchantId: "10000100", MerchantKey: "k", Passphrase: "p", Sandbox: false}
	board := circleai.NewInMemoryPayFastBoard(cfg)
	if board.Config().MerchantId != "10000100" || board.Config().Sandbox {
		t.Fatalf("config not returned: %+v", board.Config())
	}
	if !board.VerifyItn(circleai.PayFastItnPayload{MerchantId: "10000100"}) {
		t.Fatalf("matching merchant should verify")
	}
	if board.VerifyItn(circleai.PayFastItnPayload{MerchantId: "99999999"}) {
		t.Fatalf("mismatched merchant must not verify")
	}
}

func TestPayFast_RecentWebhooksReverseChronological(t *testing.T) {
	board := circleai.NewInMemoryPayFastBoard(circleai.PayFastConfig{MerchantId: "m"})
	for _, id := range []string{"w1", "w2", "w3"} {
		board.RecordWebhook(circleai.PayFastItnPayload{MerchantId: "m", PaymentId: id})
	}
	recent := board.RecentWebhooks(20)
	if len(recent) != 3 || recent[0].PaymentId != "w3" || recent[1].PaymentId != "w2" || recent[2].PaymentId != "w1" {
		t.Fatalf("recent webhooks reverse order failed: %+v", recent)
	}
	capped := board.RecentWebhooks(2)
	if len(capped) != 2 || capped[0].PaymentId != "w3" || capped[1].PaymentId != "w2" {
		t.Fatalf("capped recent failed: %+v", capped)
	}
}
