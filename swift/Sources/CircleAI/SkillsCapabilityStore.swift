// SkillsCapabilityStore.swift
//
// What the assistant can actually do, read from the capability manifest.
//
// THE POINT IS HONESTY, NOT MARKETING. The skill store existed and the service
// already injected skill context from it — nothing ever populated it. Filling
// it from the manifest means the assistant answers "can you do voice?" from the
// repo rather than from optimism.
//
// Every entry carries its STATUS, and non-shipping entries say so in words the
// model cannot miss. A capability catalogue that let the assistant claim planned
// features would be a machine for confident lying — which is precisely the
// failure this file exists to end. "Not yet" is the required answer, not an
// enthusiastic yes.
//
// Ported from src/CircleAI.Skills/CapabilityManifestSkillStore.cs.

import Foundation

public enum SkillStoreError: Error, Equatable, CustomStringConvertible {
    case manifestIsReadOnly

    public var description: String {
        "Capabilities come from the capability manifest and are verified against the "
        + "repository. Editing them at runtime would let the assistant claim things "
        + "the code cannot back up. Change the manifest instead."
    }
}

public struct CapabilityManifestSkillStore: ISkillStore, Sendable {

    private let skills: [SkillDetail]

    /// Parses a manifest. A manifest that will not parse yields an EMPTY store
    /// rather than throwing: missing self-knowledge must never stop the
    /// assistant answering ordinary questions.
    public init(manifestJson: String) {
        self.skills = Self.parse(manifestJson)
    }

    public init(skills: [SkillDetail]) {
        self.skills = skills
    }

    /// Empty. There is no embedded manifest in the Swift package (see
    /// EmbeddedVoiceConfigs for why resources are discovered rather than
    /// embedded), so a host passes its own.
    public static let empty = CapabilityManifestSkillStore(skills: [])

    public func list() async -> [SkillSummary] {
        skills.map(Self.summary)
    }

    public func get(_ id: String) async -> SkillDetail? {
        skills.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    public func search(_ query: String) async -> [SkillSummary] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return [] }

        // ID FIRST: it is the handle the compact listing hands out, so a lookup
        // by id has to resolve. Kept identical to the other stores.
        func hit(_ s: SkillDetail) -> Bool {
            s.id.localizedCaseInsensitiveContains(q)
                || s.name.localizedCaseInsensitiveContains(q)
                || s.description.localizedCaseInsensitiveContains(q)
                || s.tags.contains { $0.localizedCaseInsensitiveContains(q) }
        }
        return skills.filter(hit).map(Self.summary)
    }

    /// REFUSED, both of them. If the assistant could edit its own capability
    /// list at runtime it could write itself a capability it does not have, and
    /// then cite it.
    public func upsert(_ id: String?, draft: SkillDraft) async throws -> SkillDetail {
        throw SkillStoreError.manifestIsReadOnly
    }

    public func delete(_ id: String) async throws {
        throw SkillStoreError.manifestIsReadOnly
    }

    // MARK: - Parsing

    static func parse(_ json: String) -> [SkillDetail] {
        guard !json.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let root = try? JSONSerialization.jsonObject(with: Data(json.utf8))
                as? [String: Any],
              let caps = root["Capabilities"] as? [[String: Any]]
        else { return [] }

        return caps.compactMap { c in
            guard let id = c["Id"] as? String, !id.isEmpty,
                  let name = c["Name"] as? String, !name.isEmpty
            else { return nil }

            let status = c["Status"] as? String ?? "unknown"
            let summary = c["Summary"] as? String ?? ""

            return SkillDetail(
                id: id,
                name: name,
                // The status leads the DESCRIPTION too, not just the
                // instructions: a compact listing shows descriptions only, and
                // that is where an unqualified claim would slip through.
                description: "[\(status)] \(summary)",
                instructions: instructions(for: c, status: status, summary: summary),
                tags: tags(for: c, status: status),
                source: .inMemory,
                lastModified: Date(timeIntervalSince1970: 0))
        }
    }

    /// The instruction text the model actually reads.
    ///
    /// Written at the model, in the imperative, because a status word alone is
    /// not an instruction — "scaffold" means nothing to a model that has never
    /// seen this repo, and it will helpfully assume the feature works.
    static func instructions(for c: [String: Any], status: String, summary: String) -> String {
        var out = "Status: \(status)\n"

        switch status {
        case "shipping":
            out += "This works and is covered by tests. You may state it plainly.\n"
        case "partial":
            out += "This works WITH LIMITS. State the limits when they are relevant; "
                 + "do not oversell it.\n"
        case "scaffold":
            out += "NOT USABLE YET — contracts exist but there is no working "
                 + "implementation. Do NOT claim you can do this.\n"
        case "planned":
            out += "DOES NOT EXIST YET. Do NOT claim you can do this. Say it is planned.\n"
        case "rejected":
            out += "DELIBERATELY NOT BUILT. Do NOT claim you can do this, and do not "
                 + "offer to add it.\n"
        default:
            break
        }

        if !summary.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            out += "\n\(summary)\n"
        }

        out += list(c, key: "Requires", heading: "Requires:")
        out += list(c, key: "Limits", heading: "Limits:")

        // A MEASURED result is the strongest thing here — it names the device
        // and the date, so a claim can be checked rather than believed.
        if let m = c["Measured"] as? [String: Any] {
            let device = m["Device"] as? String ?? "an unnamed device"
            let date = m["Date"] as? String ?? "an unrecorded date"
            let result = m["Result"] as? String ?? ""
            out += "\nMeasured on \(device) (\(date)): \(result)\n"
        }

        return out.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func list(_ c: [String: Any], key: String, heading: String) -> String {
        guard let items = c[key] as? [Any], !items.isEmpty else { return "" }
        var out = "\n\(heading)\n"
        for item in items {
            if let s = item as? String { out += " - \(s)\n" }
        }
        return out
    }

    /// Status first, then the id's own segments, then the package.
    ///
    /// Status as a TAG is what makes "what can you not do yet" a searchable
    /// question rather than one the model has to reason its way to.
    static func tags(for c: [String: Any], status: String) -> [String] {
        var tags = [status]
        if let id = c["Id"] as? String {
            tags.append(contentsOf: id.split(separator: ".").map(String.init))
        }
        if let pkg = c["Package"] as? String,
           !pkg.trimmingCharacters(in: .whitespaces).isEmpty, pkg != "(none)" {
            tags.append(pkg)
        }

        var seen = Set<String>()
        return tags.filter { seen.insert($0.lowercased()).inserted }
    }

    static func summary(_ s: SkillDetail) -> SkillSummary {
        SkillSummary(id: s.id, name: s.name, description: s.description,
                     tags: s.tags, source: s.source)
    }
}
