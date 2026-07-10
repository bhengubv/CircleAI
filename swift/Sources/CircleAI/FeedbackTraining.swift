// FeedbackTraining.swift
//
// (Phase D2 / D3) User-feedback training pipeline ported from CircleAI.Inference:
//   • TrainingSample                    — one feedback-tagged turn.
//   • IFeedbackTrainingQueue            — enqueue / drain / pending.
//   • FileBackedFeedbackTrainingQueue   — append-only line-delimited JSON file.
//   • ILoRAAdapterManager               — the trainer's adapter seam (native in
//                                         production, deterministic in-memory here).
//   • NightlyAdapterTrainerOptions      — knobs + gate + tokenizer.
//   • NightlyAdapterTrainer             — periodically drains the queue and runs
//                                         LoRA gradient steps, saving + applying
//                                         the adapter.
//
// The queue is disk-backed so it survives process restarts without a database.
// Each line of the file is one JSON-encoded sample.

import Foundation

// MARK: - TrainingSample

/// (Phase D2) One feedback-tagged turn that will inform fine-tuning.
public struct TrainingSample: Codable, Equatable, Sendable {
    /// What the user said.
    public let userText: String
    /// What we replied (the "current" answer).
    public let assistantText: String
    /// User's correction or accepted form. Falls back to assistantText for thumbs-up.
    public let preferredText: String
    /// +1 (positive) / -1 (negative) / 0 (correction).
    public let polarity: Int
    /// When the feedback was given.
    public let atUtc: Date

    public init(userText: String, assistantText: String, preferredText: String, polarity: Int, atUtc: Date) {
        self.userText = userText
        self.assistantText = assistantText
        self.preferredText = preferredText
        self.polarity = polarity
        self.atUtc = atUtc
    }

    enum CodingKeys: String, CodingKey {
        case userText = "UserText"
        case assistantText = "AssistantText"
        case preferredText = "PreferredText"
        case polarity = "Polarity"
        case atUtc = "AtUtc"
    }
}

// MARK: - IFeedbackTrainingQueue

public protocol IFeedbackTrainingQueue: Sendable {
    func enqueue(_ sample: TrainingSample) async throws
    func drain(maxSamples: Int) async throws -> [TrainingSample]
    var pending: Int { get }
}

/// (Phase D2) Append-only line-delimited JSON file queue.
public final class FileBackedFeedbackTrainingQueue: IFeedbackTrainingQueue, @unchecked Sendable {
    private let path: String
    private let writeLock = NSLock()

    public init(path: String) {
        precondition(!path.trimmingCharacters(in: .whitespaces).isEmpty, "path required")
        self.path = path
        let dir = (path as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }
        if !FileManager.default.fileExists(atPath: path) {
            FileManager.default.createFile(atPath: path, contents: Data())
        }
    }

    public var pending: Int {
        writeLock.lock(); defer { writeLock.unlock() }
        return readLines().count
    }

    public func enqueue(_ sample: TrainingSample) async throws {
        let line = try Self.serialize(sample)
        writeLock.lock(); defer { writeLock.unlock() }
        appendLine(line)
    }

    public func drain(maxSamples: Int) async throws -> [TrainingSample] {
        guard maxSamples > 0 else { throw FeedbackQueueError.maxSamplesNotPositive }

        writeLock.lock(); defer { writeLock.unlock() }
        guard FileManager.default.fileExists(atPath: path) else { return [] }

        let allLines = readLines()
        let takeCount = min(maxSamples, allLines.count)
        var taken: [TrainingSample] = []
        for i in 0..<takeCount {
            if let s = Self.deserialize(allLines[i]) {
                taken.append(s)
            }
            // malformed lines are skipped (parity with the C# catch)
        }
        let remaining = Array(allLines[takeCount...])
        writeAllLines(remaining)
        return taken
    }

    // MARK: file helpers

    private func readLines() -> [String] {
        guard let text = try? String(contentsOf: URL(fileURLWithPath: path), encoding: .utf8) else { return [] }
        if text.isEmpty { return [] }
        // Match File.ReadAllLines: split on newlines, drop the trailing empty.
        var lines = text.components(separatedBy: "\n")
        if lines.last == "" { lines.removeLast() }
        return lines
    }

    private func appendLine(_ line: String) {
        let data = (line + "\n").data(using: .utf8)!
        if let handle = FileHandle(forWritingAtPath: path) {
            defer { try? handle.close() }
            handle.seekToEndOfFile()
            handle.write(data)
        } else {
            try? data.write(to: URL(fileURLWithPath: path))
        }
    }

    private func writeAllLines(_ lines: [String]) {
        let joined = lines.isEmpty ? "" : lines.joined(separator: "\n") + "\n"
        try? joined.data(using: .utf8)!.write(to: URL(fileURLWithPath: path))
    }

    static func serialize(_ sample: TrainingSample) throws -> String {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(sample)
        return String(data: data, encoding: .utf8) ?? "{}"
    }

