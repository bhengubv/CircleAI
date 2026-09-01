// EmbeddingsTextEmbedder.swift
//
// Turning text into a vector, on this device.
//
// Ported from src/CircleAI.Embeddings/TextEmbedder.cs. The MNN native backend
// is not portable - there is no MNN bridge for Swift - so the backend is a
// protocol and the host supplies one. Everything the C# does AROUND that
// backend is here: the checksum gate, the one-time init, and the refusals.

import Foundation

/// Produces a vector for one piece of text.
public protocol IEmbeddingBackend: Sendable {
    var dimension: Int { get }
    func embed(_ text: String) throws -> [Float]
}

public enum EmbeddingError: Error, CustomStringConvertible, Equatable {
    case emptyText
    case checksumFailed
    case noBackend
    case badDimension(Int)
    case disposed

    public var description: String {
        switch self {
        case .emptyText: return "Text cannot be empty."
        case .checksumFailed:
            return "Embedding model checksum verification failed. "
                 + "The file may be corrupt or tampered with."
        case .noBackend:
            return "No embedding backend is wired. Supply an IEmbeddingBackend "
                 + "(the C# uses MNN; Swift has no equivalent bridge)."
        case .badDimension(let d):
            return "Embedding model returned dimension \(d). "
                 + "Ensure the file is a valid embedding model."
        case .disposed: return "TextEmbedder has been disposed."
        }
    }
}

/// Refuses rather than returning zeros. A vector of zeros would be silently
/// wrong: it embeds, it compares, and every similarity is meaningless.
public struct NullEmbeddingBackend: IEmbeddingBackend {
    public static let instance = NullEmbeddingBackend()
    public init() {}
    public var dimension: Int { 0 }
    public func embed(_ text: String) throws -> [Float] { throw EmbeddingError.noBackend }
}

/// Loads the model once, checks it, then embeds.
public final class TextEmbedder: @unchecked Sendable {
    private let modelManager: any IModelManager
    private let expectedChecksum: [UInt8]
    private let backendFactory: @Sendable (String) throws -> any IEmbeddingBackend

    private let gate = NSLock()
    private var backend: (any IEmbeddingBackend)?
    private var disposed = false

    public init(modelManager: any IModelManager,
                expectedChecksum: [UInt8],
                backendFactory: (@Sendable (String) throws -> any IEmbeddingBackend)? = nil) {
        self.modelManager = modelManager
        self.expectedChecksum = expectedChecksum
        self.backendFactory = backendFactory ?? { _ in NullEmbeddingBackend.instance }
    }

    public func generate(_ text: String) async throws -> [Float] {
        if isDisposed { throw EmbeddingError.disposed }
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw EmbeddingError.emptyText
        }
        let b = try await ensureBackend()
        return try b.embed(text)
    }

    public func dispose() {
        gate.lock(); disposed = true; backend = nil; gate.unlock()
    }

    private var isDisposed: Bool {
        gate.lock(); defer { gate.unlock() }
        return disposed
    }

    private func cached() -> (any IEmbeddingBackend)? {
        gate.lock(); defer { gate.unlock() }
        return backend
    }

    private func store(_ b: any IEmbeddingBackend) {
        gate.lock(); backend = b; gate.unlock()
    }

    /// The CHECKSUM IS CHECKED BEFORE THE MODEL IS LOADED, and a failure
    /// throws rather than falling back. An embedding model that was tampered
    /// with produces vectors that still look like vectors.
    private func ensureBackend() async throws -> any IEmbeddingBackend {
        if let b = cached() { return b }

        let path = try await modelManager.getModelPath(modelId: "embedding")
        let verified = try await modelManager.verifyModel(modelPath: path,
                                                          expectedChecksum: expectedChecksum)
        guard verified else { throw EmbeddingError.checksumFailed }

        let b = try backendFactory(path)
        guard b.dimension > 0 else { throw EmbeddingError.badDimension(b.dimension) }

        // Double-checked: another caller may have won the race while this one
        // was downloading, and two backends over one model file is waste.
        if let existing = cached() { return existing }
        store(b)
        return b
    }
}
