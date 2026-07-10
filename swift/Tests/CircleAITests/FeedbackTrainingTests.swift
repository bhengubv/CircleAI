// FeedbackTrainingTests.swift

import XCTest
@testable import CircleAI

final class FeedbackTrainingTests: XCTestCase {

    private func tempFile() -> String {
        (NSTemporaryDirectory() as NSString).appendingPathComponent("circleai-queue-\(UUID().uuidString)/queue.jsonl")
    }

    private func sample(_ user: String, polarity: Int = 1) -> TrainingSample {
        TrainingSample(userText: user, assistantText: "a-\(user)", preferredText: "p-\(user)", polarity: polarity, atUtc: Date())
    }

    // MARK: - Queue

    func testEnqueueIncrementsPending() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        XCTAssertEqual(q.pending, 0)
        try await q.enqueue(sample("one"))
        try await q.enqueue(sample("two"))
        XCTAssertEqual(q.pending, 2)
    }

    func testDrainReturnsFifoAndRemovesTaken() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        for n in ["a", "b", "c"] { try await q.enqueue(sample(n)) }
        let taken = try await q.drain(maxSamples: 2)
        XCTAssertEqual(taken.map { $0.userText }, ["a", "b"])
        XCTAssertEqual(q.pending, 1)
        let rest = try await q.drain(maxSamples: 10)
        XCTAssertEqual(rest.map { $0.userText }, ["c"])
        XCTAssertEqual(q.pending, 0)
    }

    func testDrainSurvivesRestart() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        do {
            let q = FileBackedFeedbackTrainingQueue(path: path)
            try await q.enqueue(sample("persisted"))
        }
        // New instance over the same file.
        let q2 = FileBackedFeedbackTrainingQueue(path: path)
        XCTAssertEqual(q2.pending, 1)
        let taken = try await q2.drain(maxSamples: 1)
        XCTAssertEqual(taken.first?.userText, "persisted")
    }

    func testDrainSkipsMalformedLines() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        try await q.enqueue(sample("good"))
        // Corrupt the file by appending a junk line.
        let handle = FileHandle(forWritingAtPath: path)!
        handle.seekToEndOfFile()
        handle.write("not-json\n".data(using: .utf8)!)
        try? handle.close()

        let taken = try await q.drain(maxSamples: 10)
        XCTAssertEqual(taken.map { $0.userText }, ["good"], "malformed line is skipped, valid one survives")
    }

    func testDrainRejectsNonPositiveMax() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        do { _ = try await q.drain(maxSamples: 0); XCTFail("expected throw") }
        catch { XCTAssertEqual(error as? FeedbackQueueError, .maxSamplesNotPositive) }
    }

    // MARK: - LoRA adapter manager

    func testInMemoryAdapterTrainStepIsDeterministic() throws {
        let a = InMemoryLoRAAdapterManager()
        let l1 = try a.trainStep(input: [1, 2, 3], target: [1, 2, 4], learningRate: 1e-4, loRARank: 8)
        let b = InMemoryLoRAAdapterManager()
        let l2 = try b.trainStep(input: [1, 2, 3], target: [1, 2, 4], learningRate: 1e-4, loRARank: 8)
        XCTAssertEqual(l1, l2)
        XCTAssertGreaterThan(l1, 0)
        XCTAssertEqual(a.totalSteps, 1)
    }

    func testAdapterTrainStepValidatesArgs() {
        let a = InMemoryLoRAAdapterManager()
        XCTAssertThrowsError(try a.trainStep(input: [], target: [1], learningRate: 1e-4, loRARank: 8))
        XCTAssertThrowsError(try a.trainStep(input: [1], target: [], learningRate: 1e-4, loRARank: 8))
        XCTAssertThrowsError(try a.trainStep(input: [1], target: [1], learningRate: 0, loRARank: 8))
        XCTAssertThrowsError(try a.trainStep(input: [1], target: [1], learningRate: 1e-4, loRARank: 0))
    }

    func testAdapterTrainingDisabledThrowsNotSupported() {
        let a = InMemoryLoRAAdapterManager(trainingEnabled: false)
        XCTAssertThrowsError(try a.trainStep(input: [1], target: [2], learningRate: 1e-4, loRARank: 8)) { err in
            XCTAssertEqual(err as? LoRATrainingError, .notSupported)
        }
    }

    func testAdapterSaveAndApply() throws {
        let a = InMemoryLoRAAdapterManager()
        _ = try a.trainStep(input: [1], target: [2], learningRate: 1e-4, loRARank: 8)
        let path = (NSTemporaryDirectory() as NSString).appendingPathComponent("lora-\(UUID().uuidString).mnn")
        defer { try? FileManager.default.removeItem(atPath: path) }
        try a.saveAdapter(path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: path))
        try a.apply(path)
        XCTAssertEqual(a.currentAdapter, path)
    }

    // MARK: - NightlyAdapterTrainer

    func testRunOnceSkipsBelowMinBatch() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        try await q.enqueue(sample("one"))
        let adapter = InMemoryLoRAAdapterManager()
        let trainer = NightlyAdapterTrainer(queue: q, adapter: adapter,
            options: NightlyAdapterTrainerOptions(minBatchSize: 5))
        let steps = try await trainer.runOnce()
        XCTAssertEqual(steps, 0)
        XCTAssertEqual(q.pending, 1, "queue untouched when below min batch")
    }

    func testRunOnceTrainsSavesAndApplies() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let adapterPath = (NSTemporaryDirectory() as NSString).appendingPathComponent("lora-\(UUID().uuidString).mnn")
        defer { try? FileManager.default.removeItem(atPath: adapterPath) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        for n in ["a", "b", "c"] { try await q.enqueue(sample(n)) }
        let adapter = InMemoryLoRAAdapterManager()
        let trainer = NightlyAdapterTrainer(queue: q, adapter: adapter,
            options: NightlyAdapterTrainerOptions(minBatchSize: 2, maxSamplesPerRun: 10, adapterPath: adapterPath))

        let steps = try await trainer.runOnce()
        XCTAssertEqual(steps, 3)
        XCTAssertEqual(q.pending, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: adapterPath))
        XCTAssertEqual(adapter.currentAdapter, adapterPath)
    }

    func testRunOnceRequeuesWhenTrainingUnsupported() async throws {
        let path = tempFile(); defer { try? FileManager.default.removeItem(atPath: (path as NSString).deletingLastPathComponent) }
        let q = FileBackedFeedbackTrainingQueue(path: path)
        for n in ["a", "b"] { try await q.enqueue(sample(n)) }
        let adapter = InMemoryLoRAAdapterManager(trainingEnabled: false)
        let trainer = NightlyAdapterTrainer(queue: q, adapter: adapter,
            options: NightlyAdapterTrainerOptions(minBatchSize: 2))

        let steps = try await trainer.runOnce()
        XCTAssertEqual(steps, 0)
        // Samples were drained then re-queued → still 2 pending.
        XCTAssertEqual(q.pending, 2)
    }

    func testCharTokenizerMapsUtf16() {
        XCTAssertEqual(NightlyAdapterTrainer.charTokenizer("AB"), [65, 66])
        XCTAssertTrue(NightlyAdapterTrainer.charTokenizer("").isEmpty)
    }

    func testTrainingSampleJsonRoundTrip() throws {
        let s = sample("hello", polarity: -1)
        let line = try FileBackedFeedbackTrainingQueue.serialize(s)
        let back = FileBackedFeedbackTrainingQueue.deserialize(line)
        XCTAssertEqual(back?.userText, "hello")
        XCTAssertEqual(back?.polarity, -1)
        XCTAssertEqual(back?.preferredText, "p-hello")
    }
}
