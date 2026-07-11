// MicroAgentsTests.swift
//
// Exercises the MicroAgents port: FuncMicroAgent lambda adapter, the in-memory
// host register/list/invoke (incl. unknown → nil), capability + free-text
// search, the invocation log, and the null agent. Mirrors CircleAI.MicroAgents/*.

import XCTest
import Foundation
@testable import CircleAI

final class MicroAgentsTests: XCTestCase {

    private func agent(_ id: String, caps: [String] = []) -> FuncMicroAgent {
        FuncMicroAgent(agentId: id, description: "agent \(id)", capabilities: caps) { input in
            MicroAgentResponse(agentId: id, output: "\(id):\(input)", metadata: ["echo": input])
        }
    }

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testResponseCodableRoundTrip() throws {
        let r = MicroAgentResponse(agentId: "a", output: "o", metadata: ["k": "v"])
        XCTAssertEqual(try JSONDecoder().decode(MicroAgentResponse.self, from: try JSONEncoder().encode(r)), r)
    }

    // ── FuncMicroAgent ─────────────────────────────────────────────────────────

    func testFuncMicroAgent() async {
        let a = agent("summ", caps: ["nlp"])
        XCTAssertEqual(a.backendId, "func")
        XCTAssertEqual(a.descriptor.capabilities, ["nlp"])
        let r = await a.invoke("hi")
        XCTAssertEqual(r.output, "summ:hi")
        XCTAssertEqual(r.metadata?["echo"], "hi")
    }

    // ── Host ──────────────────────────────────────────────────────────────────

    func testHostRegisterListInvoke() async {
        let host = InMemoryMicroAgentHost()
        XCTAssertEqual(host.backendId, "in-memory")
        host.register(agent("a"))
        host.register(agent("b"))
        XCTAssertEqual(Set(host.list().map { $0.agentId }), ["a", "b"])
        let r = await host.invoke(agentId: "a", input: "x")
        XCTAssertEqual(r?.output, "a:x")
    }

    func testHostUnknownAgentReturnsNil() async {
        let host = InMemoryMicroAgentHost()
        let r = await host.invoke(agentId: "ghost", input: "x")
        XCTAssertNil(r)
    }

    func testHostRegisterReplacesById() async {
        let host = InMemoryMicroAgentHost()
        host.register(agent("dup"))
        host.register(FuncMicroAgent(agentId: "dup", description: "new", capabilities: nil) { _ in
            MicroAgentResponse(agentId: "dup", output: "replaced")
        })
        XCTAssertEqual(host.list().count, 1)
        let r = await host.invoke(agentId: "dup", input: "x")
        XCTAssertEqual(r?.output, "replaced")
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    func testSearchByCapability() {
        let descs = [agent("a", caps: ["translate", "nlp"]).descriptor,
                     agent("b", caps: ["vision"]).descriptor,
                     agent("c", caps: ["NLP"]).descriptor]
        let nlp = MicroAgentSearch.byCapability(descs, capability: "nlp")
        XCTAssertEqual(nlp.map { $0.agentId }, ["a", "c"])  // case-insensitive, id-ordered
    }

    func testSearchFreeText() {
        let descs = [MicroAgentDescriptor(agentId: "translator", description: "translates text", capabilities: []),
                     MicroAgentDescriptor(agentId: "summariser", description: "shortens", capabilities: ["text"])]
        let hits = MicroAgentSearch.search(descs, query: "text")
        XCTAssertEqual(Set(hits.map { $0.agentId }), ["translator", "summariser"])
        let topK = MicroAgentSearch.search(descs, query: "text", topK: 1)
        XCTAssertEqual(topK.count, 1)
    }

    // ── Invocation log ─────────────────────────────────────────────────────────

    func testInvocationLog() {
        let log = MicroAgentInvocationLog()
        log.append(MicroAgentInvocation(agentId: "a", input: "1", responseText: "r1",
                                        atUtc: Date(timeIntervalSince1970: 1)))
        log.append(MicroAgentInvocation(agentId: "a", input: "2", responseText: "r2",
                                        atUtc: Date(timeIntervalSince1970: 2)))
        log.append(MicroAgentInvocation(agentId: "b", input: "3", responseText: "r3",
                                        atUtc: Date(timeIntervalSince1970: 3)))
        XCTAssertEqual(log.totalInvocations, 3)
        let forA = log.forAgent("a")
        XCTAssertEqual(forA.map { $0.input }, ["2", "1"])  // newest first
        XCTAssertEqual(log.forAgent("a", limit: 1).count, 1)
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullMicroAgent() async {
        let a = NullMicroAgent.instance
        XCTAssertEqual(a.agentId, "null")
        XCTAssertEqual(a.descriptor.description, "No-op micro agent")
        let r = await a.invoke("anything")
        XCTAssertEqual(r.output, "")
    }
}
