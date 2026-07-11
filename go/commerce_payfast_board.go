// commerce_payfast_board.go
//
// Ports the CircleAI.Commerce.Integration.PayFast primitive vertical
// (PayFastPrimitives.cs):
//   PayFastConfig / PayFastItnPayload (records) -> value structs
//   IPayFastBoard        -> PayFastBoard interface (I-prefix dropped)
//   InMemoryPayFastBoard -> InMemoryPayFastBoard
//
// The CommerceIntegrationPayFastDomainContext (static prompt strings) and
// CommerceIntegrationPayFastCompanionAdapter (LLM-prompt wrapper) are out of
// scope for the deterministic in-memory board.
//
// SIGNATURE FIDELITY: SignatureFor reproduces PayFast's MD5 signature exactly:
// each ordered field is emitted as key=WebUtility.UrlEncode(value)&, the
// passphrase (when set) is appended as passphrase=<encoded>, otherwise a trailing
// '&' is trimmed, and the MD5 of the UTF-8 string is returned as lower-case hex.
//
// The C# encodes with System.Net.WebUtility.UrlEncode(value).Replace("%20","+").
// WebUtility.UrlEncode already emits '+' for space (so the Replace is a no-op),
// keeps [A-Za-z0-9] and -_.!*() unescaped, and percent-encodes everything else
// (including '~' -> %7E, unlike RFC 3986) with UPPER-CASE hex. This port ships a
// dedicated webUtilityUrlEncode reproducing that byte-for-byte (verified against
// the .NET 10 runtime), rather than the RFC-3986 escapeDataString helper used by
// the telephony surface, because the safe-character sets differ.
//
// ORDERED FIELDS: the C# takes an IReadOnlyDictionary whose enumeration order is
// the signature order. Go maps have no stable order, so SignatureFor takes an
// ordered slice of key/value pairs (PayFastField) — the faithful representation
// of "orderedFields".

package circleai

import (
	"crypto/md5"
	"encoding/hex"
	"strings"
	"sync"
)

// PayFastConfig is PayFast merchant configuration. Ports the PayFastConfig record.
type PayFastConfig struct {
	MerchantId  string
	MerchantKey string
	Passphrase  string
	Sandbox     bool
}

// PayFastItnPayload is an Instant Transaction Notification payload. Ports the
// PayFastItnPayload record.
type PayFastItnPayload struct {
	MerchantId    string
	PaymentId     string
	PaymentStatus string
	Amount        Decimal
	MPaymentId    string
	Signature     string
}

// PayFastField is one ordered key/value pair for signature computation. The
// ordered slice preserves the signature field order that the C#
// IReadOnlyDictionary enumeration provides.
type PayFastField struct {
	Key   string
	Value string
}

// DefaultPayFastWebhookLimit is the C# default `limit = 20` for RecentWebhooks.
const DefaultPayFastWebhookLimit = 20

// PayFastBoard is the PayFast signing/verification/webhook board. Ports
// IPayFastBoard. Config is exposed as a method.
type PayFastBoard interface {
	// Config returns the merchant configuration.
	Config() PayFastConfig
	// SignatureFor computes the MD5 signature over the ordered fields (+ passphrase).
	SignatureFor(orderedFields []PayFastField) string
	// VerifyItn checks a payload's MerchantId against the configured one.
	VerifyItn(p PayFastItnPayload) bool
	RecordWebhook(p PayFastItnPayload)
	// RecentWebhooks lists the most recently recorded webhooks, newest first, capped.
	RecentWebhooks(limit int) []PayFastItnPayload
}

// InMemoryPayFastBoard is a concurrency-safe in-memory PayFastBoard. Ports
// InMemoryPayFastBoard (webhooks in an ordered list guarded by a mutex).
type InMemoryPayFastBoard struct {
	mu       sync.RWMutex
	config   PayFastConfig
	webhooks []PayFastItnPayload
}

