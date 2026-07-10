// HostingMcpMultiplayerTests.swift
//
// Verifies the MCP JSON-RPC 2.0 dispatcher (initialize / tools.list / tools.call
// / resources.list / resources.read, notifications, error codes) and the
// MultiplayerHub presence + LWW-by-rev edits + colour hashing.

import XCTest
@testable import CircleAI

final class HostingMcpMultiplayerTests: XCTestCase {

    // ═══════════════════════════════════════════════════════════════════════
    // MCP
    // ═══════════════════════════════════════════════════════════════════════

    private func dispatcher(tools: [any IMcpTool] = [], resources: [any IMcpResourceProvider] = []) -> McpDispatcher {
        McpDispatcher(tools: tools, resources: resources, info: McpServerInfo())
    }

    func testInitializeReturnsServerInfo() async {
        let d = dispatcher()
        let resp = await d.dispatch(["jsonrpc": "2.0", "id": 1, "method": "initialize"])
        let result = resp?["result"] as? [String: Any]
        XCTAssertEqual(result?["protocolVersion"] as? String, "2024-11-05")
        let serverInfo = result?["serverInfo"] as? [String: Any]
        XCTAssertEqual(serverInfo?["name"] as? String, "circleai-mcp")
    }

    func testNotificationsInitializedReturnsNil() async {
        let d = dispatcher()
        let resp = await d.dispatch(["jsonrpc": "2.0", "method": "notifications/initialized"])
        XCTAssertNil(resp)
    }

    func testMissingMethodIsInvalidRequest() async {
        let d = dispatcher()
        let resp = await d.dispatch(["jsonrpc": "2.0", "id": 1])
        let err = resp?["error"] as? [String: Any]
        XCTAssertEqual(err?["code"] as? Int, -32600)
    }

    func testUnknownMethodReturns32601() async {
        let d = dispatcher()
        let resp = await d.dispatch(["jsonrpc": "2.0", "id": 1, "method": "bogus"])
        let err = resp?["error"] as? [String: Any]
        XCTAssertEqual(err?["code"] as? Int, -32601)
    }

    func testToolsListReturnsRegisteredTools() async {
        let d = dispatcher(tools: [EchoMcpTool(name: "search")])
        let resp = await d.dispatch(["jsonrpc": "2.0", "id": 2, "method": "tools/list"])
        let result = resp?["result"] as? [String: Any]
        let tools = result?["tools"] as? [[String: Any]]
        XCTAssertEqual(tools?.count, 1)
        XCTAssertEqual(tools?.first?["name"] as? String, "search")
    }

    func testToolsCallExecutesAndWrapsResult() async {
        let d = dispatcher(tools: [EchoMcpTool(name: "search")])
        let resp = await d.dispatch([
            "jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": ["name": "search", "arguments": ["q": "hi"]],
        ])
        let result = resp?["result"] as? [String: Any]
        XCTAssertEqual(result?["isError"] as? Bool, false)
        let content = result?["content"] as? [[String: Any]]
        XCTAssertEqual(content?.first?["type"] as? String, "text")
        XCTAssertTrue((content?.first?["text"] as? String ?? "").contains("hi"))
    }

    func testToolsCallUnknownToolReturns32602() async {
        let d = dispatcher(tools: [EchoMcpTool(name: "search")])
        let resp = await d.dispatch([
            "jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": ["name": "nope"],
        ])
        let err = resp?["error"] as? [String: Any]
        XCTAssertEqual(err?["code"] as? Int, -32602)
    }

    func testToolsCallToolErrorIsErrorTrue() async {
        let d = dispatcher(tools: [FailingMcpTool(name: "boom")])
        let resp = await d.dispatch([
            "jsonrpc": "2.0", "id": 4, "method": "tools/call",
            "params": ["name": "boom", "arguments": [:]],
        ])
        let result = resp?["result"] as? [String: Any]
        XCTAssertEqual(result?["isError"] as? Bool, true)
        let content = result?["content"] as? [[String: Any]]
        XCTAssertEqual(content?.first?["text"] as? String, "kaboom")
    }

    func testResourcesListAggregatesProviders() async {
        let d = dispatcher(resources: [VaultResourceProvider()])
        let resp = await d.dispatch(["jsonrpc": "2.0", "id": 5, "method": "resources/list"])
        let result = resp?["result"] as? [String: Any]
        let resources = result?["resources"] as? [[String: Any]]
        XCTAssertEqual(resources?.count, 1)
        XCTAssertEqual(resources?.first?["uri"] as? String, "vault://note/1")
    }

    func testResourcesReadReturnsContent() async {
        let d = dispatcher(resources: [VaultResourceProvider()])
        let resp = await d.dispatch([
            "jsonrpc": "2.0", "id": 6, "method": "resources/read",
            "params": ["uri": "vault://note/1"],
        ])
        let result = resp?["result"] as? [String: Any]
        let contents = result?["contents"] as? [[String: Any]]
        XCTAssertEqual(contents?.first?["text"] as? String, "note-body")
    }

    func testResourcesReadUnknownSchemeReturns32602() async {
        let d = dispatcher(resources: [VaultResourceProvider()])
        let resp = await d.dispatch([
            "jsonrpc": "2.0", "id": 6, "method": "resources/read",
            "params": ["uri": "http://x"],
        ])
        let err = resp?["error"] as? [String: Any]
        XCTAssertEqual(err?["code"] as? Int, -32602)
    }

