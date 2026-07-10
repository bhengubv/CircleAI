// HostingGenerativeUI.swift
//
// Port of the CircleAI.Hosting.GenerativeUI surface:
//   - GenerativeUI/IGenerativeUIRenderer.cs → UiComponent, UiCatalogEntry,
//                                             UiCatalogs.default, IGenerativeUIRenderer,
//                                             RecordingGenerativeUIRenderer
//   - GenerativeUI/JsonRenderParser.cs → JsonRenderParser (strict JSON→UiComponent)
//
// "AI emits JSON constrained to a typed catalog; host renders." The parser
// rejects unknown kinds and undeclared properties in strict mode.

import Foundation

// =====================================================================
// UiComponent + catalog
// =====================================================================

/// One UI element produced by a generative-UI model. Ported from `UiComponent`.
/// `properties` values are JSON-compatible (`String`, `Int64`, `Double`, `Bool`,
/// arrays, nested dictionaries, or nil).
public struct UiComponent: @unchecked Sendable {
    public let kind: String
    public let properties: [String: Any?]
    public let children: [UiComponent]?

    public init(kind: String, properties: [String: Any?], children: [UiComponent]? = nil) {
        self.kind = kind
        self.properties = properties
        self.children = children
    }
}

/// Catalog entry declaring an allowed kind + its properties. Ported from
/// `UiCatalogEntry`.
public struct UiCatalogEntry: Sendable, Equatable {
    public let kind: String
    public let description: String
    /// Property name → JSON Schema type string.
    public let allowedProperties: [String: String]
    public let allowsChildren: Bool

    public init(kind: String, description: String, allowedProperties: [String: String], allowsChildren: Bool = false) {
        self.kind = kind
        self.description = description
        self.allowedProperties = allowedProperties
        self.allowsChildren = allowsChildren
    }
}

/// Pre-canned component catalogs. Ported from `UiCatalogs`.
public enum UiCatalogs {
    /// Minimal "chat assistant tool output" catalog: card / list / button /
    /// textBlock / image. Mirrors `UiCatalogs.Default`.
    public static let `default`: [UiCatalogEntry] = [
        UiCatalogEntry(
            kind: "card",
            description: "A bordered container with a title and body. May contain children.",
            allowedProperties: ["title": "string", "caption": "string?"],
            allowsChildren: true),
        UiCatalogEntry(
            kind: "list",
            description: "An ordered or unordered list. Children are the list items.",
            allowedProperties: ["ordered": "boolean"],
            allowsChildren: true),
        UiCatalogEntry(
            kind: "button",
            description: "A tappable button. Emit an action identifier when clicked.",
            allowedProperties: ["label": "string", "action": "string", "style": "string?"]),
        UiCatalogEntry(
            kind: "textBlock",
            description: "Inline text content, optionally markdown.",
            allowedProperties: ["text": "string", "markdown": "boolean?"]),
        UiCatalogEntry(
            kind: "image",
            description: "An image displayed from a URL or data-URI.",
            allowedProperties: ["src": "string", "alt": "string?"]),
    ]
}

/// Renderer contract — consumers materialise `UiComponent` records into a native
/// UI. Ported from `IGenerativeUIRenderer`.
public protocol IGenerativeUIRenderer: AnyObject, Sendable {
    /// Render a single root component.
    func render(_ root: UiComponent) async
}

/// No-op renderer for tests and headless server scenarios. Holds the last
/// rendered component for assertion. Ported from `RecordingGenerativeUIRenderer`.
public final class RecordingGenerativeUIRenderer: IGenerativeUIRenderer, @unchecked Sendable {
    private let lock = NSLock()
    private var _lastRendered: UiComponent?
    private var _renderCount = 0

    public init() {}

    public var lastRendered: UiComponent? { lock.lock(); defer { lock.unlock() }; return _lastRendered }
    public var renderCount: Int { lock.lock(); defer { lock.unlock() }; return _renderCount }

    public func render(_ root: UiComponent) async {
        lock.lock(); _lastRendered = root; _renderCount += 1; lock.unlock()
    }
}

// =====================================================================
// JsonRenderParser
// =====================================================================

/// Errors raised while parsing a UI JSON tree. Mirrors the C#
/// `InvalidOperationException` / `ArgumentException` cases.
public enum JsonRenderError: Error, Equatable {
    case invalidJson(String)
    case notAnObject(String)
    case missingKind
    case unknownKind(String)
    case propertyNotAllowed(kind: String, property: String)
    case childrenNotAllowed(String)
}

