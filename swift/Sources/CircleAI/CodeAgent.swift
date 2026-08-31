// CodeAgent.swift
//
// An on-device coding agent: one JSON action per turn, executed against real
// seams (an editor, a command runner, a code search), with a device gate in
// front of the loop so a phone that cannot do this never pretends it can.
//
// Ported from src/CircleAI.CodeAgent.

import Foundation

// MARK: - Actions

/// The one thing the model asked for this turn.
public enum AgentActionKind: Int, Sendable, Equatable {
    case unknown = 0
    case readFile
    case editFile
    case runCommand
    case searchCode
    case finish
}

/// A parsed action. Every field is optional because the model supplies them and
/// the model is not to be trusted; ``AgentActionParser`` never throws.
public struct AgentAction: Sendable, Equatable {
    public let kind: AgentActionKind
    public let path: String?
    public let rangeStart: Int
    public let rangeEnd: Int
    public let replacement: String?
    public let executable: String?
    public let args: [String]?
    public let query: String?
    public let topK: Int
    public let summary: String?
    /// What the model actually said, kept so a parse failure can be shown back.
    public let raw: String?

    public init(
        kind: AgentActionKind,
        path: String? = nil,
        rangeStart: Int = 0,
        rangeEnd: Int = 0,
        replacement: String? = nil,
        executable: String? = nil,
        args: [String]? = nil,
        query: String? = nil,
        topK: Int = 10,
        summary: String? = nil,
        raw: String? = nil
    ) {
        self.kind = kind
        self.path = path
        self.rangeStart = rangeStart
        self.rangeEnd = rangeEnd
        self.replacement = replacement
        self.executable = executable
        self.args = args
        self.query = query
        self.topK = topK
        self.summary = summary
        self.raw = raw
    }
}

/// Turns whatever the model said into an action, or into `.unknown`.
public enum AgentActionParser {

    /// Never throws. A reply that is prose, truncated JSON or an action nobody
    /// has heard of all come back as `.unknown` carrying the raw text, so the
    /// loop can show the model what it did wrong instead of dying.
    public static func parse(_ modelText: String?) -> AgentAction {
        guard let modelText, !modelText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return AgentAction(kind: .unknown, raw: modelText)
        }
        guard let json = extractFirstJsonObject(modelText) else {
            return AgentAction(kind: .unknown, raw: modelText)
        }
        guard
            let data = json.data(using: .utf8),
            let parsed = try? JSONSerialization.jsonObject(with: data),
            let root = parsed as? [String: Any]
        else {
            return AgentAction(kind: .unknown, raw: modelText)
        }

        let action = string(root, "action")?
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()

