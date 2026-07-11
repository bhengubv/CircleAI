// DevTools.swift
//
// Port of src/CircleAI.DevTools/ — the Western-dev-tools replacement surface:
//   • Contracts.cs              — FileEdit, InlineSuggestion, AgentTurn, PatchPlan,
//                                 RefactorRequest; ICodeEditor, IInlineSuggester,
//                                 IAgentShell, IPatchPlanner, IRefactorTool
//   • InMemoryDevTools.cs       — FilesystemCodeEditor (offset-range edits),
//                                 TokenContextInlineSuggester (identifier-frequency
//                                 completion), InMemoryAgentShell (turn history +
//                                 built-in echo executor), PatternMatchPatchPlanner
//                                 (rename / remove-line / append goal parsing),
//                                 RegexRefactorTool (Rename + ExtractConstant)
//   • NullImplementations.cs    — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable, Equatable, Codable`.
//   • FileEdit ranges are UTF-16 code-unit offsets, matching C# `string.Length` /
//     `Match.Index`. The editor applies edits on the `String.utf16` view and the
//     planner emits offsets from `NSRegularExpression` (whose `NSRange.location`
//     is also UTF-16), so the two agree byte-for-byte with the C# behaviour.
//   • Regex `\bX\b` word-boundary rename is reproduced via NSRegularExpression.
//   • `Interlocked.Increment` → NSLock-guarded counter.
//   • Guards → `DevToolsError`.

import Foundation

// MARK: - Records

/// A single text edit: replace `[rangeStart, rangeEnd)` (UTF-16 offsets) in `path`.
public struct FileEdit: Sendable, Equatable, Codable {
    public let path: String
    public let rangeStart: Int
    public let rangeEnd: Int
    public let replacement: String
    public init(path: String, rangeStart: Int, rangeEnd: Int, replacement: String) {
        self.path = path
        self.rangeStart = rangeStart
        self.rangeEnd = rangeEnd
        self.replacement = replacement
    }
}

/// A ghost-text completion suggestion.
public struct InlineSuggestion: Sendable, Equatable, Codable {
    public let text: String
    public let confidence: Float
    public init(text: String, confidence: Float) {
        self.text = text
        self.confidence = confidence
    }
}

/// One agent-shell turn record.
public struct AgentTurn: Sendable, Equatable, Codable {
    public let turnId: String
    public let userPrompt: String
    public let response: String
    public let edits: [FileEdit]
    public init(turnId: String, userPrompt: String, response: String, edits: [FileEdit]) {
        self.turnId = turnId
        self.userPrompt = userPrompt
        self.response = response
        self.edits = edits
    }
}

/// A proposed multi-file patch plan.
public struct PatchPlan: Sendable, Equatable, Codable {
    public let goal: String
    public let steps: [String]
    public let proposedEdits: [FileEdit]
    public init(goal: String, steps: [String], proposedEdits: [FileEdit]) {
        self.goal = goal
        self.steps = steps
        self.proposedEdits = proposedEdits
    }
}

/// A cross-file refactor request.
public struct RefactorRequest: Sendable, Equatable, Codable {
    public let description: String
    public let targetPaths: [String]
    public init(description: String, targetPaths: [String]) {
        self.description = description
        self.targetPaths = targetPaths
    }
}

// MARK: - Errors

public enum DevToolsError: Error, Equatable, CustomStringConvertible {
    case pathRequired
    case goalRequired
    case invalidEditRange(String)
    case fileNotFound(String)
    case directoryNotFound(String)
    case limitOutOfRange

    public var description: String {
        switch self {
        case .pathRequired: return "path required"
        case .goalRequired: return "goal required"
        case .invalidEditRange(let s): return s
        case .fileNotFound(let p): return p
        case .directoryNotFound(let p): return p
        case .limitOutOfRange: return "limit out of range"
        }
    }
}

// MARK: - Contracts

public protocol ICodeEditor: Sendable {
    var backendId: String { get }
    func read(path: String) async throws -> String
    func apply(edits: [FileEdit]) async throws
    func save(path: String) async throws
}

public protocol IInlineSuggester: Sendable {
    var backendId: String { get }
    func suggest(path: String, line: Int, column: Int, contextBefore: String) async throws -> InlineSuggestion?
}

