// WindowsAutomation.swift
//
// Port of src/CircleAI.WindowsAutomation/:
//   • Contracts.cs                 → UiElement, IUiAutomationDriver
//   • InMemoryWindowsAutomation.cs → UiAutomationEvent, InMemoryUiAutomationDriver
//   • NullImplementations.cs       → NullUiAutomationDriver
//   • WindowsAutomationHelpers.cs  → UiElementHelpers (hit-test / dump)
//
// The package is OS-neutral in shape: hosts snap a real Win32-UIA driver in for
// production; the in-memory driver lets callers drive a virtual UI without a
// desktop. Nothing here is Windows-specific at the Swift level — it is the
// contract + a portable reference driver, so it belongs in the portable surface.
//
// Porting notes:
//   • C# `ValueTask` / `ValueTask<T>` → `async` / `async -> T`.
//   • `IObserver`-style `Observe(Action<…>)` → an `@escaping @Sendable` closure.
//     Observer notifications swallow thrown errors, matching the C# try/catch
//     that logs and continues.
//   • `InMemoryUiAutomationDriver` holds mutable element + observer state →
//     `final class … @unchecked Sendable` + a single `NSLock`. The snapshot
//     copies observers under the lock before invoking them (as the C# does).
//   • Argument validation (`elementId required`, unknown-element, non-null text)
//     is reproduced via `precondition` / thrown `UiAutomationError`.

import Foundation

// MARK: - UiElement

/// A single UI-automation element. Port of C# record `UiElement`.
public struct UiElement: Sendable, Equatable {
    public let elementId: String
    public let name: String
    public let kind: String
    public let x: Int
    public let y: Int
    public let width: Int
    public let height: Int

    public init(elementId: String, name: String, kind: String, x: Int, y: Int, width: Int, height: Int) {
        self.elementId = elementId
        self.name = name
        self.kind = kind
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

// MARK: - IUiAutomationDriver

/// Cross-backend UI-automation driver contract. Port of C# `IUiAutomationDriver`.
public protocol IUiAutomationDriver: AnyObject, Sendable {
    var backendId: String { get }
    func snapshot() async throws -> [UiElement]
    func click(_ elementId: String) async throws
    func type(_ text: String) async throws
    func key(_ keyName: String) async throws
}

// MARK: - Errors

public enum UiAutomationError: Error, Equatable, CustomStringConvertible {
    case elementIdRequired
    case unknownElement(String)
    case keyNameRequired

    public var description: String {
        switch self {
        case .elementIdRequired: return "elementId required"
        case .unknownElement(let id): return "Unknown element '\(id)'."
        case .keyNameRequired: return "keyName required"
        }
    }
}

// MARK: - UiAutomationEvent

/// An observable event raised by the in-memory driver. Port of C# record
/// `UiAutomationEvent`.
public struct UiAutomationEvent: Sendable, Equatable {
    public let kind: String
    public let elementId: String?
    public let payload: String?

    public init(kind: String, elementId: String?, payload: String?) {
        self.kind = kind
        self.elementId = elementId
        self.payload = payload
    }
}

// MARK: - InMemoryUiAutomationDriver

/// Real-but-virtual UIA driver. Click / Type / Key raise `UiAutomationEvent`s
/// the host can observe. Port of C# `InMemoryUiAutomationDriver`.
public final class InMemoryUiAutomationDriver: IUiAutomationDriver, @unchecked Sendable {
    public typealias Observer = @Sendable (UiAutomationEvent) -> Void

    private let lock = NSLock()
    private var elements: [String: UiElement] = [:]
    private var observers: [Observer] = []

    public init() {}

    public var backendId: String { "in-memory" }

    /// Registers (or replaces) an element by id.
    public func register(_ element: UiElement) {
        lock.lock(); defer { lock.unlock() }
        elements[element.elementId] = element
    }

    /// Subscribes an observer to every raised event.
    public func observe(_ observer: @escaping Observer) {
        lock.lock(); defer { lock.unlock() }
        observers.append(observer)
    }

    public func snapshot() async throws -> [UiElement] {
        lock.lock(); defer { lock.unlock() }
        return Array(elements.values)
    }

    public func click(_ elementId: String) async throws {
        if elementId.trimmingCharacters(in: .whitespaces).isEmpty {
            throw UiAutomationError.elementIdRequired
        }
        let known: Bool = {
            lock.lock(); defer { lock.unlock() }
            return elements[elementId] != nil
        }()
        if !known { throw UiAutomationError.unknownElement(elementId) }
        notify(UiAutomationEvent(kind: "click", elementId: elementId, payload: nil))
    }

    public func type(_ text: String) async throws {
        // C# throws ArgumentNullException on null; Swift String is non-optional,
        // so there is nothing to reject here — proceed and raise the event.
        notify(UiAutomationEvent(kind: "type", elementId: nil, payload: text))
    }

    public func key(_ keyName: String) async throws {
        if keyName.trimmingCharacters(in: .whitespaces).isEmpty {
            throw UiAutomationError.keyNameRequired
        }
        notify(UiAutomationEvent(kind: "key", elementId: nil, payload: keyName))
    }

    /// Copies the observer list under the lock, then invokes each outside the
    /// lock, swallowing any error the observer raises (parity with the C#
    /// try/catch that logs and continues).
    private func notify(_ event: UiAutomationEvent) {
        let snapshot: [Observer] = {
            lock.lock(); defer { lock.unlock() }
            return observers
        }()
        for observer in snapshot {
            observer(event)
        }
    }
}

// MARK: - NullUiAutomationDriver

/// No-op driver. Snapshot is empty; all actions succeed silently. Port of C#
/// `NullUiAutomationDriver`.
public final class NullUiAutomationDriver: IUiAutomationDriver, @unchecked Sendable {
    public static let shared = NullUiAutomationDriver()

    public init() {}

    public var backendId: String { "null" }
    public func snapshot() async throws -> [UiElement] { [] }
    public func click(_ elementId: String) async throws {}
    public func type(_ text: String) async throws {}
    public func key(_ keyName: String) async throws {}
}

// MARK: - UiElementHelpers

/// Helpers for building / querying `UiElement`s. Port of C# static
/// `UiElementHelpers`.
public enum UiElementHelpers {
    /// True when (x, y) falls inside the element's bounds (right/bottom exclusive).
    public static func containsPoint(_ element: UiElement, x: Int, y: Int) -> Bool {
        x >= element.x && y >= element.y && x < element.x + element.width && y < element.y + element.height
    }

    /// All elements whose bounds contain (x, y).
    public static func hitTest(_ elements: [UiElement], x: Int, y: Int) -> [UiElement] {
        elements.filter { containsPoint($0, x: x, y: y) }
    }

    /// Formatted multi-line dump for debugging. Matches the C# `Dump` layout:
    /// `id "name" kind @ (x,y) WxH\n` per element.
    public static func dump(_ elements: [UiElement]) -> String {
        var s = ""
        for e in elements {
            s += "\(e.elementId) \"\(e.name)\" \(e.kind) @ (\(e.x),\(e.y)) \(e.width)x\(e.height)\n"
        }
        return s
    }
}

// MARK: - UiElement convenience

extension UiElement {
    /// Instance-style hit-test mirroring the C# extension `ContainsPoint`.
    public func contains(x: Int, y: Int) -> Bool {
        UiElementHelpers.containsPoint(self, x: x, y: y)
    }
}