        switch action {
        case "read_file":
            return AgentAction(kind: .readFile, path: string(root, "path"), raw: json)
        case "edit_file":
            return AgentAction(
                kind: .editFile,
                path: string(root, "path"),
                rangeStart: int(root, "range_start"),
                rangeEnd: int(root, "range_end"),
                replacement: string(root, "replacement") ?? "",
                raw: json)
        case "run_command":
            return AgentAction(
                kind: .runCommand,
                path: string(root, "cwd"),
                executable: string(root, "executable"),
                args: stringArray(root, "args"),
                raw: json)
        case "search_code":
            return AgentAction(
                kind: .searchCode,
                query: string(root, "query"),
                topK: int(root, "top_k", fallback: 10),
                raw: json)
        case "finish":
            return AgentAction(kind: .finish, summary: string(root, "summary") ?? "", raw: json)
        default:
            return AgentAction(kind: .unknown, raw: json)
        }
    }

    /// The first balanced `{...}` in the text, brace-counting with string and
    /// escape awareness so a `}` inside a quoted replacement does not end it.
    /// Models wrap JSON in prose and code fences; this is what survives that.
    static func extractFirstJsonObject(_ text: String) -> String? {
        let chars = Array(text)
        guard let start = chars.firstIndex(of: "{") else { return nil }

        var depth = 0
        var inString = false
        var escape = false

        for i in start..<chars.count {
            let c = chars[i]
            if escape { escape = false; continue }
            if inString {
                if c == "\\" { escape = true }
                else if c == "\"" { inString = false }
                continue
            }
            switch c {
            case "\"": inString = true
            case "{": depth += 1
            case "}":
                depth -= 1
                if depth == 0 { return String(chars[start...i]) }
            default: break
            }
        }
        return nil
    }

    private static func string(_ o: [String: Any], _ name: String) -> String? {
        // NSNumber bridges to String in some casts; require the real thing.
        guard let v = o[name], let s = v as? String, !(v is NSNumber) else { return nil }
        return s
    }

    private static func int(_ o: [String: Any], _ name: String, fallback: Int = 0) -> Int {
        guard let v = o[name], let n = v as? NSNumber else { return fallback }
        // JSON true/false arrive as NSNumber too; a boolean is not a number here.
        if CFGetTypeID(n as CFTypeRef) == CFBooleanGetTypeID() { return fallback }
        let d = n.doubleValue
        guard d.rounded() == d, d >= Double(Int32.min), d <= Double(Int32.max) else { return fallback }
        return Int(n.int32Value)
    }

    private static func stringArray(_ o: [String: Any], _ name: String) -> [String] {
        guard let v = o[name] as? [Any] else { return [] }
        return v.compactMap { e in
            guard let s = e as? String, !(e is NSNumber) else { return nil }
            return s
        }
    }
}

// MARK: - What a coding model has to be

/// The floor a device and a model must both clear before coding is offered.
public struct CodingModelRequirements: Sendable, Equatable {
    public let minParametersBillion: Int
    public let minRamGb: Double
    public let minFreeStorageGb: Double
    public let minDeviceTier: DeviceTier
    public let requiredCapabilities: ChatCapability

    public init(
        minParametersBillion: Int,
        minRamGb: Double,
        minFreeStorageGb: Double,
        minDeviceTier: DeviceTier,
        requiredCapabilities: ChatCapability
    ) {
        self.minParametersBillion = minParametersBillion
        self.minRamGb = minRamGb
        self.minFreeStorageGb = minFreeStorageGb
        self.minDeviceTier = minDeviceTier
        self.requiredCapabilities = requiredCapabilities
    }

    /// Deliberately high. A 1B model that cannot hold a file in context writes
    /// code that compiles and does the wrong thing, which is worse than nothing.
    public static let `default` = CodingModelRequirements(
        minParametersBillion: 3,
        minRamGb: 8.0,
        minFreeStorageGb: 6.0,
        minDeviceTier: .tablet,
        requiredCapabilities: [.tools, .reasoning, .longContext])
}

/// One catalogued coding model.
public struct CodingModelDescriptor: Sendable, Equatable {
    public let modelId: String
    public let parametersBillion: Int
    public let minRamGb: Double
    public let minFreeStorageGb: Double
    public let totalBytes: Int64
    /// Non-empty, always: an unverifiable bundle is refused at registration.
    public let sha256: String
    public let capabilities: ChatCapability

    public init(
        modelId: String,
        parametersBillion: Int,
        minRamGb: Double,
        minFreeStorageGb: Double,
        totalBytes: Int64,
        sha256: String,
        capabilities: ChatCapability
    ) {
        self.modelId = modelId
        self.parametersBillion = parametersBillion
        self.minRamGb = minRamGb
        self.minFreeStorageGb = minFreeStorageGb
        self.totalBytes = totalBytes
        self.sha256 = sha256
        self.capabilities = capabilities
    }
}

/// Where the coding models are declared. Empty is the honest default.
public protocol ICodingModelCatalog: Sendable {
    var backendId: String { get }
    var available: [CodingModelDescriptor] { get }
}

/// No coding model is installed - which is the truth on most builds.
public struct EmptyCodingModelCatalog: ICodingModelCatalog {
    public static let instance = EmptyCodingModelCatalog()
    public init() {}
    public var backendId: String { "empty" }
    public var available: [CodingModelDescriptor] { [] }
}

