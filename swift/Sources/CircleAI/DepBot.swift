// DepBot.swift
//
// Port of src/CircleAI.DepBot/:
//   • Contracts.cs                 — Dependency, DependencyUpdate records;
//                                     IDependencyAnalyzer, IDependencyUpdater
//   • InMemoryDepBot.cs            — FilesystemDependencyAnalyzer (scans
//                                     package.json / requirements.txt /
//                                     Cargo.toml / *.csproj), TextRewriteDependencyUpdater
//                                     (proposes nothing without a registry;
//                                     ApplyUpdate rewrites manifest entries)
//   • NullImplementations.cs       — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable, Equatable, Codable`.
//   • `Directory.EnumerateFiles` → FileManager deep enumerator matching a
//     filename. `JsonDocument` → `JSONSerialization`. `Regex` → NSRegularExpression.
//   • `File.WriteAllText` / `File.WriteAllLines` → `String.write(toFile:)`.
//   • Malformed manifests are skipped (best-effort), matching the C# catch.
//   • Guards → `DepBotError`.

import Foundation

// MARK: - Records

/// A declared dependency discovered in a manifest.
public struct Dependency: Sendable, Equatable, Codable {
    public let ecosystem: String
    public let name: String
    public let currentVersion: String
    public let latestVersion: String?
    public init(ecosystem: String, name: String, currentVersion: String, latestVersion: String?) {
        self.ecosystem = ecosystem
        self.name = name
        self.currentVersion = currentVersion
        self.latestVersion = latestVersion
    }
}

/// A proposed or applied version bump for one dependency.
public struct DependencyUpdate: Sendable, Equatable, Codable {
    public let ecosystem: String
    public let name: String
    public let fromVersion: String
    public let toVersion: String
    public let isBreaking: Bool
    public init(ecosystem: String, name: String, fromVersion: String, toVersion: String, isBreaking: Bool) {
        self.ecosystem = ecosystem
        self.name = name
        self.fromVersion = fromVersion
        self.toVersion = toVersion
        self.isBreaking = isBreaking
    }
}

// MARK: - Errors

public enum DepBotError: Error, Equatable, CustomStringConvertible {
    case repoPathRequired
    case directoryNotFound(String)

    public var description: String {
        switch self {
        case .repoPathRequired: return "repoPath required"
        case .directoryNotFound(let p): return p
        }
    }
}

// MARK: - Contracts

public protocol IDependencyAnalyzer: Sendable {
    var backendId: String { get }
    func scan(repoPath: String) async throws -> [Dependency]
}

public protocol IDependencyUpdater: Sendable {
    var backendId: String { get }
    func proposeUpdates(repoPath: String) async throws -> [DependencyUpdate]
    func applyUpdate(repoPath: String, update: DependencyUpdate) async throws
}

// MARK: - Filesystem analyzer

/// Scans a repository for declared dependencies across npm / pypi / cargo / nuget.
public struct FilesystemDependencyAnalyzer: IDependencyAnalyzer {
    public init() {}
    public var backendId: String { "filesystem" }

