// Catalog.swift
//
// ModelScopeCatalogClient + signature verifier + ModelEntry / ModelRegistry.

import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public enum CatalogSignatureResult: Int, Sendable {
    case valid = 0
    case invalid = 1
    case missing = 2
    case notConfigured = 3
}

public protocol ICatalogSignatureVerifier: Sendable {
    func verify(payload: Data, signatureBase64: String?) -> CatalogSignatureResult
}

public struct NullCatalogSignatureVerifier: ICatalogSignatureVerifier {
    public init() {}
    public func verify(payload: Data, signatureBase64: String?) -> CatalogSignatureResult { .notConfigured }
}

public enum CatalogRefreshCadence: Int, Sendable {
    case onStartup = 0
    case daily = 1
    case manual = 2
    case never = 3
}

public struct ModelScopeCatalogOptions: Sendable {
    public let baseUri: String
    public let cacheDirectory: String
    public let cadence: CatalogRefreshCadence
    public let filter: String
    public let pageSize: Int
    public let userAgent: String

    public init(
        baseUri: String = "https://www.modelscope.cn",
        cacheDirectory: String = Self.defaultCacheDir(),
        cadence: CatalogRefreshCadence = .onStartup,
        filter: String = "MNN",
        pageSize: Int = 100,
        userAgent: String = "Mozilla/5.0 (Circle AI SDK) CircleAI-Swift/1.5"
    ) {
        self.baseUri = baseUri
        self.cacheDirectory = cacheDirectory
        self.cadence = cadence
        self.filter = filter
        self.pageSize = pageSize
        self.userAgent = userAgent
    }

    public static func defaultCacheDir() -> String {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return (home as NSString).appendingPathComponent(".circleai/catalog")
    }
}

public struct ModelEntry: Codable, Sendable, Equatable {
    public let name: String
    public let version: String
    public let quantization: String
    public let url: String?
    public let checksum: String?
    public let repo: String?
    public let totalBytes: Int64
    public let bundleFiles: [BundleFile]
    public let minRamGb: Double
    public let minStorageGb: Double
    public let capabilities: [String]?
    public let qualityRank: Int

    public init(
        name: String,
        version: String,
        quantization: String = "",
        url: String? = nil,
        checksum: String? = nil,
        repo: String? = nil,
        totalBytes: Int64 = 0,
        bundleFiles: [BundleFile] = [],
        minRamGb: Double = 0,
        minStorageGb: Double = 0,
        capabilities: [String]? = nil,
        qualityRank: Int = 0
    ) {
        self.name = name; self.version = version; self.quantization = quantization
        self.url = url; self.checksum = checksum; self.repo = repo
        self.totalBytes = totalBytes; self.bundleFiles = bundleFiles
        self.minRamGb = minRamGb; self.minStorageGb = minStorageGb
        self.capabilities = capabilities; self.qualityRank = qualityRank
    }

    public var isBundle: Bool { !bundleFiles.isEmpty }

    enum CodingKeys: String, CodingKey {
        case name, version, quantization, url, checksum, repo
        case totalBytes = "total_bytes"
        case bundleFiles = "bundle_files"
        case minRamGb = "min_ram_gb"
        case minStorageGb = "min_storage_gb"
        case capabilities
        case qualityRank = "quality_rank"
    }
}

public struct ModelRegistry: Codable, Sendable {
    public let registryUrl: String
    public let lastUpdated: Date
    public let models: [ModelEntry]

    public init(registryUrl: String, lastUpdated: Date, models: [ModelEntry]) {
        self.registryUrl = registryUrl; self.lastUpdated = lastUpdated; self.models = models
    }

    enum CodingKeys: String, CodingKey {
        case registryUrl = "registry_url"
        case lastUpdated = "last_updated"
        case models
    }
}