/// Registration errors. A model without a hash is refused, not warned about.
public enum CodingCatalogError: Error, CustomStringConvertible, Equatable {
    case unverifiable(String)
    public var description: String {
        switch self {
        case .unverifiable(let id):
            return "A coding model MUST carry a SHA-256 verification hash. Refusing to register " +
                   "an unverifiable bundle (\(id)) - that would fake on-device availability."
        }
    }
}

/// A catalogue the host fills in at startup. Idempotent by model id.
public final class InMemoryCodingModelCatalog: ICodingModelCatalog, @unchecked Sendable {
    private let lock = NSLock()
    private var models: [CodingModelDescriptor] = []

    public init(seed: [CodingModelDescriptor]? = nil) throws {
        if let seed {
            for d in seed { _ = try add(d) }
        }
    }

    public var backendId: String { "in-memory" }

    public var available: [CodingModelDescriptor] {
        lock.lock(); defer { lock.unlock() }
        return models
    }

    /// Registers a model. Adding the same id twice is a no-op, not an error.
    @discardableResult
    public func add(_ descriptor: CodingModelDescriptor) throws -> InMemoryCodingModelCatalog {
        guard !descriptor.sha256.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CodingCatalogError.unverifiable(descriptor.modelId)
        }
        lock.lock(); defer { lock.unlock() }
        if models.contains(where: { $0.modelId.lowercased() == descriptor.modelId.lowercased() }) {
            return self
        }
        models.append(descriptor)
        return self
    }
}

// MARK: - Running commands

/// One command to run, with the working directory and a timeout.
public struct CommandRequest: Sendable, Equatable {
    public let executable: String
    public let arguments: [String]
    public let workingDirectory: String
    public let timeoutMs: Int

    public init(executable: String, arguments: [String], workingDirectory: String, timeoutMs: Int = 60_000) {
        self.executable = executable
        self.arguments = arguments
        self.workingDirectory = workingDirectory
        self.timeoutMs = timeoutMs
    }
}

/// What happened. `executed == false` means it never ran, and `denied` says why.
public struct CommandResult: Sendable, Equatable {
    public let executed: Bool
    public let exitCode: Int32
    public let stdout: String
    public let stderr: String
    public let timedOut: Bool
    public let denied: String?

    public init(executed: Bool, exitCode: Int32, stdout: String, stderr: String,
                timedOut: Bool, denied: String? = nil) {
        self.executed = executed
        self.exitCode = exitCode
        self.stdout = stdout
        self.stderr = stderr
        self.timedOut = timedOut
        self.denied = denied
    }

    public var success: Bool { executed && !timedOut && exitCode == 0 }

    public static func notRun(_ reason: String) -> CommandResult {
        CommandResult(executed: false, exitCode: -1, stdout: "", stderr: "", timedOut: false, denied: reason)
    }
}

/// The seam that actually runs things.
public protocol ICommandRunner: Sendable {
    var backendId: String { get }
    func run(_ request: CommandRequest) async -> CommandResult
}

/// The default. Running arbitrary commands is opt-in, and this is what "not
/// opted in" looks like: a refusal with a reason, not a silent failure.
public struct DisabledCommandRunner: ICommandRunner {
    public static let instance = DisabledCommandRunner()
    public init() {}
    public var backendId: String { "disabled" }
    public func run(_ request: CommandRequest) async -> CommandResult {
        CommandResult.notRun(
            "command execution is disabled. The host must opt in with a ProcessCommandRunner allow-list.")
    }
}

#if os(macOS) || os(Linux)
/// Runs commands from an allow-list. There is no unrestricted mode: a runner
/// built with an empty allow-list throws rather than becoming a shell.
public final class ProcessCommandRunner: ICommandRunner, @unchecked Sendable {
    private let allowed: Set<String>
    private let maxOutputChars: Int

