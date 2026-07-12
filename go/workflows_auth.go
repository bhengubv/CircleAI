// workflows_auth.go
//
// Ports CircleAI.Workflows/PacaAuth.cs — auth primitives from paca: an
// HMAC-SHA256 JWT issuer/verifier (access + refresh) and an API-key registry
// that stores only SHA-256 hashes.
//
//	JwtPair / JwtPayload (records)   -> structs
//	HmacJwtAuthenticator             -> HmacJwtAuthenticator (self-contained JWT)
//	PacaApiKeyRecord (record)         -> struct (RevokedAtUTC as *time.Time)
//	PacaApiKeyAuthenticator          -> PacaApiKeyAuthenticator
//
// The JWT is a standard HS256 JWT: base64url(header).base64url(payload).sig,
// header {"alg":"HS256","typ":"JWT"}. The payload is emitted with a fixed key
// order (sub, typ, exp, then claims) so the encoded form is stable; Verify
// tolerates any key order since it parses JSON. Constant-time comparison uses
// crypto/subtle, matching the C# fixed-time equality.

package circleai

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// JwtPair is the token-shaped result of issuing a JWT. Ports the JwtPair record.
type JwtPair struct {
	AccessToken         string
	RefreshToken        string
	AccessExpiresAtUTC  time.Time
	RefreshExpiresAtUTC time.Time
}

// JwtPayload is a verified JWT payload. Ports the JwtPayload record. Claims
// excludes the reserved sub/typ/exp keys.
type JwtPayload struct {
	Subject      string
	Claims       map[string]string
	ExpiresAtUTC time.Time
}

// HmacJwtAuthenticator issues + verifies HS256 JWTs. Ports HmacJwtAuthenticator.
// Construct with NewHmacJwtAuthenticator.
type HmacJwtAuthenticator struct {
	secret          []byte
	accessLifetime  time.Duration
	refreshLifetime time.Duration
	clock           func() time.Time
}

// NewHmacJwtAuthenticator constructs the authenticator. signingSecret must be at
// least 16 characters (panics otherwise, mirroring the C# ArgumentException).
// accessLifetime<=0 defaults to 15m; refreshLifetime<=0 defaults to 7 days.
// clock may be nil (defaults to UTC now).
func NewHmacJwtAuthenticator(signingSecret string, accessLifetime, refreshLifetime time.Duration, clock func() time.Time) *HmacJwtAuthenticator {
	if len(strings.TrimSpace(signingSecret)) == 0 || len(signingSecret) < 16 {
		panic("Signing secret must be at least 16 characters.")
	}
	if accessLifetime <= 0 {
		accessLifetime = 15 * time.Minute
	}
	if refreshLifetime <= 0 {
		refreshLifetime = 7 * 24 * time.Hour
	}
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &HmacJwtAuthenticator{
		secret:          []byte(signingSecret),
		accessLifetime:  accessLifetime,
		refreshLifetime: refreshLifetime,
		clock:           clock,
	}
}

// Issue issues access + refresh tokens for subject. Ports Issue. Returns an
// error if subject is blank.
func (a *HmacJwtAuthenticator) Issue(subject string, claims map[string]string) (JwtPair, error) {
	if strings.TrimSpace(subject) == "" {
		return JwtPair{}, errors.New("subject required")
	}
	now := a.clock()
	accessExp := now.Add(a.accessLifetime)
	refreshExp := now.Add(a.refreshLifetime)
	return JwtPair{
		AccessToken:         a.encodeToken(subject, "access", accessExp, claims),
		RefreshToken:        a.encodeToken(subject, "refresh", refreshExp, nil),
		AccessExpiresAtUTC:  accessExp,
		RefreshExpiresAtUTC: refreshExp,
	}, nil
}

// Verify verifies a token, returning the payload and true, or (zero, false) if
// invalid/expired/wrong-type. Ports Verify (default expectedType "access").
func (a *HmacJwtAuthenticator) Verify(token, expectedType string) (JwtPayload, bool) {
	if expectedType == "" {
		expectedType = "access"
	}
	if strings.TrimSpace(token) == "" {
		return JwtPayload{}, false
	}
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return JwtPayload{}, false
	}
	header, payload, sig := parts[0], parts[1], parts[2]
	expected := a.signBase64URL(header + "." + payload)
	if subtle.ConstantTimeCompare([]byte(expected), []byte(sig)) != 1 {
		return JwtPayload{}, false
	}
	jsonBytes, err := base64URLDecode(payload)
	if err != nil {
		return JwtPayload{}, false
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(jsonBytes, &raw); err != nil {
		return JwtPayload{}, false
	}

	var typ string
	if err := json.Unmarshal(raw["typ"], &typ); err != nil || typ != expectedType {
		return JwtPayload{}, false
	}
	var subject string
	if err := json.Unmarshal(raw["sub"], &subject); err != nil {
		return JwtPayload{}, false
	}
	var expSeconds int64
	if err := json.Unmarshal(raw["exp"], &expSeconds); err != nil {
		return JwtPayload{}, false
	}
	exp := time.Unix(expSeconds, 0).UTC()
	if !exp.After(a.clock()) {
		return JwtPayload{}, false
	}

	extra := make(map[string]string)
	for k, v := range raw {
		if k == "typ" || k == "sub" || k == "exp" {
			continue
		}
		var s string
		if err := json.Unmarshal(v, &s); err == nil {
			extra[k] = s
		} else {
			extra[k] = strings.TrimSpace(string(v))
		}
	}
	return JwtPayload{Subject: subject, Claims: extra, ExpiresAtUTC: exp}, true
}