public actor ModelScopeCatalogClient {
    private let options: ModelScopeCatalogOptions
    private let verifier: ICatalogSignatureVerifier
    private let networkTypeProvider: (@Sendable () -> String?)?
    private var refreshedThisRun: Bool = false
    private let session: URLSession

    public init(
        options: ModelScopeCatalogOptions = ModelScopeCatalogOptions(),
        verifier: ICatalogSignatureVerifier = NullCatalogSignatureVerifier(),
        networkTypeProvider: (@Sendable () -> String?)? = nil
    ) {
        self.options = options
        self.verifier = verifier
        self.networkTypeProvider = networkTypeProvider
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 10
        self.session = URLSession(configuration: cfg)
        try? FileManager.default.createDirectory(atPath: options.cacheDirectory, withIntermediateDirectories: true)
    }

    public nonisolated var cacheFilePath: String {
        (options.cacheDirectory as NSString).appendingPathComponent("catalog.json")
    }
    public nonisolated var signatureFilePath: String {
        (options.cacheDirectory as NSString).appendingPathComponent("catalog.sig")
    }

    public func isRefreshDue() async -> Bool {
        if options.cadence == .never || options.cadence == .manual { return false }
        if let prov = networkTypeProvider {
            if let net = prov()?.lowercased(), net == "none" { return false }
        }
        if !FileManager.default.fileExists(atPath: cacheFilePath) { return true }
        if options.cadence == .onStartup { return !refreshedThisRun }
        // Daily.
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: cacheFilePath),
              let mtime = attrs[.modificationDate] as? Date else { return false }
        let cal = Calendar(identifier: .gregorian)
        return !cal.isDate(mtime, inSameDayAs: Date())
    }

    public nonisolated func loadFromDisk() -> ModelRegistry? {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: cacheFilePath)) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(ModelRegistry.self, from: data)
    }

    public func getCachedCatalog(acceptStaleOnError: Bool = true) async throws -> ModelRegistry? {
        if await isRefreshDue() {
            do {
                return try await refresh()
            } catch {
                if !acceptStaleOnError { throw error }
            }
        }
        return loadFromDisk()
    }

    public func refresh() async throws -> ModelRegistry {
        let reg = try await fetchLive()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = .prettyPrinted
        let bytes = try encoder.encode(reg)

        var existingSig: String?
        if let data = try? Data(contentsOf: URL(fileURLWithPath: signatureFilePath)),
           let s = String(data: data, encoding: .utf8) {
            existingSig = s.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let sigResult = verifier.verify(payload: bytes, signatureBase64: existingSig)
        if sigResult == .invalid {
            throw NSError(domain: "CircleAI.Catalog", code: 1, userInfo: [
                NSLocalizedDescriptionKey: "Catalog signature did not verify."
            ])
        }
        try? FileManager.default.createDirectory(atPath: options.cacheDirectory, withIntermediateDirectories: true)
        try bytes.write(to: URL(fileURLWithPath: cacheFilePath))
        refreshedThisRun = true
        return reg
    }

    private func fetchLive() async throws -> ModelRegistry {
        var comps = URLComponents(string: "\(options.baseUri)/api/v1/models")!
        comps.queryItems = [
            URLQueryItem(name: "Name", value: options.filter),
            URLQueryItem(name: "PageSize", value: String(options.pageSize)),
        ]
        let listing = try await fetchJson(comps.url!)
        let dataNode = listing["Data"] as? [String: Any] ?? [:]
        let items = dataNode["Model"] as? [[String: Any]] ?? []

        var entries: [ModelEntry] = []
        for m in items {
            guard let name = m["Name"] as? String, let path = m["Path"] as? String,
                  !name.isEmpty, !path.isEmpty else { continue }
            let filesURL = URL(string: "\(options.baseUri)/api/v1/models/\(path)/repo/files?Revision=master")!
            let filesJson: [String: Any]
            do { filesJson = try await fetchJson(filesURL) } catch { continue }
            let filesData = filesJson["Data"] as? [String: Any] ?? [:]
            let files = filesData["Files"] as? [[String: Any]] ?? []
            var bundle: [BundleFile] = []
            var total: Int64 = 0
            for f in files {
                let n = (f["Path"] as? String) ?? (f["Name"] as? String) ?? ""
                if n.isEmpty { continue }
                let size = (f["Size"] as? NSNumber)?.int64Value ?? 0
                bundle.append(BundleFile(name: n, sha256: (f["Sha256"] as? String) ?? "", sizeBytes: size))
                total += size
            }
            entries.append(ModelEntry(
                name: name,
                version: (m["Revision"] as? String) ?? "master",
                quantization: (m["Quantization"] as? String) ?? "",
                repo: path,
                totalBytes: total,
                bundleFiles: bundle
            ))
        }
        return ModelRegistry(registryUrl: options.baseUri, lastUpdated: Date(), models: entries)
    }

    private func fetchJson(_ url: URL) async throws -> [String: Any] {
        var req = URLRequest(url: url)
        req.setValue(options.userAgent, forHTTPHeaderField: "User-Agent")
        let (data, resp) = try await session.data(for: req)
        if let http = resp as? HTTPURLResponse, http.statusCode != 200 {
            throw NSError(domain: "CircleAI.Catalog", code: http.statusCode)
        }
        return (try JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }
}