    public enum RunnerError: Error, CustomStringConvertible, Equatable {
        case emptyAllowList
        public var description: String {
            "An allow-list with at least one executable is required. Refusing to run an unrestricted shell."
        }
    }

    public init(allowedExecutables: [String], maxOutputChars: Int = 64 * 1024) throws {
        let set = Set(allowedExecutables.map { $0.lowercased() })
        guard !set.isEmpty else { throw RunnerError.emptyAllowList }
        self.allowed = set
        self.maxOutputChars = maxOutputChars > 0 ? maxOutputChars : 64 * 1024
    }

    public var backendId: String { "process" }

    public func run(_ request: CommandRequest) async -> CommandResult {
        let name = (request.executable as NSString).lastPathComponent.lowercased()
        guard allowed.contains(name) || allowed.contains(request.executable.lowercased()) else {
            return CommandResult.notRun("'\(request.executable)' is not on the allow-list.")
        }
        // OFF THE COOPERATIVE POOL. Waiting on a process is a blocking wait, and
        // doing it on an async executor thread starves every other task on it.
        return await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: self.runBlocking(request))
            }
        }
    }

    private func runBlocking(_ request: CommandRequest) -> CommandResult {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: request.executable)
        process.arguments = request.arguments
        process.currentDirectoryURL = URL(fileURLWithPath: request.workingDirectory)

        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err

        do { try process.run() }
        catch { return CommandResult.notRun("could not start: \(error.localizedDescription)") }

        // READ BEFORE WAITING. A command that fills the 64 KB pipe buffer blocks
        // on its own write and never exits, so waiting first would hang forever
        // on exactly the verbose build output this exists to capture.
        let outData = out.fileHandleForReading.readDataToEndOfFile()
        let errData = err.fileHandleForReading.readDataToEndOfFile()

        let deadline = DispatchTime.now() + .milliseconds(request.timeoutMs)
        var timedOut = false
        let done = DispatchSemaphore(value: 0)
        DispatchQueue.global(qos: .utility).async {
            process.waitUntilExit()
            done.signal()
        }
        if done.wait(timeout: deadline) == .timedOut {
            timedOut = true
            process.terminate()
            _ = done.wait(timeout: .now() + .seconds(2))
        }

        return CommandResult(
            executed: true,
            exitCode: timedOut ? -1 : process.terminationStatus,
            stdout: clamp(String(data: outData, encoding: .utf8) ?? ""),
            stderr: clamp(String(data: errData, encoding: .utf8) ?? ""),
            timedOut: timedOut)
    }

    private func clamp(_ s: String) -> String {
        s.count <= maxOutputChars ? s : String(s.prefix(maxOutputChars)) + "\n...[truncated]"
    }
}
#endif

// MARK: - The device gate

/// Decides whether this device can code at all, before any model is loaded.
public protocol ICodingCapabilityPlanner: Sendable {
    func planForCoding(_ probe: DeviceProbe?) -> ModalityPlan
}

/// The honest planner: a weak phone, or a build with no installed coding model,
/// is told so in words rather than being let into a loop it cannot finish.
public struct CodingCapabilityPlanner: ICodingCapabilityPlanner {
    private let catalog: any ICodingModelCatalog
    private let req: CodingModelRequirements

    public init(catalog: (any ICodingModelCatalog)? = nil, requirements: CodingModelRequirements? = nil) {
        self.catalog = catalog ?? EmptyCodingModelCatalog.instance
        self.req = requirements ?? .default
    }