    func testDispatchBatchDropsNotificationResponses() async {
        let d = dispatcher(tools: [EchoMcpTool(name: "search")])
        let batch: [Any] = [
            ["jsonrpc": "2.0", "id": 1, "method": "tools/list"],
            ["jsonrpc": "2.0", "method": "notifications/initialized"], // no response
        ]
        let out = await d.dispatchBatch(batch) as? [[String: Any]]
        XCTAssertEqual(out?.count, 1)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multiplayer
    // ═══════════════════════════════════════════════════════════════════════

    func testConnectJoinPresence() {
        let hub = MultiplayerHub()
        hub.connect(connectionId: "c1", identity: GuestPeerIdentity(peerId: "p1", displayName: "Alice"))
        let joined = hub.joinDocument(connectionId: "c1", docId: "doc")
        if case let .peerJoined(docId, cid, name, _)? = joined {
            XCTAssertEqual(docId, "doc")
            XCTAssertEqual(cid, "c1")
            XCTAssertEqual(name, "Alice")
        } else { XCTFail("expected peerJoined") }
        XCTAssertEqual(hub.peers(docId: "doc").count, 1)
    }

    func testDisconnectEmitsPeerLeftWhenInDoc() {
        let hub = MultiplayerHub()
        hub.connect(connectionId: "c1", identity: GuestPeerIdentity(displayName: "Bob"))
        _ = hub.joinDocument(connectionId: "c1", docId: "doc")
        let left = hub.disconnect(connectionId: "c1")
        if case let .peerLeft(docId, cid, _)? = left {
            XCTAssertEqual(docId, "doc")
            XCTAssertEqual(cid, "c1")
        } else { XCTFail("expected peerLeft") }
        XCTAssertTrue(hub.peers(docId: "doc").isEmpty)
    }

    func testSendEditAppliesHigherRev() {
        let hub = MultiplayerHub()
        hub.connect(connectionId: "c1", identity: GuestPeerIdentity())
        let r1 = hub.sendEdit(connectionId: "c1", docId: "doc", content: "hello", rev: 5)
        XCTAssertEqual(r1.acceptedRev, 5)
        XCTAssertNotNil(r1.event)
        XCTAssertEqual(hub.currentRev(docId: "doc"), 5)
    }

    func testSendEditRejectsStaleRev() {
        let hub = MultiplayerHub()
        hub.connect(connectionId: "c1", identity: GuestPeerIdentity())
        _ = hub.sendEdit(connectionId: "c1", docId: "doc", content: "v10", rev: 10)
        let stale = hub.sendEdit(connectionId: "c1", docId: "doc", content: "v3", rev: 3)
        XCTAssertEqual(stale.acceptedRev, 10, "server rev wins")
        XCTAssertNil(stale.event, "stale edit is not broadcast")
        XCTAssertEqual(hub.currentRev(docId: "doc"), 10)
    }

    func testSendCursorEvent() {
        let hub = MultiplayerHub()
        hub.connect(connectionId: "c1", identity: GuestPeerIdentity(displayName: "Cara"))
        let ev = hub.sendCursor(connectionId: "c1", docId: "doc", line: 4, ch: 2)
        if case let .cursorChanged(cid, name, _, line, ch)? = ev {
            XCTAssertEqual(cid, "c1"); XCTAssertEqual(name, "Cara")
            XCTAssertEqual(line, 4); XCTAssertEqual(ch, 2)
        } else { XCTFail("expected cursorChanged") }
    }

    func testColourForIsStableAndScoped() {
        let c1 = MultiplayerHub.colourFor("peer-abc")
        let c2 = MultiplayerHub.colourFor("peer-abc")
        XCTAssertEqual(c1, c2, "same id → same colour")
        XCTAssertTrue(c1.hasPrefix("hsl("))
        XCTAssertEqual(MultiplayerHub.colourFor(""), "#5a4fcf")
    }

    func testResetStateClearsPresenceAndRevs() {
        let hub = MultiplayerHub()
        hub.connect(connectionId: "c1", identity: GuestPeerIdentity())
        _ = hub.joinDocument(connectionId: "c1", docId: "doc")
        _ = hub.sendEdit(connectionId: "c1", docId: "doc", content: "x", rev: 2)
        hub.resetStateForTesting()
        XCTAssertTrue(hub.peers(docId: "doc").isEmpty)
        XCTAssertEqual(hub.currentRev(docId: "doc"), 0)
    }

    // ── MCP helpers ─────────────────────────────────────────────────────────

    struct EchoMcpTool: IMcpTool {
        let name: String
        var description: String { "echoes its q arg" }
        var inputSchema: [String: Any] { ["type": "object"] }
        func execute(arguments: [String: Any]) async throws -> Any {
            ["echo": arguments["q"] ?? ""]
        }
    }

    struct FailingMcpTool: IMcpTool {
        let name: String
        var description: String { "always fails" }
        var inputSchema: [String: Any] { ["type": "object"] }
        func execute(arguments: [String: Any]) async throws -> Any {
            throw McpToolError("kaboom")
        }
    }

    struct VaultResourceProvider: IMcpResourceProvider {
        var uriScheme: String { "vault://" }
        func list() async throws -> [McpResource] {
            [McpResource(uri: "vault://note/1", name: "Note 1", description: nil, mimeType: "text/plain")]
        }
        func read(uri: String) async throws -> McpResourceContent? {
            uri == "vault://note/1" ? McpResourceContent(uri: uri, mimeType: "text/plain", text: "note-body") : nil
        }
    }
}
