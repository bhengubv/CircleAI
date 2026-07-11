// commerce_xero_board.go
//
// Ports the CircleAI.Commerce.Integration.Xero primitive vertical
// (XeroPrimitives.cs):
//   XeroTokens / XeroTenant / XeroWebhookEvent (records) -> value structs
//   IXeroBoard        -> XeroBoard interface (I-prefix dropped)
//   InMemoryXeroBoard -> InMemoryXeroBoard
//
// The CommerceIntegrationXeroDomainContext (static prompt strings) and
// CommerceIntegrationXeroCompanionAdapter (LLM-prompt wrapper) are out of scope
// for the deterministic in-memory board.
//
// The HTTP/OAuth plumbing is host-supplied in the C#; this board only stores
// tokens, tracks tenants (de-duplicated per user by TenantId), and records
// webhook events.

package circleai

import (
	"sort"
	"sync"
	"time"
)

// XeroTokens is a stored OAuth token set. Ports the XeroTokens record.
type XeroTokens struct {
	AccessToken  string
	RefreshToken string
	ExpiresAtUtc time.Time
	IdToken      string
}

// XeroTenant is a connected Xero organisation. Ports the XeroTenant record.
type XeroTenant struct {
	TenantId   string
	TenantName string
	TenantType string
}

// XeroWebhookEvent is a received Xero webhook. Ports the XeroWebhookEvent record.
type XeroWebhookEvent struct {
	TenantId     string
	ResourceType string
	ResourceId   string
	AtUtc        time.Time
}

// DefaultXeroRecentLimit is the C# default `limit = 20` for RecentEvents.
const DefaultXeroRecentLimit = 20

// XeroBoard is the Xero token/tenant/webhook board. Ports IXeroBoard.
type XeroBoard interface {
	StoreTokens(userId string, t XeroTokens)
	GetTokens(userId string) (XeroTokens, bool)
	// TokensExpired reports whether a user's tokens are missing or expired as of now.
	TokensExpired(userId string, now time.Time) bool
	// AddTenant records a tenant for a user, de-duplicated by TenantId.
	AddTenant(userId string, t XeroTenant)
	// TenantsFor lists a user's tenants in insertion order.
	TenantsFor(userId string) []XeroTenant
	RecordWebhook(e XeroWebhookEvent)
	// RecentEvents lists the most recent webhook events, newest first, capped at limit.
	RecentEvents(limit int) []XeroWebhookEvent
}

// InMemoryXeroBoard is a concurrency-safe in-memory XeroBoard. Ports
// InMemoryXeroBoard (tokens + per-user tenant lists in maps, events in an ordered
// list; a single mutex guards the tenant lists and the event list).
type InMemoryXeroBoard struct {
	mu      sync.RWMutex
	tokens  map[string]XeroTokens
	tenants map[string][]XeroTenant
	events  []XeroWebhookEvent
}

// NewInMemoryXeroBoard constructs an empty board.
func NewInMemoryXeroBoard() *InMemoryXeroBoard {
	return &InMemoryXeroBoard{
		tokens:  make(map[string]XeroTokens),
		tenants: make(map[string][]XeroTenant),
		events:  make([]XeroWebhookEvent, 0),
	}
}

// StoreTokens stores (or replaces by userId) a token set. Ports StoreTokens.
func (b *InMemoryXeroBoard) StoreTokens(userId string, t XeroTokens) {
	b.mu.Lock()
	b.tokens[userId] = t
	b.mu.Unlock()
}

// GetTokens returns the token set for userId and true, or (zero, false) if absent.
func (b *InMemoryXeroBoard) GetTokens(userId string) (XeroTokens, bool) {
	b.mu.RLock()
	t, ok := b.tokens[userId]
	b.mu.RUnlock()
	return t, ok
}

// TokensExpired reports true when the user has no tokens or now is at/after the
// token expiry. Ports TokensExpired (missing tokens => true).
func (b *InMemoryXeroBoard) TokensExpired(userId string, now time.Time) bool {
	b.mu.RLock()
	t, ok := b.tokens[userId]
	b.mu.RUnlock()
	if !ok {
		return true
	}
	return !now.Before(t.ExpiresAtUtc) // now >= ExpiresAtUtc
}

// AddTenant appends a tenant for a user unless one with the same TenantId is
// already present. Ports AddTenant (de-dup by TenantId, insertion order kept).
func (b *InMemoryXeroBoard) AddTenant(userId string, t XeroTenant) {
	b.mu.Lock()
	defer b.mu.Unlock()
	list := b.tenants[userId]
	for _, existing := range list {
		if existing.TenantId == t.TenantId {
			return
		}
	}
	b.tenants[userId] = append(list, t)
}

// TenantsFor lists a user's tenants in insertion order. Ports TenantsFor.
func (b *InMemoryXeroBoard) TenantsFor(userId string) []XeroTenant {
	b.mu.RLock()
	defer b.mu.RUnlock()
	list, ok := b.tenants[userId]
	if !ok {
		return []XeroTenant{}
	}
	out := make([]XeroTenant, len(list))
	copy(out, list)
	return out
}

// RecordWebhook appends a webhook event. Ports RecordWebhook.
func (b *InMemoryXeroBoard) RecordWebhook(e XeroWebhookEvent) {
	b.mu.Lock()
	b.events = append(b.events, e)
	b.mu.Unlock()
}

// RecentEvents lists up to limit events ordered by AtUtc descending (newest
// first). Ports RecentEvents. Equal timestamps break by reverse insertion order
// via a stable sort so the most recently recorded appears first deterministically.
func (b *InMemoryXeroBoard) RecentEvents(limit int) []XeroWebhookEvent {
	b.mu.RLock()
	cp := make([]XeroWebhookEvent, len(b.events))
	copy(cp, b.events)
	b.mu.RUnlock()

	sort.SliceStable(cp, func(i, j int) bool { return cp[i].AtUtc.After(cp[j].AtUtc) })
	// LINQ Take(n) yields empty for n <= 0; clamp negatives to 0 to match.
	if limit < 0 {
		limit = 0
	}
	if len(cp) > limit {
		cp = cp[:limit]
	}
	return cp
}

// Interface guard.
var _ XeroBoard = (*InMemoryXeroBoard)(nil)
