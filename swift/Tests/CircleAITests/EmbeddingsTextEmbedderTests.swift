import XCTest
@testable import CircleAI

/// The checksum gate around the embedding model.
final class EmbeddingsTextEmbedderTests: XCTestCase {

    private final class StubManager: IModelManager, @unchecked Sendable {
        let verified: Bool
        private(set) var pathCalls = 0
        init(verified: Bool) { self.verified = verified }
        func getModelPath(modelId: String) async throws -> String {
            pathCalls += 1
            return "/models/\(modelId)"
        }
        func verifyModel(modelPath: String, expectedChecksum: [UInt8]) async throws -> Bool {
            verified
        }
    }

    private struct StubBackend: IEmbeddingBackend {
        let dimension: Int
        func embed(_ text: String) throws -> [Float] {
            [Float](repeating: Float(text.count), count: dimension)
        }
    }

    func testAVerifiedModelEmbeds() async throws {
        let e = TextEmbedder(modelManager: StubManager(verified: true),
                             expectedChecksum: [1, 2, 3],
                             backendFactory: { _ in StubBackend(dimension: 4) })
        let v = try await e.generate("hello")
        XCTAssertEqual(v.count, 4)
        XCTAssertEqual(v.first, 5)
    }

    // An embedding model that was tampered with produces vectors that still
    // LOOK like vectors, so the checksum is a hard gate.
    func testAFailedChecksumRefusesRatherThanFallingBack() async {
        let e = TextEmbedder(modelManager: StubManager(verified: false),
                             expectedChecksum: [1, 2, 3],
                             backendFactory: { _ in StubBackend(dimension: 4) })
        do {
            _ = try await e.generate("hello")
            XCTFail("a failed checksum must refuse")
        } catch let err as EmbeddingError {
            XCTAssertEqual(err, .checksumFailed)
        } catch { XCTFail("wrong error: \(error)") }
    }

    func testEmptyTextIsRefusedBeforeAnythingLoads() async {
        let m = StubManager(verified: true)
        let e = TextEmbedder(modelManager: m, expectedChecksum: [1],
                             backendFactory: { _ in StubBackend(dimension: 4) })
        do {
            _ = try await e.generate("   ")
            XCTFail("empty text must refuse")
        } catch let err as EmbeddingError {
            XCTAssertEqual(err, .emptyText)
        } catch { XCTFail("wrong error") }
        XCTAssertEqual(m.pathCalls, 0, "the model must not even be resolved")
    }

    // A zero-dimension model would embed, compare, and make every similarity
    // meaningless.
    func testAZeroDimensionModelIsRefused() async {
        let e = TextEmbedder(modelManager: StubManager(verified: true),
                             expectedChecksum: [1],
                             backendFactory: { _ in StubBackend(dimension: 0) })
        do {
            _ = try await e.generate("hello")
            XCTFail("a zero dimension must refuse")
        } catch let err as EmbeddingError {
            XCTAssertEqual(err, .badDimension(0))
        } catch { XCTFail("wrong error") }
    }

    // The model is resolved ONCE, however many times it is used.
    func testTheModelIsLoadedOnlyOnce() async throws {
        let m = StubManager(verified: true)
        let e = TextEmbedder(modelManager: m, expectedChecksum: [1],
                             backendFactory: { _ in StubBackend(dimension: 2) })
        _ = try await e.generate("a")
        _ = try await e.generate("b")
        _ = try await e.generate("c")
        XCTAssertEqual(m.pathCalls, 1)
    }

    // Refuses rather than returning zeros.
    func testTheNullBackendRefusesAndSaysWhy() {
        XCTAssertEqual(NullEmbeddingBackend.instance.dimension, 0)
        XCTAssertThrowsError(try NullEmbeddingBackend.instance.embed("x")) { e in
            XCTAssertEqual(e as? EmbeddingError, .noBackend)
            XCTAssertTrue((e as! EmbeddingError).description.contains("MNN"))
        }
    }

    func testUsingADisposedEmbedderIsRefused() async {
        let e = TextEmbedder(modelManager: StubManager(verified: true),
                             expectedChecksum: [1],
                             backendFactory: { _ in StubBackend(dimension: 2) })
        e.dispose()
        do {
            _ = try await e.generate("hello")
            XCTFail("a disposed embedder must refuse")
        } catch let err as EmbeddingError {
            XCTAssertEqual(err, .disposed)
        } catch { XCTFail("wrong error") }
    }
}
