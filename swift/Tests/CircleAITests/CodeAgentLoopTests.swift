import XCTest
@testable import CircleAI

/// The path guard, the command refusal and the loop's own behaviour.
final class CodeAgentLoopTests: XCTestCase {

    // MARK: - Path traversal

    func testARelativePathResolvesInsideTheWorkspace() {
        let p = CodeAgentLoop.resolvePath("/work/repo", "src/a.swift")
        XCTAssertEqual(p, "/work/repo/src/a.swift")
    }

    // The one that matters: "edit my repo" must not become "edit /etc".
    func testDotDotEscapingTheWorkspaceIsRefused() {
        XCTAssertNil(CodeAgentLoop.resolvePath("/work/repo", "../../etc/passwd"))
    }

    func testAnAbsolutePathOutsideTheWorkspaceIsRefused() {
        XCTAssertNil(CodeAgentLoop.resolvePath("/work/repo", "/etc/passwd"))
    }

    func testAnAbsolutePathInsideTheWorkspaceIsAllowed() {
        XCTAssertEqual(CodeAgentLoop.resolvePath("/work/repo", "/work/repo/a.swift"),
                       "/work/repo/a.swift")
    }

    // A sibling directory that merely starts with the same characters is not
    // inside the workspace - the separator is what makes it a parent.
    func testASiblingWithASharedPrefixIsRefused() {
        XCTAssertNil(CodeAgentLoop.resolvePath("/work/repo", "/work/repo-secrets/keys"))
    }

    func testTheWorkspaceRootItselfIsAllowed() {
        XCTAssertEqual(CodeAgentLoop.resolvePath("/work/repo", "/work/repo"), "/work/repo")
    }

    func testAMissingPathIsRefused() {
        XCTAssertNil(CodeAgentLoop.resolvePath("/work/repo", nil))
        XCTAssertNil(CodeAgentLoop.resolvePath("/work/repo", "   "))
    }

    // Inner ".." that stays inside is fine - only escaping is refused.
    func testInnerDotDotThatStaysInsideIsAllowed() {
        XCTAssertEqual(CodeAgentLoop.resolvePath("/work/repo", "src/../lib/b.swift"),
                       "/work/repo/lib/b.swift")
    }

    // MARK: - Commands are off unless asked for

    func testTheDisabledRunnerRefusesAndSaysWhy() async {
        let res = await DisabledCommandRunner.instance.run(
            CommandRequest(executable: "rm", arguments: ["-rf", "/"], workingDirectory: "/"))
        XCTAssertFalse(res.executed)
        XCTAssertFalse(res.success)
        XCTAssertNotNil(res.denied)
        XCTAssertTrue(res.denied!.contains("disabled"))
    }

    #if os(macOS) || os(Linux)
    func testARunnerWithAnEmptyAllowListRefusesToExist() {
        XCTAssertThrowsError(try ProcessCommandRunner(allowedExecutables: []))
    }

    func testAnExecutableOffTheAllowListIsNotRun() async throws {
        let runner = try ProcessCommandRunner(allowedExecutables: ["echo"])
        let res = await runner.run(CommandRequest(
            executable: "/bin/rm", arguments: ["-rf", "/tmp/nope"], workingDirectory: "/tmp"))
        XCTAssertFalse(res.executed)
        XCTAssertTrue(res.denied!.contains("allow-list"))
    }

    func testAnAllowedExecutableRuns() async throws {
        let runner = try ProcessCommandRunner(allowedExecutables: ["echo"])
        let res = await runner.run(CommandRequest(
            executable: "/bin/echo", arguments: ["hello"], workingDirectory: "/tmp"))
        XCTAssertTrue(res.executed)
        XCTAssertTrue(res.success)
        XCTAssertTrue(res.stdout.contains("hello"))
    }
    #endif

    func testNotRunCarriesTheReasonAndIsNotSuccess() {
        let r = CommandResult.notRun("because")
        XCTAssertFalse(r.executed)
        XCTAssertFalse(r.success)
        XCTAssertEqual(r.denied, "because")
    }

    // MARK: - The system prompt only advertises what is wired

    func testTheSystemPromptHidesSearchWhenNoBackendIsWired() {
        let p = CodeAgentLoop.buildSystemPrompt(
            task: "t", workspaceRoot: "/w", allowCommands: false, hasSearch: false)
        XCTAssertFalse(p.contains("search_code"))
        XCTAssertFalse(p.contains("run_command"))
        XCTAssertTrue(p.contains("read_file"))
        XCTAssertTrue(p.contains("Task: t"))
    }

    func testTheSystemPromptOffersCommandsOnlyWhenAllowed() {
        let p = CodeAgentLoop.buildSystemPrompt(
            task: "t", workspaceRoot: "/w", allowCommands: true, hasSearch: true)
        XCTAssertTrue(p.contains("run_command"))
        XCTAssertTrue(p.contains("search_code"))
    }

    // MARK: - Truncation

    func testTruncationSaysHowMuchItRemoved() {
        let s = String(repeating: "x", count: 100)
        let t = CodeAgentLoop.truncate(s, 10)
        XCTAssertTrue(t.hasPrefix(String(repeating: "x", count: 10)))
        XCTAssertTrue(t.contains("truncated 90 chars"))
    }

    func testShortTextIsLeftAlone() {
        XCTAssertEqual(CodeAgentLoop.truncate("short", 100), "short")
        XCTAssertEqual(CodeAgentLoop.truncate("", 100), "")
    }

    // MARK: - The null agent

    func testTheNullAgentDeclinesHonestly() async throws {
        let r = try await NullCodeAgent.instance.run(task: "anything", workspaceRoot: "/w")
        XCTAssertFalse(r.available)
        XCTAssertEqual(r.quality, .unavailable)
        XCTAssertTrue(r.steps.isEmpty)
        XCTAssertTrue(r.appliedEdits.isEmpty)
    }
}
