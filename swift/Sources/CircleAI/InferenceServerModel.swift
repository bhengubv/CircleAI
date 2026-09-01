// InferenceServerModel.swift
//
// The inference server's data: what /v1/diagnostics answers, how the server is
// configured, and how a streamed chunk is framed.
//
// The ENDPOINTS are ASP.NET minimal APIs and do not cross (see
// PARITY-EXCLUSIONS.md); these do, because they are the contract a client is
// written against. A Swift client that wants to talk to the server, or a Swift
// host that wants to answer the same shapes, needs exactly this and nothing of
// ASP.NET.
//
// THE JSON KEYS ARE THE WIRE FORMAT. They are snake_case because that is what
// the server sends today and what every existing client parses; renaming one to
// suit Swift conventions would break those clients silently — the field simply
// arrives as nil.
//
// Ported from src/CircleAI.Inference.Server/{Models/Diagnostics/DiagnosticsDtos,
// Options/InferenceServerOptions, Auth/AuthSchemes, Streaming/ServerSentEventsWriter}.cs.

import Foundation

// MARK: - Diagnostics

public struct LoadedModelInfo: Sendable, Equatable, Codable {
    public var id: String
    /// Always "model". Present because the OpenAI-shaped clients expect it.
    public var object: String
    public var ownedBy: String
    public var supportsStreaming: Bool

    public init(id: String, object: String = "model", ownedBy: String = "circleai",
                supportsStreaming: Bool = true) {
        self.id = id
        self.object = object
        self.ownedBy = ownedBy
        self.supportsStreaming = supportsStreaming
    }

    enum CodingKeys: String, CodingKey {
        case id
        case object
        case ownedBy = "owned_by"
        case supportsStreaming = "supports_streaming"
    }
}

public struct HostProfileDto: Sendable, Equatable, Codable {
    public var os: String
    public var osVersion: String
    public var arch: String
    public var cpuModel: String
    public var logicalCores: Int
    public var physicalCores: Int
    public var ramBytes: Int64
    public var gpuVendor: String?
    public var gpuModel: String?
    public var gpuVramBytes: Int64?
    public var npuVendor: String?
    public var npuModel: String?

    public init(os: String = "", osVersion: String = "", arch: String = "",
                cpuModel: String = "", logicalCores: Int = 0, physicalCores: Int = 0,
                ramBytes: Int64 = 0, gpuVendor: String? = nil, gpuModel: String? = nil,
                gpuVramBytes: Int64? = nil, npuVendor: String? = nil,
                npuModel: String? = nil) {
        self.os = os
        self.osVersion = osVersion
        self.arch = arch
        self.cpuModel = cpuModel
        self.logicalCores = logicalCores
        self.physicalCores = physicalCores
        self.ramBytes = ramBytes
        self.gpuVendor = gpuVendor
        self.gpuModel = gpuModel
        self.gpuVramBytes = gpuVramBytes
        self.npuVendor = npuVendor
        self.npuModel = npuModel
    }

    enum CodingKeys: String, CodingKey {
        case os
        case osVersion = "os_version"
        case arch
        case cpuModel = "cpu_model"
        case logicalCores = "logical_cores"
        case physicalCores = "physical_cores"
        case ramBytes = "ram_bytes"
        case gpuVendor = "gpu_vendor"
        case gpuModel = "gpu_model"
        case gpuVramBytes = "gpu_vram_bytes"
        case npuVendor = "npu_vendor"
        case npuModel = "npu_model"
    }
}

public struct BackendSelectionDto: Sendable, Equatable, Codable {
    public var backend: String
    public var tier: String
    /// WHY that backend was chosen, in words. The single most useful field on
    /// this endpoint: "which backend" without "why" turns every performance
    /// question into a guess.
    public var rationale: String

    public init(backend: String = "", tier: String = "", rationale: String = "") {
        self.backend = backend
        self.tier = tier
        self.rationale = rationale
    }
}

public struct CounterSnapshot: Sendable, Equatable, Codable {
    public var totalRequests: Int64
    public var activeRequests: Int
    /// Turned away at the door (over the concurrency cap). Counted separately
    /// from failures on purpose: rejected means the server is HEALTHY and busy,
    /// failed means it is not, and one number for both hides which.
    public var rejectedRequests: Int64
    public var failedRequests: Int64

