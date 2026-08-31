import XCTest
@testable import CircleAI

/// The desktop board and the context the adapter attaches.
final class DesktopBoardTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func win(_ id: String, process: String = "code", foreground: Bool = false)
        -> WindowDescriptor {
        WindowDescriptor(windowId: id, title: "t", processName: process,
                         x: 0, y: 0, width: 800, height: 600, isForeground: foreground)
    }

    // MARK: - Windows

    func testATrackedWindowComesBackById() {
        let b = InMemoryDesktopBoard()
        b.track(win("w1"))
        XCTAssertEqual(b.window(id: "w1")?.windowId, "w1")
        XCTAssertNil(b.window(id: "nope"))
    }

    func testTrackingTheSameWindowTwiceReplacesIt() {
        let b = InMemoryDesktopBoard()
        b.track(win("w1", foreground: false))
        b.track(win("w1", foreground: true))
        XCTAssertEqual(b.windows(ofProcess: "code").count, 1)
        XCTAssertTrue(b.window(id: "w1")!.isForeground)
    }

    // The same program is reported as Code, code and CODE by different shells.
    func testProcessLookupIgnoresCase() {
        let b = InMemoryDesktopBoard()
        b.track(win("w1", process: "Code"))
        b.track(win("w2", process: "code"))
        b.track(win("w3", process: "firefox"))
        XCTAssertEqual(b.windows(ofProcess: "CODE").count, 2)
        XCTAssertEqual(b.windows(ofProcess: "firefox").count, 1)
        XCTAssertTrue(b.windows(ofProcess: "safari").isEmpty)
    }

    // MARK: - Shortcuts

    func testAShortcutResolvesToItsAction() throws {
        let b = InMemoryDesktopBoard()
        b.registerShortcut(DesktopShortcut(shortcutId: "s1", keyChord: "Ctrl+Shift+P",
                                           action: "command-palette"))
        XCTAssertEqual(try b.action(forKeyChord: "Ctrl+Shift+P"), "command-palette")
    }

    // Nobody types Ctrl and ctrl meaning two different shortcuts.
    func testChordLookupIgnoresCase() throws {
        let b = InMemoryDesktopBoard()
        b.registerShortcut(DesktopShortcut(shortcutId: "s1", keyChord: "Ctrl+K", action: "clear"))
        XCTAssertEqual(try b.action(forKeyChord: "ctrl+k"), "clear")
        XCTAssertEqual(try b.action(forKeyChord: "CTRL+K"), "clear")
    }

    func testAnUnknownChordHasNoAction() throws {
        XCTAssertNil(try InMemoryDesktopBoard().action(forKeyChord: "Ctrl+Q"))
    }

    func testABlankChordIsRefusedRatherThanMatchingNothing() {
        XCTAssertThrowsError(try InMemoryDesktopBoard().action(forKeyChord: "  ")) { e in
            XCTAssertEqual(e as? DesktopError, .missingKeyChord)
        }
    }

    func testRegisteringTheSameChordTwiceReplacesTheAction() throws {
        let b = InMemoryDesktopBoard()
        b.registerShortcut(DesktopShortcut(shortcutId: "s1", keyChord: "Ctrl+K", action: "old"))
        b.registerShortcut(DesktopShortcut(shortcutId: "s2", keyChord: "ctrl+k", action: "new"))
        XCTAssertEqual(try b.action(forKeyChord: "Ctrl+K"), "new")
    }

    // MARK: - Sessions

    func testASessionComesBackById() {
        let b = InMemoryDesktopBoard()
        b.openSession(DesktopSession(sessionId: "s1", userName: "nandi", startedUtc: now,
                                     activeWorkspaces: ["main", "side"]))
        XCTAssertEqual(b.session(id: "s1")?.userName, "nandi")
        XCTAssertEqual(b.session(id: "s1")?.activeWorkspaces, ["main", "side"])
        XCTAssertNil(b.session(id: "s2"))
    }
}
