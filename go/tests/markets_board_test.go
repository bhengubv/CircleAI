// markets_board_test.go
//
// Verifies the CircleAI.Markets port (markets_board.go): instrument catalog
// add/get/search (case-insensitive), market-data feed publish/get + subscribe
// broadcast + unsubscribe, order-router validation (quantity, limit price,
// unknown symbol) with sequential ids, enum ordinals/names, and the Null
// fail-closed backends.

package circleai_test

import (
	"context"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestMarkets_EnumOrdinals(t *testing.T) {
	if circleai.OrderSideBuy != 0 || circleai.OrderSideSell != 1 {
		t.Fatalf("OrderSide ordinals wrong")
	}
	if circleai.OrderTypeMarket != 0 || circleai.OrderTypeLimit != 1 {
		t.Fatalf("OrderType ordinals wrong")
	}
	if circleai.OrderSideBuy.String() != "Buy" || circleai.OrderTypeLimit.String() != "Limit" {
		t.Fatalf("enum names wrong")
	}
}

func TestMarkets_InstrumentCatalog(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryInstrumentCatalog()
	if c.BackendId() != "in-memory" {
		t.Fatalf("backend id wrong")
	}
	c.Add(circleai.Instrument{Symbol: "AAPL", Exchange: "NASDAQ", Currency: "USD", AssetClass: "Equity"})
	c.Add(circleai.Instrument{Symbol: "ABSA", Exchange: "JSE", Currency: "ZAR", AssetClass: "Equity"})

	// case-insensitive Get.
	got, ok, err := c.Get(ctx, "aapl")
	if err != nil || !ok || got.Exchange != "NASDAQ" {
		t.Fatalf("get aapl = %+v ok=%v err=%v", got, ok, err)
	}
	if _, _, err := c.Get(ctx, " "); err == nil {
		t.Fatalf("blank symbol get must error")
	}

	hits, err := c.Search(ctx, "ab", 20) // matches ABSA only
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(hits) != 1 || hits[0].Symbol != "ABSA" {
		t.Fatalf("search 'ab' = %+v", hits)
	}
	if _, err := c.Search(ctx, "x", 0); err == nil {
		t.Fatalf("topK<=0 must error")
	}
}

func TestMarkets_FeedPublishAndSubscribe(t *testing.T) {
	ctx := context.Background()
	f := circleai.NewInMemoryMarketDataFeed()
	now := time.Date(2026, 7, 1, 9, 0, 0, 0, time.UTC)

	var mu sync.Mutex
	var received []circleai.Quote
	unsub := f.SubscribeQuotes("AAPL", func(q circleai.Quote) {
		mu.Lock()
		received = append(received, q)
		mu.Unlock()
	})

	q1 := circleai.Quote{Symbol: "AAPL", Bid: circleai.DecimalFromInt(100), Ask: circleai.DecimalFromInt(101), Last: circleai.DecimalFromInt(100), AtUtc: now}
	f.Publish(q1)

	// GetQuote (case-insensitive) returns the latest.
	got, ok, err := f.GetQuote(ctx, "aapl")
	if err != nil || !ok || !got.Bid.Equal(circleai.DecimalFromInt(100)) {
		t.Fatalf("get quote = %+v ok=%v err=%v", got, ok, err)
	}

	mu.Lock()
	n := len(received)
	mu.Unlock()
	if n != 1 {
		t.Fatalf("subscriber got %d quotes, want 1", n)
	}

	// After unsubscribe, no further pushes.
	unsub()
	unsub() // idempotent
	f.Publish(circleai.Quote{Symbol: "AAPL", Bid: circleai.DecimalFromInt(200), AtUtc: now.Add(time.Minute)})
	mu.Lock()
	n = len(received)
	mu.Unlock()
	if n != 1 {
		t.Fatalf("after unsubscribe got %d quotes, want 1", n)
	}

	if _, ok, _ := f.GetQuote(ctx, "MSFT"); ok {
		t.Fatalf("unknown symbol must report absent")
	}
}

func TestMarkets_OrderRouter(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewInMemoryInstrumentCatalog()
	c.Add(circleai.Instrument{Symbol: "AAPL", Exchange: "NASDAQ", Currency: "USD", AssetClass: "Equity"})
	r := circleai.NewInMemoryOrderRouter(c)

	// Non-positive quantity rejected.
	res, err := r.Submit(ctx, circleai.OrderRequest{Symbol: "AAPL", Side: circleai.OrderSideBuy, Type: circleai.OrderTypeMarket, Quantity: circleai.ZeroDecimal})
	if err != nil || res.Accepted || res.FailureReason == nil || *res.FailureReason != "Quantity must be positive" {
		t.Fatalf("zero-qty reject wrong: %+v err=%v", res, err)
	}

	// Limit order without positive limit price rejected.
	res, _ = r.Submit(ctx, circleai.OrderRequest{Symbol: "AAPL", Side: circleai.OrderSideBuy, Type: circleai.OrderTypeLimit, Quantity: circleai.DecimalFromInt(10)})
	if res.Accepted || res.FailureReason == nil || *res.FailureReason != "Limit order requires positive LimitPrice" {
		t.Fatalf("limit-no-price reject wrong: %+v", res)
	}

	// Unknown symbol rejected.
	res, _ = r.Submit(ctx, circleai.OrderRequest{Symbol: "MSFT", Side: circleai.OrderSideBuy, Type: circleai.OrderTypeMarket, Quantity: circleai.DecimalFromInt(10)})
	if res.Accepted || res.FailureReason == nil || *res.FailureReason != "Unknown symbol" {
		t.Fatalf("unknown-symbol reject wrong: %+v", res)
	}

	// Valid market order accepted with sequential id.
	lp := circleai.DecimalFromInt(105)
	res, _ = r.Submit(ctx, circleai.OrderRequest{Symbol: "AAPL", Side: circleai.OrderSideSell, Type: circleai.OrderTypeLimit, Quantity: circleai.DecimalFromInt(5), LimitPrice: &lp})
	if !res.Accepted || res.FailureReason != nil {
		t.Fatalf("valid order rejected: %+v", res)
	}
	if res.OrderId == "" {
		t.Fatalf("accepted order missing id")
	}
	// Next accepted order gets a distinct id.
	res2, _ := r.Submit(ctx, circleai.OrderRequest{Symbol: "AAPL", Side: circleai.OrderSideBuy, Type: circleai.OrderTypeMarket, Quantity: circleai.DecimalFromInt(1)})
	if res2.OrderId == res.OrderId {
		t.Fatalf("order ids should be sequential/distinct: %q == %q", res.OrderId, res2.OrderId)
	}
}

func TestMarkets_NullBackends(t *testing.T) {
	ctx := context.Background()
	if _, ok, _ := circleai.NullMarketDataFeedInstance.GetQuote(ctx, "AAPL"); ok {
		t.Fatalf("null feed must report absent")
	}
	// No-op unsubscribe does not panic.
	circleai.NullMarketDataFeedInstance.SubscribeQuotes("AAPL", func(circleai.Quote) {})()

	if _, ok, _ := circleai.NullInstrumentCatalogInstance.Get(ctx, "AAPL"); ok {
		t.Fatalf("null catalog must report absent")
	}
	if hits, _ := circleai.NullInstrumentCatalogInstance.Search(ctx, "x", 5); len(hits) != 0 {
		t.Fatalf("null catalog search must be empty")
	}

	res, _ := circleai.NullOrderRouterInstance.Submit(ctx, circleai.OrderRequest{Symbol: "AAPL", Quantity: circleai.DecimalFromInt(1)})
	if res.Accepted || res.FailureReason == nil {
		t.Fatalf("null router must decline")
	}
	if res.OrderId != "00000000-0000-0000-0000-000000000000" {
		t.Fatalf("null router order id wrong: %q", res.OrderId)
	}
}
