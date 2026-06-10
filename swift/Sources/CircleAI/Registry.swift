// Registry.swift
//
// ModelRegistryService + checkForUpgrades + writeInstalledManifest.

import Foundation

public final class ModelRegistryService: @unchecked Sendable {
    private var registry: ModelRegistry?
    private let catalogClient: ModelScopeCatalogClient?

    public init(catalogClient: ModelScopeCatalogClient? = nil) {
        self.catalogClient = catalogClient
        if let client = catalogClient {
            self.registry = client.loadFromDisk()
        }
    }

    public func setRegistry(_ reg: ModelRegistry) {
        self.registry = reg
    }

    public var allModels: [ModelEntry] {
        return registry?.models ?? []
    }

    public func getLatestModel(_ name: String) -> ModelEntry? {
        return registry?.models.first { $0.name.lowercased() == name.lowercased() }
    }

    public func primeFromCatalog() async {
        guard let client = catalogClient else { return }
        do {
            if let reg = try await client.getCachedCatalog(acceptStaleOnError: true) {
                self.registry = reg
            }
        } catch {
            // best-effort
        }
    }

    public func checkForUpgrades(storageDirectory: String) -> [UpgradeInfo] {
        precondition(!storageDirectory.isEmpty, "storageDirectory is required")
        let now = Date()
        var out: [UpgradeInfo] = []

        for entry in allModels {
            let modelDir = (storageDirectory as NSString).appendingPathComponent(entry.name)
            var isDir: ObjCBool = false
            let exists = FileManager.default.fileExists(atPath: modelDir, isDirectory: &isDir)
            if !exists || !isDir.boolValue { continue }

            let manifestPath = (modelDir as NSString).appendingPathComponent("installed.json")
            let manifest = readManifest(at: manifestPath)
            if manifest == nil {
                out.append(UpgradeInfo(
                    modelId: entry.name,
                    installedVersion: nil,
                    availableVersion: entry.version,
                    reason: .unknown,
                    estimatedDownloadBytes: entry.totalBytes,
                    detectedAt: now
                ))
                continue
            }

            let m = manifest!
            let versionChanged = m.version != entry.version
            let (shaChanged, driftBytes) = compareBundleSha(installed: m.files, available: entry.bundleFiles)
            if !versionChanged && !shaChanged { continue }
            let reason: UpgradeReason
            if versionChanged && shaChanged { reason = .both }
            else if versionChanged { reason = .versionChanged }
            else { reason = .shaChanged }
            out.append(UpgradeInfo(
                modelId: entry.name,
                installedVersion: m.version,
                availableVersion: entry.version,
                reason: reason,
                estimatedDownloadBytes: driftBytes,
                detectedAt: now
            ))
        }
        return out
    }
}

public func writeInstalledManifest(
    modelDir: String,
    modelId: String,
    version: String,
    repo: String?,
    bundleFiles: [BundleFile]
) {
    do {
        try FileManager.default.createDirectory(atPath: modelDir, withIntermediateDirectories: true)
        let total: Int64 = bundleFiles.reduce(0) { $0 + max(0, $1.sizeBytes) }
        let m = InstalledManifest(
            modelId: modelId,
            version: version,
            repo: repo,
            totalBytes: total,
            files: bundleFiles,
            installedAtUtc: Date()
        )
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = .prettyPrinted
        let data = try encoder.encode(m)
        let path = (modelDir as NSString).appendingPathComponent("installed.json")
        try data.write(to: URL(fileURLWithPath: path))
    } catch {
        // best-effort
    }
}

private func readManifest(at path: String) -> InstalledManifest? {
    guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else { return nil }
    let decoder = JSONDecoder()
    decoder.dateDecodingStrategy = .iso8601
    return try? decoder.decode(InstalledManifest.self, from: data)
}

private func compareBundleSha(installed: [BundleFile], available: [BundleFile]) -> (Bool, Int64) {
    if available.isEmpty { return (false, 0) }
    var byName: [String: BundleFile] = [:]
    for f in installed { byName[f.name] = f }
    var drift = false
    var bytes: Int64 = 0
    for av in available {
        let inst = byName[av.name]
        if inst == nil || inst!.sha256.lowercased() != av.sha256.lowercased() {
            drift = true
            bytes += av.sizeBytes
        }
    }
    return (drift, bytes)
}
