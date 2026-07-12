// WorkflowsPaca.swift
//
// Port of the PACA clusters in CircleAI.Workflows that are NOT already in
// Workflows.swift (which holds Contracts + NullImplementations +
// PacaConversations). Ported here:
//   • PacaProjects.cs — PacaProject, PacaTask, InMemoryPacaStore
//   • PacaBoards.cs   — SprintState, StatusColumn, PacaSprint,
//                        TaskBoardMetadata, BoardView, PacaBoard
//   • PacaAuth.cs     — JwtPair, JwtPayload, HmacJwtAuthenticator,
//                        PacaApiKeyRecord, PacaApiKeyAuthenticator
//   • PacaAgents.cs   — MemberKind, ProjectMember, AgentLlmConfig,
//                        AgentSystemPrompts, AgentCapabilities, AgentLimits,
//                        AgentGitIdentity, AgentTriggers, AgentProfile,
//                        AgentTemplates, InMemoryPacaMemberStore
//   • PacaDocs.cs     — DocNode, DocVersion, DocActivity, DocLink, PacaDocService
//   • PacaPlugins.cs  — PluginExtensionPoint, PluginManifest,
//                        PluginResourceLimits, InstalledPlugin,
//                        IPluginRuntimeHost, PacaPluginRegistry
//   • PacaMcp.cs       — McpTransportKind, AgentMcpConfig, PacaMcpTool,
//                        PacaMcpHandler, PacaMcpServer, PacaCoreMcpTools
//   • PacaRealtime.cs — RealtimePacaEvent (enum), IRealtimeBroadcaster,
//                        PermissionCheck, PacaRealtimeHub, QueryInvalidation
//   • PacaSkills.cs   — PacaSkill, PacaSkillLibrary, SkillTemplates,
//                        PacaSkillInstaller
//   • PacaDeploy.cs   — PacaDeployMode, PacaDeployOverrides, PacaDeployArtifact,
//                        PacaDeployer
//
// Porting notes:
//   • `Guid` → `UUID`; `Guid.NewGuid().ToString("n")` → `UUID().uuidString`
//     lower-cased with hyphens removed; `DateTimeOffset` → `Date`;
//     `TimeSpan` → `TimeInterval`; `Uri?` → `String?` (URL text is opaque here).
//   • C# records with `with { … }` → structs with focused `with…` copy helpers.
//   • `ConcurrentDictionary` + per-list `lock` → a single `NSLock` guarding the
//     mutable dictionaries in each `final class @unchecked Sendable`.
//   • `RealtimePacaEvent` is an abstract record base with concrete subtypes and
//     a downstream `switch ev` in `QueryInvalidation`; Swift has no abstract
//     records, so it is modelled as an `enum` with associated payload cases,
//     exposing `projectId` / `at` computed properties so the hub can route it.
//   • `PacaSkillInstaller` uses `FileManager` for the ~/.claude/commands writes.
//   • `PacaPluginRegistry` semver parse/compare is a small numeric-component
//     comparator (matches `System.Version` for the dotted-numeric subset paca
//     uses, after stripping any `-pre` / `+build` suffix).
//   • Regexes use `NSRegularExpression`.

import Foundation
import CryptoKit

// MARK: ── Crypto helper ───────────────────────────────────────────────────

/// Minimal CryptoKit-backed hashing helper for the PACA auth primitives
/// (HMAC-SHA256 JWT signing + SHA-256 API-key hashing). Kept file-local so it
/// does not collide with the wider tree's crypto surfaces.
enum PacaCrypto {
    /// HMAC-SHA256 over `message` keyed with `key`.
    static func hmacSha256(key: Data, message: Data) -> Data {
        let mac = HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key))
        return Data(mac)
    }

    /// SHA-256 digest of `data`.
    static func sha256(_ data: Data) -> Data {
        Data(SHA256.hash(data: data))
    }
}

// MARK: ── PacaProjects ────────────────────────────────────────────────────

/// A workspace that contains tasks. (C# `PacaProject`.)
public struct PacaProject: Sendable, Equatable, Codable {
    public let id: String
    public let name: String
    public let prefix: String
    public let settingsJson: String
    public let createdAtUtc: Date
    public let deletedAtUtc: Date?

    public init(id: String, name: String, prefix: String, settingsJson: String,
                createdAtUtc: Date, deletedAtUtc: Date?) {
        self.id = id; self.name = name; self.prefix = prefix
        self.settingsJson = settingsJson; self.createdAtUtc = createdAtUtc; self.deletedAtUtc = deletedAtUtc
    }

    func with(settingsJson: String? = nil, deletedAtUtc: Date?? = nil) -> PacaProject {
        PacaProject(id: id, name: name, prefix: prefix,
                    settingsJson: settingsJson ?? self.settingsJson,
                    createdAtUtc: createdAtUtc,
                    deletedAtUtc: deletedAtUtc ?? self.deletedAtUtc)
    }
}

/// A unit of work inside a project. (C# `PacaTask`.)
public struct PacaTask: Sendable, Equatable, Codable {
    public let projectId: String
    public let number: Int
    public let title: String
    public let descriptionJson: String
    public let status: String
    public let createdAtUtc: Date
    public let deletedAtUtc: Date?

    public init(projectId: String, number: Int, title: String, descriptionJson: String,
                status: String, createdAtUtc: Date, deletedAtUtc: Date?) {
        self.projectId = projectId; self.number = number; self.title = title
        self.descriptionJson = descriptionJson; self.status = status
        self.createdAtUtc = createdAtUtc; self.deletedAtUtc = deletedAtUtc
    }

    /// e.g. "PACA-3". (C# `Reference`.)
    public func reference(_ prefix: String) -> String { "\(prefix)-\(number)" }

    func with(title: String? = nil, descriptionJson: String? = nil, status: String? = nil,
              deletedAtUtc: Date?? = nil) -> PacaTask {
        PacaTask(projectId: projectId, number: number,
                 title: title ?? self.title,
                 descriptionJson: descriptionJson ?? self.descriptionJson,
                 status: status ?? self.status, createdAtUtc: createdAtUtc,
                 deletedAtUtc: deletedAtUtc ?? self.deletedAtUtc)
    }
}

/// Errors raised by the PACA stores.
public enum PacaError: Error, Equatable, CustomStringConvertible {
    case argument(String)
    case invalidOperation(String)

    public var description: String {
        switch self {
        case .argument(let m): return m
        case .invalidOperation(let m): return m
        }
    }
}

/// In-memory project + task store. Auto-numbers tasks; soft-deletes via
/// `deletedAtUtc`. (C# `InMemoryPacaStore`.)
public final class InMemoryPacaStore: @unchecked Sendable {
    private let lock = NSLock()
    private var projects: [String: PacaProject] = [:]
    private var tasksByProject: [String: [PacaTask]] = [:]
    private var nextNumber: [String: Int] = [:]
    private let clock: @Sendable () -> Date

    public init(clock: (@Sendable () -> Date)? = nil) {
        self.clock = clock ?? { Date() }
    }

    /// Create a new project. Throws if the id already exists.
    @discardableResult
    public func createProject(id: String, name: String, prefix: String, settingsJson: String? = nil) throws -> PacaProject {
        guard !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("id required") }
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("name required") }
        guard !prefix.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("prefix required") }

        let project = PacaProject(id: id, name: name, prefix: prefix,
                                  settingsJson: settingsJson ?? "{}", createdAtUtc: clock(), deletedAtUtc: nil)
        lock.lock()
        if projects[id] != nil { lock.unlock(); throw PacaError.invalidOperation("Project '\(id)' already exists.") }
        projects[id] = project
        tasksByProject[id] = []
        nextNumber[id] = 1
        lock.unlock()
        return project
    }

    /// Get a live project by id (excludes soft-deleted).
    public func getProject(_ id: String) -> PacaProject? {
        lock.lock(); defer { lock.unlock() }
        if let p = projects[id], p.deletedAtUtc == nil { return p }
        return nil
    }

    /// Soft-delete a project. Idempotent.
    public func deleteProject(_ id: String) {
        lock.lock()
        if let existing = projects[id], existing.deletedAtUtc == nil {
            projects[id] = existing.with(deletedAtUtc: .some(clock()))
        }
        lock.unlock()
    }

    /// Update the JSON settings bag on a project.
    @discardableResult
    public func updateProjectSettings(_ projectId: String, newSettingsJson: String) throws -> PacaProject {
        lock.lock()
        guard let existing = projects[projectId], existing.deletedAtUtc == nil else {
            lock.unlock(); throw PacaError.invalidOperation("Project '\(projectId)' not found.")
        }
        let updated = existing.with(settingsJson: newSettingsJson)
        projects[projectId] = updated
        lock.unlock()
        return updated
    }

    /// Add a task to a project. Auto-numbers it.
    @discardableResult
    public func addTask(_ projectId: String, title: String, descriptionJson: String? = nil, status: String = "todo") throws -> PacaTask {
        lock.lock()
        guard let project = projects[projectId], project.deletedAtUtc == nil else {
            lock.unlock(); throw PacaError.invalidOperation("Project '\(projectId)' not found.")
        }
        let number = nextNumber[projectId] ?? 1
        nextNumber[projectId] = number + 1
        let task = PacaTask(projectId: projectId, number: number, title: title,
                            descriptionJson: descriptionJson ?? "{}", status: status,
                            createdAtUtc: clock(), deletedAtUtc: nil)
        tasksByProject[projectId, default: []].append(task)
        lock.unlock()
        return task
    }

    /// List live tasks for a project, ordered by number ascending.
    public func listTasks(_ projectId: String) -> [PacaTask] {
        lock.lock(); defer { lock.unlock() }
        guard let list = tasksByProject[projectId] else { return [] }
        return list.filter { $0.deletedAtUtc == nil }.sorted { $0.number < $1.number }
    }

    /// Find one task by reference like "PACA-3".
    public func getTaskByReference(_ projectId: String, reference: String) -> PacaTask? {
        lock.lock(); defer { lock.unlock() }
        guard let project = projects[projectId], project.deletedAtUtc == nil else { return nil }
        let expectedPrefix = project.prefix + "-"
        guard reference.lowercased().hasPrefix(expectedPrefix.lowercased()) else { return nil }
        let numberPart = String(reference.dropFirst(expectedPrefix.count))
        guard let n = Int(numberPart), let list = tasksByProject[projectId] else { return nil }
        return list.first { $0.number == n && $0.deletedAtUtc == nil }
    }

    /// Update a task in place (matched by projectId + number).
    public func updateTask(_ updated: PacaTask) {
        lock.lock()
        if var list = tasksByProject[updated.projectId] {
            for i in 0..<list.count where list[i].number == updated.number {
                list[i] = updated
                tasksByProject[updated.projectId] = list
                break
            }
        }
        lock.unlock()
    }

    /// Soft-delete a task.
    public func deleteTask(_ projectId: String, number: Int) {
        lock.lock()
        if var list = tasksByProject[projectId] {
            for i in 0..<list.count where list[i].number == number {
                list[i] = list[i].with(deletedAtUtc: .some(clock()))
                tasksByProject[projectId] = list
                break
            }
        }
        lock.unlock()
    }
}