public protocol IAgentShell: Sendable {
    var backendId: String { get }
    func runTurn(userPrompt: String) async throws -> AgentTurn
    func history(limit: Int) async throws -> [AgentTurn]
}

public protocol IPatchPlanner: Sendable {
    var backendId: String { get }
    func plan(goal: String) async throws -> PatchPlan
    func apply(plan: PatchPlan) async throws
}

public protocol IRefactorTool: Sendable {
    var backendId: String { get }
    func propose(request: RefactorRequest) async throws -> [FileEdit]
}

// MARK: - UTF-16 offset helpers

private enum U16 {
    /// Applies edits (UTF-16 offsets) to `text`, honouring the C# semantics of
    /// removing then inserting, processed highest-offset-first per file.
    static func apply(_ text: String, _ edits: [FileEdit], path: String) throws -> String {
        var units = Array(text.utf16)
        // Descending by rangeStart, matching C# OrderByDescending(e => e.RangeStart).
        for e in edits.sorted(by: { $0.rangeStart > $1.rangeStart }) {
            if e.rangeStart < 0 || e.rangeEnd > units.count || e.rangeEnd < e.rangeStart {
                throw DevToolsError.invalidEditRange("Invalid edit range \(e.rangeStart)..\(e.rangeEnd) for \(e.path)")
            }
            let repl = Array(e.replacement.utf16)
            units.replaceSubrange(e.rangeStart..<e.rangeEnd, with: repl)
        }
        return String(decoding: units, as: UTF16.self)
    }
}

// MARK: - Filesystem editor

/// Applies offset-range edits directly to files on disk.
public struct FilesystemCodeEditor: ICodeEditor {
    public init() {}
    public var backendId: String { "filesystem" }

    public func read(path: String) async throws -> String {
        if path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DevToolsError.pathRequired }
        return try String(contentsOfFile: path, encoding: .utf8)
    }

    public func apply(edits: [FileEdit]) async throws {
        // Group by file path preserving first-seen order.
        var order: [String] = []
        var byFile: [String: [FileEdit]] = [:]
        for e in edits {
            if byFile[e.path] == nil { order.append(e.path) }
            byFile[e.path, default: []].append(e)
        }
        for path in order {
            let text = try String(contentsOfFile: path, encoding: .utf8)
            let updated = try U16.apply(text, byFile[path]!, path: path)
            try updated.write(toFile: path, atomically: true, encoding: .utf8)
        }
    }

    public func save(path: String) async throws {}
}

// MARK: - Inline suggester

/// Predicts the next token from the file's own identifier vocabulary.
public struct TokenContextInlineSuggester: IInlineSuggester {
    private static let identifierRx = try! NSRegularExpression(pattern: "[A-Za-z_][A-Za-z0-9_]*")

    public init() {}
    public var backendId: String { "token-context" }

    public func suggest(path: String, line: Int, column: Int, contextBefore: String) async throws -> InlineSuggestion? {
        if path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DevToolsError.pathRequired }

        let partial = TokenContextInlineSuggester.extractPartialAtCursor(contextBefore)
        if partial.count < 2 { return nil }

        let fileText: String
        if FileManager.default.fileExists(atPath: path), let t = try? String(contentsOfFile: path, encoding: .utf8) {
            fileText = t
        } else {
            fileText = contextBefore
        }

        var freq: [String: Int] = [:]
        let ns = fileText as NSString
        for m in TokenContextInlineSuggester.identifierRx.matches(in: fileText, range: NSRange(location: 0, length: ns.length)) {
            let value = ns.substring(with: m.range)
            if value.hasPrefix(partial) && value.count > partial.count {
                freq[value, default: 0] += 1
            }
        }
        if freq.isEmpty { return nil }
        // Highest frequency, then shortest identifier. Break further ties by name
        // so selection is deterministic (C# OrderByDescending/ThenBy is stable
        // over an ordered enumeration; a dictionary here is not, so add the key tiebreak).
        let best = freq.sorted { a, b in
            if a.value != b.value { return a.value > b.value }
            if a.key.count != b.key.count { return a.key.count < b.key.count }
            return a.key < b.key
        }.first!
        let completion = String(best.key.dropFirst(partial.count))
        let confidence = Float(min(1.0, Double(best.value) / 10.0))
        return InlineSuggestion(text: completion, confidence: confidence)
    }

    private static func extractPartialAtCursor(_ contextBefore: String) -> String {
        let chars = Array(contextBefore)
        var i = chars.count
        while i > 0, chars[i - 1].isLetter || chars[i - 1].isNumber || chars[i - 1] == "_" { i -= 1 }
        return String(chars[i...])
    }
}

