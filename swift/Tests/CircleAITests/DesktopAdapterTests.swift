import XCTest
@testable import CircleAI

/// What the desktop adapter attaches to a message, and what it refuses to.
final class DesktopAdapterTests: XCTestCase {

    private final class RecordingSession: ICompanionSession, @unchecked Sendable {
        private let lock = NSLock()
        private var sent: String?
        private var agented: String?

        var sessionId: String { "sess-1" }
        var identityId: String { "id-1" }
        var interface: InterfaceKind { .web }
        var history: [CompanionTurn] { [] }

        func send(_ message: String) async throws -> String {
            lock.lock(); sent = message; lock.unlock()
            return "ok"
        }
        func stream(_ message: String) -> AsyncStream<String> {
            lock.lock(); sent = message; lock.unlock()
            return AsyncStream { $0.finish() }
        }
        func agent(_ instruction: String) async throws -> String {
            lock.lock(); agented = instruction; lock.unlock()
            return "done"
        }
        func getContext() -> CompanionContext {
            CompanionContext(identityId: "id-1", displayName: "Nandi", interface: .web,
                             personaHints: "", affectSummary: "",
                             recentMemorySnippets: [], activeGoals: [])
        }
        func refreshContext() async throws {}
        func signalFeedback(positive: Bool, note: String?) async throws {}
        var proactiveEvents: AsyncStream<CompanionProactiveEvent> { AsyncStream { $0.finish() } }

        func readSent() -> String? { lock.lock(); defer { lock.unlock() }; return sent }
        func readAgented() -> String? { lock.lock(); defer { lock.unlock() }; return agented }
    }

    // The wrapped session says web; the adapter must still say desktop.
    func testTheAdapterReportsTheDesktopSurface() {
        let a = DesktopCompanionAdapter(RecordingSession())
        XCTAssertEqual(a.interface, .desktop)
        XCTAssertEqual(a.sessionId, "sess-1")
        XCTAssertEqual(a.identityId, "id-1")
        XCTAssertEqual(a.getContext().displayName, "Nandi")
    }

    func testWithNoContextTheMessageIsUnchanged() async throws {
        let inner = RecordingSession()
        _ = try await DesktopCompanionAdapter(inner).send("what is this error")
        XCTAssertEqual(inner.readSent(), "what is this error")
    }

    func testTheActiveApplicationIsAttached() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)
        a.activeApplication = "Visual Studio Code"
        _ = try await a.send("what is this error")
        XCTAssertEqual(inner.readSent(),
                       "what is this error\n[Desktop context] Active app: Visual Studio Code")
    }

    // Somebody who just copied a password should not have all of it posted into
    // a prompt because they then asked an unrelated question.
    func testALongClipboardIsClampedToTwoHundredCharacters() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)
        a.clipboardContent = String(repeating: "s", count: 5000)
        _ = try await a.send("hello")

        let seen = inner.readSent()!
        XCTAssertTrue(seen.contains("[Clipboard] "))
        XCTAssertEqual(seen.components(separatedBy: "[Clipboard] ").last!.count,
                       DesktopCompanionAdapter.clipboardExcerptLimit)
    }

    func testAShortClipboardIsAttachedWhole() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)
        a.clipboardContent = "SELECT 1"
        _ = try await a.send("explain")
        XCTAssertTrue(inner.readSent()!.hasSuffix("[Clipboard] SELECT 1"))
    }

    func testBlankContextIsNotAttached() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)
        a.activeApplication = "   "
        a.clipboardContent = ""
        _ = try await a.send("hello")
        XCTAssertEqual(inner.readSent(), "hello")
    }

    func testBothPiecesOfContextAreAttachedInOrder() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)
        a.activeApplication = "Terminal"
        a.clipboardContent = "rm -rf"
        _ = try await a.send("is this safe")
        XCTAssertEqual(inner.readSent(),
                       "is this safe\n[Desktop context] Active app: Terminal\n[Clipboard] rm -rf")
    }

    func testEnrichmentAlsoAppliesToStreamAndAgent() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)
        a.activeApplication = "Figma"

        for await _ in a.stream("resize this") {}
        XCTAssertTrue(inner.readSent()!.contains("Active app: Figma"))

        _ = try await a.agent("do it")
        XCTAssertTrue(inner.readAgented()!.contains("Active app: Figma"))
    }

    // The helpers go straight to the agent, unenriched - they carry their own
    // full instruction and desktop context would only dilute it.
    func testTheDesktopHelpersAskTheAgentDirectly() async throws {
        let inner = RecordingSession()
        let a = DesktopCompanionAdapter(inner)

        _ = try await a.diagnoseSlowdown(symptoms: "fans loud", systemSpecs: "16GB M1")
        XCTAssertTrue(inner.readAgented()!.contains("Diagnose desktop slowdown: fans loud on 16GB M1"))

        _ = try await a.writeShortcutCheatsheet(appName: "Blender", proficiencyLevel: "beginner")
        XCTAssertTrue(inner.readAgented()!.contains("cheatsheet for Blender, beginner user"))

        _ = try await a.designWorkspaceLayout(monitorCount: "3", primaryWorkflow: "editing")
        XCTAssertTrue(inner.readAgented()!.contains("3-monitor workspace layout for: editing"))
    }
}
