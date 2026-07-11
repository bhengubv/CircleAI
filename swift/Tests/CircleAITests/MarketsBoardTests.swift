// MarketsBoardTests.swift
//
// Exercises the Markets records/enums' Codable round-trips and the deterministic
// behaviour of the in-memory + null backends — the instrument catalog
// (case-insensitive symbol lookup + substring search, topK), the market-data
// feed (quote get + subscribe/broadcast pushes, unsubscribe), and the order
// router's accept/reject rules (positive quantity, valid limit price, known
// symbol, monotonic `ord-{n}` ids). Mirrors CircleAI.Markets/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class MarketsBoardTests: XCTestCase {

    private func inst(_ sym: String, _ ex: String = "JSE") -> Instrument {
        Instrument(symbol: sym, exchange: ex, currency: "ZAR", assetClass: "Equity")
    }
    private func quote(_ sym: String, _ last: Decimal) -> Quote {
        Quote(symbol: sym, bid: last - 1, ask: last + 1, last: last, atUtc: Date(timeIntervalSince1970: 100))
    }

    // ── DTO / enum Codable round-trips ───────────────────────────────────────

    func testEnumCodableRoundTrip() throws {
        XCTAssertEqual(try JSONDecoder().decode(OrderSide.self, from: try JSONEncoder().encode(OrderSide.sell)), .sell)
        XCTAssertEqual(try JSONDecoder().decode(OrderType.self, from: try JSONEncoder().encode(OrderType.limit)), .limit)
    }

    func testOrderRequestCodableRoundTrip() throws {
        let r = OrderRequest(symbol: "NPN", side: .buy, type: .limit, quantity: 10, limitPrice: 99.5)
        XCTAssertEqual(try JSONDecoder().decode(OrderRequest.self, from: try JSONEncoder().encode(r)), r)
    }

    func testQuoteAndInstrumentCodableRoundTrip() throws {
        let q = quote("NPN", 100)
        XCTAssertEqual(try JSONDecoder().decode(Quote.self, from: try JSONEncoder().encode(q)), q)
        let i = inst("NPN")
        XCTAssertEqual(try JSONDecoder().decode(Instrument.self, from: try JSONEncoder().encode(i)), i)
    }

    // ── Instrument catalog ───────────────────────────────────────────────────

    func testCatalogGetIsCaseInsensitive() async throws {
        let cat = InMemoryInstrumentCatalog()
        XCTAssertEqual(cat.backendId, "in-memory")
        cat.add(inst("NPN"))
        XCTAssertEqual(try await cat.get("npn")?.symbol, "NPN")
        XCTAssertNil(try await cat.get("ZZZ"))
    }

    func testCatalogSearchSubstringOrderedAndTopK() async throws {
        let cat = InMemoryInstrumentCatalog()
        cat.add(inst("AGL"))
        cat.add(inst("ABG"))
        cat.add(inst("NPN"))
        let a = try await cat.search("A")
        XCTAssertEqual(a.map { $0.symbol }, ["ABG", "AGL"])
        let topOne = try await cat.search("A", topK: 1)
        XCTAssertEqual(topOne.map { $0.symbol }, ["ABG"])
    }

    func testCatalogSearchNonPositiveTopKThrowsAndBlankSymbolGetThrows() async {
        let cat = InMemoryInstrumentCatalog()
        do { _ = try await cat.search("x", topK: 0); XCTFail() } catch { XCTAssertEqual(error as? MarketsError, .topKOutOfRange) }
        do { _ = try await cat.get(" "); XCTFail() } catch { XCTAssertEqual(error as? MarketsError, .symbolRequired) }
    }

    // ── Market-data feed ─────────────────────────────────────────────────────

    func testFeedGetQuoteAfterPublish() async throws {
        let feed = InMemoryMarketDataFeed()
        XCTAssertEqual(feed.backendId, "in-memory")
        feed.publish(quote("NPN", 250))
        XCTAssertEqual(try await feed.getQuote("npn")?.last, 250)   // case-insensitive
        XCTAssertNil(try await feed.getQuote("ZZZ"))
    }

    func testFeedSubscribeReceivesPush() async throws {
        let feed = InMemoryMarketDataFeed()
        let box = Box()
        let sub = try feed.subscribeQuotes("NPN") { q in await box.set(q) }
        feed.publish(quote("NPN", 300))
        // Await delivery (handler runs on a detached task).
        let delivered = await box.waitForValue()
        XCTAssertEqual(delivered?.last, 300)
        sub.cancel()
    }

    func testFeedUnsubscribeStopsDelivery() async throws {
        let feed = InMemoryMarketDataFeed()
        let counter = Counter()
        let sub = try feed.subscribeQuotes("NPN") { _ in await counter.increment() }
        feed.publish(quote("NPN", 1))
        _ = await counter.waitForAtLeast(1)
        sub.cancel()
        feed.publish(quote("NPN", 2))
        // Give any erroneously-live handler a chance to run, then assert it stayed at 1.
        try? await Task.sleep(nanoseconds: 50_000_000)
        let final = await counter.value
        XCTAssertEqual(final, 1)
    }

    func testFeedBlankSymbolThrows() async {
        let feed = InMemoryMarketDataFeed()
        do { _ = try await feed.getQuote(""); XCTFail() } catch { XCTAssertEqual(error as? MarketsError, .symbolRequired) }
        do { _ = try feed.subscribeQuotes(" ") { _ in }; XCTFail() } catch { XCTAssertEqual(error as? MarketsError, .symbolRequired) }
    }

    // ── Order router ─────────────────────────────────────────────────────────

    func testOrderRouterAcceptsKnownMarketOrderWithMonotonicIds() async throws {
        let cat = InMemoryInstrumentCatalog()
        cat.add(inst("NPN"))
        let router = InMemoryOrderRouter(cat)
        XCTAssertEqual(router.backendId, "in-memory")
        let r1 = await router.submit(OrderRequest(symbol: "NPN", side: .buy, type: .market, quantity: 5, limitPrice: nil))
        XCTAssertTrue(r1.accepted)
        XCTAssertNil(r1.failureReason)
        XCTAssertEqual(r1.orderId, "ord-1")
        let r2 = await router.submit(OrderRequest(symbol: "NPN", side: .sell, type: .market, quantity: 1, limitPrice: nil))
        XCTAssertEqual(r2.orderId, "ord-2")
    }

    func testOrderRouterRejectsNonPositiveQuantity() async {
        let router = InMemoryOrderRouter(InMemoryInstrumentCatalog())
        let r = await router.submit(OrderRequest(symbol: "NPN", side: .buy, type: .market, quantity: 0, limitPrice: nil))
        XCTAssertFalse(r.accepted)
        XCTAssertEqual(r.failureReason, "Quantity must be positive")
    }

    func testOrderRouterRejectsLimitWithoutValidPrice() async {
        let cat = InMemoryInstrumentCatalog(); cat.add(inst("NPN"))
        let router = InMemoryOrderRouter(cat)
        let noPrice = await router.submit(OrderRequest(symbol: "NPN", side: .buy, type: .limit, quantity: 1, limitPrice: nil))
        XCTAssertEqual(noPrice.failureReason, "Limit order requires positive LimitPrice")
        let zeroPrice = await router.submit(OrderRequest(symbol: "NPN", side: .buy, type: .limit, quantity: 1, limitPrice: 0))
        XCTAssertEqual(zeroPrice.failureReason, "Limit order requires positive LimitPrice")
    }

    func testOrderRouterRejectsUnknownSymbol() async {
        let router = InMemoryOrderRouter(InMemoryInstrumentCatalog())
        let r = await router.submit(OrderRequest(symbol: "ZZZ", side: .buy, type: .market, quantity: 1, limitPrice: nil))
        XCTAssertEqual(r.failureReason, "Unknown symbol")
    }

    // ── Null backends ────────────────────────────────────────────────────────

    func testNullBackendsFailClosed() async throws {
        XCTAssertEqual(NullMarketDataFeed.instance.backendId, "null")
        XCTAssertNil(try await NullMarketDataFeed.instance.getQuote("x"))
        let sub = try NullMarketDataFeed.instance.subscribeQuotes("x") { _ in }
        sub.cancel()   // no-op

        XCTAssertEqual(NullInstrumentCatalog.instance.backendId, "null")
        XCTAssertNil(try await NullInstrumentCatalog.instance.get("x"))
        XCTAssertTrue(try await NullInstrumentCatalog.instance.search("x").isEmpty)

        XCTAssertEqual(NullOrderRouter.instance.backendId, "null")
        let r = await NullOrderRouter.instance.submit(OrderRequest(symbol: "x", side: .buy, type: .market, quantity: 1, limitPrice: nil))
        XCTAssertFalse(r.accepted)
        XCTAssertEqual(r.orderId, "00000000-0000-0000-0000-000000000000")
    }

    // ── Async test helpers ───────────────────────────────────────────────────

    /// Holds a single delivered quote; `waitForValue` polls until set.
    private actor Box {
        private var value: Quote?
        func set(_ q: Quote) { value = q }
        func waitForValue() async -> Quote? {
            for _ in 0..<200 {
                if let v = value { return v }
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
            return value
        }
    }

    /// Counts handler invocations; `waitForAtLeast` polls until the count is met.
    private actor Counter {
        private(set) var value = 0
        func increment() { value += 1 }
        func waitForAtLeast(_ n: Int) async -> Int {
            for _ in 0..<200 {
                if value >= n { return value }
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
            return value
        }
    }
}