// MARK: - Agent shell

/// Turn-history agent shell with a built-in echo executor.
public final class InMemoryAgentShell: IAgentShell, @unchecked Sendable {
    public typealias Executor = @Sendable (String) async throws -> AgentTurn

    private let executor: Executor
    private let lock = NSLock()
    private var historyList: [AgentTurn] = []
    private var seq: Int = 0

    public init(executor: Executor? = nil) {
        self.executor = executor ?? InMemoryAgentShell.builtInExecutor
    }

    public var backendId: String { "in-memory" }

    public func runTurn(userPrompt: String) async throws -> AgentTurn {
        let t = try await executor(userPrompt)
        lock.lock()
        seq += 1
        let id = seq
        lock.unlock()
        let turn = t.turnId.isEmpty
            ? AgentTurn(turnId: "turn-\(id)", userPrompt: t.userPrompt, response: t.response, edits: t.edits)
            : t
        lock.lock()
        historyList.append(turn)
        lock.unlock()
        return turn
    }

    public func history(limit: Int = 50) async throws -> [AgentTurn] {
        if limit <= 0 { throw DevToolsError.limitOutOfRange }
        lock.lock(); defer { lock.unlock() }
        // Reverse, take limit, reverse → the newest `limit` entries in chronological order.
        return Array(historyList.suffix(limit))
    }

    @Sendable
    private static func builtInExecutor(_ prompt: String) async throws -> AgentTurn {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        let response: String
        if trimmed.lowercased().hasPrefix("read ") {
            response = "Reading \(String(trimmed.dropFirst(5))) ..."
        } else if trimmed.lowercased().hasPrefix("write ") {
            response = "Writing \(String(trimmed.dropFirst(6))) ..."
        } else if trimmed.contains("?") {
            response = "Acknowledged the question; need more context to give a useful answer."
        } else {
            response = "Acknowledged: \(trimmed)."
        }
        return AgentTurn(turnId: "", userPrompt: prompt, response: response, edits: [])
    }
}

// MARK: - Patch planner

/// Parses goal text into real FileEdits: "rename X to Y [in scope]",
/// "remove line N from F", "append <text> to F".
public struct PatternMatchPatchPlanner: IPatchPlanner {
    private static let renameRx = try! NSRegularExpression(pattern: "^rename\\s+(\\S+)\\s+to\\s+(\\S+)(?:\\s+in\\s+(.+))?$", options: [.caseInsensitive])
    private static let removeRx = try! NSRegularExpression(pattern: "^remove\\s+line\\s+(\\d+)\\s+from\\s+(.+)$", options: [.caseInsensitive])
    private static let appendRx = try! NSRegularExpression(pattern: "^append\\s+(.+?)\\s+to\\s+(.+)$", options: [.caseInsensitive])

    private let editor: any ICodeEditor
    public init(editor: any ICodeEditor) { self.editor = editor }
    public var backendId: String { "pattern-match" }

    public func plan(goal: String) async throws -> PatchPlan {
        if goal.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw DevToolsError.goalRequired }
        let ns = goal as NSString
        let full = NSRange(location: 0, length: ns.length)

