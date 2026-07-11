// markets_board.go
//
// Ports the CircleAI.Markets vertical (Contracts.cs / InMemoryMarkets.cs /
// NullImplementations.cs):
//   OrderSide / OrderType (enums)                    -> int consts (stable ordinals)
//   Instrument / Quote / OrderRequest / OrderResult  -> value structs
//   IMarketDataFeed / IInstrumentCatalog / IOrderRouter
//                                                    -> interfaces (I-prefix dropped)
//   InMemoryInstrumentCatalog / InMemoryMarketDataFeed /
//       InMemoryOrderRouter                          -> in-memory impls
//   NullMarketDataFeed / NullInstrumentCatalog /
//       NullOrderRouter                              -> fail-closed defaults
//
// ASYNC: ValueTask<...>(ct) -> synchronous Go methods taking context.Context and
// returning an error (matching banking_board.go / crm_board.go). Nullable single
// returns (Quote?/Instrument?) -> (T, bool). Prices use the shared exact Decimal.
//
// SUBSCRIBE / IDisposable: SubscribeQuotes returns an unsubscribe func (the Go
// idiom for the C# IDisposable subscription token, matching aether_events.go /
// media_hub.go). Publish snapshots the subscriber list UNDER the lock and invokes
// handlers OUTSIDE it, so a handler that (un)subscribes cannot deadlock the
// publisher (the concurrency rule for stream termination handlers). Handler
// panics are swallowed (the C# try/catch around each subscriber invocation).
//
// ORDER ROUTER: SubmitAsync validates positive quantity, positive limit price for
// limit orders, and known symbol, minting sequential "ord-N" ids via an atomic
// counter (Interlocked.Increment). Nullable LimitPrice -> *Decimal.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// OrderSide is Buy or Sell. Ordinals match the C# enum (Buy=0, Sell=1). Ports
// OrderSide.
type OrderSide int

const (
	// OrderSideBuy is a buy order.
	OrderSideBuy OrderSide = iota
	// OrderSideSell is a sell order.
	OrderSideSell
)

// String renders the C# enum member name.
func (s OrderSide) String() string {
	switch s {
	case OrderSideBuy:
		return "Buy"
	case OrderSideSell:
		return "Sell"
	default:
		return "Unknown"
	}
}

// OrderType is Market or Limit. Ordinals match the C# enum (Market=0, Limit=1).
// Ports OrderType.
type OrderType int

const (
	// OrderTypeMarket is a market order.
	OrderTypeMarket OrderType = iota
	// OrderTypeLimit is a limit order.
	OrderTypeLimit
)

// String renders the C# enum member name.
func (t OrderType) String() string {
	switch t {
	case OrderTypeMarket:
		return "Market"
	case OrderTypeLimit:
		return "Limit"
	default:
		return "Unknown"
	}
}

// Instrument is a tradeable instrument. Ports the Instrument record.
type Instrument struct {
	Symbol     string
	Exchange   string
	Currency   string
	AssetClass string
}

// Quote is a market quote. Ports the Quote record. Prices use exact Decimal.
type Quote struct {
	Symbol string
	Bid    Decimal
	Ask    Decimal
	Last   Decimal
	AtUtc  time.Time
}

// OrderRequest is an order submission. Ports the OrderRequest record. LimitPrice
// is a pointer to mirror the nullable C# decimal? (nil == not supplied).
type OrderRequest struct {
	Symbol     string
	Side       OrderSide
	Type       OrderType
	Quantity   Decimal
	LimitPrice *Decimal
}

// OrderResult is the outcome of an order submission. Ports the OrderResult record.
// FailureReason is a pointer to mirror the nullable C# string? (nil on success).
type OrderResult struct {
	OrderId       string
	Accepted      bool
	FailureReason *string
}

// DefaultInstrumentSearchTopK is the C# default `topK = 20` for instrument search.
const DefaultInstrumentSearchTopK = 20

