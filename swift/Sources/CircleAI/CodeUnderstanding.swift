// CodeUnderstanding.swift
//
// Port of src/CircleAI.CodeUnderstanding/:
//   • Contracts.cs                     — CodeSymbol, CodeMatch, SymbolEdge;
//                                         ICodeIndexer, ICodeSearch, ISymbolGraph
//   • InMemoryCodeUnderstanding.cs     — FilesystemCodeIndexer (regex decl pass
//                                         over .cs/.ts/.js/.py/.go), IndexBackedCodeSearch
//                                         (substring over indexed symbols),
//                                         InMemorySymbolGraph (adjacency list)
//   • NullImplementations.cs           — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable, Equatable, Codable`.
//   • `Directory.EnumerateFiles(..., AllDirectories)` → FileManager deep
//     enumerator, skipping obj/bin/node_modules path segments.
//   • The C# per-language `Regex` with lookbehind is reproduced with
//     `NSRegularExpression`; the C# code reads capture group 2 as the symbol
//     name — the Swift patterns are shaped so group 2 is the name too.
//   • Search / semantic-search: case-insensitive substring on symbol Name,
//     scored 1.0, `Take(topK)`. Semantic == substring (no embeddings), matching C#.
//   • Guards → `CodeUnderstandingError`.

import Foundation

// MARK: - Records

/// A declared symbol found during indexing.
public struct CodeSymbol: Sendable, Equatable, Codable {
    public let path: String
    public let line: Int
    public let name: String
    public let kind: String
    public init(path: String, line: Int, name: String, kind: String) {
        self.path = path
        self.line = line
        self.name = name
        self.kind = kind
    }
}

/// A code-search hit.
public struct CodeMatch: Sendable, Equatable, Codable {
    public let path: String
    public let line: Int
    public let snippet: String
    public let score: Float
    public init(path: String, line: Int, snippet: String, score: Float) {
        self.path = path
        self.line = line
        self.snippet = snippet
        self.score = score
    }
}

/// A directed edge between two symbols (caller → callee etc.).
public struct SymbolEdge: Sendable, Equatable, Codable {
    public let from: CodeSymbol
    public let to: CodeSymbol
    public let kind: String
    public init(from: CodeSymbol, to: CodeSymbol, kind: String) {
        self.from = from
        self.to = to
        self.kind = kind
    }
}

// MARK: - Errors

public enum CodeUnderstandingError: Error, Equatable, CustomStringConvertible {
    case repoPathRequired
    case directoryNotFound(String)
    case topKOutOfRange

    public var description: String {
        switch self {
        case .repoPathRequired: return "repoPath required"
        case .directoryNotFound(let p): return p
        case .topKOutOfRange: return "topK out of range"
        }
    }
}

// MARK: - Contracts

public protocol ICodeIndexer: Sendable {
    var backendId: String { get }
    func index(repoPath: String) async throws
    func countSymbols(repoPath: String) async throws -> Int
}

public protocol ICodeSearch: Sendable {
    var backendId: String { get }
    func search(query: String, topK: Int) async throws -> [CodeMatch]
    func semanticSearch(query: String, topK: Int) async throws -> [CodeMatch]
}

public protocol ISymbolGraph: Sendable {
    var backendId: String { get }
    func callersOf(_ s: CodeSymbol) async throws -> [SymbolEdge]
    func calleesOf(_ s: CodeSymbol) async throws -> [SymbolEdge]
}

// MARK: - Filesystem indexer

/// Regex-based declaration indexer over a repo's source tree.
public final class FilesystemCodeIndexer: ICodeIndexer, @unchecked Sendable {
    /// (extension, compiled regex, kind). Group 2 of each pattern is the symbol name.
    private struct LangRule {
        let ext: String
        let regex: NSRegularExpression
        let kind: String
    }