    public func planForCoding(_ probe: DeviceProbe? = nil) -> ModalityPlan {
        let probe = probe ?? DeviceProbe.snapshot()
        let tier = probe.classify()

        // The floor uses raw free bytes; the fit uses the headroom-scaled figure.
        let floorRamGb = Double(probe.ramAvailableBytes) / (1024.0 * 1024 * 1024)
        let floorStorage = Double(probe.storageFreeBytes) / (1024.0 * 1024 * 1024)
        let fitRamGb = probe.usableRamGb
        let fitStorageGb = probe.storageFreeGb

        if tier.rawValue < req.minDeviceTier.rawValue || floorRamGb + 0.0001 < req.minRamGb {
            return ModalityPlan(quality: .unavailable, model: nil, reason:
                "on-device coding needs ~\(fmt(req.minRamGb)) GB free RAM and tier >= \(req.minDeviceTier); " +
                "this device has \(fmt(floorRamGb)) GB free and is tier \(tier). Unavailable by design.")
        }

        if floorStorage > 0 && floorStorage + 0.0001 < req.minFreeStorageGb {
            return ModalityPlan(quality: .unavailable, model: nil, reason:
                "a \(req.minParametersBillion)B+ coding model needs ~\(fmt(req.minFreeStorageGb)) GB free storage; " +
                "only \(fmt(floorStorage)) GB available.")
        }

        if catalog.available.isEmpty {
            return ModalityPlan(quality: .unavailable, model: nil, reason:
                "device is capable, but no on-device coding model is installed. A real 3-7B coding " +
                "model requires a downloaded, SHA-256-verified bundle this build does not carry. " +
                "Register one via ICodingModelCatalog to enable.")
        }

        let fits = catalog.available
            .filter { $0.capabilities.intersection(req.requiredCapabilities) == req.requiredCapabilities }
            .filter { $0.parametersBillion >= req.minParametersBillion }
            .filter { $0.minRamGb <= fitRamGb + 0.0001 &&
                      (fitStorageGb <= 0 || $0.minFreeStorageGb <= fitStorageGb + 0.0001) }
            .sorted { $0.parametersBillion > $1.parametersBillion }

        guard let winner = fits.first else {
            return ModalityPlan(quality: .nothingFits, model: nil, reason:
                "coding models are catalogued but none clears this device's RAM / storage / capability floor.")
        }

        // The catalogue does not track what is already on disk; the caller checks.
        let selection = ModelSelection(
            modelId: winner.modelId,
            requiresDownload: true,
            estimatedBytes: winner.totalBytes,
            tier: tier)

        return ModalityPlan(quality: .good, model: selection, reason:
            "\(winner.modelId) (\(winner.parametersBillion)B) fits this device.")
    }

    /// C#'s "0.#" - at most one decimal, and no trailing ".0".
    private func fmt(_ v: Double) -> String {
        let r = (v * 10).rounded() / 10
        return r == r.rounded() ? String(Int(r)) : String(format: "%.1f", r)
    }
}

// MARK: - The loop

/// Knobs. The defaults are the safe ones: no commands, tier-derived iterations.
public struct CodeAgentOptions: Sendable {
    public let maxIterations: Int?
    public let allowCommands: Bool
    public let requirements: CodingModelRequirements
    public let maxObservationChars: Int
    public let generation: GenerationOptions?

    public init(
        maxIterations: Int? = nil,
        allowCommands: Bool = false,
        requirements: CodingModelRequirements = .default,
        maxObservationChars: Int = 8 * 1024,
        generation: GenerationOptions? = nil
    ) {
        self.maxIterations = maxIterations
        self.allowCommands = allowCommands
        self.requirements = requirements
        self.maxObservationChars = maxObservationChars
        self.generation = generation
    }
}

/// One turn of the loop, kept so a person can read back what the agent did.
public struct CodeAgentStep: Sendable, Equatable {
    public let index: Int
    public let action: AgentActionKind
    public let detail: String
    public let observation: String

    public init(index: Int, action: AgentActionKind, detail: String, observation: String) {
        self.index = index
        self.action = action
        self.detail = detail
        self.observation = observation
    }
}

/// The whole run. `available == false` means the device gate refused, and
/// `reason` says why in words.
public struct CodeAgentRunResult: Sendable, Equatable {
    public let available: Bool
    public let quality: SelectionQuality
    public let reason: String
    public let steps: [CodeAgentStep]
    public let appliedEdits: [FileEdit]
    public let finalSummary: String