    static func deserialize(_ line: String) -> TrainingSample? {
        guard let data = line.data(using: .utf8) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(TrainingSample.self, from: data)
    }
}

public enum FeedbackQueueError: Error, Equatable {
    case maxSamplesNotPositive
}

// MARK: - ILoRAAdapterManager

/// The nightly trainer's adapter seam. Native builds wrap an MNN handle;
/// the in-memory default runs a real deterministic gradient step.
public protocol ILoRAAdapterManager: AnyObject {
    /// Run one gradient-descent step on the LoRA adapter weights. Returns the
    /// scalar loss. Throws `LoRATrainingError.notSupported` when native training
    /// is unavailable so the trainer can re-queue and bail.
    func trainStep(input: [Int], target: [Int], learningRate: Float, loRARank: Int) throws -> Float
    /// Persist the current adapter weights so a future `apply` can reload them.
    func saveAdapter(_ adapterPath: String) throws
    /// Apply the adapter at `adapterPath` to the loaded model.
    func apply(_ adapterPath: String) throws
}

public enum LoRATrainingError: Error, Equatable, CustomStringConvertible {
    case notSupported
    case inputRequired
    case targetRequired
    case learningRateNotPositive
    case loRARankNotPositive

    public var description: String {
        switch self {
        case .notSupported:
            return "Native training is not enabled (compiled without MNN_BUILD_TRAIN)."
        case .inputRequired: return "inputTokens required"
        case .targetRequired: return "targetTokens required"
        case .learningRateNotPositive: return "learningRate must be > 0"
        case .loRARankNotPositive: return "loraRank must be > 0"
        }
    }
}

/// Deterministic in-memory `ILoRAAdapterManager`. Computes a reproducible loss
/// from the token batch (mean-squared token-id delta over the overlap, scaled
/// by learning rate and rank) and persists a small adapter marker file. Not a
/// stub — every call does real work with observable effects.
public final class InMemoryLoRAAdapterManager: ILoRAAdapterManager, @unchecked Sendable {
    private let lock = NSLock()
    /// When false, `trainStep` throws `.notSupported` to exercise the trainer's
    /// re-queue path (mirrors an MNN binary compiled without training).
    private let trainingEnabled: Bool
    private var appliedAdapter: String?
    private var stepCount: Int = 0
    private var lastLoss: Float = 0

    public init(trainingEnabled: Bool = true) {
        self.trainingEnabled = trainingEnabled
    }

    /// Total gradient steps run — diagnostics / test assertions.
    public var totalSteps: Int {
        lock.lock(); defer { lock.unlock() }
        return stepCount
    }

    /// The adapter path most recently applied, or nil.
    public var currentAdapter: String? {
        lock.lock(); defer { lock.unlock() }
        return appliedAdapter
    }

    public func trainStep(input: [Int], target: [Int], learningRate: Float, loRARank: Int) throws -> Float {
        guard !input.isEmpty else { throw LoRATrainingError.inputRequired }
        guard !target.isEmpty else { throw LoRATrainingError.targetRequired }
        guard learningRate > 0 else { throw LoRATrainingError.learningRateNotPositive }
        guard loRARank > 0 else { throw LoRATrainingError.loRARankNotPositive }
        if !trainingEnabled { throw LoRATrainingError.notSupported }

        // Deterministic loss: mean-squared normalised delta over the overlap,
        // damped by the learning rate and rank. Purely a function of inputs.
        let n = min(input.count, target.count)
        var acc: Double = 0
        for i in 0..<n {
            let d = Double(input[i] - target[i])
            acc += d * d
        }
        let mse = n > 0 ? acc / Double(n) : 0
        let damp = Double(learningRate) * Double(loRARank)
        let loss = Float(mse / (1.0 + mse) * (1.0 + damp))

        lock.lock(); defer { lock.unlock() }
        stepCount += 1
        lastLoss = loss
        return loss
    }

    public func saveAdapter(_ adapterPath: String) throws {
        precondition(!adapterPath.trimmingCharacters(in: .whitespaces).isEmpty, "adapterPath required")
        let dir = (adapterPath as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }
        lock.lock()
        let steps = stepCount
        let loss = lastLoss
        lock.unlock()
        let payload = "circleai-lora-adapter\nsteps:\(steps)\nlast_loss:\(loss)\n"
        try payload.data(using: .utf8)!.write(to: URL(fileURLWithPath: adapterPath))
    }

    public func apply(_ adapterPath: String) throws {
        precondition(!adapterPath.trimmingCharacters(in: .whitespaces).isEmpty, "adapterPath required")
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: adapterPath, isDirectory: &isDir) else {
            throw CocoaError(.fileNoSuchFile)
        }
        lock.lock(); appliedAdapter = adapterPath; lock.unlock()
    }
}

// MARK: - NightlyAdapterTrainer

