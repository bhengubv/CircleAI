// Desktop.swift
//
// The desktop board - windows, shortcuts and sessions - and a companion adapter
// that folds the active application and the clipboard into what gets asked.
//
// Ported from src/CircleAI.Desktop.

import Foundation

// MARK: - The board

public struct WindowDescriptor: Sendable, Equatable {
    public let windowId: String
    public let title: String
    public let processName: String
    public let x: Int
    public let y: Int
    public let width: Int
    public let height: Int
    public let isForeground: Bool

    public init(windowId: String, title: String, processName: String,
                x: Int, y: Int, width: Int, height: Int, isForeground: Bool) {
        self.windowId = windowId
        self.title = title
        self.processName = processName
        self.x = x
        self.y = y
        self.width = width
        self.height = height
        self.isForeground = isForeground
    }
}

public struct DesktopShortcut: Sendable, Equatable {
    public let shortcutId: String
    public let keyChord: String
    public let action: String

    public init(shortcutId: String, keyChord: String, action: String) {
        self.shortcutId = shortcutId
        self.keyChord = keyChord
        self.action = action
    }
}

public struct DesktopSession: Sendable, Equatable {
    public let sessionId: String
    public let userName: String
    public let startedUtc: Date
    public let activeWorkspaces: [String]

    public init(sessionId: String, userName: String, startedUtc: Date, activeWorkspaces: [String]) {
        self.sessionId = sessionId
        self.userName = userName
        self.startedUtc = startedUtc
        self.activeWorkspaces = activeWorkspaces
    }
}

public enum DesktopError: Error, CustomStringConvertible, Equatable {
    case missingKeyChord
    public var description: String { "keyChord required" }
}

public protocol IDesktopBoard: Sendable {
    func track(_ window: WindowDescriptor)
    func window(id: String) -> WindowDescriptor?
    func windows(ofProcess processName: String) -> [WindowDescriptor]
    func registerShortcut(_ shortcut: DesktopShortcut)
    func action(forKeyChord keyChord: String) throws -> String?
    func openSession(_ session: DesktopSession)
    func session(id: String) -> DesktopSession?
}

public final class InMemoryDesktopBoard: IDesktopBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var windows: [String: WindowDescriptor] = [:]
    /// Keyed on the LOWERCASED chord: nobody types Ctrl and ctrl meaning two
    /// different shortcuts, and the C# uses an ordinal-ignore-case dictionary.
    private var shortcuts: [String: DesktopShortcut] = [:]
    private var sessions: [String: DesktopSession] = [:]

    public init() {}

    public func track(_ window: WindowDescriptor) {
        lock.lock(); windows[window.windowId] = window; lock.unlock()
    }

    public func window(id: String) -> WindowDescriptor? {
        lock.lock(); defer { lock.unlock() }
        return windows[id]
    }

    /// Process names are matched case-insensitively - the same program is
    /// reported as Code, code and CODE by different shells.
    public func windows(ofProcess processName: String) -> [WindowDescriptor] {
        lock.lock(); defer { lock.unlock() }
        let needle = processName.lowercased()
        return windows.values.filter { $0.processName.lowercased() == needle }
    }

    public func registerShortcut(_ shortcut: DesktopShortcut) {
        lock.lock(); shortcuts[shortcut.keyChord.lowercased()] = shortcut; lock.unlock()
    }

    public func action(forKeyChord keyChord: String) throws -> String? {
        guard !keyChord.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw DesktopError.missingKeyChord
        }
        lock.lock(); defer { lock.unlock() }
        return shortcuts[keyChord.lowercased()]?.action
    }

    public func openSession(_ session: DesktopSession) {
        lock.lock(); sessions[session.sessionId] = session; lock.unlock()
    }

    public func session(id: String) -> DesktopSession? {
        lock.lock(); defer { lock.unlock() }
        return sessions[id]
    }
}

// MARK: - The companion adapter

/// Wraps a companion session and folds desktop context into every message.
///
/// The clipboard is CLAMPED to 200 characters. Somebody who just copied a
/// password, a private key or half a document should not have all of it posted
/// into a prompt because they then asked an unrelated question.
public final class DesktopCompanionAdapter: ICompanionSession, @unchecked Sendable {
    /// Longest clipboard excerpt that will ever be attached.
    public static let clipboardExcerptLimit = 200

    private let inner: ICompanionSession
    private let lock = NSLock()
    private var activeApp: String?
    private var clipboard: String?

    public init(_ inner: ICompanionSession) { self.inner = inner }

    public var activeApplication: String? {
        get { lock.lock(); defer { lock.unlock() }; return activeApp }
        set { lock.lock(); activeApp = newValue; lock.unlock() }
    }

    public var clipboardContent: String? {
        get { lock.lock(); defer { lock.unlock() }; return clipboard }
        set { lock.lock(); clipboard = newValue; lock.unlock() }
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    /// Always Desktop, whatever the wrapped session says - that is the point
    /// of the adapter.
    public var interface: InterfaceKind { .desktop }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String {
        try await inner.agent(enrich(instruction))
    }

    /// Appends whatever desktop context is set, and nothing when none is.
    func enrich(_ message: String) -> String {
        var out = message
        if let app = activeApplication, !app.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            out += "\n[Desktop context] Active app: \(app)"
        }
        if let clip = clipboardContent, !clip.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            out += "\n[Clipboard] \(String(clip.prefix(Self.clipboardExcerptLimit)))"
        }
        while let last = out.last, last.isWhitespace { out.removeLast() }
        return out
    }

    // ── Desktop helpers ───────────────────────────────────────────────────

    public func diagnoseSlowdown(symptoms: String, systemSpecs: String) async throws -> String {
        try await inner.agent(
            "Diagnose desktop slowdown: \(symptoms) on \(systemSpecs). Top 5 suspect causes + how to verify each in 60 seconds.")
    }

    public func writeShortcutCheatsheet(appName: String, proficiencyLevel: String) async throws -> String {
        try await inner.agent(
            "Write a one-page keyboard shortcut cheatsheet for \(appName), \(proficiencyLevel) user. Group by action category.")
    }

    public func automateRepetitiveTask(taskDescription: String, preferredTool: String) async throws -> String {
        try await inner.agent(
            "Suggest automation for: \(taskDescription) using \(preferredTool). Step-by-step + edge cases.")
    }

    public func designWorkspaceLayout(monitorCount: String, primaryWorkflow: String) async throws -> String {
        try await inner.agent(
            "Design a \(monitorCount)-monitor workspace layout for: \(primaryWorkflow). Apps per screen, hotkey conventions, eye-line ergonomics.")
    }
}