        if let m = PatternMatchPatchPlanner.renameRx.firstMatch(in: goal, range: full) {
            let oldName = ns.substring(with: m.range(at: 1))
            let newName = ns.substring(with: m.range(at: 2))
            let scope = m.range(at: 3).location != NSNotFound ? ns.substring(with: m.range(at: 3)) : FileManager.default.currentDirectoryPath
            let edits = try PatternMatchPatchPlanner.computeRenameEdits(scope: scope, oldName: oldName, newName: newName)
            return PatchPlan(goal: goal, steps: ["Rename '\(oldName)' -> '\(newName)' across \(edits.count) location(s)"], proposedEdits: edits)
        }
        if let m = PatternMatchPatchPlanner.removeRx.firstMatch(in: goal, range: full) {
            let lineNo = Int(ns.substring(with: m.range(at: 1))) ?? 0
            let path = ns.substring(with: m.range(at: 2)).trimmingCharacters(in: .whitespaces)
            let edits = try PatternMatchPatchPlanner.computeRemoveLineEdits(path: path, lineNo: lineNo)
            return PatchPlan(goal: goal, steps: ["Remove line \(lineNo) from \(path)"], proposedEdits: edits)
        }
        if let m = PatternMatchPatchPlanner.appendRx.firstMatch(in: goal, range: full) {
            var text = ns.substring(with: m.range(at: 1)).trimmingCharacters(in: .whitespaces)
            text = text.trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            let path = ns.substring(with: m.range(at: 2)).trimmingCharacters(in: .whitespaces)
            let len: Int
            if FileManager.default.fileExists(atPath: path), let content = try? String(contentsOfFile: path, encoding: .utf8) {
                len = content.utf16.count
            } else {
                len = 0
            }
            return PatchPlan(goal: goal, steps: ["Append to \(path)"], proposedEdits: [FileEdit(path: path, rangeStart: len, rangeEnd: len, replacement: text)])
        }
        return PatchPlan(goal: goal, steps: ["no recognised intent"], proposedEdits: [])
    }

    public func apply(plan: PatchPlan) async throws {
        try await editor.apply(edits: plan.proposedEdits)
    }

    private static func computeRenameEdits(scope: String, oldName: String, newName: String) throws -> [FileEdit] {
        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: scope, isDirectory: &isDir)
        if !exists { throw DevToolsError.directoryNotFound(scope) }

        let files: [String]
        if !isDir.boolValue {
            files = [scope]
        } else {
            var acc: [String] = []
            if let en = FileManager.default.enumerator(atPath: scope) {
                for case let rel as String in en where (rel as NSString).pathExtension.lowercased() == "cs" {
                    let norm = rel.replacingOccurrences(of: "\\", with: "/")
                    if norm.contains("/obj/") || norm.hasPrefix("obj/") { continue }
                    if norm.contains("/bin/") || norm.hasPrefix("bin/") { continue }
                    acc.append((scope as NSString).appendingPathComponent(rel))
                }
            }
            files = acc
        }

        let rx = try NSRegularExpression(pattern: "\\b\(NSRegularExpression.escapedPattern(for: oldName))\\b")
        var edits: [FileEdit] = []
        for f in files {
            guard let text = try? String(contentsOfFile: f, encoding: .utf8) else { continue }
            let ns = text as NSString
            for m in rx.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
                edits.append(FileEdit(path: f, rangeStart: m.range.location, rangeEnd: m.range.location + m.range.length, replacement: newName))
            }
        }
        return edits
    }

    private static func computeRemoveLineEdits(path: String, lineNo: Int) throws -> [FileEdit] {
        guard FileManager.default.fileExists(atPath: path) else { throw DevToolsError.fileNotFound(path) }
        let text = try String(contentsOfFile: path, encoding: .utf8)
        let units = Array(text.utf16)
        var current = 1
        let newline = UInt16(UnicodeScalar("\n").value)
        var i = 0
        while i < units.count {
            if current == lineNo {
                // Find end of this line (index just past the next '\n', or EOF).
                var end = i
                while end < units.count, units[end] != newline { end += 1 }
                let rangeEnd = end >= units.count ? units.count : end + 1
                return [FileEdit(path: path, rangeStart: i, rangeEnd: rangeEnd, replacement: "")]
            }
            if units[i] == newline { current += 1 }
            i += 1
        }
        return []
    }
}

// MARK: - Refactor tool

/// Real Rename + ExtractConstant primitives via regex.
public struct RegexRefactorTool: IRefactorTool {
    public init() {}
    public var backendId: String { "regex" }

    public func propose(request: RefactorRequest) async throws -> [FileEdit] {
        let description = request.description.trimmingCharacters(in: .whitespaces)
        if description.lowercased().hasPrefix("rename ") {
            let rx = try NSRegularExpression(pattern: "^rename\\s+(\\S+)\\s+to\\s+(\\S+)", options: [.caseInsensitive])
            let ns = description as NSString
            guard let m = rx.firstMatch(in: description, range: NSRange(location: 0, length: ns.length)) else { return [] }
            return RegexRefactorTool.renameInFiles(paths: request.targetPaths, oldName: ns.substring(with: m.range(at: 1)), newName: ns.substring(with: m.range(at: 2)))
        }
        if description.lowercased().hasPrefix("extract ") {
            let rx = try NSRegularExpression(pattern: "^extract\\s+constant\\s+from\\s+\"([^\"]+)\"\\s+as\\s+(\\S+)", options: [.caseInsensitive])
            let ns = description as NSString
            guard let m = rx.firstMatch(in: description, range: NSRange(location: 0, length: ns.length)) else { return [] }
            return RegexRefactorTool.extractConstant(paths: request.targetPaths, literal: ns.substring(with: m.range(at: 1)), constantName: ns.substring(with: m.range(at: 2)))
        }
        return []
    }