    public init(totalRequests: Int64 = 0, activeRequests: Int = 0,
                rejectedRequests: Int64 = 0, failedRequests: Int64 = 0) {
        self.totalRequests = totalRequests
        self.activeRequests = activeRequests
        self.rejectedRequests = rejectedRequests
        self.failedRequests = failedRequests
    }

    enum CodingKeys: String, CodingKey {
        case totalRequests = "total_requests"
        case activeRequests = "active_requests"
        case rejectedRequests = "rejected_requests"
        case failedRequests = "failed_requests"
    }
}

public struct NativeRuntimePathsDto: Sendable, Equatable, Codable {
    public var rid: String
    public var expectedNativeDir: String
    public var mnnBridgePath: String
    public var mnnBridgeLoaded: Bool
    public var mnnCoreFetchedPath: String
    public var mnnCoreFlattenedPath: String
    public var mnnCorePreloaded: Bool
    /// Carried as separate nullable fields rather than one "error": a runtime
    /// that flattened and failed to preload is a different problem from one
    /// that never unpacked, and collapsing them loses which stage broke.
    public var flattenError: String?
    public var preloadError: String?

    public init(rid: String = "", expectedNativeDir: String = "",
                mnnBridgePath: String = "", mnnBridgeLoaded: Bool = false,
                mnnCoreFetchedPath: String = "", mnnCoreFlattenedPath: String = "",
                mnnCorePreloaded: Bool = false, flattenError: String? = nil,
                preloadError: String? = nil) {
        self.rid = rid
        self.expectedNativeDir = expectedNativeDir
        self.mnnBridgePath = mnnBridgePath
        self.mnnBridgeLoaded = mnnBridgeLoaded
        self.mnnCoreFetchedPath = mnnCoreFetchedPath
        self.mnnCoreFlattenedPath = mnnCoreFlattenedPath
        self.mnnCorePreloaded = mnnCorePreloaded
        self.flattenError = flattenError
        self.preloadError = preloadError
    }

    enum CodingKeys: String, CodingKey {
        case rid
        case expectedNativeDir = "expected_native_dir"
        case mnnBridgePath = "mnnbridge_path"
        case mnnBridgeLoaded = "mnnbridge_loaded"
        case mnnCoreFetchedPath = "mnn_core_fetched_path"
        case mnnCoreFlattenedPath = "mnn_core_flattened_path"
        case mnnCorePreloaded = "mnn_core_preloaded"
        case flattenError = "flatten_error"
        case preloadError = "preload_error"
    }
}

public struct DiagnosticsResponse: Sendable, Equatable, Codable {
    public var serverVersion: String
    public var uptimeSeconds: Double
    public var startedAt: Date
    public var loadedModels: [LoadedModelInfo]
    public var hostProfile: HostProfileDto?
    public var backendSelection: BackendSelectionDto?
    public var counters: CounterSnapshot
    public var nativeRuntime: NativeRuntimePathsDto?

    public init(serverVersion: String = "", uptimeSeconds: Double = 0,
                startedAt: Date = Date(), loadedModels: [LoadedModelInfo] = [],
                hostProfile: HostProfileDto? = nil,
                backendSelection: BackendSelectionDto? = nil,
                counters: CounterSnapshot = CounterSnapshot(),
                nativeRuntime: NativeRuntimePathsDto? = nil) {
        self.serverVersion = serverVersion
        self.uptimeSeconds = uptimeSeconds
        self.startedAt = startedAt
        self.loadedModels = loadedModels
        self.hostProfile = hostProfile
        self.backendSelection = backendSelection
        self.counters = counters
        self.nativeRuntime = nativeRuntime
    }

    enum CodingKeys: String, CodingKey {
        case serverVersion = "server_version"
        case uptimeSeconds = "uptime_seconds"
        case startedAt = "started_at"
        case loadedModels = "loaded_models"
        case hostProfile = "host_profile"
        case backendSelection = "backend_selection"
        case counters
        case nativeRuntime = "native_runtime"
    }
}

public struct HealthResponse: Sendable, Equatable, Codable {
    public var status: String
    public var at: Date

    public init(status: String = "ok", at: Date = Date()) {
        self.status = status
        self.at = at
    }
}