// MARK: ── PacaBoards ──────────────────────────────────────────────────────

/// Sprint lifecycle. (C# `SprintState`.)
public enum SprintState: Int, Sendable, Codable, CaseIterable {
    case planning = 0
    case active = 1
    case completed = 2
}

/// Status column in the workflow. (C# `StatusColumn`.)
public struct StatusColumn: Sendable, Equatable, Codable {
    public let name: String
    public let category: String
    public let position: Int
    public let collapsed: Bool

    public init(name: String, category: String, position: Int, collapsed: Bool) {
        self.name = name; self.category = category; self.position = position; self.collapsed = collapsed
    }

    func with(collapsed: Bool) -> StatusColumn {
        StatusColumn(name: name, category: category, position: position, collapsed: collapsed)
    }
}

/// Sprint. (C# `PacaSprint`.)
public struct PacaSprint: Sendable, Equatable, Codable {
    public let id: String
    public let projectId: String
    public let name: String
    public let goal: String
    public let startDate: Date
    public let endDate: Date
    public let state: SprintState

    public init(id: String, projectId: String, name: String, goal: String,
                startDate: Date, endDate: Date, state: SprintState) {
        self.id = id; self.projectId = projectId; self.name = name; self.goal = goal
        self.startDate = startDate; self.endDate = endDate; self.state = state
    }

    func with(state: SprintState) -> PacaSprint {
        PacaSprint(id: id, projectId: projectId, name: name, goal: goal,
                   startDate: startDate, endDate: endDate, state: state)
    }
}

/// Extra board-only metadata on top of `PacaTask`. (C# `TaskBoardMetadata`.)
public struct TaskBoardMetadata: Sendable, Equatable, Codable {
    public let projectId: String
    public let number: Int
    public let storyPoints: Int
    public let importance: Int  // 0..5
    public let assigneeMemberId: String?
    public let reporterMemberId: String?
    public let parentTaskNumber: Int?
    public let sprintId: String?
    public let tags: [String]
    public let customFields: [String: String]
    public let positionInColumn: Int

    public init(projectId: String, number: Int, storyPoints: Int, importance: Int,
                assigneeMemberId: String?, reporterMemberId: String?, parentTaskNumber: Int?,
                sprintId: String?, tags: [String], customFields: [String: String], positionInColumn: Int) {
        self.projectId = projectId; self.number = number; self.storyPoints = storyPoints
        self.importance = importance; self.assigneeMemberId = assigneeMemberId
        self.reporterMemberId = reporterMemberId; self.parentTaskNumber = parentTaskNumber
        self.sprintId = sprintId; self.tags = tags; self.customFields = customFields
        self.positionInColumn = positionInColumn
    }

    func with(positionInColumn: Int) -> TaskBoardMetadata {
        TaskBoardMetadata(projectId: projectId, number: number, storyPoints: storyPoints,
                          importance: importance, assigneeMemberId: assigneeMemberId,
                          reporterMemberId: reporterMemberId, parentTaskNumber: parentTaskNumber,
                          sprintId: sprintId, tags: tags, customFields: customFields,
                          positionInColumn: positionInColumn)
    }
}

/// A per-user / per-board named view. (C# `BoardView`.)
public struct BoardView: Sendable, Equatable, Codable {
    public let name: String
    public let filterTagsCsv: String?
    public let filterAssignee: String?
    public let sortBy: String?
    public let sortDescending: Bool
    public let visibleColumns: [String]
    public let visibleFields: [String]

    public init(name: String, filterTagsCsv: String?, filterAssignee: String?, sortBy: String?,
                sortDescending: Bool, visibleColumns: [String], visibleFields: [String]) {
        self.name = name; self.filterTagsCsv = filterTagsCsv; self.filterAssignee = filterAssignee
        self.sortBy = sortBy; self.sortDescending = sortDescending
        self.visibleColumns = visibleColumns; self.visibleFields = visibleFields
    }
}

/// Board service over a project. Sprints + columns + per-task metadata + views.
/// (C# `PacaBoard`.)
public final class PacaBoard: @unchecked Sendable {
    private let tasks: InMemoryPacaStore
    private let lock = NSLock()
    private var columns: [String: StatusColumn] = [:]
    private var sprints: [String: PacaSprint] = [:]
    private var metadata: [String: TaskBoardMetadata] = [:]  // keyed "projectId/number"
    private var views: [String: BoardView] = [:]
    private let clock: @Sendable () -> Date

    public init(tasks: InMemoryPacaStore, clock: (@Sendable () -> Date)? = nil) {
        self.tasks = tasks
        self.clock = clock ?? { Date() }
        addDefaultColumns()
    }

    private func addDefaultColumns() {
        lock.lock()
        columns["todo"]        = StatusColumn(name: "todo",        category: "open",      position: 0, collapsed: false)
        columns["in_progress"] = StatusColumn(name: "in_progress", category: "in-flight", position: 1, collapsed: false)
        columns["in_review"]   = StatusColumn(name: "in_review",   category: "review",    position: 2, collapsed: false)
        columns["done"]        = StatusColumn(name: "done",        category: "closed",    position: 3, collapsed: false)
        columns["cancelled"]   = StatusColumn(name: "cancelled",   category: "cancelled", position: 4, collapsed: false)
        columns["blocked"]     = StatusColumn(name: "blocked",     category: "blocked",   position: 5, collapsed: true)
        lock.unlock()
    }

    public var columnList: [StatusColumn] {
        lock.lock(); let all = Array(columns.values); lock.unlock()
        return all.sorted { $0.position < $1.position }
    }

    public func addColumn(_ col: StatusColumn) {
        lock.lock(); columns[col.name] = col; lock.unlock()
    }

    public func collapseColumn(_ name: String, collapsed: Bool) {
        lock.lock()
        if let col = columns[name] { columns[name] = col.with(collapsed: collapsed) }
        lock.unlock()
    }

    /// Move a task between status columns, updating its in-column position.
    public func moveTask(_ projectId: String, number: Int, newStatus: String, newPosition: Int) throws {
        let task = tasks.getTaskByReference(projectId, reference: "\(projectId)-\(number)")
            ?? tasks.listTasks(projectId).first { $0.number == number }
        guard let task = task else { throw PacaError.invalidOperation("Task not found.") }
        lock.lock(); let known = columns[newStatus] != nil; lock.unlock()
        guard known else { throw PacaError.argument("Unknown status '\(newStatus)'.") }

        tasks.updateTask(task.with(status: newStatus))
        let meta = getOrCreateMetadata(projectId, number: number).with(positionInColumn: newPosition)
        lock.lock(); metadata[Self.key(projectId, number)] = meta; lock.unlock()
    }

    /// Attach board metadata to an existing task.
    public func setTaskMetadata(_ metadata: TaskBoardMetadata) {
        lock.lock(); self.metadata[Self.key(metadata.projectId, metadata.number)] = metadata; lock.unlock()
    }

    public func getTaskMetadata(_ projectId: String, number: Int) -> TaskBoardMetadata? {
        lock.lock(); defer { lock.unlock() }
        return metadata[Self.key(projectId, number)]
    }

    /// Paginated column read for lazy loading.
    public func tasksInColumn(_ projectId: String, status: String, skip: Int = 0, take: Int = 50) -> [PacaTask] {
        let live = tasks.listTasks(projectId).filter { $0.status == status }
        let ordered = live.sorted {
            getOrCreateMetadata($0.projectId, number: $0.number).positionInColumn
                < getOrCreateMetadata($1.projectId, number: $1.number).positionInColumn
        }
        let start = min(max(0, skip), ordered.count)
        let end = min(start + max(0, take), ordered.count)
        return Array(ordered[start..<end])
    }

    /// Tasks bucketed by sprint (Scrumban board).
    public func tasksInSprint(_ sprintId: String) -> [PacaTask] {
        lock.lock(); let metas = Array(metadata.values); lock.unlock()
        return metas
            .filter { $0.sprintId == sprintId }
            .compactMap { m in tasks.listTasks(m.projectId).first { $0.number == m.number } }
    }

    /// Create a sprint in Planning.
    @discardableResult
    public func createSprint(id: String, projectId: String, name: String, goal: String,
                             start: Date, end: Date) -> PacaSprint {
        let s = PacaSprint(id: id, projectId: projectId, name: name, goal: goal,
                           startDate: start, endDate: end, state: .planning)
        lock.lock(); sprints[id] = s; lock.unlock()
        return s
    }

    public func getSprint(_ id: String) -> PacaSprint? {
        lock.lock(); defer { lock.unlock() }
        return sprints[id]
    }

    @discardableResult public func startSprint(_ id: String) throws -> PacaSprint { try transition(id, to: .active) }
    @discardableResult public func completeSprint(_ id: String) throws -> PacaSprint { try transition(id, to: .completed) }

    private func transition(_ id: String, to: SprintState) throws -> PacaSprint {
        lock.lock()
        guard let sprint = sprints[id] else { lock.unlock(); throw PacaError.invalidOperation("Sprint '\(id)' not found.") }
        let updated = sprint.with(state: to)
        sprints[id] = updated
        lock.unlock()
        return updated
    }

    /// Save a named view (filters + sort + visible fields).
    public func saveView(_ view: BoardView) {
        lock.lock(); views[view.name] = view; lock.unlock()
    }

    public func getView(_ name: String) -> BoardView? {
        lock.lock(); defer { lock.unlock() }
        return views[name]
    }