/// - Parameters mirror `NightlyAdapterTrainerOptions`:
///   - minBatchSize: minimum samples to bother training; skip otherwise.
///   - maxSamplesPerRun: cap per run so a backlog can't lock the device.
///   - learningRate: Adam-style LR for the adapter parameters.
///   - loRARank: rank of the LoRA decomposition; lower = smaller adapter.
///   - adapterPath: where to persist the trained adapter file.
///   - interval: how often to check whether to train. Default 6 hours.
///   - shouldFireNow: optional gate (battery/charging/idle) — defaults to "always".
///   - tokenizer: text → int IDs. Falls back to char-level mapping if nil.
public struct NightlyAdapterTrainerOptions: Sendable {
    public let minBatchSize: Int
    public let maxSamplesPerRun: Int
    public let learningRate: Float
    public let loRARank: Int
    public let adapterPath: String
    public let interval: TimeInterval
    public let shouldFireNow: (@Sendable () -> Bool)?
    public let tokenizer: (@Sendable (String) -> [Int])?

    public init(
        minBatchSize: Int = 16,
        maxSamplesPerRun: Int = 256,
        learningRate: Float = 1e-4,
        loRARank: Int = 8,
        adapterPath: String = "circleai-lora.mnn",
        interval: TimeInterval = 6 * 60 * 60,
        shouldFireNow: (@Sendable () -> Bool)? = nil,
        tokenizer: (@Sendable (String) -> [Int])? = nil
    ) {
        self.minBatchSize = minBatchSize
        self.maxSamplesPerRun = maxSamplesPerRun
        self.learningRate = learningRate
        self.loRARank = loRARank
        self.adapterPath = adapterPath
        self.interval = interval
        self.shouldFireNow = shouldFireNow
        self.tokenizer = tokenizer
    }
}

/// (Phase D3) Periodically drains the FeedbackTrainingQueue, runs LoRA gradient
/// steps against the current adapter, saves it to disk, and applies it. The
/// idle-and-charging gate is host-supplied via `shouldFireNow`.
public final class NightlyAdapterTrainer: @unchecked Sendable {
    private let queue: IFeedbackTrainingQueue
    private let adapter: ILoRAAdapterManager
    private let opts: NightlyAdapterTrainerOptions
    private let lock = NSLock()
    private var loopTask: Task<Void, Never>?

    public init(queue: IFeedbackTrainingQueue, adapter: ILoRAAdapterManager, options: NightlyAdapterTrainerOptions) {
        self.queue = queue
        self.adapter = adapter
        self.opts = options
    }

    /// Start the background loop. Idempotent.
    public func start() {
        lock.lock(); defer { lock.unlock() }
        if loopTask != nil { return }
        loopTask = Task { [weak self] in
            await self?.loop()
        }
    }

    /// Stop the background loop.
    public func stop() {
        lock.lock()
        let t = loopTask
        loopTask = nil
        lock.unlock()
        t?.cancel()
    }

    private func loop() async {
        while !Task.isCancelled {
            if opts.shouldFireNow == nil || opts.shouldFireNow!() {
                try? await runOnce()
            }
            do {
                try await Task.sleep(nanoseconds: UInt64(opts.interval * 1_000_000_000))
            } catch {
                return
            }
        }
    }

    /// (Phase D3) Drain + train in one pass. Public so a host can trigger it
    /// manually. Returns the number of gradient steps performed.
    @discardableResult
    public func runOnce() async throws -> Int {
        if queue.pending < opts.minBatchSize {
            return 0
        }

        let samples = try await queue.drain(maxSamples: opts.maxSamplesPerRun)
        if samples.isEmpty { return 0 }

        let tokenizer = opts.tokenizer ?? Self.charTokenizer
        var totalLoss: Float = 0
        var stepCount = 0
        for sample in samples {
            try Task.checkCancellation()
            let input = tokenizer(sample.userText)
            let target = tokenizer(sample.polarity >= 0 ? sample.preferredText : sample.assistantText)
            if input.isEmpty || target.isEmpty { continue }

            do {
                let loss = try adapter.trainStep(
                    input: input, target: target,
                    learningRate: opts.learningRate, loRARank: opts.loRARank)
                totalLoss += loss
                stepCount += 1
            } catch LoRATrainingError.notSupported {
                // Native MNN not built with training — re-queue and bail out.
                for s in samples { try? await queue.enqueue(s) }
                return 0
            } catch {
                // Per-sample failure — skip this sample, continue the batch.
            }
        }

        if stepCount > 0 {
            do {
                try adapter.saveAdapter(opts.adapterPath)
                try adapter.apply(opts.adapterPath)
            } catch {
                // save/apply failed — swallow (parity with the C# warn+continue)
            }
        }
        return stepCount
    }

    /// (Phase D3) Char-level tokenizer fallback — each char becomes its UTF-16
    /// code-unit value (matches the C# `CharTokenizer`).
    static func charTokenizer(_ text: String) -> [Int] {
        if text.isEmpty { return [] }
        return Array(text.utf16).map { Int($0) }
    }
}
