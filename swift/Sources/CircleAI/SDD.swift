// SDD.swift
//
// Port of src/CircleAI.SDD/ (Spec-Driven Development, spec-kit pattern):
//   • Contracts.cs                 — Specification, SpecValidationResult,
//                                     ScaffoldedProject; ISpecificationStore,
//                                     ISpecificationValidator, ISpecToScaffold
//   • InMemorySDD.cs               — InMemorySpecificationStore,
//                                     JsonShapeSpecificationValidator (body+schema
//                                     JSON checks), HelloWorldSpecToScaffold
//                                     (minimal compilable C#/TS/Python project)
//   • NullImplementations.cs       — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable`. `ReadOnlyMemory<byte>` → `[UInt8]`.
//     ScaffoldedProject holds bytes → Sendable + Equatable only.
//   • `JsonDocument.Parse` → `JSONSerialization`; a JSON object with a top-level
//     "type" is a valid schema (matches the C# shape check).
//   • `NotSupportedException` for an unknown target language → `SDDError.languageNotSupported`.
//   • Guards → `SDDError`.

import Foundation

// MARK: - Records

/// A specification document (title + body + optional JSON schema + metadata).
public struct Specification: Sendable, Equatable, Codable {
    public let specId: String
    public let title: String
    public let body: String
    public let schema: String?
    public let metadata: [String: String]?
    public init(specId: String, title: String, body: String, schema: String?, metadata: [String: String]? = nil) {
        self.specId = specId
        self.title = title
        self.body = body
        self.schema = schema
        self.metadata = metadata
    }
}

/// The outcome of validating a specification.
public struct SpecValidationResult: Sendable, Equatable, Codable {
    public let isValid: Bool
    public let errors: [String]
    public init(isValid: Bool, errors: [String]) {
        self.isValid = isValid
        self.errors = errors
    }
}

/// A scaffolded project — a map of relative path → file bytes.
public struct ScaffoldedProject: Sendable, Equatable {
    public let projectId: String
    public let files: [String: [UInt8]]
    public init(projectId: String, files: [String: [UInt8]]) {
        self.projectId = projectId
        self.files = files
    }
}

// MARK: - Errors

public enum SDDError: Error, Equatable, CustomStringConvertible {
    case specIdRequired
    case idRequired
    case targetLanguageRequired
    case languageNotSupported(String)

    public var description: String {
        switch self {
        case .specIdRequired: return "SpecId required"
        case .idRequired: return "specId required"
        case .targetLanguageRequired: return "targetLanguage required"
        case .languageNotSupported(let l):
            return "Language '\(l)' is not supported by HelloWorldSpecToScaffold (csharp / typescript / python)."
        }
    }
}

// MARK: - Contracts

public protocol ISpecificationStore: Sendable {
    var backendId: String { get }
    func upsert(_ spec: Specification) async throws
    func get(specId: String) async throws -> Specification?
    func list() async throws -> [Specification]
}

public protocol ISpecificationValidator: Sendable {
    var backendId: String { get }
    func validate(_ spec: Specification) async throws -> SpecValidationResult
}

public protocol ISpecToScaffold: Sendable {
    var backendId: String { get }
    func scaffold(_ spec: Specification, targetLanguage: String) async throws -> ScaffoldedProject
}

// MARK: - In-memory store

/// Thread-safe in-memory specification store.
public final class InMemorySpecificationStore: ISpecificationStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: Specification] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func upsert(_ spec: Specification) async throws {
        if spec.specId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw SDDError.specIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        items[spec.specId] = spec
    }

    public func get(specId: String) async throws -> Specification? {
        if specId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw SDDError.idRequired
        }
        lock.lock(); defer { lock.unlock() }
        return items[specId]
    }

    public func list() async throws -> [Specification] {
        lock.lock(); defer { lock.unlock() }
        return Array(items.values)
    }
}

// MARK: - JSON-shape validator

/// Validates that title + body are present and, when a schema is supplied, that
/// it parses as a JSON object declaring a top-level "type".
public struct JsonShapeSpecificationValidator: ISpecificationValidator {
    public init() {}
    public var backendId: String { "json-shape" }

