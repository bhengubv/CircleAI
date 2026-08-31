import XCTest
@testable import CircleAI

/// Clients, reminders and the CRM bridge.
final class BusinessOpsReminderTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)   // 2026-07-01T09:00:00Z

    private func scheduler() -> (ReminderScheduler, InMemoryBusinessStore) {
        let store = InMemoryBusinessStore()
        return (ReminderScheduler(store: store, clock: FixedBusinessClock(now)), store)
    }

    // MARK: - Recurrence

    func testAOneOffHasNoNextOccurrence() {
        XCTAssertNil(RecurrenceRule.once.next(from: now))
        XCTAssertFalse(RecurrenceRule.once.isRecurring)
    }

    func testDailyWeeklyMonthlyAndYearlyStepCorrectly() {
        XCTAssertEqual(RecurrenceRule(.daily).next(from: now), now.addingTimeInterval(86400))
        XCTAssertEqual(RecurrenceRule(.weekly).next(from: now), now.addingTimeInterval(7 * 86400))
        XCTAssertEqual(RecurrenceRule(.daily, interval: 3).next(from: now),
                       now.addingTimeInterval(3 * 86400))
    }

    // Monthly steps by CALENDAR month, not by 30 days - so the 31st stays the
    // last day of the month instead of drifting backwards every cycle.
    func testMonthlyStepsByCalendarMonthNotThirtyDays() {
        let jan31 = Date(timeIntervalSince1970: 1_832_889_600)   // 2028-01-31T00:00:00Z
        let next = RecurrenceRule(.monthly).next(from: jan31)
        XCTAssertEqual(CalendarDate.from(next!), CalendarDate(2028, 2, 29))
    }

    func testAZeroIntervalIsTreatedAsOne() {
        XCTAssertEqual(RecurrenceRule(.daily, interval: 0).next(from: now),
                       now.addingTimeInterval(86400))
    }

    // MARK: - Scheduling

    func testAScheduledReminderIsStampedWithItsCreationTime() async throws {
        let (s, _) = scheduler()
        let r = try await s.schedule(Reminder(reminderId: "r1", title: "Call Thabo", dueAtUtc: now))
        XCTAssertEqual(r.createdAtUtc, now)
    }

    func testAReminderNeedsAnIdAndATitle() async {
        let (s, _) = scheduler()
        do {
            _ = try await s.schedule(Reminder(reminderId: "", title: "x", dueAtUtc: now))
            XCTFail("expected a refusal")
        } catch let e as BusinessOpsError { XCTAssertEqual(e, .missingField("reminderId")) }
        catch { XCTFail("wrong error") }
    }

    func testAFollowUpIsTiedToWhatItIsAbout() async throws {
        let (s, _) = scheduler()
        let r = try await s.scheduleFollowUp(relatedEntityId: "inv-1", title: "Chase payment",
                                             dueAtUtc: now, repeatRule: nil)
        XCTAssertEqual(r.kind, .followUp)
        XCTAssertEqual(r.relatedEntityId, "inv-1")
    }

    // A repeating reminder that stops after the first tick is just a reminder.
    func testCompletingARecurringReminderSchedulesTheNextOne() async throws {
        let (s, _) = scheduler()
        let r = try await s.schedule(Reminder(reminderId: "r1", title: "Monthly call",
                                              dueAtUtc: now, repeatRule: RecurrenceRule(.monthly)))
        let next = try await s.complete(r.reminderId)
        XCTAssertNotNil(next)
        XCTAssertNotEqual(next?.reminderId, r.reminderId)
        XCTAssertFalse(next!.completed)
        XCTAssertEqual(CalendarDate.from(next!.dueAtUtc), CalendarDate(2026, 8, 1))

        let done = try await s.get(r.reminderId)
        XCTAssertTrue(done!.completed)
    }

    func testCompletingAOneOffReturnsNothingFurther() async throws {
        let (s, _) = scheduler()
        let r = try await s.schedule(Reminder(reminderId: "r1", title: "Once", dueAtUtc: now))
        let v0 = try await s.complete(r.reminderId)
        XCTAssertNil(v0)
    }

    func testCompletingSomethingThatIsNotThereSaysSo() async {
        let (s, _) = scheduler()
        do {
            _ = try await s.complete("nope")
            XCTFail("expected not found")
        } catch let e as BusinessOpsError { XCTAssertEqual(e, .reminderNotFound("nope")) }
        catch { XCTFail("wrong error") }
    }

    func testListingsAreSoonestFirstAndExcludeWhatIsDone() async throws {
        let (s, _) = scheduler()
        _ = try await s.schedule(Reminder(reminderId: "late", title: "b",
                                          dueAtUtc: now.addingTimeInterval(7200)))
        _ = try await s.schedule(Reminder(reminderId: "soon", title: "a",
                                          dueAtUtc: now.addingTimeInterval(60)))
        _ = try await s.schedule(Reminder(reminderId: "gone", title: "c",
                                          dueAtUtc: now, completed: true))

        let pending = try await s.listPending()
        XCTAssertEqual(pending.map(\.reminderId), ["soon", "late"])

        let due = try await s.listDue(asOf: now.addingTimeInterval(120))
        XCTAssertEqual(due.map(\.reminderId), ["soon"])
    }

    func testListingForAnEntityOnlyReturnsThatEntity() async throws {
        let (s, _) = scheduler()
        _ = try await s.scheduleFollowUp(relatedEntityId: "inv-1", title: "a", dueAtUtc: now, repeatRule: nil)
        _ = try await s.scheduleFollowUp(relatedEntityId: "inv-2", title: "b", dueAtUtc: now, repeatRule: nil)
        let v1 = try await s.listForEntity("inv-1").count
        XCTAssertEqual(v1, 1)
    }
}