    private static let rules: [LangRule] = {
        func rx(_ pattern: String, _ opts: NSRegularExpression.Options = []) -> NSRegularExpression {
            // Force-try is safe: these are fixed, known-good patterns.
            return try! NSRegularExpression(pattern: pattern, options: opts)
        }
        // The C# patterns use a lookbehind so the match starts at the symbol name
        // and read `m.Groups[2].Value`. NSRegularExpression (ICU) rejects
        // variable-length lookbehind, so the lookbehind is folded into the match:
        // group 1 is the keyword, group 2 remains the symbol name — identical to
        // the names the C# extractor yields. (`\s+` widened from the C# `\s` where
        // the C# lookbehind used a single `\s`, since matching the whole construct
        // must span all intervening whitespace.)
        return [
            LangRule(ext: ".cs", regex: rx(#"\b(class|interface|record|enum|struct)\s+(\w+)"#), kind: "csharp"),
            LangRule(ext: ".cs", regex: rx(#"\b(public|private|internal|protected|static)\s+\w+\s+(\w+)\s*\("#), kind: "csharp-method"),
            LangRule(ext: ".ts", regex: rx(#"\b(class|interface|type|enum)\s+(\w+)"#), kind: "ts"),
            LangRule(ext: ".js", regex: rx(#"\b(class|function)\s+(\w+)"#), kind: "js"),
            LangRule(ext: ".py", regex: rx(#"^\s*(def|class)\s+(\w+)"#, [.anchorsMatchLines]), kind: "python"),
            // `func` captured as group 1 so the Go symbol name stays at group 2,
            // matching every other rule's group layout. The receiver clause is
            // non-capturing.
            LangRule(ext: ".go", regex: rx(#"^\s*(func)\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)\b"#, [.anchorsMatchLines]), kind: "go"),
        ]
    }()

    private let lock = NSLock()
    // repoPath → symbols. Package-internal read used by IndexBackedCodeSearch.
    var index: [String: [CodeSymbol]] = [:]

    public init() {}
    public var backendId: String { "filesystem" }

    /// Snapshot of every indexed symbol across all repos (lock-guarded).
    func allSymbols() -> [CodeSymbol] {
        lock.lock(); defer { lock.unlock() }
        return index.values.flatMap { $0 }
    }

    public func index(repoPath: String) async throws {
        if repoPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw CodeUnderstandingError.repoPathRequired
        }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: repoPath, isDirectory: &isDir), isDir.boolValue else {
            throw CodeUnderstandingError.directoryNotFound(repoPath)
        }

        var symbols: [CodeSymbol] = []
        for path in FilesystemCodeIndexer.enumerateSourceFiles(root: repoPath) {
            let ext = "." + (path as NSString).pathExtension.lowercased()
            guard let content = try? String(contentsOfFile: path, encoding: .utf8) else { continue }
            let lines = content.components(separatedBy: "\n")
            for (i, rawLine) in lines.enumerated() {
                let line = rawLine.hasSuffix("\r") ? String(rawLine.dropLast()) : rawLine
                for rule in FilesystemCodeIndexer.rules where rule.ext == ext {
                    let ns = line as NSString
                    let matches = rule.regex.matches(in: line, range: NSRange(location: 0, length: ns.length))
                    for m in matches where m.numberOfRanges >= 3 {
                        let g2 = m.range(at: 2)
                        if g2.location != NSNotFound {
                            let name = ns.substring(with: g2)
                            symbols.append(CodeSymbol(path: path, line: i + 1, name: name, kind: rule.kind))
                        }
                    }
                }
            }
        }
        lock.lock()
        index[repoPath] = symbols
        lock.unlock()
    }

    public func countSymbols(repoPath: String) async throws -> Int {
        lock.lock(); defer { lock.unlock() }
        return index[repoPath]?.count ?? 0
    }

    private static func enumerateSourceFiles(root: String) -> [String] {
        let exts: Set<String> = ["cs", "ts", "js", "py", "go"]
        let sep = "/"
        var out: [String] = []
        guard let en = FileManager.default.enumerator(atPath: root) else { return out }
        for case let rel as String in en {
            let ext = (rel as NSString).pathExtension.lowercased()
            guard exts.contains(ext) else { continue }
            // Normalise separators for the obj/bin/node_modules guards.
            let norm = rel.replacingOccurrences(of: "\\", with: "/")
            if norm.contains("\(sep)obj\(sep)") || norm.hasPrefix("obj\(sep)") { continue }
            if norm.contains("\(sep)bin\(sep)") || norm.hasPrefix("bin\(sep)") { continue }
            if norm.contains("\(sep)node_modules\(sep)") || norm.hasPrefix("node_modules\(sep)") { continue }
            out.append((root as NSString).appendingPathComponent(rel))
        }
        return out
    }
}

// MARK: - Index-backed search

/// Substring search over an indexer's symbols.
public final class IndexBackedCodeSearch: ICodeSearch, @unchecked Sendable {
    private let indexer: FilesystemCodeIndexer
    public init(indexer: FilesystemCodeIndexer) { self.indexer = indexer }
    public var backendId: String { "index-backed" }

    public func search(query: String, topK: Int = 10) async throws -> [CodeMatch] {
        if topK <= 0 { throw CodeUnderstandingError.topKOutOfRange }
        let hits = indexer.allSymbols()
            .filter { $0.name.range(of: query, options: .caseInsensitive) != nil }
            .prefix(topK)
            .map { CodeMatch(path: $0.path, line: $0.line, snippet: "\($0.kind) \($0.name)", score: 1.0) }
        return Array(hits)
    }

    public func semanticSearch(query: String, topK: Int = 10) async throws -> [CodeMatch] {
        try await search(query: query, topK: topK) // No real embedding; substring fallback.
    }
}

// MARK: - In-memory symbol graph

/// Host-populated adjacency list of symbol edges.
public final class InMemorySymbolGraph: ISymbolGraph, @unchecked Sendable {
    private let lock = NSLock()
    private var edges: [SymbolEdge] = []

    public init() {}
    public var backendId: String { "in-memory" }

    public func link(from: CodeSymbol, to: CodeSymbol, kind: String = "calls") {
        lock.lock(); defer { lock.unlock() }
        edges.append(SymbolEdge(from: from, to: to, kind: kind))
    }

    public func callersOf(_ s: CodeSymbol) async throws -> [SymbolEdge] {
        lock.lock(); defer { lock.unlock() }
        return edges.filter { $0.to.name == s.name }
    }

    public func calleesOf(_ s: CodeSymbol) async throws -> [SymbolEdge] {
        lock.lock(); defer { lock.unlock() }
        return edges.filter { $0.from.name == s.name }
    }
}

// MARK: - Null backends

public struct NullCodeIndexer: ICodeIndexer {
    public static let instance = NullCodeIndexer()
    public init() {}
    public var backendId: String { "null" }
    public func index(repoPath: String) async throws {}
    public func countSymbols(repoPath: String) async throws -> Int { 0 }
}

public struct NullCodeSearch: ICodeSearch {
    public static let instance = NullCodeSearch()
    public init() {}
    public var backendId: String { "null" }
    public func search(query: String, topK: Int = 10) async throws -> [CodeMatch] { [] }
    public func semanticSearch(query: String, topK: Int = 10) async throws -> [CodeMatch] { [] }
}

public struct NullSymbolGraph: ISymbolGraph {
    public static let instance = NullSymbolGraph()
    public init() {}
    public var backendId: String { "null" }
    public func callersOf(_ s: CodeSymbol) async throws -> [SymbolEdge] { [] }
    public func calleesOf(_ s: CodeSymbol) async throws -> [SymbolEdge] { [] }
}