// MarketDataFeed provides quotes and quote subscriptions. Ports IMarketDataFeed.
type MarketDataFeed interface {
	// BackendId identifies the backing store (e.g. "in-memory", "null").
	BackendId() string
	// GetQuote returns the latest quote for symbol and true, or (zero, false) if
	// none is known.
	GetQuote(ctx context.Context, symbol string) (Quote, bool, error)
	// SubscribeQuotes registers handler for pushes on symbol and returns an
	// unsubscribe func (ports the IDisposable token).
	SubscribeQuotes(symbol string, handler func(Quote)) func()
}

// InstrumentCatalog looks up and searches instruments. Ports IInstrumentCatalog.
type InstrumentCatalog interface {
	BackendId() string
	// Get returns the instrument for symbol and true, or (zero, false) if absent.
	Get(ctx context.Context, symbol string) (Instrument, bool, error)
	// Search returns up to topK instruments whose Symbol contains query
	// (case-insensitive), ordered by Symbol.
	Search(ctx context.Context, query string, topK int) ([]Instrument, error)
}

// OrderRouter submits orders. Ports IOrderRouter.
type OrderRouter interface {
	BackendId() string
	// Submit validates and (in-memory) accepts an order, returning its result.
	Submit(ctx context.Context, req OrderRequest) (OrderResult, error)
}

// --- In-memory implementations ---

// InMemoryInstrumentCatalog is a concurrency-safe in-memory InstrumentCatalog.
// Ports InMemoryInstrumentCatalog (symbol keys are case-insensitive, matching the
// C# ConcurrentDictionary(OrdinalIgnoreCase)). BackendId is "in-memory".
type InMemoryInstrumentCatalog struct {
	mu    sync.RWMutex
	items map[string]Instrument // key: lower-cased symbol (OrdinalIgnoreCase)
}

// NewInMemoryInstrumentCatalog constructs an empty catalog.
func NewInMemoryInstrumentCatalog() *InMemoryInstrumentCatalog {
	return &InMemoryInstrumentCatalog{items: make(map[string]Instrument)}
}

// BackendId ports the BackendId property.
func (c *InMemoryInstrumentCatalog) BackendId() string { return "in-memory" }

// Add stores (or replaces case-insensitively by Symbol) an instrument. Ports the
// Add method. The stored value keeps the original Symbol casing.
func (c *InMemoryInstrumentCatalog) Add(item Instrument) {
	c.mu.Lock()
	if c.items == nil {
		c.items = make(map[string]Instrument)
	}
	c.items[strings.ToLower(item.Symbol)] = item
	c.mu.Unlock()
}

// Get returns the instrument for symbol (case-insensitive) and true, or
// (zero, false) if absent. Ports GetAsync (ArgumentException on blank symbol).
func (c *InMemoryInstrumentCatalog) Get(_ context.Context, symbol string) (Instrument, bool, error) {
	if strings.TrimSpace(symbol) == "" {
		return Instrument{}, false, errors.New("symbol required")
	}
	c.mu.RLock()
	i, ok := c.items[strings.ToLower(symbol)]
	c.mu.RUnlock()
	return i, ok, nil
}

// Search returns up to topK instruments whose Symbol contains query
// (case-insensitive) ordered by Symbol. Ports SearchAsync (ArgumentOutOfRange on
// topK <= 0). C# OrderBy(Symbol) with no comparer is culture-sensitive; symbols
// are ASCII tickers so cultureLess reproduces it.
func (c *InMemoryInstrumentCatalog) Search(_ context.Context, query string, topK int) ([]Instrument, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	q := strings.ToLower(query)
	c.mu.RLock()
	out := make([]Instrument, 0)
	for _, i := range c.items {
		if strings.Contains(strings.ToLower(i.Symbol), q) {
			out = append(out, i)
		}
	}
	c.mu.RUnlock()
	sort.SliceStable(out, func(a, b int) bool { return cultureLess(out[a].Symbol, out[b].Symbol) })
	if len(out) > topK {
		out = out[:topK]
	}
	return out, nil
}

// InMemoryMarketDataFeed is a concurrency-safe in-memory MarketDataFeed with
// subscribe/broadcast quote pushes. Ports InMemoryMarketDataFeed. BackendId is
// "in-memory".
type InMemoryMarketDataFeed struct {
	mu     sync.RWMutex
	quotes map[string]Quote                // key: lower-cased symbol
	subs   map[string][]*quoteSubscription // key: lower-cased symbol
}