    public func listViews() -> [BoardView] {
        lock.lock(); let all = Array(views.values); lock.unlock()
        return all.sorted { $0.name < $1.name }
    }

    private func getOrCreateMetadata(_ projectId: String, number: Int) -> TaskBoardMetadata {
        let k = Self.key(projectId, number)
        lock.lock(); defer { lock.unlock() }
        if let existing = metadata[k] { return existing }
        let created = TaskBoardMetadata(projectId: projectId, number: number, storyPoints: 0,
                                        importance: 3, assigneeMemberId: nil, reporterMemberId: nil,
                                        parentTaskNumber: nil, sprintId: nil, tags: [],
                                        customFields: [:], positionInColumn: 0)
        metadata[k] = created
        return created
    }

    private static func key(_ projectId: String, _ number: Int) -> String { "\(projectId)/\(number)" }
}

// MARK: ── PacaAuth ────────────────────────────────────────────────────────

/// Token-shaped JWT result. (C# `JwtPair`.)
public struct JwtPair: Sendable, Equatable, Codable {
    public let accessToken: String
    public let refreshToken: String
    public let accessExpiresAtUtc: Date
    public let refreshExpiresAtUtc: Date

    public init(accessToken: String, refreshToken: String, accessExpiresAtUtc: Date, refreshExpiresAtUtc: Date) {
        self.accessToken = accessToken; self.refreshToken = refreshToken
        self.accessExpiresAtUtc = accessExpiresAtUtc; self.refreshExpiresAtUtc = refreshExpiresAtUtc
    }
}

/// Verified JWT payload. (C# `JwtPayload`.)
public struct JwtPayload: Sendable, Equatable, Codable {
    public let subject: String
    public let claims: [String: String]
    public let expiresAtUtc: Date

    public init(subject: String, claims: [String: String], expiresAtUtc: Date) {
        self.subject = subject; self.claims = claims; self.expiresAtUtc = expiresAtUtc
    }
}

/// HMAC-SHA256 JWT issuer + verifier. (C# `HmacJwtAuthenticator`.)
public final class HmacJwtAuthenticator: @unchecked Sendable {
    private let secret: Data
    private let accessLifetime: TimeInterval
    private let refreshLifetime: TimeInterval
    private let clock: @Sendable () -> Date

    public init(signingSecret: String, accessLifetime: TimeInterval? = nil,
                refreshLifetime: TimeInterval? = nil, clock: (@Sendable () -> Date)? = nil) throws {
        guard signingSecret.count >= 16 else {
            throw PacaError.argument("Signing secret must be at least 16 characters.")
        }
        self.secret = Data(signingSecret.utf8)
        self.accessLifetime = accessLifetime ?? (15 * 60)
        self.refreshLifetime = refreshLifetime ?? (7 * 86_400)
        self.clock = clock ?? { Date() }
    }

    /// Issue access + refresh tokens for `subject`.
    public func issue(_ subject: String, claims: [String: String]? = nil) throws -> JwtPair {
        guard !subject.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PacaError.argument("subject required")
        }
        let now = clock()
        let accessExp = now.addingTimeInterval(accessLifetime)
        let refreshExp = now.addingTimeInterval(refreshLifetime)
        let access = encodeToken(subject: subject, type: "access", expires: accessExp, claims: claims)
        let refresh = encodeToken(subject: subject, type: "refresh", expires: refreshExp, claims: nil)
        return JwtPair(accessToken: access, refreshToken: refresh,
                       accessExpiresAtUtc: accessExp, refreshExpiresAtUtc: refreshExp)
    }

    /// Verify a token; returns the payload or `nil` if invalid/expired.
    public func verify(_ token: String, expectedType: String = "access") -> JwtPayload? {
        guard !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        let parts = token.split(separator: ".", omittingEmptySubsequences: false).map(String.init)
        guard parts.count == 3 else { return nil }

        let header = parts[0], payload = parts[1], sig = parts[2]
        let signing = "\(header).\(payload)"
        let expected = signBase64Url(signing)
        guard Self.fixedTimeEquals(expected, sig) else { return nil }

        guard let jsonBytes = Self.base64UrlDecode(payload),
              let obj = try? JSONSerialization.jsonObject(with: jsonBytes) as? [String: Any] else {
            return nil
        }

        guard let typ = obj["typ"] as? String, typ == expectedType else { return nil }
        guard let subject = obj["sub"] as? String else { return nil }
        // exp is a Unix-seconds integer.
        let expSeconds: Int64
        if let n = obj["exp"] as? Int64 { expSeconds = n }
        else if let n = obj["exp"] as? Int { expSeconds = Int64(n) }
        else if let d = obj["exp"] as? Double { expSeconds = Int64(d) }
        else { return nil }
        let exp = Date(timeIntervalSince1970: TimeInterval(expSeconds))
        if exp <= clock() { return nil }

        var extraClaims: [String: String] = [:]
        for (k, v) in obj where k != "typ" && k != "sub" && k != "exp" {
            if let s = v as? String { extraClaims[k] = s }
            else { extraClaims[k] = "\(v)" }
        }
        return JwtPayload(subject: subject, claims: extraClaims, expiresAtUtc: exp)
    }

    private func encodeToken(subject: String, type: String, expires: Date, claims: [String: String]?) -> String {
        let header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}"
        var payload: [String: Any] = [
            "sub": subject,
            "typ": type,
            "exp": Int64(expires.timeIntervalSince1970),
        ]
        if let claims = claims { for (k, v) in claims { payload[k] = v } }

        let headerB = Self.base64UrlEncode(Data(header.utf8))
        let payloadData = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data()
        let payloadB = Self.base64UrlEncode(payloadData)
        let signing = "\(headerB).\(payloadB)"
        let sig = signBase64Url(signing)
        return "\(signing).\(sig)"
    }

    private func signBase64Url(_ signing: String) -> String {
        let mac = PacaCrypto.hmacSha256(key: secret, message: Data(signing.utf8))
        return Self.base64UrlEncode(mac)
    }

    static func base64UrlEncode(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    static func base64UrlDecode(_ input: String) -> Data? {
        var s = input.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        switch s.count % 4 {
        case 2: s += "=="
        case 3: s += "="
        default: break
        }
        return Data(base64Encoded: s)
    }

    static func fixedTimeEquals(_ a: String, _ b: String) -> Bool {
        let ba = Array(a.utf8), bb = Array(b.utf8)
        if ba.count != bb.count { return false }
        var diff: UInt8 = 0
        for i in 0..<ba.count { diff |= ba[i] ^ bb[i] }
        return diff == 0
    }
}

/// Issued API key — store hashes only. (C# `PacaApiKeyRecord`.)
public struct PacaApiKeyRecord: Sendable, Equatable, Codable {
    public let keyId: String
    public let label: String
    public let hashedSecret: String
    public let createdAtUtc: Date
    public let revokedAtUtc: Date?

    public init(keyId: String, label: String, hashedSecret: String, createdAtUtc: Date, revokedAtUtc: Date?) {
        self.keyId = keyId; self.label = label; self.hashedSecret = hashedSecret
        self.createdAtUtc = createdAtUtc; self.revokedAtUtc = revokedAtUtc
    }

    func with(revokedAtUtc: Date?) -> PacaApiKeyRecord {
        PacaApiKeyRecord(keyId: keyId, label: label, hashedSecret: hashedSecret,
                         createdAtUtc: createdAtUtc, revokedAtUtc: revokedAtUtc)
    }
}

/// API-key registry separate from JWT user auth. (C# `PacaApiKeyAuthenticator`.)
public final class PacaApiKeyAuthenticator: @unchecked Sendable {
    private let lock = NSLock()
    private var keys: [String: PacaApiKeyRecord] = [:]
    private let clock: @Sendable () -> Date

    public init(clock: (@Sendable () -> Date)? = nil) {
        self.clock = clock ?? { Date() }
    }

    /// Generate a fresh key; the raw secret is returned ONCE for the caller to store.
    public func issue(_ label: String) throws -> (record: PacaApiKeyRecord, rawSecret: String) {
        guard !label.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PacaError.argument("label required")
        }
        let keyId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let secret = Self.randomBase64(32)
        let hashed = Self.hash(secret)
        let record = PacaApiKeyRecord(keyId: keyId, label: label, hashedSecret: hashed,
                                      createdAtUtc: clock(), revokedAtUtc: nil)
        lock.lock(); keys[keyId] = record; lock.unlock()
        return (record, secret)
    }

    /// Verify an incoming key. Returns the record if valid and live.
    public func verify(keyId: String, presentedSecret: String) -> PacaApiKeyRecord? {
        lock.lock(); let record = keys[keyId]; lock.unlock()
        guard let record = record, record.revokedAtUtc == nil else { return nil }
        let hashed = Self.hash(presentedSecret)
        return HmacJwtAuthenticator.fixedTimeEquals(hashed, record.hashedSecret) ? record : nil
    }

    /// Revoke a key. Idempotent.
    public func revoke(_ keyId: String) {
        lock.lock()
        if let existing = keys[keyId], existing.revokedAtUtc == nil {
            keys[keyId] = existing.with(revokedAtUtc: clock())
        }
        lock.unlock()
    }

    private static func hash(_ secret: String) -> String {
        PacaCrypto.sha256(Data(secret.utf8)).base64EncodedString().replacingOccurrences(of: "=", with: "")
    }

    private static func randomBase64(_ n: Int) -> String {
        var bytes = [UInt8](repeating: 0, count: n)
        for i in 0..<n { bytes[i] = UInt8.random(in: 0...255) }
        return Data(bytes).base64EncodedString().replacingOccurrences(of: "=", with: "")
    }
}

// MARK: ── PacaAgents ──────────────────────────────────────────────────────

/// Member kind. (C# `MemberKind`.)
public enum MemberKind: Int, Sendable, Codable, CaseIterable {
    case human = 0
    case agent = 1
}

/// Shared identity for humans + agents in a project. (C# `ProjectMember`.)
public struct ProjectMember: Sendable, Equatable, Codable {
    public let id: String
    public let projectId: String
    public let kind: MemberKind
    public let displayName: String
    public let handle: String
    public let role: String
    public let avatarUrl: String?
    public let createdAtUtc: Date
    public let deletedAtUtc: Date?