// MARK: - Configuration
//
// ApiKeyOptions is NOT redeclared here: it already sits beside the auth handler
// that reads it in InferenceServer.swift. A second one would compile and then
// silently be the wrong one at half the call sites. It gained Equatable and
// Codable there instead, which is what binding it from configuration needs.

public struct JwtOptions: Sendable, Equatable, Codable {
    /// OFF by default. A JWT scheme with an empty signing key that is ON would
    /// accept tokens nobody signed.
    public var enabled: Bool
    public var issuer: String
    public var audience: String
    public var signingKey: String

    public init(enabled: Bool = false, issuer: String = "", audience: String = "",
                signingKey: String = "") {
        self.enabled = enabled
        self.issuer = issuer
        self.audience = audience
        self.signingKey = signingKey
    }
}

public struct AuthOptions: Sendable, Equatable, Codable {
    public var apiKey: ApiKeyOptions
    public var jwt: JwtOptions

    public init(apiKey: ApiKeyOptions = ApiKeyOptions(), jwt: JwtOptions = JwtOptions()) {
        self.apiKey = apiKey
        self.jwt = jwt
    }
}

public struct InferenceServerOptions: Sendable, Equatable, Codable {
    public static let sectionName = "CircleAIServer"

    public var runtimeCacheRoot: String
    public var modelStorageRoot: String
    /// The concurrency cap. Requests over it are REJECTED rather than queued —
    /// a queue on a box with one GPU turns a slow answer into a timeout for
    /// everybody instead of a clear "busy" for one caller.
    public var maxConcurrentRequests: Int
    public var requestTimeoutSeconds: Int
    public var auth: AuthOptions

    public init(runtimeCacheRoot: String = "%LOCALAPPDATA%/CircleAI/runtime",
                modelStorageRoot: String = "%LOCALAPPDATA%/CircleAI/models",
                maxConcurrentRequests: Int = 16,
                requestTimeoutSeconds: Int = 120,
                auth: AuthOptions = AuthOptions()) {
        self.runtimeCacheRoot = runtimeCacheRoot
        self.modelStorageRoot = modelStorageRoot
        self.maxConcurrentRequests = maxConcurrentRequests
        self.requestTimeoutSeconds = requestTimeoutSeconds
        self.auth = auth
    }
}

/// Constant scheme names, so endpoint code and the auth handler agree on the
/// identifiers. A typo in one of these is an endpoint that requires a policy
/// nothing satisfies — which reads as "authentication is broken".
public enum AuthSchemes {
    public static let apiKey = "ApiKey"
    /// "Bearer", not "Jwt": it is the HTTP scheme name that goes on the wire.
    public static let jwt = "Bearer"
    public static let authenticatedPolicy = "Authenticated"
}

// MARK: - Server-sent events

/// Frames a payload as an SSE chunk.
///
/// Separated from any HTTP response object so the FRAMING can be tested and
/// reused: the bytes are the contract, and every client parsing them cares only
/// that "data: " prefixes the JSON and a blank line ends the event.
public enum ServerSentEventsWriter {

    /// The headers an SSE response must carry.
    ///
    /// `X-Accel-Buffering: no` is the one that is easy to leave out and
    /// impossible to debug: nginx buffers the whole stream by default, so
    /// streaming works perfectly in development and arrives all at once, at the
    /// end, in production.
    public static let headers: [String: String] = [
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-store",
        "Connection": "keep-alive",
        "X-Accel-Buffering": "no",
    ]

    /// The terminator every OpenAI-shaped client waits for. A stream that just
    /// closes leaves those clients hanging until their own timeout.
    public static let terminator = "data: [DONE]\n\n"

    public static func frame(json: String) -> Data {
        Data("data: \(json)\n\n".utf8)
    }

    public static func frame<T: Encodable>(_ payload: T) throws -> Data {
        let encoder = JSONEncoder()
        // Nulls are OMITTED, matching the server: a client that treats a present
        // null differently from an absent key sees a different message otherwise.
        encoder.outputFormatting = [.sortedKeys]
        let json = String(decoding: try encoder.encode(payload), as: UTF8.self)
        return frame(json: json)
    }

    public static func terminatorFrame() -> Data { Data(terminator.utf8) }
}
