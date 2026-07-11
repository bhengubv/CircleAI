// PluginsTests.swift
//
// Exercises the Plugins port: the event bus (raise → handler, dispose stops
// delivery, unknown event no-op), the default + permissioned contexts
// (workspace/events gating), and the in-memory registry (register/enable/grant/
// revoke/uninstall). Mirrors CircleAI.Plugins/*.

import XCTest
import Foundation
@testable import CircleAI

final class PluginsTests: XCTestCase {

    // ── Event bus ─────────────────────────────────────────────────────────────

    func testRaiseDeliversToSubscribers() {
        let bus = PluginEvents()
        let box = Box()
        let sub = bus.subscribe(PluginEventNames.chatMessage) { payload in
            box.value = payload as? String
        }
        XCTAssertEqual(bus.subscriberCount(PluginEventNames.chatMessage), 1)
        bus.raise(PluginEventNames.chatMessage, "hello")
        XCTAssertEqual(box.value, "hello")
        sub.dispose()
        XCTAssertEqual(bus.subscriberCount(PluginEventNames.chatMessage), 0)
        bus.raise(PluginEventNames.chatMessage, "ignored")
        XCTAssertEqual(box.value, "hello")  // unchanged after dispose
    }

    func testRaiseUnknownEventIsNoop() {
        let bus = PluginEvents()
        bus.raise("nobody.listening", 42)  // no crash
    }

    func testDisposeIsIdempotent() {
        let bus = PluginEvents()
        let sub = bus.subscribe("e") { _ in }
        sub.dispose()
        sub.dispose()
        XCTAssertEqual(bus.subscriberCount("e"), 0)
    }

    // ── Contexts ──────────────────────────────────────────────────────────────

    func testDefaultContextExposesWorkspaceAndEvents() {
        let bus = PluginEvents()
        let ctx = PluginContext(workspacePathAccessor: { "/ws" }, events: bus,
                                logger: ConsoleCircleAILogger())
        XCTAssertEqual(ctx.workspacePath, "/ws")
        XCTAssertTrue(ctx.events is PluginEvents)
    }

    func testPermissionedContextGatesWorkspace() {
        let inner = PluginContext(workspacePathAccessor: { "/ws" }, events: PluginEvents(),
                                  logger: ConsoleCircleAILogger())
        // No permissions → workspace hidden, events silenced.
        let denied = PermissionedPluginContext(inner: inner, grantedPermissions: [])
        XCTAssertNil(denied.workspacePath)
        // Silent bus: subscribing + raising does nothing, but doesn't crash.
        let box = Box()
        let sub = denied.events.subscribe("e") { _ in box.value = "fired" }
        denied.events.raise("e", "x")
        sub.dispose()
        XCTAssertNil(box.value)

        // Granting workspace.read reveals the path.
        let readable = PermissionedPluginContext(
            inner: inner, grantedPermissions: [PermissionedPluginContext.Permissions.workspaceRead])
        XCTAssertEqual(readable.workspacePath, "/ws")

        // Granting events.subscribe forwards to the real bus.
        let subd = PermissionedPluginContext(
            inner: inner, grantedPermissions: [PermissionedPluginContext.Permissions.eventsSubscribe])
        let box2 = Box()
        let s2 = subd.events.subscribe("e") { p in box2.value = p as? String }
        subd.events.raise("e", "real")
        XCTAssertEqual(box2.value, "real")
        s2.dispose()
    }

    func testPermissionCheckIsCaseInsensitive() {
        let inner = PluginContext(workspacePathAccessor: { "/ws" }, events: PluginEvents(),
                                  logger: ConsoleCircleAILogger())
        let ctx = PermissionedPluginContext(inner: inner, grantedPermissions: ["WORKSPACE.WRITE"])
        XCTAssertEqual(ctx.workspacePath, "/ws")
    }

    // ── Registry ──────────────────────────────────────────────────────────────

    func testRegistryLifecycle() {
        let reg = PluginRegistry()
        let entry = reg.register(id: "p1", displayName: "Plugin One", version: "1.0.0", permissions: ["a"])
        XCTAssertFalse(entry.enabled)
        XCTAssertEqual(reg.all.count, 1)
        XCTAssertEqual(reg.get("P1")?.displayName, "Plugin One")  // case-insensitive

        XCTAssertTrue(reg.setEnabled("p1", true))
        XCTAssertEqual(reg.get("p1")?.enabled, true)
        XCTAssertFalse(reg.setEnabled("ghost", true))

        XCTAssertTrue(reg.grantPermission("p1", "b"))
        XCTAssertEqual(reg.get("p1")?.permissions.count, 2)
        // Idempotent grant.
        XCTAssertTrue(reg.grantPermission("p1", "b"))
        XCTAssertEqual(reg.get("p1")?.permissions.count, 2)

        XCTAssertTrue(reg.revokePermission("p1", "a"))
        XCTAssertFalse(reg.revokePermission("p1", "missing"))
        XCTAssertEqual(reg.get("p1")?.permissions, ["b"])

        XCTAssertTrue(reg.uninstall("p1"))
        XCTAssertFalse(reg.uninstall("p1"))
        XCTAssertTrue(reg.all.isEmpty)
    }

    func testRegisterReplacesById() {
        let reg = PluginRegistry()
        reg.register(id: "p", displayName: "old", version: "1", permissions: [])
        reg.register(id: "P", displayName: "new", version: "2", permissions: [])  // case-insensitive replace
        XCTAssertEqual(reg.all.count, 1)
        XCTAssertEqual(reg.get("p")?.displayName, "new")
    }

    // Reference box so synchronous event callbacks can record without actors.
    private final class Box: @unchecked Sendable { var value: String? }
}