    public init(id: String, projectId: String, kind: MemberKind, displayName: String, handle: String,
                role: String, avatarUrl: String?, createdAtUtc: Date, deletedAtUtc: Date?) {
        self.id = id; self.projectId = projectId; self.kind = kind; self.displayName = displayName
        self.handle = handle; self.role = role; self.avatarUrl = avatarUrl
        self.createdAtUtc = createdAtUtc; self.deletedAtUtc = deletedAtUtc
    }

    func with(deletedAtUtc: Date?) -> ProjectMember {
        ProjectMember(id: id, projectId: projectId, kind: kind, displayName: displayName,
                      handle: handle, role: role, avatarUrl: avatarUrl,
                      createdAtUtc: createdAtUtc, deletedAtUtc: deletedAtUtc)
    }
}

/// Per-agent LLM config. `Uri?` → `String?`. (C# `AgentLlmConfig`.)
public struct AgentLlmConfig: Sendable, Equatable, Codable {
    public let provider: String
    public let model: String
    public let apiKey: String?
    public let baseAddress: String?

    public init(provider: String, model: String, apiKey: String?, baseAddress: String?) {
        self.provider = provider; self.model = model; self.apiKey = apiKey; self.baseAddress = baseAddress
    }
}

/// Per-agent context-specific system prompts. (C# `AgentSystemPrompts`.)
public struct AgentSystemPrompts: Sendable, Equatable, Codable {
    public let taskPrompt: String?
    public let docPrompt: String?
    public let chatPrompt: String?

    public init(taskPrompt: String?, docPrompt: String?, chatPrompt: String?) {
        self.taskPrompt = taskPrompt; self.docPrompt = docPrompt; self.chatPrompt = chatPrompt
    }
}

/// Capability flags an agent is permitted to do. (C# `AgentCapabilities`.)
public struct AgentCapabilities: Sendable, Equatable, Codable {
    public let canCloneRepos: Bool
    public let canCreatePRs: Bool
    public let canWriteFiles: Bool
    public let canCallExternalTools: Bool

    public init(canCloneRepos: Bool, canCreatePRs: Bool, canWriteFiles: Bool, canCallExternalTools: Bool) {
        self.canCloneRepos = canCloneRepos; self.canCreatePRs = canCreatePRs
        self.canWriteFiles = canWriteFiles; self.canCallExternalTools = canCallExternalTools
    }
}

/// Runtime limits an agent must respect. (C# `AgentLimits`.)
public struct AgentLimits: Sendable, Equatable, Codable {
    public let maxIterations: Int
    public let timeout: TimeInterval

    public init(maxIterations: Int, timeout: TimeInterval) {
        self.maxIterations = maxIterations; self.timeout = timeout
    }
}

/// Git identity an agent uses when committing. (C# `AgentGitIdentity`.)
public struct AgentGitIdentity: Sendable, Equatable, Codable {
    public let name: String
    public let email: String

    public init(name: String, email: String) { self.name = name; self.email = email }
}

/// Trigger keywords that wake the agent for each event class. (C# `AgentTriggers`.)
public struct AgentTriggers: Sendable, Equatable, Codable {
    public let taskCreated: String?
    public let chatMention: String?
    public let docEdit: String?
    public let directMention: String?

    public init(taskCreated: String?, chatMention: String?, docEdit: String?, directMention: String?) {
        self.taskCreated = taskCreated; self.chatMention = chatMention
        self.docEdit = docEdit; self.directMention = directMention
    }
}

/// Full agent profile. (C# `AgentProfile`.)
public struct AgentProfile: Sendable, Equatable, Codable {
    public let memberId: String
    public let llm: AgentLlmConfig
    public let prompts: AgentSystemPrompts
    public let capabilities: AgentCapabilities
    public let limits: AgentLimits
    public let gitIdentity: AgentGitIdentity
    public let triggers: AgentTriggers

    public init(memberId: String, llm: AgentLlmConfig, prompts: AgentSystemPrompts,
                capabilities: AgentCapabilities, limits: AgentLimits,
                gitIdentity: AgentGitIdentity, triggers: AgentTriggers) {
        self.memberId = memberId; self.llm = llm; self.prompts = prompts
        self.capabilities = capabilities; self.limits = limits
        self.gitIdentity = gitIdentity; self.triggers = triggers
    }

    func with(memberId: String) -> AgentProfile {
        AgentProfile(memberId: memberId, llm: llm, prompts: prompts, capabilities: capabilities,
                     limits: limits, gitIdentity: gitIdentity, triggers: triggers)
    }
}

/// Five preset agent templates from paca. (C# `AgentTemplates`.)
public enum AgentTemplates {
    public static func developmentAgent(memberId: String, apiKey: String, baseAddress: String? = nil) -> AgentProfile {
        AgentProfile(
            memberId: memberId,
            llm: AgentLlmConfig(provider: "openai", model: "gpt-4o-mini", apiKey: apiKey, baseAddress: baseAddress),
            prompts: AgentSystemPrompts(
                taskPrompt: "You are a senior developer. Implement requested changes, write tests, open PRs.",
                docPrompt: "You write engineering docs that are precise and example-driven.",
                chatPrompt: "You answer engineering questions with concrete code samples."),
            capabilities: AgentCapabilities(canCloneRepos: true, canCreatePRs: true, canWriteFiles: true, canCallExternalTools: true),
            limits: AgentLimits(maxIterations: 25, timeout: 10 * 60),
            gitIdentity: AgentGitIdentity(name: "CircleAI Dev Agent", email: "dev-agent@circleai.local"),
            triggers: AgentTriggers(taskCreated: "dev", chatMention: "@dev", docEdit: nil, directMention: "dev"))
    }

    public static func productManagerAgent(memberId: String, apiKey: String) -> AgentProfile {
        AgentProfile(
            memberId: memberId,
            llm: AgentLlmConfig(provider: "openai", model: "gpt-4o-mini", apiKey: apiKey, baseAddress: nil),
            prompts: AgentSystemPrompts(
                taskPrompt: "You are a product manager. Triage tasks, break them down, assign owners.",
                docPrompt: "You write product specs and PRDs.",
                chatPrompt: "You answer product/priority questions."),
            capabilities: AgentCapabilities(canCloneRepos: false, canCreatePRs: false, canWriteFiles: true, canCallExternalTools: true),
            limits: AgentLimits(maxIterations: 15, timeout: 5 * 60),
            gitIdentity: AgentGitIdentity(name: "CircleAI PM Agent", email: "pm-agent@circleai.local"),
            triggers: AgentTriggers(taskCreated: "pm", chatMention: "@pm", docEdit: "@pm", directMention: "pm"))
    }

    public static func designerAgent(memberId: String, apiKey: String) -> AgentProfile {
        AgentProfile(
            memberId: memberId,
            llm: AgentLlmConfig(provider: "openai", model: "gpt-4o-mini", apiKey: apiKey, baseAddress: nil),
            prompts: AgentSystemPrompts(
                taskPrompt: "You are a designer. Sketch UI ideas, write copy, propose flows.",
                docPrompt: "You write design memos.",
                chatPrompt: "You answer design questions and propose concepts."),
            capabilities: AgentCapabilities(canCloneRepos: false, canCreatePRs: false, canWriteFiles: true, canCallExternalTools: false),
            limits: AgentLimits(maxIterations: 10, timeout: 5 * 60),
            gitIdentity: AgentGitIdentity(name: "CircleAI Design Agent", email: "design-agent@circleai.local"),
            triggers: AgentTriggers(taskCreated: "design", chatMention: "@design", docEdit: "@design", directMention: "design"))
    }

    public static func qaAgent(memberId: String, apiKey: String) -> AgentProfile {
        AgentProfile(
            memberId: memberId,
            llm: AgentLlmConfig(provider: "openai", model: "gpt-4o-mini", apiKey: apiKey, baseAddress: nil),
            prompts: AgentSystemPrompts(
                taskPrompt: "You are a QA engineer. Write test plans, generate test cases, validate against AC.",
                docPrompt: "You write QA reports.",
                chatPrompt: "You answer QA questions and propose test strategies."),
            capabilities: AgentCapabilities(canCloneRepos: true, canCreatePRs: false, canWriteFiles: true, canCallExternalTools: true),
            limits: AgentLimits(maxIterations: 20, timeout: 7 * 60),
            gitIdentity: AgentGitIdentity(name: "CircleAI QA Agent", email: "qa-agent@circleai.local"),
            triggers: AgentTriggers(taskCreated: "qa", chatMention: "@qa", docEdit: nil, directMention: "qa"))
    }

    public static func codeReviewerAgent(memberId: String, apiKey: String) -> AgentProfile {
        AgentProfile(
            memberId: memberId,
            llm: AgentLlmConfig(provider: "openai", model: "gpt-4o-mini", apiKey: apiKey, baseAddress: nil),
            prompts: AgentSystemPrompts(
                taskPrompt: "You are a senior code reviewer. Comment for clarity, correctness, security.",
                docPrompt: "You write code review checklists.",
                chatPrompt: "You answer questions about code patterns and best practices."),
            capabilities: AgentCapabilities(canCloneRepos: true, canCreatePRs: false, canWriteFiles: false, canCallExternalTools: true),
            limits: AgentLimits(maxIterations: 15, timeout: 7 * 60),
            gitIdentity: AgentGitIdentity(name: "CircleAI Reviewer Agent", email: "reviewer-agent@circleai.local"),
            triggers: AgentTriggers(taskCreated: nil, chatMention: "@review", docEdit: nil, directMention: "review"))
    }

    public static let presetNames: [String] = ["development", "pm", "design", "qa", "review"]
}

/// In-memory store for members + agent profiles. (C# `InMemoryPacaMemberStore`.)
public final class InMemoryPacaMemberStore: @unchecked Sendable {
    private let lock = NSLock()
    private var members: [String: ProjectMember] = [:]
    private var profiles: [String: AgentProfile] = [:]
    private let clock: @Sendable () -> Date

    public init(clock: (@Sendable () -> Date)? = nil) {
        self.clock = clock ?? { Date() }
    }

    @discardableResult
    public func addHuman(id: String, projectId: String, displayName: String, handle: String,
                         role: String = "developer", avatar: String? = nil) throws -> ProjectMember {
        try addMember(id: id, projectId: projectId, kind: .human, displayName: displayName,
                      handle: handle, role: role, avatar: avatar)
    }