    public init(available: Bool, quality: SelectionQuality, reason: String,
                steps: [CodeAgentStep], appliedEdits: [FileEdit], finalSummary: String) {
        self.available = available
        self.quality = quality
        self.reason = reason
        self.steps = steps
        self.appliedEdits = appliedEdits
        self.finalSummary = finalSummary
    }
}

public protocol ICodeAgent: Sendable {
    func run(task: String, workspaceRoot: String, probe: DeviceProbe?) async throws -> CodeAgentRunResult
}

public extension ICodeAgent {
    func run(task: String, workspaceRoot: String) async throws -> CodeAgentRunResult {
        try await run(task: task, workspaceRoot: workspaceRoot, probe: nil)
    }
}

public enum CodeAgentError: Error, CustomStringConvertible, Equatable {
    case missingTask
    case missingWorkspaceRoot
    public var description: String {
        switch self {
        case .missingTask: return "task required"
        case .missingWorkspaceRoot: return "workspaceRoot required"
        }
    }
}

/// On-device coding is not wired on this build. Says so; does nothing.
public struct NullCodeAgent: ICodeAgent {
    public static let instance = NullCodeAgent()
    public init() {}
    public func run(task: String, workspaceRoot: String, probe: DeviceProbe? = nil) async throws -> CodeAgentRunResult {
        CodeAgentRunResult(
            available: false,
            quality: .unavailable,
            reason: "null code agent: on-device coding is not wired on this build.",
            steps: [], appliedEdits: [], finalSummary: "")
    }
}

/// The real loop: gate, load, then decide-act-observe until the model finishes
/// or the iteration budget runs out.
public final class CodeAgentLoop: ICodeAgent, @unchecked Sendable {
    private let brain: any IAIService
    private let editor: any ICodeEditor
    private let runner: any ICommandRunner
    private let planner: any ICodingCapabilityPlanner
    private let search: (any ICodeSearch)?
    private let options: CodeAgentOptions

    public init(
        brain: any IAIService,
        editor: any ICodeEditor,
        runner: any ICommandRunner,
        planner: any ICodingCapabilityPlanner,
        search: (any ICodeSearch)? = nil,
        options: CodeAgentOptions? = nil
    ) {
        self.brain = brain
        self.editor = editor
        self.runner = runner
        self.planner = planner
        self.search = search
        self.options = options ?? CodeAgentOptions()
    }

