// CircleEngine.swift
//
// Port of CircleAI.Core.CircleEngine + ICircleModule + IEmbeddingService.
//
// Top-level facade for the CircleAI on-device stack. Holds the IModelLoader
// and a small registry of attached modules (embeddings, search, chat
// generators, tool bridges) wired in from downstream code via extensions.
//
// CircleAI.Core deliberately knows nothing about Inference / Embeddings /
// Search / Tools. Those attach their own services through the
// registerModule / getModule pair, or through settable properties such as
// `embeddingService`.

import Foundation

// MARK: - ICircleModule — CircleAI.Core.ICircleModule

/// A pluggable CircleAI module. `IDisposable` in C# maps to an explicit
/// `dispose()`; conformers own native/model state that must be released.
public protocol ICircleModule: AnyObject {
    var moduleName: String { get }
    func initAsync(engine: CircleEngine) async throws
    var isModelLoaded: Bool { get }
    func dispose()
}

// MARK: - IEmbeddingService — CircleAI.Core.IEmbeddingService

/// An embedding module. Extends `ICircleModule` (as in C#) and adds
/// synchronous embedding generation plus the vector size.
public protocol IEmbeddingService: ICircleModule {
    func generateEmbedding(_ text: String) -> [Float]
    var embeddingSize: Int { get }
}

// MARK: - CircleEngine — CircleAI.Core.CircleEngine

/// Top-level facade. A constructor that takes an `IModelLoader`, a public
/// `modelLoader`, and a type-keyed module bag.
///
/// Stateful (the module bag mutates), so a `final class ... @unchecked
/// Sendable` guarded by an `NSLock`. The lock is confined to the synchronous
/// mutation helpers.
public final class CircleEngine: @unchecked Sendable {
    private let lock = NSLock()
    private var modules: [ObjectIdentifier: Any] = [:]

    /// The model loader used to acquire and cache model files.
    public let modelLoader: any IModelLoader

    /// Optional embedding service. Wired in by an embeddings extension. Kept as
    /// `Any?` so Core does not need to reference downstream implementations.
    private var _embeddingService: Any?
    public var embeddingService: Any? {
        get { lock.lock(); defer { lock.unlock() }; return _embeddingService }
        set { lock.lock(); _embeddingService = newValue; lock.unlock() }
    }

    public init(modelLoader: any IModelLoader) {
        self.modelLoader = modelLoader
    }

    /// Register a module instance keyed by its concrete or protocol type `T`.
    @discardableResult
    public func registerModule<T>(_ module: T, as type: T.Type = T.self) -> CircleEngine {
        lock.lock()
        modules[ObjectIdentifier(type)] = module
        lock.unlock()
        return self
    }

    /// Retrieve a previously registered module, or `nil` if none was
    /// registered for that type.
    public func getModule<T>(_ type: T.Type = T.self) -> T? {
        lock.lock(); defer { lock.unlock() }
        return modules[ObjectIdentifier(type)] as? T
    }

    /// Returns true if a module of the given type has been registered.
    public func hasModule<T>(_ type: T.Type = T.self) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return modules[ObjectIdentifier(type)] != nil
    }
}