    @discardableResult
    public func addAgent(id: String, projectId: String, displayName: String, handle: String,
                         profile: AgentProfile, avatar: String? = nil) throws -> ProjectMember {
        let member = try addMember(id: id, projectId: projectId, kind: .agent, displayName: displayName,
                                   handle: handle, role: "agent", avatar: avatar)
        lock.lock(); profiles[id] = profile.with(memberId: id); lock.unlock()
        return member
    }

    private func addMember(id: String, projectId: String, kind: MemberKind, displayName: String,
                           handle: String, role: String, avatar: String?) throws -> ProjectMember {
        guard !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("id required") }
        guard !projectId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("projectId required") }
        guard !displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("displayName required") }
        guard !handle.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("handle required") }

        let member = ProjectMember(id: id, projectId: projectId, kind: kind, displayName: displayName,
                                   handle: handle, role: role, avatarUrl: avatar, createdAtUtc: clock(), deletedAtUtc: nil)
        lock.lock()
        if members[id] != nil { lock.unlock(); throw PacaError.invalidOperation("Member '\(id)' already exists.") }
        members[id] = member
        lock.unlock()
        return member
    }

    public func getMember(_ id: String) -> ProjectMember? {
        lock.lock(); defer { lock.unlock() }
        if let m = members[id], m.deletedAtUtc == nil { return m }
        return nil
    }

    public func getAgentProfile(_ memberId: String) -> AgentProfile? {
        lock.lock(); defer { lock.unlock() }
        return profiles[memberId]
    }

    public func listMembers(_ projectId: String, kind: MemberKind? = nil) -> [ProjectMember] {
        lock.lock(); let all = Array(members.values); lock.unlock()
        return all
            .filter { $0.projectId == projectId && $0.deletedAtUtc == nil && (kind == nil || $0.kind == kind) }
            .sorted { $0.displayName < $1.displayName }
    }

    public func removeMember(_ id: String) {
        lock.lock()
        if let existing = members[id], existing.deletedAtUtc == nil {
            members[id] = existing.with(deletedAtUtc: clock())
        }
        lock.unlock()
    }

    @discardableResult
    public func updateAgentProfile(_ memberId: String, updated: AgentProfile) throws -> AgentProfile {
        guard let m = getMember(memberId), m.kind == .agent else {
            throw PacaError.invalidOperation("Member '\(memberId)' is not an agent.")
        }
        let fixed = updated.with(memberId: memberId)
        lock.lock(); profiles[memberId] = fixed; lock.unlock()
        return fixed
    }
}

// MARK: ── PacaDocs ────────────────────────────────────────────────────────

/// A doc node (folder OR document). (C# `DocNode`.)
public struct DocNode: Sendable, Equatable, Codable {
    public let id: String
    public let projectId: String
    public let parentId: String?
    public let isFolder: Bool
    public let title: String
    public let contentJson: String
    public let createdAtUtc: Date
    public let deletedAtUtc: Date?

    public init(id: String, projectId: String, parentId: String?, isFolder: Bool, title: String,
                contentJson: String, createdAtUtc: Date, deletedAtUtc: Date?) {
        self.id = id; self.projectId = projectId; self.parentId = parentId; self.isFolder = isFolder
        self.title = title; self.contentJson = contentJson
        self.createdAtUtc = createdAtUtc; self.deletedAtUtc = deletedAtUtc
    }

    func with(contentJson: String) -> DocNode {
        DocNode(id: id, projectId: projectId, parentId: parentId, isFolder: isFolder, title: title,
                contentJson: contentJson, createdAtUtc: createdAtUtc, deletedAtUtc: deletedAtUtc)
    }
}

/// One immutable snapshot of a doc. (C# `DocVersion`.)
public struct DocVersion: Sendable, Equatable, Codable {
    public let versionId: String
    public let docId: String
    public let contentJson: String
    public let savedAtUtc: Date
    public let authorMemberId: String

    public init(versionId: String, docId: String, contentJson: String, savedAtUtc: Date, authorMemberId: String) {
        self.versionId = versionId; self.docId = docId; self.contentJson = contentJson
        self.savedAtUtc = savedAtUtc; self.authorMemberId = authorMemberId
    }
}

/// One document-activity event. (C# `DocActivity`.)
public struct DocActivity: Sendable, Equatable, Codable {
    public let activityId: String
    public let docId: String
    public let authorMemberId: String
    public let action: String
    public let detail: String?
    public let at: Date

    public init(activityId: String, docId: String, authorMemberId: String, action: String, detail: String?, at: Date) {
        self.activityId = activityId; self.docId = docId; self.authorMemberId = authorMemberId
        self.action = action; self.detail = detail; self.at = at
    }
}

/// Link between a doc section and a task / epic. (C# `DocLink`.)
public struct DocLink: Sendable, Equatable, Codable {
    public let linkId: String
    public let docId: String
    public let sectionAnchor: String
    public let projectId: String
    public let taskNumber: Int

    public init(linkId: String, docId: String, sectionAnchor: String, projectId: String, taskNumber: Int) {
        self.linkId = linkId; self.docId = docId; self.sectionAnchor = sectionAnchor
        self.projectId = projectId; self.taskNumber = taskNumber
    }
}

/// In-memory doc service. (C# `PacaDocService`.)
public final class PacaDocService: @unchecked Sendable {
    private let lock = NSLock()
    private var nodes: [String: DocNode] = [:]
    private var versions: [String: [DocVersion]] = [:]
    private var activity: [String: [DocActivity]] = [:]
    private var links: [String: [DocLink]] = [:]
    private let clock: @Sendable () -> Date

    public init(clock: (@Sendable () -> Date)? = nil) {
        self.clock = clock ?? { Date() }
    }

    @discardableResult
    public func createFolder(id: String, projectId: String, parentId: String?, title: String) throws -> DocNode {
        try create(id: id, projectId: projectId, parentId: parentId, isFolder: true, title: title,
                   contentJson: "{}", authorMemberId: "system")
    }

    @discardableResult
    public func createDocument(id: String, projectId: String, parentId: String?, title: String,
                               contentJson: String, authorMemberId: String) throws -> DocNode {
        try create(id: id, projectId: projectId, parentId: parentId, isFolder: false, title: title,
                   contentJson: contentJson, authorMemberId: authorMemberId)
    }

    private func create(id: String, projectId: String, parentId: String?, isFolder: Bool, title: String,
                        contentJson: String, authorMemberId: String) throws -> DocNode {
        guard !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("id required") }
        guard !projectId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PacaError.argument("projectId required") }

        let node = DocNode(id: id, projectId: projectId, parentId: parentId, isFolder: isFolder,
                           title: title, contentJson: contentJson, createdAtUtc: clock(), deletedAtUtc: nil)
        lock.lock()
        if nodes[id] != nil { lock.unlock(); throw PacaError.invalidOperation("Doc '\(id)' already exists.") }
        nodes[id] = node
        if !isFolder {
            versions[id] = []
            activity[id] = [DocActivity(activityId: Self.newId(), docId: id, authorMemberId: authorMemberId,
                                        action: "created", detail: nil, at: clock())]
        }
        lock.unlock()
        return node
    }

    public func get(_ id: String) -> DocNode? {
        lock.lock(); defer { lock.unlock() }
        if let n = nodes[id], n.deletedAtUtc == nil { return n }
        return nil
    }

    public func listChildren(_ projectId: String, parentId: String?) -> [DocNode] {
        lock.lock(); let all = Array(nodes.values); lock.unlock()
        return all
            .filter { $0.projectId == projectId && $0.parentId == parentId && $0.deletedAtUtc == nil }
            .sorted { $0.title < $1.title }
    }

    /// Edit a document: writes a new version + activity entry, returns mentioned handles.
    @discardableResult
    public func edit(_ id: String, newContentJson: String, authorMemberId: String, isAiEdit: Bool = false) throws -> [String] {
        lock.lock()
        guard let node = nodes[id], !node.isFolder, node.deletedAtUtc == nil else {
            lock.unlock(); throw PacaError.invalidOperation("Doc '\(id)' is not editable.")
        }
        nodes[id] = node.with(contentJson: newContentJson)
        let version = DocVersion(versionId: Self.newId(), docId: id, contentJson: node.contentJson,
                                 savedAtUtc: clock(), authorMemberId: authorMemberId)
        versions[id, default: []].append(version)
        activity[id, default: []].append(DocActivity(activityId: Self.newId(), docId: id, authorMemberId: authorMemberId,
                                                      action: isAiEdit ? "ai-edited" : "edited", detail: nil, at: clock()))
        lock.unlock()
        return Self.extractMentions(newContentJson)
    }

    public func versionsFor(_ docId: String) -> [DocVersion] {
        lock.lock(); defer { lock.unlock() }
        return versions[docId] ?? []
    }

    /// Cheap diff between two versions — added + removed text lines.
    public func diffLines(before: String, after: String) -> (added: [String], removed: [String]) {
        let b = Set(before.components(separatedBy: "\n"))
        let a = Set(after.components(separatedBy: "\n"))
        return (Array(a.subtracting(b)), Array(b.subtracting(a)))
    }

    public func activityFor(_ docId: String) -> [DocActivity] {
        lock.lock(); defer { lock.unlock() }
        return activity[docId] ?? []
    }

    @discardableResult
    public func link(docId: String, sectionAnchor: String, projectId: String, taskNumber: Int) -> DocLink {
        let link = DocLink(linkId: Self.newId(), docId: docId, sectionAnchor: sectionAnchor,
                           projectId: projectId, taskNumber: taskNumber)
        lock.lock()
        links[docId, default: []].append(link)
        activity[docId, default: []].append(DocActivity(activityId: Self.newId(), docId: docId, authorMemberId: "system",
                                                        action: "linked", detail: "\(projectId)-\(taskNumber)@\(sectionAnchor)", at: clock()))
        lock.unlock()
        return link
    }

    public func linksFor(_ docId: String) -> [DocLink] {
        lock.lock(); defer { lock.unlock() }
        return links[docId] ?? []
    }

    private static func newId() -> String { UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }

    static func extractMentions(_ content: String) -> [String] {
        let regex = try? NSRegularExpression(pattern: "@([a-zA-Z0-9_\\-]+)")
        let range = NSRange(content.startIndex..<content.endIndex, in: content)
        var set: [String] = []
        var seen = Set<String>()
        regex?.enumerateMatches(in: content, range: range) { match, _, _ in
            guard let match = match, match.numberOfRanges >= 2,
                  let r = Range(match.range(at: 1), in: content) else { return }
            let handle = String(content[r])
            let lower = handle.lowercased()
            if !seen.contains(lower) { seen.insert(lower); set.append(handle) }
        }
        return set
    }
}

