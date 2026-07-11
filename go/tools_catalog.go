// tools_catalog.go
//
// Ports CircleAI.Tools.Catalog (the composio-pattern provider catalog):
//   Contracts.cs           -> AuthKind, ProviderDescriptor, OAuth2Descriptor,
//                             CredentialBundle, QuotaPolicy, ToolNamespace,
//                             IProviderCatalog, ICredentialStore,
//                             IOAuth2FlowDriver, IQuotaGuard, IToolNamespaceStore
//   InMemoryToolsCatalog.cs -> InMemoryProviderCatalog, AesGcmCredentialStore,
//                             OAuth2FlowDriver, SlidingWindowQuotaGuard,
//                             InMemoryToolNamespaceStore
//   NullImplementations.cs  -> NullProviderCatalog, NullCredentialStore,
//                             NullOAuth2FlowDriver, NullQuotaGuard,
//                             NullToolNamespaceStore
//
// The provider catalog supports substring + tag search; credentials are
// encrypted at rest via AES-256-GCM with a host-supplied 32-byte key. The
// OAuth2 flow driver builds standards-compliant authorize URLs and delegates
// the vendor-specific token exchange to a host function. The quota guard
// enforces per-minute, daily, and concurrency caps over a sliding window.
//
// Async note: the C# surface is ValueTask-based with a CancellationToken. The
// Go port takes ctx context.Context and returns (value, error). Methods that
// throw ArgumentException in C# return a non-nil error in Go.

package circleai

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/url"
	"sort"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// AuthKind
// ---------------------------------------------------------------------------

// AuthKind describes how a provider authenticates. Ports the AuthKind enum
// (stable ordinals).
type AuthKind int

const (
	// AuthKindNone — the provider needs no authentication.
	AuthKindNone AuthKind = iota
	// AuthKindApiKey — a static API key.
	AuthKindApiKey
	// AuthKindBearerToken — a bearer token.
	AuthKindBearerToken
	// AuthKindOAuth2 — a 3-legged OAuth2 flow (see OAuth2Descriptor).
	AuthKindOAuth2
	// AuthKindBasic — HTTP basic auth.
	AuthKindBasic
	// AuthKindCustom — a provider-specific scheme.
	AuthKindCustom
)

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------

// OAuth2Descriptor holds OAuth2 configuration when a provider's Auth is
// AuthKindOAuth2. Ports the OAuth2Descriptor record.
type OAuth2Descriptor struct {
	AuthorizeUrl string
	TokenUrl     string
	Scopes       []string
	UserInfoUrl  *string // nil when absent
}

// ProviderDescriptor is one provider in the catalog (Gmail, Slack, Linear, …).
// Ports the ProviderDescriptor record.
type ProviderDescriptor struct {
	ProviderId   string
	DisplayName  string
	Description  string
	Homepage     *string // nil when absent
	Auth         AuthKind
	Tags         []string
	Capabilities []string
	OAuth2       *OAuth2Descriptor // nil unless Auth == AuthKindOAuth2
}

// CredentialBundle is one stored credential for one user / one provider.
// Ports the CredentialBundle record. Serialised to JSON for encryption at rest.
type CredentialBundle struct {
	ProviderId   string            `json:"ProviderId"`
	UserId       string            `json:"UserId"`
	Fields       map[string]string `json:"Fields"`
	ExpiresAtUtc *time.Time        `json:"ExpiresAtUtc,omitempty"` // nil when the credential never expires
}

// QuotaPolicy is a quota / rate-limit policy on one (provider, user) pair.
// Ports the QuotaPolicy record.
type QuotaPolicy struct {
	ProviderId      string
	UserId          string
	DailyCallBudget int
	MaxConcurrent   int
	PerMinuteCap    int
}

// ToolNamespace is a namespace partition — one user's tool list, kept separate
// from the next. Ports the ToolNamespace record.
type ToolNamespace struct {
	NamespaceId string
	OwnerUserId string
	ProviderIds []string
}

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