    private static func renameInFiles(paths: [String], oldName: String, newName: String) -> [FileEdit] {
        var edits: [FileEdit] = []
        guard let rx = try? NSRegularExpression(pattern: "\\b\(NSRegularExpression.escapedPattern(for: oldName))\\b") else { return [] }
        for p in paths {
            guard FileManager.default.fileExists(atPath: p), let text = try? String(contentsOfFile: p, encoding: .utf8) else { continue }
            let ns = text as NSString
            for m in rx.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
                edits.append(FileEdit(path: p, rangeStart: m.range.location, rangeEnd: m.range.location + m.range.length, replacement: newName))
            }
        }
        return edits
    }

    private static func extractConstant(paths: [String], literal: String, constantName: String) -> [FileEdit] {
        var edits: [FileEdit] = []
        let quoted = "\"" + literal + "\""
        for p in paths {
            guard FileManager.default.fileExists(atPath: p), let text = try? String(contentsOfFile: p, encoding: .utf8) else { continue }
            let ns = text as NSString
            let firstRange = ns.range(of: quoted)
            if firstRange.location == NSNotFound { continue }
            // Inject a private const at the top of the first class declaration.
            let classRange = ns.range(of: "class ")
            if classRange.location == NSNotFound { continue }
            let braceRange = ns.range(of: "{", options: [], range: NSRange(location: classRange.location, length: ns.length - classRange.location))
            if braceRange.location == NSNotFound { continue }
            let insertion = "\n    private const string \(constantName) = \(quoted);\n"
            edits.append(FileEdit(path: p, rangeStart: braceRange.location + 1, rangeEnd: braceRange.location + 1, replacement: insertion))
            // Replace every literal occurrence.
            var searchStart = firstRange.location
            while searchStart < ns.length {
                let r = ns.range(of: quoted, options: [], range: NSRange(location: searchStart, length: ns.length - searchStart))
                if r.location == NSNotFound { break }
                edits.append(FileEdit(path: p, rangeStart: r.location, rangeEnd: r.location + r.length, replacement: constantName))
                searchStart = r.location + 1
            }
        }
        return edits
    }
}

// MARK: - Null backends

public struct NullCodeEditor: ICodeEditor {
    public static let instance = NullCodeEditor()
    public init() {}
    public var backendId: String { "null" }
    public func read(path: String) async throws -> String { "" }
    public func apply(edits: [FileEdit]) async throws {}
    public func save(path: String) async throws {}
}

public struct NullInlineSuggester: IInlineSuggester {
    public static let instance = NullInlineSuggester()
    public init() {}
    public var backendId: String { "null" }
    public func suggest(path: String, line: Int, column: Int, contextBefore: String) async throws -> InlineSuggestion? { nil }
}

public struct NullAgentShell: IAgentShell {
    public static let instance = NullAgentShell()
    public init() {}
    public var backendId: String { "null" }
    public func runTurn(userPrompt: String) async throws -> AgentTurn {
        AgentTurn(turnId: "00000000-0000-0000-0000-000000000000", userPrompt: userPrompt, response: "", edits: [])
    }
    public func history(limit: Int = 50) async throws -> [AgentTurn] { [] }
}

public struct NullPatchPlanner: IPatchPlanner {
    public static let instance = NullPatchPlanner()
    public init() {}
    public var backendId: String { "null" }
    public func plan(goal: String) async throws -> PatchPlan { PatchPlan(goal: goal, steps: [], proposedEdits: []) }
    public func apply(plan: PatchPlan) async throws {}
}

public struct NullRefactorTool: IRefactorTool {
    public static let instance = NullRefactorTool()
    public init() {}
    public var backendId: String { "null" }
    public func propose(request: RefactorRequest) async throws -> [FileEdit] { [] }
}