// MARK: ── PacaPlugins ─────────────────────────────────────────────────────

/// Plugin extension points supported by the marketplace. (C# `PluginExtensionPoint`.)
public enum PluginExtensionPoint: Int, Sendable, Codable, CaseIterable {
    case sidebar = 0
    case taskDetail = 1
    case settings = 2
    case customView = 3
    case route = 4
    case event = 5
    case mcpTool = 6
}

/// Per-plugin resource limits. (C# `PluginResourceLimits`.)
public struct PluginResourceLimits: Sendable, Equatable, Codable {
    public let callTimeoutMs: Int
    public let memoryCeilingBytes: Int64

    public init(callTimeoutMs: Int = 5000, memoryCeilingBytes: Int64 = 64 * 1024 * 1024) {
        self.callTimeoutMs = callTimeoutMs
        self.memoryCeilingBytes = memoryCeilingBytes
    }
}

/// Plugin manifest from plugin.json. `Uri?` → `String?`. (C# `PluginManifest`.)
public struct PluginManifest: Sendable, Equatable, Codable {
    public let name: String
    public let displayName: String
    public let version: String
    public let description: String
    public let artifactWasmUrl: String?
    public let frontendModuleUrl: String?
    public let extensionPoints: [PluginExtensionPoint]
    public let mcpTools: [String]
    public let sqlMigrationFiles: [String]
    public let limits: PluginResourceLimits

    public init(name: String, displayName: String, version: String, description: String,
                artifactWasmUrl: String?, frontendModuleUrl: String?,
                extensionPoints: [PluginExtensionPoint], mcpTools: [String],
                sqlMigrationFiles: [String], limits: PluginResourceLimits) {
        self.name = name; self.displayName = displayName; self.version = version
        self.description = description; self.artifactWasmUrl = artifactWasmUrl
        self.frontendModuleUrl = frontendModuleUrl; self.extensionPoints = extensionPoints
        self.mcpTools = mcpTools; self.sqlMigrationFiles = sqlMigrationFiles; self.limits = limits
    }
}

/// Installed instance. (C# `InstalledPlugin`.)
public struct InstalledPlugin: Sendable, Equatable, Codable {
    public let id: String
    public let manifest: PluginManifest
    public let installedFromCatalog: String
    public let installedAtUtc: Date
    public let enabled: Bool

    public init(id: String, manifest: PluginManifest, installedFromCatalog: String, installedAtUtc: Date, enabled: Bool) {
        self.id = id; self.manifest = manifest; self.installedFromCatalog = installedFromCatalog
        self.installedAtUtc = installedAtUtc; self.enabled = enabled
    }

    func with(enabled: Bool) -> InstalledPlugin {
        InstalledPlugin(id: id, manifest: manifest, installedFromCatalog: installedFromCatalog,
                        installedAtUtc: installedAtUtc, enabled: enabled)
    }
}

/// Plugin runtime host (wazero-style). Provided by the deploy. (C# `IPluginRuntimeHost`.)
public protocol IPluginRuntimeHost: Sendable {
    /// Install + initialise. Run SQL migrations + cache the WASM artifact.
    func install(_ plugin: InstalledPlugin) async throws
    /// Uninstall — drop WASM + clean artifacts.
    func uninstall(_ pluginId: String, dropArtifacts: Bool) async throws
    /// Hot-swap to a new version (semver upgrade).
    func upgrade(from: InstalledPlugin, to: InstalledPlugin) async throws
}

/// Plugin lifecycle manager. (C# `PacaPluginRegistry`.)
public final class PacaPluginRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var installed: [String: InstalledPlugin] = [:]
    private let runtime: any IPluginRuntimeHost
    private let clock: @Sendable () -> Date

    public init(runtime: any IPluginRuntimeHost, clock: (@Sendable () -> Date)? = nil) {
        self.runtime = runtime
        self.clock = clock ?? { Date() }
    }

    public func listInstalled() -> [InstalledPlugin] {
        lock.lock(); defer { lock.unlock() }
        return Array(installed.values)
    }

    public func get(_ id: String) -> InstalledPlugin? {
        lock.lock(); defer { lock.unlock() }
        return installed[id]
    }

    /// Validate a manifest before install / upgrade. (C# `ValidateManifest`.)
    public static func validateManifest(_ manifest: PluginManifest) throws {
        if !isReverseDns(manifest.name) {
            throw PacaError.argument("Plugin name '\(manifest.name)' must be reverse-DNS (e.g. com.paca.bdd).")
        }
        if parseVersion(stripPrerelease(manifest.version)) == nil {
            throw PacaError.argument("Plugin version '\(manifest.version)' is not parseable SemVer.")
        }
        if manifest.limits.callTimeoutMs <= 0 { throw PacaError.argument("CallTimeoutMs must be positive.") }
        if manifest.limits.memoryCeilingBytes <= 0 { throw PacaError.argument("MemoryCeilingBytes must be positive.") }
    }

    /// Install plugin from the supplied manifest.
    @discardableResult
    public func install(_ manifest: PluginManifest, catalog: String) async throws -> InstalledPlugin {
        try Self.validateManifest(manifest)
        lock.lock()
        if installed[manifest.name] != nil {
            lock.unlock(); throw PacaError.invalidOperation("Plugin '\(manifest.name)' is already installed; use upgrade.")
        }
        lock.unlock()

        let installedPlugin = InstalledPlugin(id: manifest.name, manifest: manifest, installedFromCatalog: catalog,
                                              installedAtUtc: clock(), enabled: true)
        try await runtime.install(installedPlugin)
        lock.lock(); installed[manifest.name] = installedPlugin; lock.unlock()
        return installedPlugin
    }

    /// Upgrade if `newManifest`'s SemVer is strictly newer.
    @discardableResult
    public func upgrade(_ newManifest: PluginManifest, catalog: String) async throws -> InstalledPlugin {
        try Self.validateManifest(newManifest)
        lock.lock(); let current = installed[newManifest.name]; lock.unlock()
        guard let current = current else {
            throw PacaError.invalidOperation("Plugin '\(newManifest.name)' is not installed.")
        }
        if Self.compareSemver(newManifest.version, current.manifest.version) <= 0 {
            throw PacaError.invalidOperation("Version \(newManifest.version) is not newer than \(current.manifest.version).")
        }
        let next = InstalledPlugin(id: newManifest.name, manifest: newManifest, installedFromCatalog: catalog,
                                   installedAtUtc: clock(), enabled: current.enabled)
        try await runtime.upgrade(from: current, to: next)
        lock.lock(); installed[newManifest.name] = next; lock.unlock()
        return next
    }

    public func uninstall(_ id: String, dropArtifacts: Bool = true) async throws {
        lock.lock(); let existed = installed.removeValue(forKey: id) != nil; lock.unlock()
        guard existed else { return }
        try await runtime.uninstall(id, dropArtifacts: dropArtifacts)
    }

    public func setEnabled(_ id: String, enabled: Bool) {
        lock.lock()
        if let current = installed[id] { installed[id] = current.with(enabled: enabled) }
        lock.unlock()
    }

    /// Compare SemVer-ish strings: returns <0 / 0 / >0. (C# `CompareSemver`.)
    public static func compareSemver(_ a: String, _ b: String) -> Int {
        let va = parseVersion(stripPrerelease(a)) ?? [0, 0, 0, 0]
        let vb = parseVersion(stripPrerelease(b)) ?? [0, 0, 0, 0]
        for i in 0..<4 {
            if va[i] != vb[i] { return va[i] < vb[i] ? -1 : 1 }
        }
        return 0
    }

    private static func stripPrerelease(_ v: String) -> String {
        // Split off the first '-' or '+' suffix.
        var out = v
        if let dash = out.firstIndex(of: "-") { out = String(out[out.startIndex..<dash]) }
        if let plus = out.firstIndex(of: "+") { out = String(out[out.startIndex..<plus]) }
        return out
    }

    /// Parse a dotted-numeric version into 4 padded components (major, minor,
    /// build, revision) — matching the `System.Version` subset paca uses.
    private static func parseVersion(_ v: String) -> [Int]? {
        let parts = v.split(separator: ".", omittingEmptySubsequences: false).map(String.init)
        guard parts.count >= 2, parts.count <= 4 else { return nil }
        var out: [Int] = [0, 0, 0, 0]
        for (i, p) in parts.enumerated() {
            guard let n = Int(p), n >= 0 else { return nil }
            out[i] = n
        }
        return out
    }

    private static func isReverseDns(_ name: String) -> Bool {
        // ^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$
        guard let regex = try? NSRegularExpression(pattern: "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_-]*)+$") else { return false }
        let range = NSRange(name.startIndex..<name.endIndex, in: name)
        return regex.firstMatch(in: name, range: range) != nil
    }
}

// MARK: ── PacaMcp ─────────────────────────────────────────────────────────

/// MCP transport types. (C# `McpTransportKind`.)
public enum McpTransportKind: Int, Sendable, Codable, CaseIterable {
    case stdio = 0
    case serverSentEvents = 1
    case http = 2
}

/// Per-agent MCP server config. (C# `AgentMcpConfig`.)
public struct AgentMcpConfig: Sendable, Equatable, Codable {
    public let agentMemberId: String
    public let transports: [McpTransportKind]
    public let enabledTools: [String]
    public let toolSettings: [String: String]

    public init(agentMemberId: String, transports: [McpTransportKind], enabledTools: [String], toolSettings: [String: String]) {
        self.agentMemberId = agentMemberId; self.transports = transports
        self.enabledTools = enabledTools; self.toolSettings = toolSettings
    }
}

/// MCP tool descriptor. (C# `PacaMcpTool`.)
public struct PacaMcpTool: Sendable, Equatable, Codable {
    public let name: String
    public let description: String
    public let inputSchema: String