    public func validate(_ spec: Specification) async throws -> SpecValidationResult {
        var errors: [String] = []
        if spec.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { errors.append("Title is required.") }
        if spec.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { errors.append("Body is required.") }
        if let schema = spec.schema, !schema.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            if let data = schema.data(using: .utf8),
               let obj = try? JSONSerialization.jsonObject(with: data) {
                if let dict = obj as? [String: Any] {
                    if dict["type"] == nil { errors.append("Schema must declare a top-level 'type'.") }
                } else {
                    errors.append("Schema must be a JSON object.")
                }
            } else {
                errors.append("Schema is not valid JSON.")
            }
        }
        return SpecValidationResult(isValid: errors.isEmpty, errors: errors)
    }
}

// MARK: - Hello-world scaffolder

/// Turns a spec into a minimal compilable project for C#, TypeScript, or Python.
public struct HelloWorldSpecToScaffold: ISpecToScaffold {
    public init() {}
    public var backendId: String { "hello-world" }

    public func scaffold(_ spec: Specification, targetLanguage: String) async throws -> ScaffoldedProject {
        if targetLanguage.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw SDDError.targetLanguageRequired
        }
        var files: [String: [UInt8]] = [:]
        let lang = targetLanguage.lowercased()
        let name = HelloWorldSpecToScaffold.sanitizeName(spec.specId)
        let title = HelloWorldSpecToScaffold.escapeText(spec.title)
        let bodyEsc = HelloWorldSpecToScaffold.escapeText(spec.body)

        switch lang {
        case "csharp", "c#":
            files["Program.cs"] = bytes("Console.WriteLine(\"\(name): \(title)\");\n")
            files["\(name).csproj"] = bytes("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>\n</Project>\n")
            files["README.md"] = bytes("# \(title)\n\n\(bodyEsc)\n")
        case "typescript", "ts":
            files["index.ts"] = bytes("console.log(\"\(name): \(title)\");\n")
            files["package.json"] = bytes("{\"name\":\"\(name)\",\"version\":\"0.1.0\",\"main\":\"index.ts\",\"scripts\":{\"start\":\"ts-node index.ts\"}}\n")
            files["tsconfig.json"] = bytes("{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"commonjs\"}}\n")
            files["README.md"] = bytes("# \(title)\n\n\(bodyEsc)\n")
        case "python", "py":
            files["main.py"] = bytes("def main():\n    print(\"\(name): \(title)\")\n\nif __name__ == \"__main__\":\n    main()\n")
            files["pyproject.toml"] = bytes("[project]\nname = \"\(name)\"\nversion = \"0.1.0\"\nrequires-python = \">=3.10\"\n")
            files["README.md"] = bytes("# \(title)\n\n\(bodyEsc)\n")
        default:
            throw SDDError.languageNotSupported(targetLanguage)
        }

        return ScaffoldedProject(projectId: "\(name)-\(lang)", files: files)
    }

    private func bytes(_ s: String) -> [UInt8] { Array(s.utf8) }

    private static func sanitizeName(_ id: String) -> String {
        if id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "project" }
        let filtered = id.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) || $0 == "_" || $0 == "-" }
        let s = String(String.UnicodeScalarView(filtered))
        return s.isEmpty ? "project" : s
    }

    private static func escapeText(_ s: String) -> String {
        s.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n")
    }
}

// MARK: - Null backends

public struct NullSpecificationStore: ISpecificationStore {
    public static let instance = NullSpecificationStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ spec: Specification) async throws {}
    public func get(specId: String) async throws -> Specification? { nil }
    public func list() async throws -> [Specification] { [] }
}

public struct NullSpecificationValidator: ISpecificationValidator {
    public static let instance = NullSpecificationValidator()
    public init() {}
    public var backendId: String { "null" }
    public func validate(_ spec: Specification) async throws -> SpecValidationResult {
        SpecValidationResult(isValid: false, errors: ["No real validator wired."])
    }
}

public struct NullSpecToScaffold: ISpecToScaffold {
    public static let instance = NullSpecToScaffold()
    public init() {}
    public var backendId: String { "null" }
    public func scaffold(_ spec: Specification, targetLanguage: String) async throws -> ScaffoldedProject {
        ScaffoldedProject(projectId: "00000000-0000-0000-0000-000000000000", files: [:])
    }
}
