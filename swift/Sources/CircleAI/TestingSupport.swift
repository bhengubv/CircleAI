// TestingSupport.swift
//
// Golden-file snapshot testing, a clock that does not move, and ids derived
// from a seed. Everything here exists so a test can be repeated tomorrow and
// get the same answer.
//
// Ported from src/CircleAI.Testing.

import Foundation

/// The outcome of comparing an answer against its golden copy.
public struct SnapshotDiff: Sendable, Equatable {
    public let equal: Bool
    public let diff: String?
    public init(equal: Bool, diff: String?) {
        self.equal = equal
        self.diff = diff
    }
}

public enum TestingError: Error, CustomStringConvertible, Equatable {
    case missingTestId
    case missingSeed
    public var description: String {
        switch self {
        case .missingTestId: return "testId required"
        case .missingSeed: return "seed required"
        }
    }
}

public protocol IGoldenStore: Sendable {
    var backendId: String { get }
    func read(_ testId: String) async throws -> String?
    func write(_ testId: String, golden: String) async throws
}

public protocol ISnapshotComparer: Sendable {
    var backendId: String { get }
    func compare(_ testId: String, actual: String) async throws -> SnapshotDiff
}

/// No golden store is wired, so nothing can match. Reports NOT equal rather
/// than equal - a comparer that passes by default passes everything.
public struct NullSnapshotComparer: ISnapshotComparer {
    public static let instance = NullSnapshotComparer()
    public init() {}
    public var backendId: String { "null" }
    public func compare(_ testId: String, actual: String) async throws -> SnapshotDiff {
        SnapshotDiff(equal: false, diff: "NullSnapshotComparer - no golden store wired.")
    }
}

public struct NullGoldenStore: IGoldenStore {
    public static let instance = NullGoldenStore()
    public init() {}
    public var backendId: String { "null" }
    public func read(_ testId: String) async throws -> String? { nil }
    public func write(_ testId: String, golden: String) async throws {}
}

public final class InMemoryGoldenStore: IGoldenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: String] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    private func stored(_ key: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return items[key]
    }

    public func read(_ testId: String) async throws -> String? {
        guard !testId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw TestingError.missingTestId
        }
        return stored(testId)
    }

    public func write(_ testId: String, golden: String) async throws {
        guard !testId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw TestingError.missingTestId
        }
        store(testId, golden)
    }

    private func store(_ key: String, _ value: String) {
        lock.lock(); defer { lock.unlock() }
        items[key] = value
    }
}

/// Line-by-line comparison against the golden copy.
public struct LineDiffSnapshotComparer: ISnapshotComparer {
    private let store: any IGoldenStore

    public init(store: any IGoldenStore) { self.store = store }

    public var backendId: String { "line-diff" }

    public func compare(_ testId: String, actual: String) async throws -> SnapshotDiff {
        guard !testId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw TestingError.missingTestId
        }
        guard let golden = try await store.read(testId) else {
            // Never seen before is NOT a pass. A missing golden has to be
            // written deliberately, or every new test passes on day one.
            return SnapshotDiff(equal: false, diff: "(no golden)")
        }
        let a = Self.normalise(actual)
        let g = Self.normalise(golden)
        return a == g ? SnapshotDiff(equal: true, diff: nil)
                      : SnapshotDiff(equal: false, diff: Self.buildDiff(expected: g, actual: a))
    }

    /// Line endings and trailing spaces are editor noise, not content. Without
    /// this every golden file fails the first time it crosses an OS.
    static func normalise(_ s: String) -> String {
        s.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n")
            .map { line -> String in
                var l = line
                while let last = l.last, last == " " || last == "\t" { l.removeLast() }
                return l
            }
            .joined(separator: "\n")
    }

    /// Only the lines that differ, expected then actual, so a failure message
    /// is readable when the document is a thousand lines long.
    static func buildDiff(expected: String, actual: String) -> String {
        let exp = expected.components(separatedBy: "\n")
        let act = actual.components(separatedBy: "\n")
        var out = ""
        for i in 0..<max(exp.count, act.count) {
            let e = i < exp.count ? exp[i] : ""
            let a = i < act.count ? act[i] : ""
            if e != a {
                out += "-\(e)\n"
                out += "+\(a)\n"
            }
        }
        return out
    }
}

/// Ids that are stable across runs and machines. FNV-1a over the UTF-16 code
/// units, which is what the C# iterates - a different unit gives a different id
/// and every golden file breaks.
public enum DeterministicIds {
    public static func fromSeed(_ seed: String, prefix: String = "test") throws -> String {
        guard !seed.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw TestingError.missingSeed
        }
        var h: UInt32 = 2_166_136_261
        for u in seed.utf16 {
            h ^= UInt32(u)
            h = h &* 16_777_619
        }
        return "\(prefix)-\(String(format: "%08x", h))"
    }
}

/// Time that only moves when a test moves it.
public final class FrozenClock: @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date

    public init(_ start: Date) { self.current = start }

    public var now: Date {
        lock.lock(); defer { lock.unlock() }
        return current
    }

    public func advance(by seconds: TimeInterval) {
        lock.lock(); current = current.addingTimeInterval(seconds); lock.unlock()
    }

    public func set(to instant: Date) {
        lock.lock(); current = instant; lock.unlock()
    }
}
