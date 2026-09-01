// InferenceBundleLoader.swift
//
// The loader that understands the BUNDLE registry shape — which is the shape
// every entry in the catalogue actually uses.
//
// WHY THIS EXISTS, as two concrete defects in the single-file loader:
//
//   1. It THROWS on any entry with bundle files, telling the caller to go and
//      use the download service directly. Since every registry entry is
//      bundle-shaped, that loader cannot fetch a single current model — so the
//      host's startup path could never download one at all.
//
//   2. It returns the WEIGHT file as the load path. The runtime's create call
//      wants config.json; handed the weight blob it fails deep inside a native
//      library, nowhere near the registry entry that caused it.
//
// The weight file stays the INTEGRITY anchor — it is the largest file, so a
// hash mismatch there is the most diagnostic thing that can fail. It is just no
// longer the load path.
//
// Ported from src/CircleAI.Inference/BundleModelLoader.cs.

import Foundation

public final class BundleModelLoader: IModelLoader, @unchecked Sendable {

    static let configFileName = "config.json"
    /// The canonical MNN weight blob, and the preferred integrity anchor.
    static let anchorFileName = "llm.mnn.weight"

    private let registry: ModelRegistryService
    private let downloads: ModelDownloadService
    private let storageRoot: String
    private let gate: (any IModelDownloadGate)?
    /// How an entry's modality is known; only chat bundles additionally require
    /// config.json to be present before they count as loadable.
    private let modalityOf: @Sendable (ModelEntry) -> ModelModality?

    private let lock = NSLock()
    private var disposed = false

    public init(modelDirectory: String? = nil,
                registry: ModelRegistryService = ModelRegistryService(),
                gate: (any IModelDownloadGate)? = nil,
                downloads: ModelDownloadService? = nil,
                modalityOf: (@Sendable (ModelEntry) -> ModelModality?)? = nil) {
        // Through ModelPaths, not the application-data folder: that resolves to
        // a SUBDIRECTORY of the folder the app actually uses on Android, so a
        // caller that passed nothing downloaded a second copy of every model.
        self.storageRoot = ModelPaths.resolve(modelDirectory)
        self.registry = registry
        self.gate = gate
        self.downloads = downloads ?? ModelDownloadService(storageDirectory: storageRoot)
        self.modalityOf = modalityOf ?? { SpeechModelSelector.inferModality($0) ?? .chat }
    }

    // MARK: - Download

    public func downloadModel(_ modelName: String,
                              progress: (@Sendable (Float) -> Void)? = nil) async throws -> String {
        try checkNotDisposed()
        guard !modelName.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw ModelDownloadError.emptyModelId
        }
        guard let entry = registry.getLatestModel(modelName) else {
            throw ModelDownloadError.emptyModelId
        }

        // THE METERED GATE IS CHECKED BEFORE ANY BYTES MOVE, and skipped when
        // the bundle is already cached — re-verifying a model that is on disk
        // must never be refused for being "on mobile data".
        if let gate, !modelExists(modelName) {
            if let blocked = gate.blockReason(estimatedBytes: entry.totalBytes) {
                throw ModelDownloadBlocked(blocked)
            }
        }

        let relay: (@Sendable (Double) -> Void)? = progress.map { p in
            { d in p(Float(d)) }
        }

        if entry.isBundle {
            guard let repo = entry.repo, !repo.trimmingCharacters(in: .whitespaces).isEmpty else {
                throw ModelDownloadError.emptyRepo
            }
            let specs = entry.bundleFiles.map {
                BundleFileSpec(name: $0.name, sha256: $0.sha256, sizeBytes: $0.sizeBytes)
            }
            let modelDir = try await downloads.ensureBundle(
                modelId: modelName, repo: repo, bundleFiles: specs, progress: relay)

            // Stamped so a later upgrade check can spot drift. Best-effort:
            // never fail a load for a manifest.
            await downloads.writeInstalledManifest(
                modelDir: modelDir, modelId: modelName, version: entry.version,
                repo: entry.repo, bundleFiles: specs)

            return try resolveLoadPath(entry: entry, modelDir: modelDir, modelName: modelName)
        }