    public init(name: String, description: String, inputSchema: String) {
        self.name = name; self.description = description; self.inputSchema = inputSchema
    }
}

/// MCP tool handler signature. (C# `PacaMcpHandler` delegate.)
public typealias PacaMcpHandler = @Sendable (_ argumentsJson: String) async -> String

/// Paca's MCP server: registers built-in workflow tools + plugin tools.
/// (C# `PacaMcpServer`.)
public final class PacaMcpServer: @unchecked Sendable {
    private let lock = NSLock()
    private var tools: [String: (tool: PacaMcpTool, handler: PacaMcpHandler)] = [:]  // keyed lower-case
    private var agentConfigs: [String: AgentMcpConfig] = [:]

    public init() {}

    public var toolList: [PacaMcpTool] {
        lock.lock(); let all = tools.values.map { $0.tool }; lock.unlock()
        return all
    }

    public func registerTool(_ tool: PacaMcpTool, handler: @escaping PacaMcpHandler) {
        lock.lock(); tools[tool.name.lowercased()] = (tool, handler); lock.unlock()
    }

    /// Configure a per-agent toolset.
    public func configureAgent(_ config: AgentMcpConfig) {
        lock.lock(); agentConfigs[config.agentMemberId] = config; lock.unlock()
    }

    public func getAgentConfig(_ agentMemberId: String) -> AgentMcpConfig? {
        lock.lock(); defer { lock.unlock() }
        return agentConfigs[agentMemberId]
    }

    /// Invoke a tool for a specific agent — enforces the agent's enabled-tool list.
    public func invoke(agentMemberId: String, toolName: String, argumentsJson: String) async -> String {
        lock.lock()
        let entry = tools[toolName.lowercased()]
        let cfg = agentConfigs[agentMemberId]
        lock.unlock()

        guard let entry = entry else { return Self.wrapError("Unknown tool '\(toolName)'.") }
        if let cfg = cfg, !cfg.enabledTools.isEmpty,
           !cfg.enabledTools.contains(where: { $0.caseInsensitiveCompare(toolName) == .orderedSame }) {
            return Self.wrapError("Tool '\(toolName)' is not enabled for agent '\(agentMemberId)'.")
        }
        return await entry.handler(argumentsJson)
    }

    /// JSON-RPC tools/list response payload. Emits a `tools` array with each
    /// tool's name/description and its parsed input schema.
    public func toolsListJson() -> String {
        lock.lock(); let all = tools.values.map { $0.tool }; lock.unlock()
        var toolObjs: [[String: Any]] = []
        for t in all {
            var obj: [String: Any] = ["name": t.name, "description": t.description]
            if let data = t.inputSchema.data(using: .utf8),
               let parsed = try? JSONSerialization.jsonObject(with: data) {
                obj["inputSchema"] = parsed
            } else {
                obj["inputSchema"] = [:]
            }
            toolObjs.append(obj)
        }
        let root: [String: Any] = ["tools": toolObjs]
        guard let data = try? JSONSerialization.data(withJSONObject: root),
              let s = String(data: data, encoding: .utf8) else { return "{\"tools\":[]}" }
        return s
    }

    private static func wrapError(_ message: String) -> String {
        let root: [String: Any] = ["error": ["message": message]]
        guard let data = try? JSONSerialization.data(withJSONObject: root),
              let s = String(data: data, encoding: .utf8) else { return "{\"error\":{\"message\":\"\(message)\"}}" }
        return s
    }
}

/// Built-in workflow tools. (C# `PacaCoreMcpTools`.)
public enum PacaCoreMcpTools {
    public static let createTask = PacaMcpTool(
        name: "create_task",
        description: "Create a new task in a project.",
        inputSchema: "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}},\"required\":[\"project_id\",\"title\"]}")

    public static let listTasks = PacaMcpTool(
        name: "list_tasks",
        description: "List live tasks in a project.",
        inputSchema: "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"}},\"required\":[\"project_id\"]}")

    public static let editTask = PacaMcpTool(
        name: "edit_task",
        description: "Edit a task (title, description, status).",
        inputSchema: "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"number\":{\"type\":\"integer\"},\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"}},\"required\":[\"project_id\",\"number\"]}")

    public static let createDoc = PacaMcpTool(
        name: "create_doc",
        description: "Create a doc in the project's doc tree.",
        inputSchema: "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"parent_id\":{\"type\":\"string\",\"nullable\":true},\"content_json\":{\"type\":\"string\"}},\"required\":[\"project_id\",\"title\",\"content_json\"]}")

    public static let linkDocToTask = PacaMcpTool(
        name: "link_doc_to_task",
        description: "Link a doc section to a task.",
        inputSchema: "{\"type\":\"object\",\"properties\":{\"doc_id\":{\"type\":\"string\"},\"section_anchor\":{\"type\":\"string\"},\"project_id\":{\"type\":\"string\"},\"task_number\":{\"type\":\"integer\"}},\"required\":[\"doc_id\",\"section_anchor\",\"project_id\",\"task_number\"]}")
}

// MARK: ── PacaRealtime ────────────────────────────────────────────────────

/// Realtime event union. Modelled as an enum with associated payloads because
/// C#'s abstract-record hierarchy is switched-on downstream. Every case carries
/// a `projectId` + `at`, surfaced via computed properties. (C# `RealtimePacaEvent`.)
public enum RealtimePacaEvent: Sendable {
    case taskUpdated(projectId: String, at: Date, taskNumber: Int)
    case queryInvalidation(projectId: String, at: Date, queryKey: String)
    case docCursorMove(projectId: String, at: Date, docId: String, memberId: String, cursorOffset: Int)
    case agentActivity(projectId: String, at: Date, agentMemberId: String, action: String, detailJson: String)
    case conversationStep(projectId: String, at: Date, conversationId: String, step: ConversationStep)

    public var projectId: String {
        switch self {
        case .taskUpdated(let p, _, _),
             .queryInvalidation(let p, _, _),
             .docCursorMove(let p, _, _, _, _),
             .agentActivity(let p, _, _, _, _),
             .conversationStep(let p, _, _, _):
            return p
        }
    }

    public var at: Date {
        switch self {
        case .taskUpdated(_, let a, _),
             .queryInvalidation(_, let a, _),
             .docCursorMove(_, let a, _, _, _),
             .agentActivity(_, let a, _, _, _),
             .conversationStep(_, let a, _, _):
            return a
        }
    }
}

/// Host-supplied broadcaster (Socket.IO / Valkey Streams / etc.). (C# `IRealtimeBroadcaster`.)
public protocol IRealtimeBroadcaster: Sendable {
    func broadcast(room: String, event: RealtimePacaEvent) async
}

/// Permission check — returns true if the member may join the room.
/// (C# `PermissionCheck` delegate.)
public typealias PermissionCheck = @Sendable (_ memberId: String, _ room: String) async -> Bool

/// Realtime hub: routes events into rooms, gates joins with a permission check.
/// (C# `PacaRealtimeHub`.)
public final class PacaRealtimeHub: @unchecked Sendable {
    private let broadcaster: any IRealtimeBroadcaster
    private let permission: PermissionCheck
    private let lock = NSLock()
    private var membersByRoom: [String: Set<String>] = [:]

    public init(broadcaster: any IRealtimeBroadcaster, permission: PermissionCheck? = nil) {
        self.broadcaster = broadcaster
        self.permission = permission ?? { _, _ in true }
    }

    /// Member tries to join a room. Returns true if permission allowed.
    @discardableResult
    public func join(memberId: String, room: String) async -> Bool {
        if !(await permission(memberId, room)) { return false }
        lock.lock(); membersByRoom[room, default: []].insert(memberId); lock.unlock()
        return true
    }

    public func leave(memberId: String, room: String) {
        lock.lock(); membersByRoom[room]?.remove(memberId); lock.unlock()
    }

    public func members(_ room: String) -> [String] {
        lock.lock(); defer { lock.unlock() }
        return Array(membersByRoom[room] ?? [])
    }

    /// Publish an event to the project's main room.
    public func publish(_ event: RealtimePacaEvent) async {
        await broadcaster.broadcast(room: "project:\(event.projectId)", event: event)
    }

    /// Publish to a doc collaboration sub-room.
    public func publishToDoc(_ docId: String, event: RealtimePacaEvent) async {
        await broadcaster.broadcast(room: "doc:\(docId)", event: event)
    }
}

/// Maps known events to query-invalidation keys for client UIs. (C# `QueryInvalidation`.)
public enum QueryInvalidation {
    public static func keysFor(_ event: RealtimePacaEvent) -> [String] {
        switch event {
        case .taskUpdated(let p, _, let n):
            return ["tasks/\(p)", "task/\(p)/\(n)"]
        case .agentActivity(let p, _, let agent, _, _):
            return ["activity/\(p)", "agent/\(agent)"]
        case .conversationStep(let p, _, let convId, _):
            return ["conversation/\(convId)", "conversations/\(p)"]
        case .docCursorMove(_, _, let docId, _, _):
            return ["doc/\(docId)/cursors"]
        case .queryInvalidation(_, _, let key):
            return [key]
        }
    }
}

// MARK: ── PacaSkills ──────────────────────────────────────────────────────

/// A skill definition: frontmatter metadata + body. (C# `PacaSkill`.)
public struct PacaSkill: Sendable, Equatable, Codable {
    public let name: String
    public let description: String
    public let body: String

    public init(name: String, description: String, body: String) {
        self.name = name; self.description = description; self.body = body
    }

    /// Render as a Claude-Code-compatible markdown file with frontmatter.
    public func toMarkdown() -> String {
        "---\nname: \(name)\ndescription: \(description)\n---\n\n\(body)"
    }

    /// Render as the bare body (frontmatter stripped) for the installer.
    public func toBodyOnly() -> String { body }
}

/// The nine creator-skill templates (markdown body). (C# `SkillTemplates`.)
public enum SkillTemplates {
    public static let epic = "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks."
    public static let breakdown = "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria."
    public static let clarify = "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task."
    public static let sprint = "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools."
    public static let estimate = "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions."
    public static let prioritize = "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning."
    public static let doTask = "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done."
    public static let test = "You are running paca-test. Write and run unit + integration tests for the current change."
    public static let doc = "You are running paca-doc. Update the living document with the smallest accurate diff."
}