// quoteSubscription wraps one handler so identical handler values can be
// unsubscribed by pointer identity.
type quoteSubscription struct {
	handler func(Quote)
}

// NewInMemoryMarketDataFeed constructs an empty feed.
func NewInMemoryMarketDataFeed() *InMemoryMarketDataFeed {
	return &InMemoryMarketDataFeed{
		quotes: make(map[string]Quote),
		subs:   make(map[string][]*quoteSubscription),
	}
}

// BackendId ports the BackendId property.
func (f *InMemoryMarketDataFeed) BackendId() string { return "in-memory" }

// Publish stores q as the latest quote for its symbol and fans it out to all
// current subscribers. Ports Publish. The subscriber list is snapshotted under
// the lock and handlers are invoked outside it; a handler panic is swallowed
// (the C# try/catch per subscriber).
func (f *InMemoryMarketDataFeed) Publish(q Quote) {
	key := strings.ToLower(q.Symbol)
	f.mu.Lock()
	if f.quotes == nil {
		f.quotes = make(map[string]Quote)
	}
	f.quotes[key] = q
	snap := make([]*quoteSubscription, len(f.subs[key]))
	copy(snap, f.subs[key])
	f.mu.Unlock()

	for _, s := range snap {
		func() {
			defer func() { _ = recover() }() // swallow subscriber panic (C# try/catch)
			s.handler(q)
		}()
	}
}

// GetQuote returns the latest quote for symbol (case-insensitive) and true, or
// (zero, false) if none. Ports GetQuoteAsync (ArgumentException on blank symbol).
func (f *InMemoryMarketDataFeed) GetQuote(_ context.Context, symbol string) (Quote, bool, error) {
	if strings.TrimSpace(symbol) == "" {
		return Quote{}, false, errors.New("symbol required")
	}
	f.mu.RLock()
	q, ok := f.quotes[strings.ToLower(symbol)]
	f.mu.RUnlock()
	return q, ok, nil
}

// SubscribeQuotes registers handler for pushes on symbol and returns an
// idempotent unsubscribe func. Ports SubscribeQuotes + its Subscription
// IDisposable. Panics on a blank symbol or nil handler (mirrors the C#
// ArgumentException / ArgumentNullException).
func (f *InMemoryMarketDataFeed) SubscribeQuotes(symbol string, handler func(Quote)) func() {
	if strings.TrimSpace(symbol) == "" {
		panic("symbol required")
	}
	if handler == nil {
		panic("handler must not be nil")
	}
	key := strings.ToLower(symbol)
	sub := &quoteSubscription{handler: handler}
	f.mu.Lock()
	if f.subs == nil {
		f.subs = make(map[string][]*quoteSubscription)
	}
	f.subs[key] = append(f.subs[key], sub)
	f.mu.Unlock()

	var once sync.Once
	return func() { once.Do(func() { f.unsubscribe(key, sub) }) }
}

func (f *InMemoryMarketDataFeed) unsubscribe(key string, sub *quoteSubscription) {
	f.mu.Lock()
	defer f.mu.Unlock()
	list := f.subs[key]
	for i, s := range list {
		if s == sub {
			f.subs[key] = append(list[:i], list[i+1:]...)
			return
		}
	}
}

// InMemoryOrderRouter validates + accepts orders against an InstrumentCatalog.
// Ports InMemoryOrderRouter. BackendId is "in-memory".
type InMemoryOrderRouter struct {
	catalog InstrumentCatalog
	seq     int64
}

// NewInMemoryOrderRouter constructs a router backed by catalog. Panics if catalog
// is nil (mirrors the C# ArgumentNullException in the constructor).
func NewInMemoryOrderRouter(catalog InstrumentCatalog) *InMemoryOrderRouter {
	if catalog == nil {
		panic("catalog must not be nil")
	}
	return &InMemoryOrderRouter{catalog: catalog}
}

// BackendId ports the BackendId property.
func (r *InMemoryOrderRouter) BackendId() string { return "in-memory" }