        guard let raw = entry.url, let uri = URL(string: raw), uri.scheme != nil else {
            throw ModelDownloadError.emptyRepo
        }
        return try await downloads.ensureModel(
            modelId: modelName, downloadUri: uri, expectedSha256: entry.checksum, progress: relay)
    }

    // MARK: - Paths

    public func getModelPath(_ modelName: String) throws -> String {
        try checkNotDisposed()
        guard let entry = registry.getLatestModel(modelName) else {
            throw ModelDownloadError.emptyModelId
        }

        guard entry.isBundle else {
            return (storageRoot as NSString).appendingPathComponent(modelName + ".gguf")
        }

        let modelDir = (storageRoot as NSString).appendingPathComponent(modelName)
        if FileManager.default.fileExists(atPath: modelDir),
           let resolved = try? resolveLoadPath(entry: entry, modelDir: modelDir,
                                               modelName: modelName) {
            return resolved
        }
        // Not fully downloaded yet: hand back the CONVENTIONAL anchor so a
        // caller can existence-test it and trigger a download, rather than an
        // error it has to special-case.
        return (modelDir as NSString).appendingPathComponent(Self.configFileName)
    }

    /// WHICH file the runtime loads is modality-specific. Chat means MNN and
    /// therefore config.json; a speech bundle loads its own graph, so the
    /// largest catalogued file is the honest answer.
    func resolveLoadPath(entry: ModelEntry, modelDir: String,
                         modelName: String) throws -> String {
        if modalityOf(entry) == .chat {
            let config = (modelDir as NSString).appendingPathComponent(Self.configFileName)
            guard FileManager.default.fileExists(atPath: config) else {
                throw ModelDownloadError.emptyBundleList
            }
            return config
        }
        guard let anchor = Self.anchor(of: entry) else {
            throw ModelDownloadError.emptyBundleList
        }
        return (modelDir as NSString).appendingPathComponent(anchor.name)
    }

    /// The integrity anchor: the MNN weight blob when there is one, else the
    /// LARGEST catalogued file. Biggest file means a hash mismatch there is the
    /// most diagnostic failure available.
    static func anchor(of entry: ModelEntry) -> BundleFile? {
        if let weight = entry.bundleFiles.first(where: {
            $0.name.caseInsensitiveCompare(anchorFileName) == .orderedSame
        }) { return weight }
        return entry.bundleFiles.max { $0.sizeBytes < $1.sizeBytes }
    }

    // MARK: - Presence

    /// Present on disk, by SIZE. Cheap enough to call on a UI path.
    public func modelPresent(_ modelName: String) -> Bool {
        guard let entry = registry.getLatestModel(modelName) else { return false }
        let fm = FileManager.default

        guard entry.isBundle else {
            return fm.fileExists(
                atPath: (storageRoot as NSString).appendingPathComponent(modelName + ".gguf"))
        }

        let modelDir = (storageRoot as NSString).appendingPathComponent(modelName)
        guard fm.fileExists(atPath: modelDir) else { return false }
        if modalityOf(entry) == .chat,
           !fm.fileExists(atPath: (modelDir as NSString).appendingPathComponent(Self.configFileName)) {
            return false
        }
        guard let anchor = Self.anchor(of: entry) else { return false }

        let path = (modelDir as NSString).appendingPathComponent(anchor.name)
        let attrs = try? fm.attributesOfItem(atPath: path)
        let size = (attrs?[.size] as? NSNumber)?.int64Value ?? -1
        return size >= anchor.sizeBytes
    }

    /// Present AND verified. Distinct from `modelPresent` on purpose: hashing a
    /// 500 MB weight file is not something to do on every screen paint, so a
    /// caller picks which question it is asking.
    public func modelExists(_ modelName: String) -> Bool {
        guard let entry = registry.getLatestModel(modelName) else { return false }
        let fm = FileManager.default

        guard entry.isBundle else {
            let path = (storageRoot as NSString).appendingPathComponent(modelName + ".gguf")
            guard fm.fileExists(atPath: path) else { return false }
            guard let checksum = entry.checksum else { return true }
            return SideloadedBundleImporter.sha256Hex(ofFileAt: path)?
                .caseInsensitiveCompare(checksum) == .orderedSame
        }

        let modelDir = (storageRoot as NSString).appendingPathComponent(modelName)
        guard fm.fileExists(atPath: modelDir) else { return false }
        if modalityOf(entry) == .chat,
           !fm.fileExists(atPath: (modelDir as NSString).appendingPathComponent(Self.configFileName)) {
            return false
        }
        guard let anchor = Self.anchor(of: entry) else { return false }

        let path = (modelDir as NSString).appendingPathComponent(anchor.name)
        guard fm.fileExists(atPath: path) else { return false }
        guard !anchor.sha256.trimmingCharacters(in: .whitespaces).isEmpty else { return true }
        return SideloadedBundleImporter.sha256Hex(ofFileAt: path)?
            .caseInsensitiveCompare(anchor.sha256) == .orderedSame
    }

    public func checkForCriticalUpdate() async -> Bool { false }

    public func dispose() {
        lock.lock(); disposed = true; lock.unlock()
    }

    private func checkNotDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw ModelDownloadError.emptyModelId }
    }
}