// IProviderCatalog is the provider directory. Ports the IProviderCatalog interface.
type IProviderCatalog interface {
	// BackendId identifies the backing implementation.
	BackendId() string
	// ListProviders returns every provider, ordered by ProviderId.
	ListProviders(ctx context.Context) ([]ProviderDescriptor, error)
	// GetProvider returns the descriptor for providerId, or nil if absent.
	GetProvider(ctx context.Context, providerId string) (*ProviderDescriptor, error)
	// SearchProviders runs a semantic (substring + tag) search, returning the top-k hits.
	SearchProviders(ctx context.Context, query string, topK int) ([]ProviderDescriptor, error)
}

// ICredentialStore is credential storage. Implementations must encrypt at rest.
// Ports the ICredentialStore interface.
type ICredentialStore interface {
	BackendId() string
	Upsert(ctx context.Context, bundle CredentialBundle) error
	Get(ctx context.Context, providerId, userId string) (*CredentialBundle, error)
	Delete(ctx context.Context, providerId, userId string) error
}

// IOAuth2FlowDriver initiates and completes a 3-legged OAuth2 flow.
// Ports the IOAuth2FlowDriver interface.
type IOAuth2FlowDriver interface {
	BackendId() string
	// Start builds the redirect URL for the user's browser.
	Start(ctx context.Context, providerId, userId, redirectUri string) (string, error)
	// Complete exchanges the authorization code for a credential bundle.
	Complete(ctx context.Context, providerId, userId, authorizationCode, redirectUri string) (CredentialBundle, error)
}

// IQuotaGuard enforces per-(provider,user) quota. Ports the IQuotaGuard interface.
type IQuotaGuard interface {
	BackendId() string
	// TryAcquire attempts to reserve one call slot; false when a cap is hit.
	TryAcquire(ctx context.Context, providerId, userId string) (bool, error)
	// SetPolicy sets (or replaces) the policy for a (provider, user) pair.
	SetPolicy(ctx context.Context, policy QuotaPolicy) error
	// GetPolicy returns the policy for a (provider, user) pair, or nil.
	GetPolicy(ctx context.Context, providerId, userId string) (*QuotaPolicy, error)
}

// IToolNamespaceStore stores per-user tool namespaces. Ports the
// IToolNamespaceStore interface.
type IToolNamespaceStore interface {
	BackendId() string
	Upsert(ctx context.Context, ns ToolNamespace) error
	Get(ctx context.Context, namespaceId string) (*ToolNamespace, error)
	ListForUser(ctx context.Context, userId string) ([]ToolNamespace, error)
}

// ---------------------------------------------------------------------------
// InMemoryProviderCatalog
// ---------------------------------------------------------------------------

// InMemoryProviderCatalog is an in-memory IProviderCatalog with substring + tag
// search. Ports InMemoryProviderCatalog. Registration is case-insensitive by
// ProviderId.
type InMemoryProviderCatalog struct {
	mu    sync.RWMutex
	items map[string]ProviderDescriptor // key = lower-cased ProviderId
}

// NewInMemoryProviderCatalog creates an empty catalog.
func NewInMemoryProviderCatalog() *InMemoryProviderCatalog {
	return &InMemoryProviderCatalog{items: make(map[string]ProviderDescriptor)}
}

// BackendId returns "in-memory".
func (c *InMemoryProviderCatalog) BackendId() string { return "in-memory" }

// Register adds or replaces a provider. A provider with an empty ProviderId is
// ignored (the C# original throws on a null descriptor).
func (c *InMemoryProviderCatalog) Register(p ProviderDescriptor) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.items[strings.ToLower(p.ProviderId)] = p
}

// ListProviders returns every provider ordered by ProviderId.
func (c *InMemoryProviderCatalog) ListProviders(ctx context.Context) ([]ProviderDescriptor, error) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := make([]ProviderDescriptor, 0, len(c.items))
	for _, p := range c.items {
		out = append(out, p)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ProviderId < out[j].ProviderId })
	return out, nil
}