func (a *HmacJwtAuthenticator) encodeToken(subject, typ string, expires time.Time, claims map[string]string) string {
	const header = `{"alg":"HS256","typ":"JWT"}`
	// Emit payload with a deterministic key order: sub, typ, exp, then the
	// extra claims sorted by key. json.Marshal of a map would sort ALL keys,
	// which is also stable — but this ordering keeps the reserved trio first.
	var sb strings.Builder
	sb.WriteString(`{`)
	sb.WriteString(`"sub":`)
	sb.Write(mustJSONString(subject))
	sb.WriteString(`,"typ":`)
	sb.Write(mustJSONString(typ))
	sb.WriteString(`,"exp":`)
	sb.WriteString(strconv.FormatInt(expires.Unix(), 10))
	if len(claims) > 0 {
		keys := make([]string, 0, len(claims))
		for k := range claims {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		for _, k := range keys {
			if k == "sub" || k == "typ" || k == "exp" {
				continue
			}
			sb.WriteString(`,`)
			sb.Write(mustJSONString(k))
			sb.WriteString(`:`)
			sb.Write(mustJSONString(claims[k]))
		}
	}
	sb.WriteString(`}`)

	headerB := base64URLEncode([]byte(header))
	payloadB := base64URLEncode([]byte(sb.String()))
	signing := headerB + "." + payloadB
	return signing + "." + a.signBase64URL(signing)
}

func (a *HmacJwtAuthenticator) signBase64URL(signing string) string {
	mac := hmac.New(sha256.New, a.secret)
	mac.Write([]byte(signing))
	return base64URLEncode(mac.Sum(nil))
}

// base64URLEncode encodes without padding, matching the C# TrimEnd('=') +
// '+'->'-' / '/'->'_' transform (which is exactly RawURLEncoding).
func base64URLEncode(b []byte) string {
	return base64.RawURLEncoding.EncodeToString(b)
}

// base64URLDecode decodes a padding-less base64url string, tolerating input
// that happens to carry padding.
func base64URLDecode(s string) ([]byte, error) {
	s = strings.TrimRight(s, "=")
	return base64.RawURLEncoding.DecodeString(s)
}

func mustJSONString(s string) []byte {
	b, _ := json.Marshal(s)
	return b
}

// PacaApiKeyRecord is an issued API key (hashes only). Ports the
// PacaApiKeyRecord record. RevokedAtUTC is nil for a live key.
type PacaApiKeyRecord struct {
	KeyID        string
	Label        string
	HashedSecret string
	CreatedAtUTC time.Time
	RevokedAtUTC *time.Time
}

// PacaApiKeyAuthenticator is an API-key registry separate from JWT user auth.
// Ports PacaApiKeyAuthenticator. Construct with NewPacaApiKeyAuthenticator.
type PacaApiKeyAuthenticator struct {
	mu    sync.Mutex
	keys  map[string]PacaApiKeyRecord
	clock func() time.Time
}

// NewPacaApiKeyAuthenticator constructs an empty registry. clock may be nil
// (defaults to UTC now).
func NewPacaApiKeyAuthenticator(clock func() time.Time) *PacaApiKeyAuthenticator {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &PacaApiKeyAuthenticator{keys: make(map[string]PacaApiKeyRecord), clock: clock}
}

// Issue generates a fresh key. The raw secret is returned ONCE for the caller to
// store; only its hash is retained. Ports Issue. Returns an error if label is
// blank.
func (p *PacaApiKeyAuthenticator) Issue(label string) (PacaApiKeyRecord, string, error) {
	if strings.TrimSpace(label) == "" {
		return PacaApiKeyRecord{}, "", errors.New("label required")
	}
	keyID := newHexGUID()
	secretBytes := make([]byte, 32)
	if _, err := rand.Read(secretBytes); err != nil {
		return PacaApiKeyRecord{}, "", err
	}
	secret := base64.RawStdEncoding.EncodeToString(secretBytes)
	record := PacaApiKeyRecord{
		KeyID:        keyID,
		Label:        label,
		HashedSecret: apiKeyHash(secret),
		CreatedAtUTC: p.clock(),
	}
	p.mu.Lock()
	p.keys[keyID] = record
	p.mu.Unlock()
	return record, secret, nil
}

// Verify verifies an incoming key, returning the record and true if valid and
// live. Ports Verify.
func (p *PacaApiKeyAuthenticator) Verify(keyID, presentedSecret string) (PacaApiKeyRecord, bool) {
	p.mu.Lock()
	record, ok := p.keys[keyID]
	p.mu.Unlock()
	if !ok || record.RevokedAtUTC != nil {
		return PacaApiKeyRecord{}, false
	}
	if subtle.ConstantTimeCompare([]byte(apiKeyHash(presentedSecret)), []byte(record.HashedSecret)) != 1 {
		return PacaApiKeyRecord{}, false
	}
	return record, true
}

// Revoke revokes a key. Idempotent. Ports Revoke.
func (p *PacaApiKeyAuthenticator) Revoke(keyID string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	existing, ok := p.keys[keyID]
	if !ok || existing.RevokedAtUTC != nil {
		return
	}
	now := p.clock()
	existing.RevokedAtUTC = &now
	p.keys[keyID] = existing
}

// apiKeyHash renders SHA-256(secret) as unpadded base64, matching the C# Hash
// (Convert.ToBase64String(SHA256.HashData(...)).TrimEnd('=')).
func apiKeyHash(secret string) string {
	sum := sha256.Sum256([]byte(secret))
	return base64.RawStdEncoding.EncodeToString(sum[:])
}