    public func run(task: String, workspaceRoot: String, probe: DeviceProbe? = nil) async throws -> CodeAgentRunResult {
        guard !task.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CodeAgentError.missingTask
        }
        guard !workspaceRoot.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CodeAgentError.missingWorkspaceRoot
        }
        let probe = probe ?? DeviceProbe.snapshot()

        // 1. TIER GATE. The whole "honest on a P30 Lite" promise: a weak phone
        //    (or a build with no installed coding model) never enters the loop.
        let plan = planner.planForCoding(probe)
        guard plan.isAvailable else { return Self.declined(plan.quality, plan.reason) }

        // 2. Bring the brain up. The gate cleared the device and the catalogue,
        //    but the concrete service still has to load a model.
        do { try await brain.start() }
        catch {
            return Self.declined(.unavailable,
                "coding gate passed but the brain failed to load: \(error.localizedDescription)")
        }
        guard brain.isReady else {
            return Self.declined(.unavailable, "the brain did not become ready; no on-device model is loaded.")
        }

        let tier = probe.classify()
        var maxIter = options.maxIterations ?? DeviceTierDefaults.agenticMaxIterations(tier)
        if maxIter < 1 { maxIter = 1 }

        var steps: [CodeAgentStep] = []
        var applied: [FileEdit] = []
        var transcript: [ChatMessage] = [
            ChatMessage(role: "system", content: Self.buildSystemPrompt(
                task: task, workspaceRoot: workspaceRoot,
                allowCommands: options.allowCommands, hasSearch: search != nil)),
            ChatMessage(role: "user", content: task),
        ]
        var finalSummary = ""

        for i in 0..<maxIter {
            try Task.checkCancellation()

            let reply: String
            do { reply = try await brain.chat(transcript, options: options.generation) }
            catch is CancellationError { throw CancellationError() }
            catch {
                steps.append(CodeAgentStep(index: i, action: .unknown, detail: "chat",
                                           observation: "brain error: \(error.localizedDescription)"))
                finalSummary = "run stopped: the brain raised an error."
                break
            }
            transcript.append(ChatMessage(role: "assistant", content: reply))

            let action = AgentActionParser.parse(reply)
            if action.kind == .finish {
                finalSummary = action.summary ?? ""
                steps.append(CodeAgentStep(index: i, action: .finish, detail: "finish", observation: finalSummary))
                break
            }

            let outcome = await execute(action, workspaceRoot: workspaceRoot)
            applied.append(contentsOf: outcome.edits)
            steps.append(CodeAgentStep(index: i, action: action.kind,
                                       detail: outcome.detail, observation: outcome.observation))

            // Feed the observation back as a tool turn so the next decision sees it.
            transcript.append(ChatMessage(role: "tool",
                content: Self.truncate(outcome.observation, options.maxObservationChars)))
        }

        if finalSummary.isEmpty {
            finalSummary = steps.isEmpty
                ? "no steps were taken."
                : "reached the iteration budget without an explicit finish."
        }

        return CodeAgentRunResult(
            available: true, quality: plan.quality, reason: plan.reason,
            steps: steps, appliedEdits: applied, finalSummary: finalSummary)
    }

    private static func declined(_ quality: SelectionQuality, _ reason: String) -> CodeAgentRunResult {
        CodeAgentRunResult(available: false, quality: quality, reason: reason,
                           steps: [], appliedEdits: [], finalSummary: "")
    }

    private struct Outcome {
        let detail: String
        let observation: String
        let edits: [FileEdit]
    }

    /// Dispatch one parsed action to the appropriate real seam. Never throws:
    /// an error is an observation the model gets to read and correct.
    private func execute(_ action: AgentAction, workspaceRoot: String) async -> Outcome {
        switch action.kind {
        case .readFile:
            guard let path = Self.resolvePath(workspaceRoot, action.path) else {
                return Outcome(detail: "read",
                               observation: "error: missing path, or path escapes the workspace", edits: [])
            }
            do {
                let text = try await editor.read(path: path)
                return Outcome(detail: "read \(action.path ?? "")",
                               observation: Self.truncate(text, options.maxObservationChars), edits: [])
            } catch {
                return Outcome(detail: "read \(action.path ?? "")",
                               observation: "error: \(error.localizedDescription)", edits: [])
            }

        case .editFile:
            guard let path = Self.resolvePath(workspaceRoot, action.path) else {
                return Outcome(detail: "edit",
                               observation: "error: missing path, or path escapes the workspace", edits: [])
            }
            let edit = FileEdit(path: path, rangeStart: action.rangeStart,
                                rangeEnd: action.rangeEnd, replacement: action.replacement ?? "")
            do {
                try await editor.apply(edits: [edit])
                try await editor.save(path: path)
                return Outcome(detail: "edit \(action.path ?? "") [\(action.rangeStart)..\(action.rangeEnd)]",
                               observation: "ok: edit applied", edits: [edit])
            } catch {
                return Outcome(detail: "edit \(action.path ?? "")",
                               observation: "error: \(error.localizedDescription)", edits: [])
            }

        case .runCommand:
            guard options.allowCommands else {
                return Outcome(detail: "run",
                               observation: "error: command execution is off (CodeAgentOptions.allowCommands=false)",
                               edits: [])
            }
            guard let exe = action.executable,
                  !exe.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return Outcome(detail: "run", observation: "error: no executable", edits: [])
            }
            let cwd = Self.resolvePath(workspaceRoot, action.path) ?? workspaceRoot
            let res = await runner.run(CommandRequest(
                executable: exe, arguments: action.args ?? [], workingDirectory: cwd))
            let obs = res.executed
                ? "exit=\(res.exitCode)\(res.timedOut ? " (timed out)" : "")\nstdout:\n\(res.stdout)\nstderr:\n\(res.stderr)"
                : "not run: \(res.denied ?? "")"
            return Outcome(detail: "run \(exe)",
                           observation: Self.truncate(obs, options.maxObservationChars), edits: [])

        case .searchCode:
            guard let search else {
                return Outcome(detail: "search", observation: "error: no code-search backend is wired", edits: [])
            }
            guard let query = action.query,
                  !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return Outcome(detail: "search", observation: "error: no query", edits: [])
            }
            do {
                let matches = try await search.search(query: query, topK: action.topK)
                var sb = ""
                for m in matches { sb += "\(m.path):\(m.line)  \(m.snippet)\n" }
                let body = sb.isEmpty ? "(no matches)" : sb
                return Outcome(detail: "search '\(query)'",
                               observation: Self.truncate(body, options.maxObservationChars), edits: [])
            } catch {
                return Outcome(detail: "search", observation: "error: \(error.localizedDescription)", edits: [])
            }

        case .finish, .unknown:
            return Outcome(detail: "unknown",
                observation: "error: could not parse a known action from the reply. " +
                             "Reply with a single JSON action object. " +
                             "Raw: \(Self.truncate(action.raw ?? "", 512))",
                edits: [])
        }
    }

    /// Resolve a model-supplied path against the workspace and refuse anything
    /// that escapes it. Path traversal out of the workspace is the obvious way
    /// an on-device agent goes from "edit my repo" to "edit /etc" - closed here.
    static func resolvePath(_ workspaceRoot: String, _ candidate: String?) -> String? {
        guard let candidate, !candidate.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        let root = URL(fileURLWithPath: workspaceRoot).standardized.path
        let full = candidate.hasPrefix("/")
            ? URL(fileURLWithPath: candidate).standardized.path
            : URL(fileURLWithPath: root).appendingPathComponent(candidate).standardized.path

        if full.lowercased() == root.lowercased() { return full }
        let rootWithSep = root.hasSuffix("/") ? root : root + "/"
        return full.lowercased().hasPrefix(rootWithSep.lowercased()) ? full : nil
    }

    /// The contract handed to the model. Actions it cannot perform are not
    /// listed, so it never asks for one and never gets refused for asking.
    static func buildSystemPrompt(task: String, workspaceRoot: String,
                                  allowCommands: Bool, hasSearch: Bool) -> String {
        var lines: [String] = [
            "You are an on-device coding agent working inside the workspace: \(workspaceRoot).",
            "Work ONE step at a time. Reply with a SINGLE JSON object and nothing else.",
            "Supported actions:",
            "  {\"action\":\"read_file\",\"path\":\"relative/path\"}",
        ]
        if hasSearch {
            lines.append("  {\"action\":\"search_code\",\"query\":\"text\",\"top_k\":10}")
        }
        lines.append("  {\"action\":\"edit_file\",\"path\":\"relative/path\",\"range_start\":0,\"range_end\":0,\"replacement\":\"text\"}")
        if allowCommands {
            lines.append("  {\"action\":\"run_command\",\"executable\":\"dotnet\",\"args\":[\"build\"],\"cwd\":\".\"}")
        }
        lines.append("  {\"action\":\"finish\",\"summary\":\"what you did\"}")
        lines.append("range_start/range_end are absolute character offsets into the file's CURRENT text; read before you edit.")
        lines.append("Paths must stay inside the workspace. After each action you receive an observation. Finish when done.")
        lines.append("Task: \(task)")
        return lines.joined(separator: "\n")
    }

    static func truncate(_ s: String, _ max: Int) -> String {
        if s.isEmpty { return s }
        let max = max < 1 ? 1 : max
        guard s.count > max else { return s }
        return String(s.prefix(max)) + "\n...[truncated \(s.count - max) chars]"
    }
}
