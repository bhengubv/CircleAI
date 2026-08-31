import XCTest
@testable import CircleAI

/// Money, currency formatting and calendar dates.
final class BusinessOpsMoneyTests: XCTestCase {

    private func zar(_ d: Decimal) -> Money { Money(d, "ZAR")! }

    // MARK: - Money

    func testACurrencyIsRequired() {
        XCTAssertNil(Money(100, ""))
        XCTAssertNil(Money(100, "   "))
    }

    func testTheCodeIsNormalisedSoZarAndZarAreTheSameCurrency() {
        XCTAssertEqual(Money(100, " zar ")?.currency, "ZAR")
    }

    // The whole reason Money exists: rand and naira must never silently add.
    func testAddingTwoCurrenciesThrows() {
        XCTAssertThrowsError(try Money.add(zar(100), Money(100, "NGN")!)) { e in
            XCTAssertEqual(e as? MoneyError, .mixedCurrency("ZAR", "NGN"))
        }
    }

    func testSameCurrencyAddsAndSubtracts() throws {
        XCTAssertEqual(try Money.add(zar(100), zar(50)).amount, 150)
        XCTAssertEqual(try Money.subtract(zar(100), zar(50)).amount, 50)
    }

    func testMultiplicationScalesTheAmountAndKeepsTheCurrency() {
        let m = zar(100) * 3
        XCTAssertEqual(m.amount, 300)
        XCTAssertEqual(m.currency, "ZAR")
        XCTAssertEqual((3 * zar(100)).amount, 300)
    }

    // Decimal, not Double. On a Double this assertion fails.
    func testTenthsAddUpExactly() throws {
        var sum = zar(0)
        for _ in 1...10 { sum = try Money.add(sum, zar(Decimal(string: "0.1")!)) }
        XCTAssertEqual(sum.amount, 1)
    }

    // Half away from zero, not bankers rounding: 2.5 cents is 3, not 2.
    func testRoundingIsHalfAwayFromZeroNotBankers() {
        XCTAssertEqual(zar(Decimal(string: "1.005")!).rounded().amount, Decimal(string: "1.01"))
        XCTAssertEqual(zar(Decimal(string: "2.675")!).rounded().amount, Decimal(string: "2.68"))
        XCTAssertEqual(zar(Decimal(string: "-1.005")!).rounded().amount, Decimal(string: "-1.01"))
    }

    func testZeroIsZero() {
        XCTAssertTrue(Money.zero("ZAR").isZero)
        XCTAssertEqual(Money.zero("ZAR").currency, "ZAR")
    }

    // MARK: - Formatting

    func testKnownCurrenciesGetTheirSymbol() {
        XCTAssertEqual(Currencies.symbol(for: "ZAR"), "R")
        XCTAssertEqual(Currencies.symbol(for: "ngn"), "\u{20A6}")
        XCTAssertEqual(Currencies.symbol(for: "USD"), "$")
    }

    func testAnUnknownCurrencyPrintsItsCodeRatherThanNothing() {
        XCTAssertEqual(Currencies.symbol(for: "XYZ"), "XYZ")
        XCTAssertEqual(Currencies.symbol(for: ""), "")
    }

    // A locale-driven format would print "R 1.234,56" on a German phone and
    // turn a thousand rand into one.
    func testFormattingIsInvariantWithSpaceSeparatedThousands() {
        XCTAssertEqual(Currencies.format(zar(Decimal(string: "1234567.5")!)), "R 1 234 567.50")
        XCTAssertEqual(Currencies.format(zar(0)), "R 0.00")
        XCTAssertEqual(Currencies.format(zar(Decimal(string: "9.4")!)), "R 9.40")
    }

    // MARK: - Calendar dates

    func testADateHasNoTimeAndPrintsAsIso() {
        XCTAssertEqual(CalendarDate(2026, 7, 1).description, "2026-07-01")
    }

    func testAddingDaysCrossesMonthAndYearBoundaries() {
        XCTAssertEqual(CalendarDate(2026, 7, 1).addingDays(30), CalendarDate(2026, 7, 31))
        XCTAssertEqual(CalendarDate(2026, 12, 20).addingDays(30), CalendarDate(2027, 1, 19))
    }

    func testAddingDaysHandlesLeapDay() {
        XCTAssertEqual(CalendarDate(2028, 2, 28).addingDays(1), CalendarDate(2028, 2, 29))
        XCTAssertEqual(CalendarDate(2027, 2, 28).addingDays(1), CalendarDate(2027, 3, 1))
    }

    func testDatesCompareChronologically() {
        XCTAssertTrue(CalendarDate(2026, 7, 1) < CalendarDate(2026, 7, 2))
        XCTAssertTrue(CalendarDate(2026, 7, 31) < CalendarDate(2026, 8, 1))
        XCTAssertTrue(CalendarDate(2026, 12, 31) < CalendarDate(2027, 1, 1))
    }

    func testTheUnsetDateIsBeforeEverythingReal() {
        XCTAssertTrue(CalendarDate.unset < CalendarDate(2026, 1, 1))
    }
}