    public func scan(repoPath: String) async throws -> [Dependency] {
        if repoPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DepBotError.repoPathRequired
        }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: repoPath, isDirectory: &isDir), isDir.boolValue else {
            throw DepBotError.directoryNotFound(repoPath)
        }

        var results: [Dependency] = []

        // npm / yarn — package.json
        for pkg in DepBotFiles.find(named: "package.json", under: repoPath) {
            if pkg.contains("node_modules") { continue }
            guard let data = FileManager.default.contents(atPath: pkg),
                  let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
            for key in ["dependencies", "devDependencies"] {
                guard let section = root[key] as? [String: Any] else { continue }
                for (name, value) in section {
                    results.append(Dependency(ecosystem: "npm", name: name, currentVersion: (value as? String) ?? "", latestVersion: nil))
                }
            }
        }

        // Python — requirements.txt
        let reqRx = try! NSRegularExpression(pattern: #"^([A-Za-z0-9_.\-]+)\s*([=<>!~]=?)?\s*([0-9.A-Za-z_\-]+)?"#)
        for req in DepBotFiles.find(named: "requirements.txt", under: repoPath) {
            guard let text = try? String(contentsOfFile: req, encoding: .utf8) else { continue }
            for rawLine in text.components(separatedBy: "\n") {
                let line = rawLine.trimmingCharacters(in: .whitespaces)
                if line.isEmpty || line.hasPrefix("#") { continue }
                let ns = line as NSString
                guard let m = reqRx.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)) else { continue }
                let name = DepBotFiles.group(m, 1, ns)
                let ver = DepBotFiles.group(m, 3, ns)
                results.append(Dependency(ecosystem: "pypi", name: name, currentVersion: ver, latestVersion: nil))
            }
        }

        // Rust — Cargo.toml [dependencies]
        let cargoRx = try! NSRegularExpression(pattern: #"^([A-Za-z0-9_\-]+)\s*=\s*"([^"]+)""#)
        for toml in DepBotFiles.find(named: "Cargo.toml", under: repoPath) {
            if toml.contains("target") { continue }
            guard let text = try? String(contentsOfFile: toml, encoding: .utf8) else { continue }
            var inDeps = false
            for rawLine in text.components(separatedBy: "\n") {
                let line = rawLine.trimmingCharacters(in: .whitespaces)
                if line.hasPrefix("[") {
                    inDeps = line.caseInsensitiveCompare("[dependencies]") == .orderedSame
                    continue
                }
                if !inDeps || line.isEmpty || line.hasPrefix("#") { continue }
                let ns = line as NSString
                guard let m = cargoRx.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)) else { continue }
                results.append(Dependency(ecosystem: "cargo", name: DepBotFiles.group(m, 1, ns), currentVersion: DepBotFiles.group(m, 2, ns), latestVersion: nil))
            }
        }

        // .NET — *.csproj <PackageReference Include="X" Version="Y" />
        let csprojRx = try! NSRegularExpression(pattern: #"<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)""#)
        for csproj in DepBotFiles.find(extension: "csproj", under: repoPath) {
            guard let text = try? String(contentsOfFile: csproj, encoding: .utf8) else { continue }
            let ns = text as NSString
            let matches = csprojRx.matches(in: text, range: NSRange(location: 0, length: ns.length))
            for m in matches {
                results.append(Dependency(ecosystem: "nuget", name: DepBotFiles.group(m, 1, ns), currentVersion: DepBotFiles.group(m, 2, ns), latestVersion: nil))
            }
        }

        return results
    }
}

// MARK: - Text-rewrite updater

/// Proposes nothing without a registry (no invented LatestVersion); ApplyUpdate
/// rewrites manifest entries in place per ecosystem.
public struct TextRewriteDependencyUpdater: IDependencyUpdater {
    public init() {}
    public var backendId: String { "text-rewrite" }