// NewInMemoryPayFastBoard constructs a board with the given config. Ports the C#
// constructor (which throws ArgumentNullException on a null config; a value
// PayFastConfig has no null analogue here).
func NewInMemoryPayFastBoard(cfg PayFastConfig) *InMemoryPayFastBoard {
	return &InMemoryPayFastBoard{config: cfg, webhooks: make([]PayFastItnPayload, 0)}
}

// Config returns the merchant configuration. Ports the Config property.
func (b *InMemoryPayFastBoard) Config() PayFastConfig { return b.config }

// SignatureFor computes the PayFast MD5 signature. Ports SignatureFor exactly:
// build "key=<enc>&" for each field, append "passphrase=<enc>" when a passphrase
// is set else trim a trailing '&', then MD5 the UTF-8 bytes and lower-case-hex
// the digest.
func (b *InMemoryPayFastBoard) SignatureFor(orderedFields []PayFastField) string {
	var sb strings.Builder
	for _, kv := range orderedFields {
		sb.WriteString(kv.Key)
		sb.WriteByte('=')
		sb.WriteString(webUtilityUrlEncode(kv.Value))
		sb.WriteByte('&')
	}
	s := sb.String()
	if b.config.Passphrase != "" {
		s += "passphrase=" + webUtilityUrlEncode(b.config.Passphrase)
	} else if len(s) > 0 && s[len(s)-1] == '&' {
		s = s[:len(s)-1]
	}
	sum := md5.Sum([]byte(s))
	return hex.EncodeToString(sum[:])
}

// VerifyItn returns true when the payload's MerchantId matches the configured
// MerchantId. Ports VerifyItn.
func (b *InMemoryPayFastBoard) VerifyItn(p PayFastItnPayload) bool {
	return p.MerchantId == b.config.MerchantId
}

// RecordWebhook appends a webhook payload. Ports RecordWebhook.
func (b *InMemoryPayFastBoard) RecordWebhook(p PayFastItnPayload) {
	b.mu.Lock()
	b.webhooks = append(b.webhooks, p)
	b.mu.Unlock()
}

// RecentWebhooks lists up to limit webhooks most-recent-first. Ports
// RecentWebhooks (AsEnumerable().Reverse().Take(limit)) — i.e. reverse insertion
// order, capped.
func (b *InMemoryPayFastBoard) RecentWebhooks(limit int) []PayFastItnPayload {
	// LINQ Take(n) yields empty for n <= 0; clamp negatives to 0 to match.
	if limit < 0 {
		limit = 0
	}
	b.mu.RLock()
	defer b.mu.RUnlock()
	out := make([]PayFastItnPayload, 0)
	for i := len(b.webhooks) - 1; i >= 0; i-- {
		if len(out) >= limit {
			break
		}
		out = append(out, b.webhooks[i])
	}
	return out
}

// webUtilityUrlEncode reproduces System.Net.WebUtility.UrlEncode(...).Replace(
// "%20","+") byte-for-byte (verified against .NET 10): space -> '+'; the safe set
// [A-Za-z0-9] plus - _ . ! * ( ) is kept; every other byte is percent-encoded
// with UPPER-CASE hex (notably '~' -> %7E, unlike RFC 3986). Operates on UTF-8
// bytes so multi-byte runes are encoded per byte, as .NET does.
func webUtilityUrlEncode(s string) string {
	var sb strings.Builder
	for i := 0; i < len(s); i++ {
		ch := s[i]
		switch {
		case ch == ' ':
			sb.WriteByte('+')
		case webUtilitySafe(ch):
			sb.WriteByte(ch)
		default:
			sb.WriteByte('%')
			sb.WriteByte(hexUpper(ch >> 4))
			sb.WriteByte(hexUpper(ch & 0xF))
		}
	}
	return sb.String()
}

// webUtilitySafe reports whether ch is left unescaped by WebUtility.UrlEncode
// (space is handled separately as '+'). Safe set: alphanumerics and -_.!*().
func webUtilitySafe(ch byte) bool {
	if (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') {
		return true
	}
	switch ch {
	case '-', '_', '.', '!', '*', '(', ')':
		return true
	}
	return false
}

// Interface guard.
var _ PayFastBoard = (*InMemoryPayFastBoard)(nil)