// Submit validates req and (in-memory) accepts it. Rejects non-positive
// quantity, a limit order without a positive LimitPrice, and an unknown symbol,
// each with the C# failure reason. Order ids are sequential "ord-N". Ports
// SubmitAsync.
func (r *InMemoryOrderRouter) Submit(ctx context.Context, req OrderRequest) (OrderResult, error) {
	if req.Quantity.Sign() <= 0 {
		return r.reject("Quantity must be positive"), nil
	}
	if req.Type == OrderTypeLimit && (req.LimitPrice == nil || req.LimitPrice.Sign() <= 0) {
		return r.reject("Limit order requires positive LimitPrice"), nil
	}
	_, ok, err := r.catalog.Get(ctx, req.Symbol)
	if err != nil {
		return OrderResult{}, err
	}
	if !ok {
		return r.reject("Unknown symbol"), nil
	}
	return OrderResult{OrderId: r.nextId(), Accepted: true, FailureReason: nil}, nil
}

func (r *InMemoryOrderRouter) reject(reason string) OrderResult {
	return OrderResult{OrderId: r.nextId(), Accepted: false, FailureReason: &reason}
}

func (r *InMemoryOrderRouter) nextId() string {
	return "ord-" + strconv.FormatInt(atomic.AddInt64(&r.seq, 1), 10)
}

// --- Null (fail-closed) backends ---

// NullMarketDataFeed serves no quotes and no pushes. Ports NullMarketDataFeed.
type NullMarketDataFeed struct{}

// NullMarketDataFeedInstance is the shared fail-closed feed (ports the static Instance).
var NullMarketDataFeedInstance = NullMarketDataFeed{}

// BackendId ports the BackendId property ("null").
func (NullMarketDataFeed) BackendId() string { return "null" }

// GetQuote always reports absent. Ports NullMarketDataFeed.GetQuoteAsync.
func (NullMarketDataFeed) GetQuote(context.Context, string) (Quote, bool, error) {
	return Quote{}, false, nil
}

// SubscribeQuotes returns a no-op unsubscribe. Ports NullMarketDataFeed's
// EmptyDisposable.
func (NullMarketDataFeed) SubscribeQuotes(string, func(Quote)) func() { return func() {} }

// NullInstrumentCatalog serves no instruments. Ports NullInstrumentCatalog.
type NullInstrumentCatalog struct{}

// NullInstrumentCatalogInstance is the shared fail-closed catalog.
var NullInstrumentCatalogInstance = NullInstrumentCatalog{}

// BackendId ports the BackendId property ("null").
func (NullInstrumentCatalog) BackendId() string { return "null" }

// Get always reports absent. Ports NullInstrumentCatalog.GetAsync.
func (NullInstrumentCatalog) Get(context.Context, string) (Instrument, bool, error) {
	return Instrument{}, false, nil
}

// Search always returns empty. Ports NullInstrumentCatalog.SearchAsync.
func (NullInstrumentCatalog) Search(context.Context, string, int) ([]Instrument, error) {
	return []Instrument{}, nil
}

// NullOrderRouter always declines. Ports NullOrderRouter. The declined result
// carries the empty-Guid id and the fail-closed reason.
type NullOrderRouter struct{}

// NullOrderRouterInstance is the shared fail-closed router.
var NullOrderRouterInstance = NullOrderRouter{}

// BackendId ports the BackendId property ("null").
func (NullOrderRouter) BackendId() string { return "null" }

// Submit always declines with the fail-closed reason. Ports
// NullOrderRouter.SubmitAsync.
func (NullOrderRouter) Submit(context.Context, OrderRequest) (OrderResult, error) {
	reason := "NullOrderRouter — fail-closed."
	return OrderResult{
		OrderId:       "00000000-0000-0000-0000-000000000000",
		Accepted:      false,
		FailureReason: &reason,
	}, nil
}

// Interface guards.
var (
	_ MarketDataFeed    = (*InMemoryMarketDataFeed)(nil)
	_ InstrumentCatalog = (*InMemoryInstrumentCatalog)(nil)
	_ OrderRouter       = (*InMemoryOrderRouter)(nil)
	_ MarketDataFeed    = NullMarketDataFeed{}
	_ InstrumentCatalog = NullInstrumentCatalog{}
	_ OrderRouter       = NullOrderRouter{}
)