// GetProvider returns the descriptor for providerId, or nil.
func (c *InMemoryProviderCatalog) GetProvider(ctx context.Context, providerId string) (*ProviderDescriptor, error) {
	if strings.TrimSpace(providerId) == "" {
		return nil, errors.New("providerId required")
	}
	c.mu.RLock()
	defer c.mu.RUnlock()
	if p, ok := c.items[strings.ToLower(providerId)]; ok {
		out := p
		return &out, nil
	}
	return nil, nil
}

// SearchProviders scores providers by substring match on name/description/tags/
// capabilities and returns the top-k. Ties are broken by ProviderId so the
// result is deterministic. Ports SearchProvidersAsync + Score.
func (c *InMemoryProviderCatalog) SearchProviders(ctx context.Context, query string, topK int) ([]ProviderDescriptor, error) {
	if topK <= 0 {
		return nil, errors.New("topK must be positive")
	}
	c.mu.RLock()
	defer c.mu.RUnlock()

	type scored struct {
		p ProviderDescriptor
		s int
	}
	var hits []scored
	for _, p := range c.items {
		if s := scoreProvider(p, query); s > 0 {
			hits = append(hits, scored{p, s})
		}
	}
	sort.Slice(hits, func(i, j int) bool {
		if hits[i].s != hits[j].s {
			return hits[i].s > hits[j].s
		}
		return hits[i].p.ProviderId < hits[j].p.ProviderId
	})
	if len(hits) > topK {
		hits = hits[:topK]
	}
	out := make([]ProviderDescriptor, len(hits))
	for i, h := range hits {
		out[i] = h.p
	}
	return out, nil
}

func scoreProvider(p ProviderDescriptor, q string) int {
	s := 0
	if substringFold(p.DisplayName, q) {
		s += 3
	}
	if substringFold(p.Description, q) {
		s++
	}
	for _, t := range p.Tags {
		if substringFold(t, q) {
			s += 2
			break
		}
	}
	for _, cap := range p.Capabilities {
		if substringFold(cap, q) {
			s += 2
			break
		}
	}
	return s
}

// substringFold reports a case-insensitive substring match, mirroring the C#
// string.Contains(q, StringComparison.OrdinalIgnoreCase) used by Score.
func substringFold(haystack, needle string) bool {
	return strings.Contains(strings.ToLower(haystack), strings.ToLower(needle))
}

var _ IProviderCatalog = (*InMemoryProviderCatalog)(nil)

// ---------------------------------------------------------------------------
// AesGcmCredentialStore
// ---------------------------------------------------------------------------

// AesGcmCredentialStore is an AES-256-GCM-encrypted ICredentialStore. The host
// supplies the 32-byte key. Ports AesGcmCredentialStore. On-wire layout per
// entry is nonce(12) || tag(16) || ciphertext, matching the C# original so a
// blob written by either implementation is readable by the other.
type AesGcmCredentialStore struct {
	key []byte
	mu  sync.Mutex
	enc map[string][]byte
}

// NewAesGcmCredentialStore constructs the store. key32 must be exactly 32 bytes.
func NewAesGcmCredentialStore(key32 []byte) (*AesGcmCredentialStore, error) {
	if len(key32) != 32 {
		return nil, errors.New("key must be 32 bytes (AES-256-GCM)")
	}
	k := make([]byte, 32)
	copy(k, key32)
	return &AesGcmCredentialStore{key: k, enc: make(map[string][]byte)}, nil
}

// BackendId returns "aes-gcm".
func (s *AesGcmCredentialStore) BackendId() string { return "aes-gcm" }

// Upsert encrypts and stores the bundle keyed by (provider, user).
func (s *AesGcmCredentialStore) Upsert(ctx context.Context, bundle CredentialBundle) error {
	pt, err := json.Marshal(bundle)
	if err != nil {
		return err
	}
	block, err := aes.NewCipher(s.key)
	if err != nil {
		return err
	}
	gcm, err := cipher.NewGCMWithTagSize(block, 16)
	if err != nil {
		return err
	}
	nonce := make([]byte, gcm.NonceSize()) // 12
	if _, err := rand.Read(nonce); err != nil {
		return err
	}
	// Seal appends ciphertext||tag; split so we can store nonce||tag||ciphertext.
	sealed := gcm.Seal(nil, nonce, pt, nil)
	ctLen := len(sealed) - 16
	ctBuf := sealed[:ctLen]
	tag := sealed[ctLen:]

	combined := make([]byte, 0, len(nonce)+16+ctLen)
	combined = append(combined, nonce...)
	combined = append(combined, tag...)
	combined = append(combined, ctBuf...)

	s.mu.Lock()
	defer s.mu.Unlock()
	s.enc[credKey(bundle.ProviderId, bundle.UserId)] = combined
	return nil
}