/// Strict JSON → `UiComponent` parser. Rejects any kind not in the catalog and
/// any property not declared on its kind (in strict mode). Ported from
/// `JsonRenderParser`.
public enum JsonRenderParser {

    /// Parse one JSON document into a `UiComponent` tree.
    /// - Parameter strict: when true, unknown kinds throw; when false they become
    ///   a `textBlock` with the raw marker (matches the C# fallback).
    public static func parse(_ json: String, catalog: [UiCatalogEntry], strict: Bool = true) throws -> UiComponent {
        if json.isEmpty { throw JsonRenderError.invalidJson("json must not be empty") }
        guard let data = json.data(using: .utf8) else {
            throw JsonRenderError.invalidJson("json is not valid UTF-8")
        }
        let root: Any
        do {
            root = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        } catch {
            throw JsonRenderError.invalidJson(error.localizedDescription)
        }
        var index: [String: UiCatalogEntry] = [:]
        for c in catalog { index[c.kind.lowercased()] = c }
        return try parseElement(root, catalog: index, strict: strict)
    }

    private static func parseElement(_ el: Any, catalog: [String: UiCatalogEntry], strict: Bool) throws -> UiComponent {
        guard let obj = el as? [String: Any] else {
            throw JsonRenderError.notAnObject("Expected JSON object.")
        }

        guard let kind = obj["kind"] as? String, !kind.isEmpty else {
            throw JsonRenderError.missingKind
        }

        guard let entry = catalog[kind.lowercased()] else {
            if strict { throw JsonRenderError.unknownKind(kind) }
            return UiComponent(
                kind: "textBlock",
                properties: ["text": "[unknown kind '\(kind)']", "markdown": false])
        }

        var props: [String: Any?] = [:]
        if let propsObj = obj["properties"] as? [String: Any] {
            for (k, v) in propsObj {
                if strict && entry.allowedProperties[k] == nil {
                    throw JsonRenderError.propertyNotAllowed(kind: kind, property: k)
                }
                props[k] = toManaged(v)
            }
        }

        var children: [UiComponent]? = nil
        if let childArr = obj["children"] as? [Any] {
            if !entry.allowsChildren {
                if strict { throw JsonRenderError.childrenNotAllowed(kind) }
            } else {
                var list: [UiComponent] = []
                for c in childArr {
                    list.append(try parseElement(c, catalog: catalog, strict: strict))
                }
                children = list
            }
        }

        return UiComponent(kind: kind, properties: props, children: children)
    }

    /// Normalise a JSON value the way C# `ToManaged` does: numbers become Int64
    /// when integral, else Double; NSNull → nil; arrays/objects recurse.
    private static func toManaged(_ v: Any) -> Any? {
        if v is NSNull { return nil }
        if let n = v as? NSNumber {
            // Distinguish bool from numeric (NSNumber boxes both).
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return n.boolValue }
            let d = n.doubleValue
            if d == d.rounded() && abs(d) < 9.223372036854775e18 {
                return Int64(n.int64Value)
            }
            return d
        }
        if let s = v as? String { return s }
        if let b = v as? Bool { return b }
        if let arr = v as? [Any] { return arr.map { toManaged($0) } }
        if let obj = v as? [String: Any] {
            var out: [String: Any?] = [:]
            for (k, val) in obj { out[k] = toManaged(val) }
            return out
        }
        return nil
    }

    /// Build a system-prompt snippet describing the catalog to the model.
    /// Mirrors `DescribeCatalogForPrompt`.
    public static func describeCatalogForPrompt(_ catalog: [UiCatalogEntry]) -> String {
        var sb = ""
        sb += "You may respond with a single JSON object describing one UI component.\n"
        sb += "Allowed shape: { \"kind\": string, \"properties\": { ... }, \"children\"?: [ ... ] }\n"
        sb += "\n"
        sb += "Allowed kinds:\n"
        for e in catalog {
            sb += "- \(e.kind) — \(e.description)\n"
            // Sort property keys so the prompt is deterministic across runs
            // (Swift dictionaries have no stable iteration order).
            for name in e.allowedProperties.keys.sorted() {
                sb += "    - \(name): \(e.allowedProperties[name]!)\n"
            }
            if e.allowsChildren { sb += "    - children: array of components\n" }
        }
        return sb
    }
}
