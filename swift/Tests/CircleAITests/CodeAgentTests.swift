import XCTest
@testable import CircleAI

final class CodeAgentTests: XCTestCase {

    // MARK: - Parsing what the model said

    func testParsesReadFile() {
        let a = AgentActionParser.parse("{\"action\":\"read_file\",\"path\":\"src/a.swift\"}")
        XCTAssertEqual(a.kind, .readFile)
        XCTAssertEqual(a.path, "src/a.swift")
    }

    func testParsesEditFileWithRange() {
        let a = AgentActionParser.parse(
            "{\"action\":\"edit_file\",\"path\":\"a.txt\",\"range_start\":4,\"range_end\":9,\"replacement\":\"hi\"}")
        XCTAssertEqual(a.kind, .editFile)
        XCTAssertEqual(a.rangeStart, 4)
        XCTAssertEqual(a.rangeEnd, 9)
        XCTAssertEqual(a.replacement, "hi")
    }

    func testParsesRunCommandWithArgs() {
        let a = AgentActionParser.parse(
            "{\"action\":\"run_command\",\"executable\":\"swift\",\"args\":[\"build\",\"-c\",\"release\"],\"cwd\":\".\"}")
        XCTAssertEqual(a.kind, .runCommand)
        XCTAssertEqual(a.executable, "swift")
        XCTAssertEqual(a.args ?? [], ["build", "-c", "release"])
        XCTAssertEqual(a.path, ".")
    }

    func testSearchTopKDefaultsToTenWhenAbsent() {
        let a = AgentActionParser.parse("{\"action\":\"search_code\",\"query\":\"parser\"}")
        XCTAssertEqual(a.kind, .searchCode)
        XCTAssertEqual(a.topK, 10)
    }

    func testFinishCarriesSummary() {
        let a = AgentActionParser.parse("{\"action\":\"finish\",\"summary\":\"renamed the thing\"}")
        XCTAssertEqual(a.kind, .finish)
        XCTAssertEqual(a.summary, "renamed the thing")
    }

    // A model that wraps its JSON in prose and a code fence is the normal case,
    // not the exception - the brace scanner exists for exactly this.
    func testExtractsJsonFromSurroundingProse() {
        let reply = """
        Sure! Here is what I will do:

        ```json
        {"action":"read_file","path":"README.md"}
        ```

        Let me know if that works.
        """
        let a = AgentActionParser.parse(reply)
        XCTAssertEqual(a.kind, .readFile)
        XCTAssertEqual(a.path, "README.md")
    }

    // A brace inside a quoted replacement must not close the object early.
    func testBracesInsideStringsDoNotEndTheObject() {
        let a = AgentActionParser.parse(
            "{\"action\":\"edit_file\",\"path\":\"a.c\",\"replacement\":\"if (x) { y(); }\"}")
        XCTAssertEqual(a.kind, .editFile)
        XCTAssertEqual(a.replacement, "if (x) { y(); }")
    }

    func testEscapedQuoteInsideStringIsNotAStringTerminator() {
        let a = AgentActionParser.parse(
            "{\"action\":\"finish\",\"summary\":\"said \\\"done\\\" and {stopped}\"}")
        XCTAssertEqual(a.kind, .finish)
        XCTAssertEqual(a.summary, "said \"done\" and {stopped}")
    }

    func testUnknownActionKeepsTheJsonAsRaw() {
        let a = AgentActionParser.parse("{\"action\":\"launch_missiles\"}")
        XCTAssertEqual(a.kind, .unknown)
        XCTAssertEqual(a.raw, "{\"action\":\"launch_missiles\"}")
    }

    func testProseWithNoJsonIsUnknownAndKeepsTheText() {
        let a = AgentActionParser.parse("I think we should refactor this.")
        XCTAssertEqual(a.kind, .unknown)
        XCTAssertEqual(a.raw, "I think we should refactor this.")
    }

    func testTruncatedJsonIsUnknownRatherThanACrash() {
        let a = AgentActionParser.parse("{\"action\":\"read_file\",\"path\":\"a.sw")
        XCTAssertEqual(a.kind, .unknown)
    }

    func testEmptyReplyIsUnknown() {
        XCTAssertEqual(AgentActionParser.parse("").kind, .unknown)
        XCTAssertEqual(AgentActionParser.parse(nil).kind, .unknown)
        XCTAssertEqual(AgentActionParser.parse("   ").kind, .unknown)
    }

    // A number where a string belongs, and a string where a number belongs,
    // must both fall back rather than coerce.
    func testWrongTypesFallBackInsteadOfCoercing() {
        let a = AgentActionParser.parse(
            "{\"action\":\"edit_file\",\"path\":42,\"range_start\":\"nine\"}")
        XCTAssertEqual(a.kind, .editFile)
        XCTAssertNil(a.path)
        XCTAssertEqual(a.rangeStart, 0)
    }

    func testBooleanIsNotANumber() {
        let a = AgentActionParser.parse("{\"action\":\"search_code\",\"query\":\"x\",\"top_k\":true}")
        XCTAssertEqual(a.topK, 10)
    }

    func testNonStringArrayEntriesAreDropped() {
        let a = AgentActionParser.parse(
            "{\"action\":\"run_command\",\"executable\":\"ls\",\"args\":[\"-l\",7,\"-a\"]}")
        XCTAssertEqual(a.args ?? [], ["-l", "-a"])
    }

    func testActionNameIsCaseAndSpaceInsensitive() {
        let a = AgentActionParser.parse("{\"action\":\"  Read_File \",\"path\":\"x\"}")
        XCTAssertEqual(a.kind, .readFile)
    }
}