    public func proposeUpdates(repoPath: String) async throws -> [DependencyUpdate] {
        if repoPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DepBotError.repoPathRequired
        }
        return []
    }

    public func applyUpdate(repoPath: String, update: DependencyUpdate) async throws {
        if repoPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw DepBotError.repoPathRequired
        }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: repoPath, isDirectory: &isDir), isDir.boolValue else {
            throw DepBotError.directoryNotFound(repoPath)
        }

        switch update.ecosystem.lowercased() {
        case "nuget":
            let pattern = #"<PackageReference\s+Include="\#(DepBotFiles.escape(update.name))"\s+Version="[^"]+""#
            let replacement = "<PackageReference Include=\"\(update.name)\" Version=\"\(update.toVersion)\""
            let rx = try! NSRegularExpression(pattern: pattern)
            for csproj in DepBotFiles.find(extension: "csproj", under: repoPath) {
                guard let text = try? String(contentsOfFile: csproj, encoding: .utf8) else { continue }
                let ns = text as NSString
                let updated = rx.stringByReplacingMatches(in: text, range: NSRange(location: 0, length: ns.length), withTemplate: NSRegularExpression.escapedTemplate(for: replacement))
                if updated != text { try? updated.write(toFile: csproj, atomically: true, encoding: .utf8) }
            }

        case "npm":
            let pattern = #""\#(DepBotFiles.escape(update.name))"\s*:\s*"[^"]+""#
            let replacement = "\"\(update.name)\": \"\(update.toVersion)\""
            let rx = try! NSRegularExpression(pattern: pattern)
            for pkg in DepBotFiles.find(named: "package.json", under: repoPath) {
                if pkg.contains("node_modules") { continue }
                guard let text = try? String(contentsOfFile: pkg, encoding: .utf8) else { continue }
                let ns = text as NSString
                let updated = rx.stringByReplacingMatches(in: text, range: NSRange(location: 0, length: ns.length), withTemplate: NSRegularExpression.escapedTemplate(for: replacement))
                try? updated.write(toFile: pkg, atomically: true, encoding: .utf8)
            }

        case "pypi":
            let pattern = #"^\#(DepBotFiles.escape(update.name))\s*[=<>!~]=?\s*[0-9.A-Za-z_\-]+"#
            let rx = try! NSRegularExpression(pattern: pattern)
            for req in DepBotFiles.find(named: "requirements.txt", under: repoPath) {
                guard let text = try? String(contentsOfFile: req, encoding: .utf8) else { continue }
                var lines = text.components(separatedBy: "\n")
                for i in 0..<lines.count {
                    let line = lines[i].trimmingCharacters(in: .whitespaces)
                    if line.hasPrefix("#") || line.isEmpty { continue }
                    let ns = line as NSString
                    if rx.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)) != nil {
                        lines[i] = "\(update.name)==\(update.toVersion)"
                    }
                }
                try? lines.joined(separator: "\n").write(toFile: req, atomically: true, encoding: .utf8)
            }

        default:
            break
        }
    }
}

// MARK: - Filesystem helpers

enum DepBotFiles {
    /// Deep-enumerate files with an exact basename under `root`.
    static func find(named name: String, under root: String) -> [String] {
        var out: [String] = []
        guard let en = FileManager.default.enumerator(atPath: root) else { return out }
        for case let rel as String in en where (rel as NSString).lastPathComponent == name {
            out.append((root as NSString).appendingPathComponent(rel))
        }
        return out
    }

    /// Deep-enumerate files with a given extension under `root`.
    static func find(extension ext: String, under root: String) -> [String] {
        var out: [String] = []
        guard let en = FileManager.default.enumerator(atPath: root) else { return out }
        for case let rel as String in en where (rel as NSString).pathExtension.lowercased() == ext.lowercased() {
            out.append((root as NSString).appendingPathComponent(rel))
        }
        return out
    }

    static func group(_ m: NSTextCheckingResult, _ i: Int, _ ns: NSString) -> String {
        guard i < m.numberOfRanges else { return "" }
        let r = m.range(at: i)
        return r.location == NSNotFound ? "" : ns.substring(with: r)
    }

    /// Regex-escape a literal for embedding in a pattern (mirrors Regex.Escape).
    static func escape(_ s: String) -> String {
        NSRegularExpression.escapedPattern(for: s)
    }
}

// MARK: - Null backends

public struct NullDependencyAnalyzer: IDependencyAnalyzer {
    public static let instance = NullDependencyAnalyzer()
    public init() {}
    public var backendId: String { "null" }
    public func scan(repoPath: String) async throws -> [Dependency] { [] }
}

public struct NullDependencyUpdater: IDependencyUpdater {
    public static let instance = NullDependencyUpdater()
    public init() {}
    public var backendId: String { "null" }
    public func proposeUpdates(repoPath: String) async throws -> [DependencyUpdate] { [] }
    public func applyUpdate(repoPath: String, update: DependencyUpdate) async throws {}
}