// Get decrypts and returns the bundle for (provider, user), or nil. A
// decryption failure (tampered blob) yields (nil, nil), matching the C#
// CryptographicException catch that returns null.
func (s *AesGcmCredentialStore) Get(ctx context.Context, providerId, userId string) (*CredentialBundle, error) {
	if strings.TrimSpace(providerId) == "" {
		return nil, errors.New("providerId required")
	}
	if strings.TrimSpace(userId) == "" {
		return nil, errors.New("userId required")
	}
	s.mu.Lock()
	combined, ok := s.enc[credKey(providerId, userId)]
	s.mu.Unlock()
	if !ok {
		return nil, nil
	}
	if len(combined) < 28 {
		return nil, nil
	}
	nonce := combined[:12]
	tag := combined[12:28]
	ctBuf := combined[28:]

	block, err := aes.NewCipher(s.key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCMWithTagSize(block, 16)
	if err != nil {
		return nil, err
	}
	// Recompose ciphertext||tag for Open.
	sealed := make([]byte, 0, len(ctBuf)+len(tag))
	sealed = append(sealed, ctBuf...)
	sealed = append(sealed, tag...)
	pt, err := gcm.Open(nil, nonce, sealed, nil)
	if err != nil {
		return nil, nil // tampered / wrong key — treat as absent, per C#.
	}
	var bundle CredentialBundle
	if err := json.Unmarshal(pt, &bundle); err != nil {
		return nil, nil
	}
	return &bundle, nil
}

// Delete removes the credential for (provider, user).
func (s *AesGcmCredentialStore) Delete(ctx context.Context, providerId, userId string) error {
	if strings.TrimSpace(providerId) == "" {
		return errors.New("providerId required")
	}
	if strings.TrimSpace(userId) == "" {
		return errors.New("userId required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.enc, credKey(providerId, userId))
	return nil
}

func credKey(p, u string) string { return p + "/" + u }

var _ ICredentialStore = (*AesGcmCredentialStore)(nil)

// ---------------------------------------------------------------------------
// OAuth2FlowDriver
// ---------------------------------------------------------------------------

// OAuth2TokenExchange performs the vendor-specific token exchange for
// OAuth2FlowDriver.Complete. It receives (providerId, userId, authorizationCode,
// redirectUri) and returns the resulting credential bundle. Ports the C#
// exchange delegate.
type OAuth2TokenExchange func(ctx context.Context, providerId, userId, authorizationCode, redirectUri string) (CredentialBundle, error)

// OAuth2FlowDriver builds authorize URLs and delegates the token exchange to a
// host function. Ports OAuth2FlowDriver.
type OAuth2FlowDriver struct {
	catalog     IProviderCatalog
	clientIdFor func(providerId string) string
	exchange    OAuth2TokenExchange
}

// NewOAuth2FlowDriver constructs the driver. All three dependencies are required.
func NewOAuth2FlowDriver(catalog IProviderCatalog, clientIdFor func(string) string, exchange OAuth2TokenExchange) *OAuth2FlowDriver {
	if catalog == nil {
		panic("catalog is required")
	}
	if clientIdFor == nil {
		panic("clientIdFor is required")
	}
	if exchange == nil {
		panic("exchange is required")
	}
	return &OAuth2FlowDriver{catalog: catalog, clientIdFor: clientIdFor, exchange: exchange}
}

// BackendId returns "oauth2".
func (d *OAuth2FlowDriver) BackendId() string { return "oauth2" }

// Start builds a standards-compliant authorize URL with a random state token.
// Errors when the provider is unknown or is not an OAuth2 provider.
func (d *OAuth2FlowDriver) Start(ctx context.Context, providerId, userId, redirectUri string) (string, error) {
	if strings.TrimSpace(providerId) == "" {
		return "", errors.New("providerId required")
	}
	if strings.TrimSpace(userId) == "" {
		return "", errors.New("userId required")
	}
	if strings.TrimSpace(redirectUri) == "" {
		return "", errors.New("redirectUri required")
	}
	provider, err := d.catalog.GetProvider(ctx, providerId)
	if err != nil {
		return "", err
	}
	if provider == nil {
		return "", errors.New("Unknown provider '" + providerId + "'.")
	}
	if provider.OAuth2 == nil {
		return "", errors.New("Provider '" + providerId + "' is not OAuth2.")
	}

	stateBytes := make([]byte, 16)
	if _, err := rand.Read(stateBytes); err != nil {
		return "", err
	}
	// URL-safe base64 without padding, matching the C# TrimEnd('=')+/->-_ dance.
	state := base64.StdEncoding.EncodeToString(stateBytes)
	state = strings.TrimRight(state, "=")
	state = strings.NewReplacer("+", "-", "/", "_").Replace(state)

	scopes := strings.Join(provider.OAuth2.Scopes, " ")
	clientID := d.clientIdFor(providerId)
	u := provider.OAuth2.AuthorizeUrl + "?response_type=code" +
		"&client_id=" + url.QueryEscape(clientID) +
		"&redirect_uri=" + url.QueryEscape(redirectUri) +
		"&scope=" + url.QueryEscape(scopes) +
		"&state=" + url.QueryEscape(state)
	return u, nil
}

// Complete delegates the code→token exchange to the host function.
func (d *OAuth2FlowDriver) Complete(ctx context.Context, providerId, userId, authorizationCode, redirectUri string) (CredentialBundle, error) {
	if strings.TrimSpace(providerId) == "" {
		return CredentialBundle{}, errors.New("providerId required")
	}
	if strings.TrimSpace(userId) == "" {
		return CredentialBundle{}, errors.New("userId required")
	}
	if strings.TrimSpace(authorizationCode) == "" {
		return CredentialBundle{}, errors.New("authorizationCode required")
	}
	if strings.TrimSpace(redirectUri) == "" {
		return CredentialBundle{}, errors.New("redirectUri required")
	}
	return d.exchange(ctx, providerId, userId, authorizationCode, redirectUri)
}

var _ IOAuth2FlowDriver = (*OAuth2FlowDriver)(nil)

// ---------------------------------------------------------------------------
// SlidingWindowQuotaGuard
// ---------------------------------------------------------------------------

// SlidingWindowQuotaGuard enforces a per-minute cap, daily budget, and
// max-concurrent limit over a sliding window. Ports SlidingWindowQuotaGuard.
// A (provider, user) pair with no policy is unlimited.
type SlidingWindowQuotaGuard struct {
	mu       sync.Mutex
	policies map[string]QuotaPolicy
	calls    map[string][]time.Time
	inflight map[string]int
}

// NewSlidingWindowQuotaGuard creates an empty guard.
func NewSlidingWindowQuotaGuard() *SlidingWindowQuotaGuard {
	return &SlidingWindowQuotaGuard{
		policies: make(map[string]QuotaPolicy),
		calls:    make(map[string][]time.Time),
		inflight: make(map[string]int),
	}
}

// BackendId returns "sliding-window".
func (g *SlidingWindowQuotaGuard) BackendId() string { return "sliding-window" }

// TryAcquire reserves a call slot subject to the per-minute cap, daily budget,
// and concurrency limit. Returns true when the call may proceed. When granted,
// the caller must later call Release to free the concurrency slot.
func (g *SlidingWindowQuotaGuard) TryAcquire(ctx context.Context, providerId, userId string) (bool, error) {
	key := credKey(providerId, userId)
	g.mu.Lock()
	defer g.mu.Unlock()

	policy, ok := g.policies[key]
	if !ok {
		return true, nil // no policy = unlimited
	}
	now := time.Now().UTC()

	// Prune calls older than one minute (per-minute window).
	list := g.calls[key]
	cutoff := now.Add(-time.Minute)
	kept := list[:0]
	for _, t := range list {
		if !t.Before(cutoff) {
			kept = append(kept, t)
		}
	}
	list = kept

	if len(list) >= policy.PerMinuteCap {
		g.calls[key] = list
		return false, nil
	}

	// Daily budget: count calls within the last 24h.
	dayCutoff := now.Add(-24 * time.Hour)
	dayCount := 0
	for _, t := range list {
		if !t.Before(dayCutoff) {
			dayCount++
		}
	}
	if dayCount >= policy.DailyCallBudget {
		g.calls[key] = list
		return false, nil
	}

	// Concurrency.
	if g.inflight[key] >= policy.MaxConcurrent {
		g.calls[key] = list
		return false, nil
	}

	list = append(list, now)
	g.calls[key] = list
	g.inflight[key]++
	return true, nil
}

// Release frees one concurrency slot previously reserved by TryAcquire.
func (g *SlidingWindowQuotaGuard) Release(providerId, userId string) {
	key := credKey(providerId, userId)
	g.mu.Lock()
	defer g.mu.Unlock()
	if n := g.inflight[key]; n > 0 {
		g.inflight[key] = n - 1
	}
}

// SetPolicy sets (or replaces) the policy for a (provider, user) pair.
func (g *SlidingWindowQuotaGuard) SetPolicy(ctx context.Context, policy QuotaPolicy) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.policies[credKey(policy.ProviderId, policy.UserId)] = policy
	return nil
}

// GetPolicy returns the policy for a (provider, user) pair, or nil.
func (g *SlidingWindowQuotaGuard) GetPolicy(ctx context.Context, providerId, userId string) (*QuotaPolicy, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if p, ok := g.policies[credKey(providerId, userId)]; ok {
		out := p
		return &out, nil
	}
	return nil, nil
}

var _ IQuotaGuard = (*SlidingWindowQuotaGuard)(nil)

// ---------------------------------------------------------------------------
// InMemoryToolNamespaceStore
// ---------------------------------------------------------------------------

// InMemoryToolNamespaceStore is an in-memory IToolNamespaceStore. Ports
// InMemoryToolNamespaceStore.
type InMemoryToolNamespaceStore struct {
	mu    sync.RWMutex
	items map[string]ToolNamespace
}

// NewInMemoryToolNamespaceStore creates an empty store.
func NewInMemoryToolNamespaceStore() *InMemoryToolNamespaceStore {
	return &InMemoryToolNamespaceStore{items: make(map[string]ToolNamespace)}
}

// BackendId returns "in-memory".
func (s *InMemoryToolNamespaceStore) BackendId() string { return "in-memory" }

// Upsert adds or replaces a namespace keyed by NamespaceId.
func (s *InMemoryToolNamespaceStore) Upsert(ctx context.Context, ns ToolNamespace) error {
	if strings.TrimSpace(ns.NamespaceId) == "" {
		return errors.New("NamespaceId required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.items[ns.NamespaceId] = ns
	return nil
}

// Get returns the namespace for namespaceId, or nil.
func (s *InMemoryToolNamespaceStore) Get(ctx context.Context, namespaceId string) (*ToolNamespace, error) {
	if strings.TrimSpace(namespaceId) == "" {
		return nil, errors.New("namespaceId required")
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	if ns, ok := s.items[namespaceId]; ok {
		out := ns
		return &out, nil
	}
	return nil, nil
}

// ListForUser returns every namespace owned by userId.
func (s *InMemoryToolNamespaceStore) ListForUser(ctx context.Context, userId string) ([]ToolNamespace, error) {
	if strings.TrimSpace(userId) == "" {
		return nil, errors.New("userId required")
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	var out []ToolNamespace
	for _, ns := range s.items {
		if ns.OwnerUserId == userId {
			out = append(out, ns)
		}
	}
	return out, nil
}

var _ IToolNamespaceStore = (*InMemoryToolNamespaceStore)(nil)

// ---------------------------------------------------------------------------
// Null implementations (NullImplementations.cs) — fail-closed defaults
// ---------------------------------------------------------------------------

// NullProviderCatalog is a fail-closed IProviderCatalog that lists nothing.
type NullProviderCatalog struct{}

// NullProviderCatalogInstance is the shared singleton.
var NullProviderCatalogInstance = NullProviderCatalog{}

func (NullProviderCatalog) BackendId() string { return "null" }
func (NullProviderCatalog) ListProviders(ctx context.Context) ([]ProviderDescriptor, error) {
	return []ProviderDescriptor{}, nil
}
func (NullProviderCatalog) GetProvider(ctx context.Context, providerId string) (*ProviderDescriptor, error) {
	return nil, nil
}
func (NullProviderCatalog) SearchProviders(ctx context.Context, query string, topK int) ([]ProviderDescriptor, error) {
	return []ProviderDescriptor{}, nil
}

var _ IProviderCatalog = NullProviderCatalog{}

// NullCredentialStore is a fail-closed ICredentialStore that stores nothing.
type NullCredentialStore struct{}

// NullCredentialStoreInstance is the shared singleton.
var NullCredentialStoreInstance = NullCredentialStore{}

func (NullCredentialStore) BackendId() string                                         { return "null" }
func (NullCredentialStore) Upsert(ctx context.Context, bundle CredentialBundle) error { return nil }
func (NullCredentialStore) Get(ctx context.Context, p, u string) (*CredentialBundle, error) {
	return nil, nil
}
func (NullCredentialStore) Delete(ctx context.Context, p, u string) error { return nil }

var _ ICredentialStore = NullCredentialStore{}

// NullOAuth2FlowDriver is a fail-closed IOAuth2FlowDriver.
type NullOAuth2FlowDriver struct{}

// NullOAuth2FlowDriverInstance is the shared singleton.
var NullOAuth2FlowDriverInstance = NullOAuth2FlowDriver{}

func (NullOAuth2FlowDriver) BackendId() string { return "null" }
func (NullOAuth2FlowDriver) Start(ctx context.Context, p, u, r string) (string, error) {
	return "about:blank", nil
}
func (NullOAuth2FlowDriver) Complete(ctx context.Context, p, u, code, redirect string) (CredentialBundle, error) {
	return CredentialBundle{}, errors.New("NullOAuth2FlowDriver: no real provider wired.")
}

var _ IOAuth2FlowDriver = NullOAuth2FlowDriver{}

// NullQuotaGuard is a fail-closed IQuotaGuard that always denies.
type NullQuotaGuard struct{}

// NullQuotaGuardInstance is the shared singleton.
var NullQuotaGuardInstance = NullQuotaGuard{}

func (NullQuotaGuard) BackendId() string { return "null" }
func (NullQuotaGuard) TryAcquire(ctx context.Context, p, u string) (bool, error) {
	return false, nil
}
func (NullQuotaGuard) SetPolicy(ctx context.Context, policy QuotaPolicy) error { return nil }
func (NullQuotaGuard) GetPolicy(ctx context.Context, p, u string) (*QuotaPolicy, error) {
	return nil, nil
}

var _ IQuotaGuard = NullQuotaGuard{}

// NullToolNamespaceStore is a fail-closed IToolNamespaceStore.
type NullToolNamespaceStore struct{}

// NullToolNamespaceStoreInstance is the shared singleton.
var NullToolNamespaceStoreInstance = NullToolNamespaceStore{}

func (NullToolNamespaceStore) BackendId() string                                  { return "null" }
func (NullToolNamespaceStore) Upsert(ctx context.Context, ns ToolNamespace) error { return nil }
func (NullToolNamespaceStore) Get(ctx context.Context, nsId string) (*ToolNamespace, error) {
	return nil, nil
}
func (NullToolNamespaceStore) ListForUser(ctx context.Context, userId string) ([]ToolNamespace, error) {
	return []ToolNamespace{}, nil
}

var _ IToolNamespaceStore = NullToolNamespaceStore{}