/// The eleven built-in paca skills. (C# `PacaSkillLibrary`.)
public enum PacaSkillLibrary {
    public static let all: [PacaSkill] = [
        PacaSkill(name: "paca", description: "Run the paca workflow on the current ask.", body: "Use the paca MCP tools to plan and execute the user's request."),
        PacaSkill(name: "paca-epic", description: "Capture a large initiative as a paca epic.", body: SkillTemplates.epic),
        PacaSkill(name: "paca-breakdown", description: "Break a paca epic into actionable tasks.", body: SkillTemplates.breakdown),
        PacaSkill(name: "paca-clarify", description: "Ask the right clarifying questions before estimating.", body: SkillTemplates.clarify),
        PacaSkill(name: "paca-sprint", description: "Form / close a sprint with the paca sprint surface.", body: SkillTemplates.sprint),
        PacaSkill(name: "paca-estimate", description: "Estimate story points for a set of tasks.", body: SkillTemplates.estimate),
        PacaSkill(name: "paca-prioritize", description: "Reorder the backlog by importance.", body: SkillTemplates.prioritize),
        PacaSkill(name: "paca-do", description: "Pick the next-best task and start it.", body: SkillTemplates.doTask),
        PacaSkill(name: "paca-test", description: "Generate and run tests for the current change.", body: SkillTemplates.test),
        PacaSkill(name: "paca-doc", description: "Update the project's living doc to reflect the latest change.", body: SkillTemplates.doc),
        PacaSkill(name: "paca-setup", description: "First-run setup: pick project, configure agents, install plugins.", body: "Walk the user through paca first-run setup."),
    ]

    public static func find(_ name: String) -> PacaSkill? {
        all.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }
    }
}

/// Installer that drops bare skill bodies into ~/.claude/commands/. (C# `PacaSkillInstaller`.)
public final class PacaSkillInstaller: @unchecked Sendable {
    private let commandsDir: String

    public init(commandsDir: String) throws {
        guard !commandsDir.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PacaError.argument("commandsDir required")
        }
        self.commandsDir = commandsDir
    }

    /// Install all built-in skills. Returns the written file paths.
    @discardableResult
    public func installAll() throws -> [String] { try installEach(PacaSkillLibrary.all) }

    /// Install a custom set of skills. Returns the written file paths.
    @discardableResult
    public func installEach(_ skills: [PacaSkill]) throws -> [String] {
        try FileManager.default.createDirectory(atPath: commandsDir, withIntermediateDirectories: true)
        var installed: [String] = []
        for skill in skills {
            let path = (commandsDir as NSString).appendingPathComponent("\(skill.name).md")
            let body = Self.stripFrontmatter(skill.toMarkdown())
            try body.write(toFile: path, atomically: true, encoding: .utf8)
            installed.append(path)
        }
        return installed
    }

    /// Uninstall a set of skills by name. Returns the count removed.
    @discardableResult
    public func uninstallByName(_ names: [String]) -> Int {
        var count = 0
        for name in names {
            let path = (commandsDir as NSString).appendingPathComponent("\(name).md")
            if FileManager.default.fileExists(atPath: path) {
                if (try? FileManager.default.removeItem(atPath: path)) != nil { count += 1 }
            }
        }
        return count
    }

    /// Strip the frontmatter block from a markdown skill file. (C# `StripFrontmatter`.)
    public static func stripFrontmatter(_ markdown: String) -> String {
        if markdown.isEmpty { return "" }
        // ^\s*---.*?---\s*\n  (singleline / dotall), anchored at index 0.
        guard let regex = try? NSRegularExpression(pattern: "^\\s*---.*?---\\s*\\n", options: [.dotMatchesLineSeparators]) else {
            return leadingTrimmed(markdown)
        }
        let range = NSRange(markdown.startIndex..<markdown.endIndex, in: markdown)
        guard let match = regex.firstMatch(in: markdown, range: range), match.range.location == 0,
              let r = Range(match.range, in: markdown) else {
            return leadingTrimmed(markdown)
        }
        return leadingTrimmed(String(markdown[r.upperBound...]))
    }

    private static func leadingTrimmed(_ s: String) -> String {
        var idx = s.startIndex
        while idx < s.endIndex, s[idx].isWhitespace { idx = s.index(after: idx) }
        return String(s[idx...])
    }
}

// MARK: ── PacaDeploy ──────────────────────────────────────────────────────

/// Deployment mode. (C# `PacaDeployMode`.)
public enum PacaDeployMode: Int, Sendable, Codable, CaseIterable {
    case dev = 0
    case prod = 1
    case e2e = 2

    var lowerName: String {
        switch self {
        case .dev: return "dev"
        case .prod: return "prod"
        case .e2e: return "e2e"
        }
    }
}

/// Optional overrides. (C# `PacaDeployOverrides`.)
public struct PacaDeployOverrides: Sendable, Equatable, Codable {
    public let useExternalPostgres: String?
    public let useExternalS3: String?
    public let skipAiAgent: Bool

    public init(useExternalPostgres: String? = nil, useExternalS3: String? = nil, skipAiAgent: Bool = false) {
        self.useExternalPostgres = useExternalPostgres
        self.useExternalS3 = useExternalS3
        self.skipAiAgent = skipAiAgent
    }
}

/// Compose-file + .env pair the installer writes. (C# `PacaDeployArtifact`.)
public struct PacaDeployArtifact: Sendable, Equatable, Codable {
    public let composeYaml: String
    public let envFile: String

    public init(composeYaml: String, envFile: String) {
        self.composeYaml = composeYaml; self.envFile = envFile
    }
}

/// Generates compose + .env files for the paca stack. (C# `PacaDeployer`.)
public enum PacaDeployer {
    /// Build the compose + env pair for a given mode.
    public static func build(_ mode: PacaDeployMode, overrides: PacaDeployOverrides? = nil) -> PacaDeployArtifact {
        let overrides = overrides ?? PacaDeployOverrides()
        var sb = ""
        func line(_ s: String) { sb += s + "\n" }

        line("version: '3.9'")
        line("services:")

        line("  paca-web:")
        line("    image: bhengubv/paca-web:\(mode == .prod ? "stable" : "latest")")
        line("    env_file: [.env]")
        line("    ports:")
        line("      - \"\(mode == .prod ? 443 : 8080):8080\"")

        if (overrides.useExternalPostgres ?? "").isEmpty {
            line("  paca-postgres:")
            line("    image: postgres:16-alpine")
            line("    environment:")
            line("      POSTGRES_USER:     ${PACA_PG_USER}")
            line("      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}")
            line("      POSTGRES_DB:       ${PACA_PG_DB}")
            line("    volumes: [paca_pg_data:/var/lib/postgresql/data]")
        }

        line("  paca-valkey:")
        line("    image: valkey/valkey:8")

        if (overrides.useExternalS3 ?? "").isEmpty {
            line("  paca-minio:")
            line("    image: minio/minio:latest")
            line("    environment:")
            line("      MINIO_ROOT_USER:     ${PACA_S3_KEY}")
            line("      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}")
            line("    command: server /data")
        }

        line("  paca-nginx:")
        line("    image: nginx:1.27-alpine")

        if !overrides.skipAiAgent {
            line("  paca-ai:")
            line("    image: bhengubv/paca-ai:latest")
            line("    env_file: [.env]")
        }

        if (overrides.useExternalPostgres ?? "").isEmpty {
            line("volumes:")
            line("  paca_pg_data: {}")
        }

        let env = buildEnvFile(mode, overrides: overrides)
        return PacaDeployArtifact(composeYaml: sb, envFile: env)
    }

    /// Build the bash install-plugin script that drives the plugin lifecycle from CLI.
    public static func buildInstallPluginScript(_ pluginName: String) throws -> String {
        guard !pluginName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PacaError.argument("pluginName required")
        }
        return """
        #!/usr/bin/env bash
        set -euo pipefail
        echo "[paca] Building WASM module for \(pluginName)..."
        wasm-pack build --target web ./plugins/\(pluginName)
        echo "[paca] Building frontend bundle..."
        cd ./plugins/\(pluginName)/frontend && pnpm install && pnpm build
        cd -
        echo "[paca] Registering plugin with the API..."
        paca-cli plugins install ./plugins/\(pluginName)/dist
        echo "[paca] Done."
        """
    }

    /// Bash script that uninstalls + cleans plugin artifacts.
    public static func buildUninstallPluginScript(_ pluginName: String) throws -> String {
        guard !pluginName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PacaError.argument("pluginName required")
        }
        return """
        #!/usr/bin/env bash
        set -euo pipefail
        echo "[paca] Uninstalling \(pluginName)..."
        paca-cli plugins uninstall \(pluginName)
        rm -rf ./plugins/\(pluginName)/dist
        echo "[paca] Done."
        """
    }

    private static func buildEnvFile(_ mode: PacaDeployMode, overrides: PacaDeployOverrides) -> String {
        var sb = ""
        func line(_ s: String) { sb += s + "\n" }
        line("PACA_MODE=\(mode.lowerName)")
        line("PACA_PG_USER=paca")
        line("PACA_PG_PASSWORD=\(randomSecret(32))")
        line("PACA_PG_DB=paca")
        if let pg = overrides.useExternalPostgres, !pg.isEmpty {
            line("PACA_PG_URL=\(pg)")
        }
        line("PACA_VALKEY_URL=redis://paca-valkey:6379")
        line("PACA_S3_KEY=\(randomSecret(20))")
        line("PACA_S3_SECRET=\(randomSecret(40))")
        if let s3 = overrides.useExternalS3, !s3.isEmpty {
            line("PACA_S3_ENDPOINT=\(s3)")
        }
        line("PACA_JWT_SIGNING_SECRET=\(randomSecret(48))")
        line("PACA_AI_ENABLED=\(!overrides.skipAiAgent)")
        return sb
    }

    private static func randomSecret(_ length: Int) -> String {
        // URL-safe base64; trim padding; truncate to the requested length. To
        // guarantee enough characters, generate extra raw bytes first.
        var bytes = [UInt8](repeating: 0, count: length + 8)
        for i in 0..<bytes.count { bytes[i] = UInt8.random(in: 0...255) }
        let encoded = Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        return String(encoded.prefix(length))
    }
}
