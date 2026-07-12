// ToolsGeekNetwork.swift
//
// Port of the CircleAI.Tools/ catalog + generators + bridges. The DTOs
// (ToolDefinition, ToolParameter, ToolInvocation, ToolResult, IToolBridge) and
// the Facex types already live in Tools.swift; this file adds:
//   • ToolManifestGenerator.cs   — ToolManifestGenerator (JSON + Markdown)
//   • ToolDefinitionBuilder.cs   — ToolDefinitionBuilder (fluent)
//   • DeviceDiagnosticsTools.cs  — DeviceDiagnosticsTools (device.diagnose)
//   • FacexTools.cs              — FacexTools (facex.extract_features)
//   • TheGeekNetworkTools.cs     — TheGeekNetworkTools (36-API catalogue)
//   • HttpToolBridge.cs          — HttpToolBridge (REST routing table)
//   • ComposioToolBridge.cs      — ComposioToolBridge (JSON-RPC MCP bridge)
//
// Porting notes:
//   • The two bridges depend on an HTTP seam — the injected
//     `IToolHttpTransport` protocol — NOT on a concrete client, matching the
//     tree's convention (VisionCloud's `IImageHttpTransport`, RealtimeCloud's
//     `IUltravoxHttpTransport`). `ToolHttpResponse` carries statusCode + body,
//     `isSuccess` == 2xx. The C# routing / URL-building / JSON-parse logic is
//     ported verbatim behind that seam.
//   • `ToolResult.result` / `ToolInvocation.arguments` are `Any?` (JSON-ish),
//     so the bridges parse response bodies into Foundation JSON objects.
//   • `DeviceDiagnosticsTools.diagnoseFromContext` adapts to the Swift
//     `IDeviceContext` shape (Device.swift): `thermalState` is already a
//     `String?` there, and memory/storage are `Int64?` — so the C# enum branch
//     collapses to a string passthrough and the MB conversion divides Int64.
//   • `ToolDefinitionBuilder` throws `ToolBuildError` where the C# threw
//     `ArgumentException` / `InvalidOperationException`.

import Foundation

// MARK: - Errors

/// Errors raised by the tool builder / bridges. (C# `ArgumentException` /
/// `InvalidOperationException`.)
public enum ToolBuildError: Error, Equatable, CustomStringConvertible {
    case argument(String)
    case invalidOperation(String)

    public var description: String {
        switch self {
        case .argument(let m): return m
        case .invalidOperation(let m): return m
        }
    }
}

// MARK: - ToolDefinitionBuilder

/// Fluent builder for `ToolDefinition`. Accumulates parameters and builds an
/// immutable definition on `build()`. (C# `ToolDefinitionBuilder`.)
public final class ToolDefinitionBuilder: @unchecked Sendable {
    private let name: String
    private var descriptionText: String?
    private var parameters: [(name: String, parameter: ToolParameter, required: Bool)] = []

    private init(name: String) {
        self.name = name
    }

    /// Creates a builder for a tool named `name` (non-empty).
    public static func create(_ name: String) throws -> ToolDefinitionBuilder {
        guard !name.isEmpty else { throw ToolBuildError.argument("name required") }
        return ToolDefinitionBuilder(name: name)
    }

    /// Sets the human-readable description (non-empty).
    @discardableResult
    public func description(_ description: String) throws -> ToolDefinitionBuilder {
        guard !description.isEmpty else { throw ToolBuildError.argument("description required") }
        self.descriptionText = description
        return self
    }

    /// Adds one parameter. `type` is a JSON-schema type string.
    @discardableResult
    public func parameter(
        _ name: String,
        type: String,
        description: String,
        required: Bool = false,
        enumValues: [String]? = nil
    ) throws -> ToolDefinitionBuilder {
        guard !name.isEmpty else { throw ToolBuildError.argument("name required") }
        guard !type.isEmpty else { throw ToolBuildError.argument("type required") }
        guard !description.isEmpty else { throw ToolBuildError.argument("description required") }
        let param = ToolParameter(type: type, description: description, enumValues: enumValues)
        parameters.append((name: name, parameter: param, required: required))
        return self
    }

    /// Builds the immutable `ToolDefinition`. Throws when `description` was
    /// never set.
    public func build() throws -> ToolDefinition {
        guard let desc = descriptionText, !desc.isEmpty else {
            throw ToolBuildError.invalidOperation(
                "ToolDefinition '\(name)' requires a description. Call description() before build().")
        }
        var params: [String: ToolParameter] = [:]
        var required: [String] = []
        for entry in parameters {
            params[entry.name] = entry.parameter
            if entry.required { required.append(entry.name) }
        }
        return ToolDefinition(name: name, description: desc, parameters: params, requiredParameters: required)
    }
}

// MARK: - ToolManifestGenerator

/// Renders `ToolDefinition` collections into an OpenAI/Qwen function-calling
/// JSON manifest or a Markdown summary. (C# `ToolManifestGenerator`.)
public enum ToolManifestGenerator {

    /// OpenAI/Qwen function-calling JSON array. Each element is
    /// `{ "type": "function", "function": { name, description, parameters } }`.
    /// Indented (two spaces) and null-omitting, matching the C# options.
    public static func generateJsonManifest(_ tools: [ToolDefinition]) -> String {
        var array: [[String: Any]] = []
        array.reserveCapacity(tools.count)

        for tool in tools {
            var properties: [String: Any] = [:]
            for (key, value) in tool.parameters {
                var prop: [String: Any] = [
                    "type": value.type,
                    "description": value.description,
                ]
                if let e = value.enumValues, !e.isEmpty {
                    prop["enum"] = e
                }
                properties[key] = prop
            }

            let parameters: [String: Any] = [
                "type": "object",
                "properties": properties,
                "required": tool.requiredParameters,
            ]

            array.append([
                "type": "function",
                "function": [
                    "name": tool.name,
                    "description": tool.description,
                    "parameters": parameters,
                ] as [String: Any],
            ])
        }

        guard
            let data = try? JSONSerialization.data(
                withJSONObject: array,
                options: [.prettyPrinted, .sortedKeys]),
            let json = String(data: data, encoding: .utf8)
        else {
            return "[]"
        }
        return json
    }

    /// Human-readable Markdown, grouped by API slug ("tgn.<api>"). (C#
    /// `GenerateMarkdownManifest`.)
    public static func generateMarkdownManifest(_ tools: [ToolDefinition]) -> String {
        var sb = ""
        sb += "# Available Tools\n"
        sb += "\n"
        sb += "Total: \(tools.count) tools.\n"
        sb += "\n"

        // Group by API slug, ordinal-sorted keys (C# SortedDictionary Ordinal).
        var groups: [String: [ToolDefinition]] = [:]
        for tool in tools {
            let key = extractApiSlug(tool.name)
            groups[key, default: []].append(tool)
        }

        for key in groups.keys.sorted() {
            let list = groups[key] ?? []
            sb += "## \(key)\n"
            sb += "\n"
            for tool in list {
                sb += "### `\(tool.name)`\n"
                sb += "\n"
                sb += "\(tool.description)\n"
                sb += "\n"

                if tool.parameters.isEmpty {
                    sb += "_No parameters._\n"
                    sb += "\n"
                    continue
                }

                sb += "Parameters:\n"
                sb += "\n"
                sb += "| Name | Type | Required | Description |\n"
                sb += "|------|------|----------|-------------|\n"

                let requiredSet = Set(tool.requiredParameters)
                for (pkey, value) in tool.parameters {
                    let required = requiredSet.contains(pkey) ? "yes" : "no"
                    var desc = escapePipe(value.description)
                    if let e = value.enumValues, !e.isEmpty {
                        desc += " Allowed values: " + e.joined(separator: ", ") + "."
                    }
                    sb += "| `\(pkey)` | \(value.type) | \(required) | \(desc) |\n"
                }
                sb += "\n"
            }
        }

        return sb
    }

    /// Tool names are "tgn.<api>.<verb>"; groups become "tgn.<api>".
    private static func extractApiSlug(_ toolName: String) -> String {
        let prefix = "tgn."
        guard toolName.hasPrefix(prefix) else { return toolName }
        let rest = String(toolName.dropFirst(prefix.count))
        if let dotIdx = rest.firstIndex(of: ".") {
            return prefix + String(rest[rest.startIndex..<dotIdx])
        }
        return prefix + rest
    }

    private static func escapePipe(_ s: String) -> String {
        s.replacingOccurrences(of: "|", with: "\\|")
    }
}

// MARK: - DeviceDiagnosticsTools

/// Tool definitions for on-device diagnostics. (C# `DeviceDiagnosticsTools`.)
public enum DeviceDiagnosticsTools {

    /// The single `device.diagnose` tool. Register alongside
    /// `TheGeekNetworkTools.getAllTools()` when an `IDeviceContext` is available.
    public static func diagnostics() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "device.diagnose",
                description:
                    "Return a snapshot of the host device's health: CPU usage fraction, "
                    + "available memory in MB, thermal state (normal/warm/critical), and "
                    + "free storage in MB. Use before scheduling heavy inference to avoid "
                    + "OOM conditions or OS thermal throttling.",
                parameters: [:],
                requiredParameters: []),
        ]
    }

    /// Reads an `IDeviceContext` and produces a compact JSON string suitable for
    /// returning as tool output. Null members serialise as JSON `null` so the
    /// model knows the data was unavailable, not zero. (C# `DiagnoseFromContext`.)
    ///
    /// Adapted to the Swift `IDeviceContext` (Device.swift): its `thermalState`
    /// is already a `String?`, `cpuUsagePercent` a `Float?`, and memory/storage
    /// are `Int64?`.
    public static func diagnoseFromContext(_ ctx: any IDeviceContext) -> String {
        func frac(_ v: Float?) -> String {
            guard let v = v else { return "null" }
            // Float is not directly a %f CVarArg in Swift — widen to Double.
            return String(format: "%.3f", Double(v))
        }
        func longMb(_ v: Int64?) -> String {
            guard let v = v else { return "null" }
            return String(v / (1024 * 1024))
        }
        func thermal(_ v: String?) -> String {
            guard let v = v else { return "null" }
            return "\"\(v.lowercased())\""
        }

        return "{"
            + "\"cpu_usage_fraction\":\(frac(ctx.cpuUsagePercent)),"
            + "\"available_memory_mb\":\(longMb(ctx.availableMemoryBytes)),"
            + "\"thermal_state\":\(thermal(ctx.thermalState)),"
            + "\"storage_free_mb\":\(longMb(ctx.storageFreeBytes))"
            + "}"
    }
}

// MARK: - FacexTools

/// Tool definitions for the facex on-device computer-vision pipeline.
/// (C# `FacexTools`.)
public enum FacexTools {

    private static func param(_ type: String, _ description: String, _ enumValues: [String]? = nil) -> ToolParameter {
        ToolParameter(type: type, description: description, enumValues: enumValues)
    }

    /// The `facex.extract_features` tool. Register when the host has a camera.
    /// The tool is stateless — it returns absolute coordinates for one frame.
    public static func faceExtract() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "facex.extract_features",
                description:
                    "Extract facial landmark coordinates, a bounding box, an expression "
                    + "classification, and a detection confidence score from the current "
                    + "camera frame. Returns a single FacialMetricMatrix snapshot. "
                    + "Operates entirely on-device with no network calls. "
                    + "This tool is stateless — it returns absolute coordinates for one frame; "
                    + "call it on consecutive frames and subtract to obtain temporal deltas.",
                parameters: [
                    "frame_width": param("number", "Width of the source camera frame in pixels. Required."),
                    "frame_height": param("number", "Height of the source camera frame in pixels. Required."),
                    "format": param(
                        "string",
                        "Pixel format of the frame buffer.",
                        ["yuv420", "rgb24", "bgr24", "grayscale"]),
                    "min_confidence": param(
                        "number",
                        "Minimum detection confidence threshold in [0.0, 1.0]. "
                        + "Detections below this score are not returned. Default 0.5."),
                ],
                requiredParameters: ["frame_width", "frame_height", "format"]),
        ]
    }
}

// MARK: - TheGeekNetworkTools

/// Static catalogue of tool definitions covering the 36 APIs in TheGeekNetwork
/// ecosystem. Names follow "tgn.<api_slug>.<verb>". (C# `TheGeekNetworkTools`.)
public enum TheGeekNetworkTools {

    private static func param(_ type: String, _ description: String, _ enumValues: [String]? = nil) -> ToolParameter {
        ToolParameter(type: type, description: description, enumValues: enumValues)
    }

    // AccountAPI
    public static func account() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.account.get_profile",
                description: "Get the authenticated user's account profile (display name, email, phone, country, KYC level).",
                parameters: ["user_id": param("string", "Target user ID. Use 'me' for the current authenticated user.")],
                requiredParameters: ["user_id"]),
            ToolDefinition(
                name: "tgn.account.update_profile",
                description: "Update profile fields for the current user (display name, avatar, country).",
                parameters: [
                    "display_name": param("string", "New display name. Optional."),
                    "avatar_url": param("string", "URL of the new avatar image. Optional."),
                    "country_code": param("string", "ISO-3166 alpha-2 country code. Optional."),
                ],
                requiredParameters: []),
        ]
    }

    // AuditAPI
    public static func audit() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.audit.list_events",
                description: "List recent audit events for the authenticated user, optionally filtered by category.",
                parameters: [
                    "category": param("string", "Optional event category filter (e.g. 'auth', 'payment', 'profile')."),
                    "limit": param("number", "Max number of events to return. Default 50, max 500."),
                ],
                requiredParameters: []),
        ]
    }

    // AuthAPI
    public static func auth() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.auth.request_otp",
                description: "Send a one-time password to the user's phone via SMS for login or sensitive action confirmation.",
                parameters: [
                    "phone_number": param("string", "E.164-formatted phone number, e.g. +27821234567."),
                    "purpose": param("string", "Reason for the OTP.", ["login", "signup", "transaction", "reset_pin"]),
                ],
                requiredParameters: ["phone_number", "purpose"]),
            ToolDefinition(
                name: "tgn.auth.verify_otp",
                description: "Verify an OTP code previously sent to the user. Returns a session token on success.",
                parameters: [
                    "phone_number": param("string", "E.164-formatted phone number."),
                    "code": param("string", "The OTP code the user received."),
                ],
                requiredParameters: ["phone_number", "code"]),
            ToolDefinition(
                name: "tgn.auth.push_to_app",
                description: "Trigger a push-to-app biometric approval on the user's mobile device for a web login or sensitive action.",
                parameters: [
                    "session_id": param("string", "The web session awaiting approval."),
                    "reason": param("string", "Human-readable reason shown to the user on the device."),
                ],
                requiredParameters: ["session_id", "reason"]),
        ]
    }

    // BidBaasAPI
    public static func bidBaas() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.bidbaas.list_active_auctions",
                description: "List currently active BidBaas auctions, optionally filtered by category or location.",
                parameters: [
                    "category": param("string", "Optional category filter, e.g. 'electronics', 'vehicles'."),
                    "country_code": param("string", "Optional ISO-3166 country code."),
                    "limit": param("number", "Max number of auctions to return. Default 25."),
                ],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.bidbaas.place_bid",
                description: "Place a bid on an active BidBaas auction.",
                parameters: [
                    "auction_id": param("string", "Auction identifier."),
                    "amount": param("number", "Bid amount in the auction's listed currency."),
                    "currency": param("string", "ISO-4217 currency code, e.g. 'ZAR', 'USD'."),
                ],
                requiredParameters: ["auction_id", "amount", "currency"]),
            ToolDefinition(
                name: "tgn.bidbaas.get_auction_details",
                description: "Get full details for a specific auction including current top bid, time remaining, and seller info.",
                parameters: ["auction_id": param("string", "Auction identifier.")],
                requiredParameters: ["auction_id"]),
        ]
    }

    // BillPaymentAPI
    public static func billPayment() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.billpayment.list_billers",
                description: "List available billers (utilities, telcos, councils) the user can pay.",
                parameters: [
                    "country_code": param("string", "ISO-3166 country code, e.g. 'ZA'."),
                    "category": param("string", "Optional category filter, e.g. 'water', 'rates', 'data'."),
                ],
                requiredParameters: ["country_code"]),
            ToolDefinition(
                name: "tgn.billpayment.pay_bill",
                description: "Pay a bill for a specified biller using the user's wallet balance.",
                parameters: [
                    "biller_id": param("string", "Biller identifier from list_billers."),
                    "account_number": param("string", "User's account number with that biller."),
                    "amount": param("number", "Amount to pay."),
                    "currency": param("string", "ISO-4217 currency code."),
                ],
                requiredParameters: ["biller_id", "account_number", "amount", "currency"]),
        ]
    }

    // BlockchainAPI
    public static func blockchain() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.blockchain.get_transaction",
                description: "Look up a SDPKT/Aether on-chain transaction by hash.",
                parameters: ["tx_hash": param("string", "Transaction hash.")],
                requiredParameters: ["tx_hash"]),
            ToolDefinition(
                name: "tgn.blockchain.get_address_info",
                description: "Get on-chain info about an Aether address (balance, recent activity).",
                parameters: ["address": param("string", "Aether wallet address.")],
                requiredParameters: ["address"]),
        ]
    }

    // ButlerAPI
    public static func butler() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.butler.log_interaction",
                description: "Log a B!/Butler interaction for analytics and personalisation.",
                parameters: [
                    "intent": param("string", "Detected intent name."),
                    "transcript": param("string", "Raw user utterance, redacted as needed."),
                    "success": param("boolean", "Whether the action succeeded."),
                ],
                requiredParameters: ["intent", "transcript", "success"]),
            ToolDefinition(
                name: "tgn.butler.get_user_context",
                description: "Fetch the server-side context for the current user (recent intents, preferences, capabilities).",
                parameters: ["user_id": param("string", "Target user ID. Use 'me' for the current user.")],
                requiredParameters: ["user_id"]),
        ]
    }

    // CircleAetherAPI
    public static func circleAether() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.circleaether.get_node_status",
                description: "Get current mesh-node status (peers, throughput, region) for the authenticated device.",
                parameters: ["device_id": param("string", "Device identifier. Use 'this' for the current device.")],
                requiredParameters: ["device_id"]),
            ToolDefinition(
                name: "tgn.circleaether.list_nearby_peers",
                description: "List mesh peers reachable from the current node, with link quality and tipping eligibility.",
                parameters: ["max_peers": param("number", "Max number of peers to return. Default 25.")],
                requiredParameters: []),
        ]
    }

    // EcommerceAPI
    public static func ecommerce() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.ecommerce.search_products",
                description: "Search the unified product catalogue across merchants in the ecosystem.",
                parameters: [
                    "query": param("string", "Free-text search query."),
                    "category": param("string", "Optional category filter."),
                    "max_price": param("number", "Optional maximum price."),
                    "currency": param("string", "ISO-4217 currency code."),
                ],
                requiredParameters: ["query"]),
            ToolDefinition(
                name: "tgn.ecommerce.get_product",
                description: "Get full product details by ID, including stock, variants, and merchant info.",
                parameters: ["product_id": param("string", "Product identifier.")],
                requiredParameters: ["product_id"]),
        ]
    }

    // ElectricityAPI
    public static func electricity() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.electricity.buy_token",
                description: "Buy prepaid electricity for a meter and return the STS token to enter into the meter.",
                parameters: [
                    "meter_number": param("string", "11-digit meter number."),
                    "amount": param("number", "Amount to spend on electricity."),
                    "currency": param("string", "ISO-4217 currency code, typically 'ZAR'."),
                ],
                requiredParameters: ["meter_number", "amount", "currency"]),
            ToolDefinition(
                name: "tgn.electricity.list_recent_purchases",
                description: "List the user's recent prepaid-electricity purchases.",
                parameters: ["limit": param("number", "Max number of purchases to return. Default 10.")],
                requiredParameters: []),
        ]
    }

    // GeoAPI
    public static func geo() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.geo.get_user_location",
                description: "Get the authenticated user's current best-known location (lat/lng, accuracy, country).",
                parameters: [:],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.geo.geocode_address",
                description: "Convert a human-readable address to coordinates.",
                parameters: [
                    "address": param("string", "Free-text address to geocode."),
                    "country_code": param("string", "Optional ISO-3166 country bias."),
                ],
                requiredParameters: ["address"]),
        ]
    }

    // GlocellAPI
    public static func glocell() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.glocell.list_products",
                description: "List Glocell retail products (airtime, data, vouchers) available to the user.",
                parameters: ["category": param("string", "Optional category filter, e.g. 'airtime', 'data'.")],
                requiredParameters: []),
        ]
    }

    // IncentivesAPI
    public static func incentives() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.incentives.get_qi_balance",
                description: "Get the user's current Qi (and Karma) balance and earning streak.",
                parameters: [:],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.incentives.list_active_quests",
                description: "List quests/challenges the user can complete to earn Qi.",
                parameters: ["limit": param("number", "Max number of quests to return. Default 10.")],
                requiredParameters: []),
        ]
    }

    // KiffStoreAPI
    public static func kiffStore() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.kiffstore.search_items",
                description: "Search KiffStore listings.",
                parameters: [
                    "query": param("string", "Free-text search query."),
                    "limit": param("number", "Max number of results. Default 25."),
                ],
                requiredParameters: ["query"]),
        ]
    }

    // LedgerAPI
    public static func ledger() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.ledger.get_account_balance",
                description: "Get the running balance for a ledger account belonging to the user.",
                parameters: ["account_id": param("string", "Ledger account identifier.")],
                requiredParameters: ["account_id"]),
            ToolDefinition(
                name: "tgn.ledger.list_entries",
                description: "List ledger entries for an account in reverse chronological order.",
                parameters: [
                    "account_id": param("string", "Ledger account identifier."),
                    "limit": param("number", "Max number of entries to return. Default 50."),
                ],
                requiredParameters: ["account_id"]),
        ]
    }

    // LocalizationAPI
    public static func localization() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.localization.translate_text",
                description: "Translate a piece of text from one language to another using the ecosystem translation service.",
                parameters: [
                    "text": param("string", "Text to translate."),
                    "source_language": param("string", "ISO-639-1 source code or 'auto' for auto-detect."),
                    "target_language": param("string", "ISO-639-1 target code, e.g. 'en', 'zu', 'fr'."),
                ],
                requiredParameters: ["text", "target_language"]),
            ToolDefinition(
                name: "tgn.localization.list_supported_languages",
                description: "List all language codes supported by the ecosystem.",
                parameters: [:],
                requiredParameters: []),
        ]
    }

    // MapsAPI
    public static func maps() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.maps.geocode",
                description: "Forward-geocode an address to coordinates via DataAcuity.",
                parameters: ["address": param("string", "Free-text address.")],
                requiredParameters: ["address"]),
            ToolDefinition(
                name: "tgn.maps.reverse_geocode",
                description: "Reverse-geocode coordinates to an address.",
                parameters: [
                    "latitude": param("number", "Latitude in decimal degrees."),
                    "longitude": param("number", "Longitude in decimal degrees."),
                ],
                requiredParameters: ["latitude", "longitude"]),
        ]
    }

    // MapsDataAPI
    public static func mapsData() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.mapsdata.search_pois",
                description: "Search points of interest near a location, filtered by category.",
                parameters: [
                    "latitude": param("number", "Latitude in decimal degrees."),
                    "longitude": param("number", "Longitude in decimal degrees."),
                    "radius_meters": param("number", "Search radius in metres. Default 1000."),
                    "category": param("string", "Optional POI category, e.g. 'pharmacy', 'fuel'."),
                ],
                requiredParameters: ["latitude", "longitude"]),
        ]
    }

    // MediaAPI
    public static func media() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.media.create_upload_url",
                description: "Create a pre-signed URL the client can PUT a media file to. Does not upload the file itself.",
                parameters: [
                    "mime_type": param("string", "MIME type of the file, e.g. 'image/jpeg'."),
                    "size_bytes": param("number", "File size in bytes."),
                ],
                requiredParameters: ["mime_type", "size_bytes"]),
            ToolDefinition(
                name: "tgn.media.get_media",
                description: "Get metadata and a viewable URL for a previously uploaded media item.",
                parameters: ["media_id": param("string", "Media identifier.")],
                requiredParameters: ["media_id"]),
        ]
    }

    // MessagingAPI
    public static func messaging() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.messaging.send_message",
                description: "Send a TxTMe message to a contact or conversation.",
                parameters: [
                    "recipient": param("string", "Recipient identifier - phone number (E.164) or user_id."),
                    "body": param("string", "Message body."),
                    "conversation_id": param("string", "Optional existing conversation to post into."),
                ],
                requiredParameters: ["recipient", "body"]),
            ToolDefinition(
                name: "tgn.messaging.list_conversations",
                description: "List the user's active TxTMe conversations, most recent first.",
                parameters: ["limit": param("number", "Max number of conversations to return. Default 25.")],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.messaging.get_messages",
                description: "Get messages in a specific conversation, most recent first.",
                parameters: [
                    "conversation_id": param("string", "Conversation identifier."),
                    "limit": param("number", "Max number of messages to return. Default 50."),
                ],
                requiredParameters: ["conversation_id"]),
        ]
    }

    // NotificationAPI
    public static func notification() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.notification.send_push",
                description: "Send a push notification to a user's registered devices.",
                parameters: [
                    "user_id": param("string", "Target user ID."),
                    "title": param("string", "Notification title."),
                    "body": param("string", "Notification body text."),
                    "data": param("object", "Optional structured payload for the app to handle."),
                ],
                requiredParameters: ["user_id", "title", "body"]),
            ToolDefinition(
                name: "tgn.notification.list_for_user",
                description: "List recent in-app notifications for the authenticated user.",
                parameters: [
                    "unread_only": param("boolean", "If true, return only unread notifications. Default false."),
                    "limit": param("number", "Max number to return. Default 50."),
                ],
                requiredParameters: []),
        ]
    }

    // OpSupportAPI
    public static func opSupport() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.opsupport.create_ticket",
                description: "File a support ticket on the user's behalf.",
                parameters: [
                    "category": param("string", "Ticket category.", ["billing", "account", "bug", "feature_request", "other"]),
                    "subject": param("string", "Short subject line."),
                    "body": param("string", "Full description of the issue."),
                ],
                requiredParameters: ["category", "subject", "body"]),
            ToolDefinition(
                name: "tgn.opsupport.get_system_status",
                description: "Get current system / API status (uptime, incidents).",
                parameters: [:],
                requiredParameters: []),
        ]
    }

    // PanikAPI
    public static func panik() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.panik.trigger_sos",
                description: "Trigger an SOS emergency alert. Notifies the user's panic contacts and optionally dispatches help.",
                parameters: [
                    "latitude": param("number", "Current latitude in decimal degrees."),
                    "longitude": param("number", "Current longitude in decimal degrees."),
                    "category": param("string", "Type of emergency.", ["medical", "crime", "fire", "accident", "other"]),
                    "note": param("string", "Optional short note describing the emergency."),
                ],
                requiredParameters: ["latitude", "longitude", "category"]),
            ToolDefinition(
                name: "tgn.panik.cancel_sos",
                description: "Cancel an in-progress SOS alert raised by the current user.",
                parameters: [
                    "alert_id": param("string", "SOS alert identifier."),
                    "reason": param("string", "Optional reason for cancellation."),
                ],
                requiredParameters: ["alert_id"]),
        ]
    }

    // PayfastAPI
    public static func payfast() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.payfast.create_payment",
                description: "Create a PayFast payment intent and return the redirect URL the user should open.",
                parameters: [
                    "amount": param("number", "Amount to charge."),
                    "currency": param("string", "ISO-4217 currency code, e.g. 'ZAR'."),
                    "item_name": param("string", "Short description shown on the PayFast page."),
                    "return_url": param("string", "URL to return to on completion."),
                ],
                requiredParameters: ["amount", "currency", "item_name"]),
        ]
    }

    // SdpktAPI
    public static func sdpkt() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.sdpkt.get_balance",
                description: "Get the user's SDPKT wallet balance, including any sub-balances (Qi, Karma, fiat-pegged).",
                parameters: [:],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.sdpkt.send_payment",
                description: "Send an SDPKT payment to another user or wallet address.",
                parameters: [
                    "recipient": param("string", "Recipient identifier - user ID, phone number (E.164), or wallet address."),
                    "amount": param("number", "Amount to send."),
                    "currency": param("string", "Currency code: 'SDPKT', 'QI', 'KARMA', or fiat ISO-4217."),
                    "memo": param("string", "Optional memo attached to the transaction."),
                ],
                requiredParameters: ["recipient", "amount", "currency"]),
            ToolDefinition(
                name: "tgn.sdpkt.get_transactions",
                description: "List the user's recent SDPKT wallet transactions.",
                parameters: ["limit": param("number", "Max number of transactions to return. Default 25.")],
                requiredParameters: []),
        ]
    }

    // ShhMoneyAPI
    public static func shhMoney() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.shhmoney.create_discreet_payment",
                description: "Create a discreet ShhMoney payment - sender and recipient identifiers are hidden from third parties on the ledger surface.",
                parameters: [
                    "recipient": param("string", "Recipient identifier."),
                    "amount": param("number", "Amount to send."),
                    "currency": param("string", "ISO-4217 currency code."),
                ],
                requiredParameters: ["recipient", "amount", "currency"]),
        ]
    }

    // SleptOnAPI
    public static func sleptOn() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.slepton.list_stories",
                description: "List recent SleptOn stories, optionally filtered by topic or country.",
                parameters: [
                    "topic": param("string", "Optional topic filter."),
                    "country_code": param("string", "Optional ISO-3166 country code."),
                    "limit": param("number", "Max number of stories. Default 25."),
                ],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.slepton.get_story",
                description: "Get a SleptOn story's full body and metadata.",
                parameters: ["story_id": param("string", "Story identifier.")],
                requiredParameters: ["story_id"]),
        ]
    }

    // SortedClothingAPI
    public static func sortedClothing() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.sortedclothing.search_items",
                description: "Search the SortedClothing inventory.",
                parameters: [
                    "query": param("string", "Free-text search query."),
                    "size": param("string", "Optional size filter."),
                    "limit": param("number", "Max results. Default 25."),
                ],
                requiredParameters: ["query"]),
        ]
    }

    // TagMeAPI
    public static func tagMe() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.tagme.create_tag",
                description: "Create a geo-tag at a location with optional note and visibility.",
                parameters: [
                    "latitude": param("number", "Latitude in decimal degrees."),
                    "longitude": param("number", "Longitude in decimal degrees."),
                    "note": param("string", "Optional text note."),
                    "visibility": param("string", "Who can see the tag.", ["public", "friends", "private"]),
                ],
                requiredParameters: ["latitude", "longitude"]),
            ToolDefinition(
                name: "tgn.tagme.list_nearby_tags",
                description: "List geo-tags near a location.",
                parameters: [
                    "latitude": param("number", "Latitude in decimal degrees."),
                    "longitude": param("number", "Longitude in decimal degrees."),
                    "radius_meters": param("number", "Radius in metres. Default 500."),
                ],
                requiredParameters: ["latitude", "longitude"]),
        ]
    }

    // TakemehomeAPI
    public static func takemehome() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.takemehome.search_flights",
                description: "Search flights across multiple suppliers and return ranked options.",
                parameters: [
                    "origin": param("string", "Origin IATA code or city name."),
                    "destination": param("string", "Destination IATA code or city name."),
                    "depart_date": param("string", "Departure date in YYYY-MM-DD."),
                    "return_date": param("string", "Optional return date in YYYY-MM-DD."),
                    "passengers": param("number", "Number of passengers. Default 1."),
                ],
                requiredParameters: ["origin", "destination", "depart_date"]),
            ToolDefinition(
                name: "tgn.takemehome.search_stays",
                description: "Search accommodation options for a destination and date range.",
                parameters: [
                    "destination": param("string", "Destination city or area."),
                    "check_in": param("string", "Check-in date in YYYY-MM-DD."),
                    "check_out": param("string", "Check-out date in YYYY-MM-DD."),
                    "guests": param("number", "Number of guests. Default 1."),
                ],
                requiredParameters: ["destination", "check_in", "check_out"]),
        ]
    }

    // TheHotListAPI
    public static func theHotList() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.thehotlist.list_entries",
                description: "List curated 'hot list' entries, optionally filtered by category or country.",
                parameters: [
                    "category": param("string", "Optional category filter."),
                    "country_code": param("string", "Optional ISO-3166 country code."),
                    "limit": param("number", "Max entries to return. Default 25."),
                ],
                requiredParameters: []),
        ]
    }

    // TheJobCenterAPI
    public static func theJobCenter() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.thejobcenter.search_jobs",
                description: "Search job postings.",
                parameters: [
                    "query": param("string", "Free-text search query, e.g. 'plumber Cape Town'."),
                    "country_code": param("string", "Optional ISO-3166 country code."),
                    "limit": param("number", "Max results. Default 25."),
                ],
                requiredParameters: ["query"]),
            ToolDefinition(
                name: "tgn.thejobcenter.apply",
                description: "Submit an application to a job posting on the user's behalf.",
                parameters: [
                    "job_id": param("string", "Job posting identifier."),
                    "cover_note": param("string", "Optional cover note."),
                ],
                requiredParameters: ["job_id"]),
        ]
    }

    // ThirdPartyAPI
    public static func thirdParty() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.thirdparty.list_integrations",
                description: "List configured third-party integrations available to the user (e.g. Xero, Zapier-style hooks).",
                parameters: [:],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.thirdparty.invoke_integration",
                description: "Invoke a registered third-party integration by name with a JSON payload.",
                parameters: [
                    "integration_name": param("string", "Integration name from list_integrations."),
                    "payload": param("object", "JSON payload to forward to the integration."),
                ],
                requiredParameters: ["integration_name", "payload"]),
        ]
    }

    // TrustSealAPI
    public static func trustSeal() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.trustseal.get_status",
                description: "Get the user's TrustSeal verification status (KYC level, document checks).",
                parameters: [:],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.trustseal.start_verification",
                description: "Start a verification flow for a specified KYC level.",
                parameters: ["level": param("string", "Target KYC level.", ["basic", "verified", "enhanced"])],
                requiredParameters: ["level"]),
        ]
    }

    // WalletAPI
    public static func wallet() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.wallet.get_balance",
                description: "Get the user's wallet balance(s) across all supported currencies.",
                parameters: ["currency": param("string", "Optional ISO-4217 currency to restrict the balance to.")],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.wallet.get_transactions",
                description: "List the user's recent wallet transactions.",
                parameters: [
                    "currency": param("string", "Optional ISO-4217 currency filter."),
                    "limit": param("number", "Max transactions to return. Default 25."),
                ],
                requiredParameters: []),
        ]
    }

    // WhatWeWantAPI
    public static func whatWeWant() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.whatwewant.list_stories",
                description: "List WhatWeWant stories, sorted by recency.",
                parameters: [
                    "topic": param("string", "Optional topic filter."),
                    "limit": param("number", "Max stories to return. Default 25."),
                ],
                requiredParameters: []),
            ToolDefinition(
                name: "tgn.whatwewant.get_story",
                description: "Get a single WhatWeWant story's full body and metadata.",
                parameters: ["story_id": param("string", "Story identifier.")],
                requiredParameters: ["story_id"]),
        ]
    }

    // WolverineAPI
    public static func wolverine() -> [ToolDefinition] {
        [
            ToolDefinition(
                name: "tgn.wolverine.list_jobs",
                description: "List background jobs visible to the user (status, last run, next run).",
                parameters: ["status": param("string", "Optional status filter.", ["queued", "running", "succeeded", "failed"])],
                requiredParameters: []),
        ]
    }

    /// Concatenates every API's tools into a single canonical list. (C#
    /// `GetAllTools`.)
    public static func getAllTools() -> [ToolDefinition] {
        var all: [ToolDefinition] = []
        all.reserveCapacity(96)
        all += account()
        all += audit()
        all += auth()
        all += bidBaas()
        all += billPayment()
        all += blockchain()
        all += butler()
        all += circleAether()
        all += ecommerce()
        all += electricity()
        all += geo()
        all += glocell()
        all += incentives()
        all += kiffStore()
        all += ledger()
        all += localization()
        all += maps()
        all += mapsData()
        all += media()
        all += messaging()
        all += notification()
        all += opSupport()
        all += panik()
        all += payfast()
        all += sdpkt()
        all += shhMoney()
        all += sleptOn()
        all += sortedClothing()
        all += tagMe()
        all += takemehome()
        all += theHotList()
        all += theJobCenter()
        all += thirdParty()
        all += trustSeal()
        all += wallet()
        all += whatWeWant()
        all += wolverine()
        return all
    }
}

// MARK: - HTTP seam

/// One HTTP response the tool transport hands back: status code + body bytes.
/// (Matches the tree's `IImageHttpTransport` / `IUltravoxHttpTransport` leaf.)
public struct ToolHttpResponse: Sendable, Equatable {
    public let statusCode: Int
    public let body: Data
    /// Content-Type media type (lowercased), if the server sent one. Lets the
    /// bridge decide whether to parse the body as JSON (C# checked the header).
    public let contentType: String?

    public init(statusCode: Int, body: Data, contentType: String? = nil) {
        self.statusCode = statusCode
        self.body = body
        self.contentType = contentType
    }

    /// Mirrors `HttpResponseMessage.IsSuccessStatusCode` (2xx).
    public var isSuccess: Bool { (200..<300).contains(statusCode) }
    /// Best-effort UTF-8 rendering of the body.
    public var reasonPhrase: String { "\(statusCode)" }
}

/// The single injected HTTP leaf the tool bridges use. Keeps the SDK free of a
/// baked-in HTTP client / API key, exactly as the cloud providers do. Sends a
/// method + full URL + headers + optional JSON body.
public protocol IToolHttpTransport: Sendable {
    /// Send an HTTP request. `method` is "GET" / "POST" / "PATCH" etc.
    /// `url` is the fully-resolved absolute URL (base + path + query).
    /// `jsonBody` is the request body for methods that carry one (nil = none).
    func send(
        method: String,
        url: String,
        headers: [String: String],
        jsonBody: Data?
    ) async throws -> ToolHttpResponse
}

// MARK: - HttpToolBridge

/// HTTP-backed `IToolBridge` routing tool calls to the TheGeekNetwork APIs over
/// REST. The tool-name → endpoint mapping covers the representative operations
/// in `TheGeekNetworkTools`; unmapped tools return a structured error. No wire
/// traffic on construction or `availableTools`. (C# `HttpToolBridge`.)
public final class HttpToolBridge: IToolBridge, @unchecked Sendable {
    /// HTTP method + path template + body strategy for one route.
    private struct EndpointMapping {
        let method: String
        let pathTemplate: String
        let body: BodyStrategy
    }

    private enum BodyStrategy {
        case none    // no body, no query
        case query   // args → query string
        case json    // args → JSON body
    }

    private let transport: any IToolHttpTransport
    private let baseUri: String  // guaranteed to end with "/"
    private let tools: [ToolDefinition]
    private let routes: [String: EndpointMapping]

    /// - Parameters:
    ///   - baseUrl: absolute base URL of the API gateway.
    ///   - transport: injected HTTP leaf.
    ///   - tools: exposed tool list (defaults to the full catalogue).
    public init(baseUrl: String, transport: any IToolHttpTransport, tools: [ToolDefinition] = TheGeekNetworkTools.getAllTools()) throws {
        let trimmed = baseUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw ToolBuildError.argument("baseUrl required") }
        self.transport = transport
        self.baseUri = trimmed.hasSuffix("/") ? trimmed : trimmed + "/"
        self.tools = tools
        self.routes = Self.buildRoutes()
    }

    public var availableTools: [ToolDefinition] { tools }

    public func getAvailableTools() async throws -> [ToolDefinition] { tools }

    public func invoke(_ invocation: ToolInvocation) async throws -> ToolResult {
        guard let mapping = routes[invocation.toolName] else {
            return ToolResult(
                toolName: invocation.toolName,
                success: false,
                error: "Tool '\(invocation.toolName)' is not registered in this bridge instance.")
        }

        do {
            let url = try resolveUrl(mapping, invocation.arguments)
            let headers: [String: String] = mapping.body == .json ? ["Content-Type": "application/json"] : [:]
            let jsonBody: Data? = mapping.body == .json ? Self.encodeBody(mapping, invocation.arguments) : nil

            let response = try await transport.send(
                method: mapping.method,
                url: url,
                headers: headers,
                jsonBody: jsonBody)

            // Parse the body as JSON when the server said JSON (or when it
            // parses cleanly); otherwise carry the raw string. Mirrors the C#
            // content-type sniff → JsonElement / string fallback.
            let body: Any? = Self.parseBody(response)

            if !response.isSuccess {
                return ToolResult(
                    toolName: invocation.toolName,
                    success: false,
                    result: body,
                    error: "HTTP \(response.statusCode)")
            }

            return ToolResult(toolName: invocation.toolName, success: true, result: body)
        } catch let e as ToolBuildError {
            return ToolResult(toolName: invocation.toolName, success: false, error: e.description)
        } catch {
            return ToolResult(toolName: invocation.toolName, success: false, error: error.localizedDescription)
        }
    }

    // MARK: URL / request building

    private func resolveUrl(_ mapping: EndpointMapping, _ arguments: [String: Any?]) throws -> String {
        var path = mapping.pathTemplate
        for placeholder in Self.extractPlaceholders(mapping.pathTemplate) {
            // arguments[placeholder] is Any?? — flatten both optional layers.
            let doubleOptional: Any?? = arguments[placeholder]
            guard let rawUnwrapped = doubleOptional.flatMap({ $0 }) else {
                throw ToolBuildError.invalidOperation(
                    "Tool argument '\(placeholder)' is required to build URL '\(mapping.pathTemplate)'.")
            }
            let encoded = Self.escapeDataString(Self.stringify(rawUnwrapped))
            path = path.replacingOccurrences(of: "{\(placeholder)}", with: encoded)
        }

        var url = baseUri + path

        if mapping.body == .query {
            let query = Self.buildQueryString(Self.bodyArgs(mapping, arguments))
            if !query.isEmpty {
                url += (url.contains("?") ? "&" : "?") + query
            }
        }

        return url
    }

    /// Args minus the URL placeholders (already substituted into the path).
    private static func bodyArgs(_ mapping: EndpointMapping, _ arguments: [String: Any?]) -> [String: Any?] {
        let placeholders = Set(extractPlaceholders(mapping.pathTemplate))
        var result: [String: Any?] = [:]
        for (k, v) in arguments where !placeholders.contains(k) {
            result[k] = v
        }
        return result
    }

    private static func encodeBody(_ mapping: EndpointMapping, _ arguments: [String: Any?]) -> Data {
        let args = bodyArgs(mapping, arguments)
        // Replace nil-optionals with NSNull so JSONSerialization keeps the key,
        // matching the C# body which serialised nulls.
        var jsonReady: [String: Any] = [:]
        for (k, v) in args {
            jsonReady[k] = v ?? NSNull()   // v is Any? — nil becomes JSON null
        }
        return (try? JSONSerialization.data(withJSONObject: jsonReady, options: [.sortedKeys])) ?? Data("{}".utf8)
    }

    private static func extractPlaceholders(_ template: String) -> [String] {
        var result: [String] = []
        var idx = template.startIndex
        while idx < template.endIndex {
            guard let open = template[idx...].firstIndex(of: "{") else { break }
            guard let close = template[template.index(after: open)...].firstIndex(of: "}") else { break }
            let name = String(template[template.index(after: open)..<close])
            result.append(name)
            idx = template.index(after: close)
        }
        return result
    }

    private static func buildQueryString(_ args: [String: Any?]) -> String {
        if args.isEmpty { return "" }
        var parts: [String] = []
        // Deterministic ordering (C# iterated the dictionary; Swift dictionaries
        // are unordered so we sort by key for a stable wire form).
        for key in args.keys.sorted() {
            let doubleOptional: Any?? = args[key]
            guard let unwrapped = doubleOptional.flatMap({ $0 }) else { continue }
            guard let rendered = renderQueryValue(unwrapped) else { continue }
            parts.append("\(escapeDataString(key))=\(escapeDataString(rendered))")
        }
        return parts.joined(separator: "&")
    }

    private static func renderQueryValue(_ value: Any) -> String? {
        switch value {
        case let s as String: return s
        case let b as Bool: return b ? "true" : "false"
        case let i as Int: return String(i)
        case let i as Int64: return String(i)
        case let d as Double: return String(d)
        default: return stringify(value)
        }
    }

    /// Renders a JSON-ish value to its string form (C# `object.ToString()`).
    private static func stringify(_ value: Any) -> String {
        switch value {
        case let s as String: return s
        case let b as Bool: return b ? "True" : "False"  // C# bool.ToString()
        case let i as Int: return String(i)
        case let i as Int64: return String(i)
        case let d as Double: return String(d)
        default: return "\(value)"
        }
    }

    /// Percent-encodes for a URL path/query component (≈ `Uri.EscapeDataString`).
    private static func escapeDataString(_ s: String) -> String {
        let allowed = CharacterSet(charactersIn:
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }

    private static func parseBody(_ response: ToolHttpResponse) -> Any? {
        if response.body.isEmpty { return nil }
        let isJson = (response.contentType?.contains("json") ?? false)
        if isJson || response.contentType == nil {
            if let obj = try? JSONSerialization.jsonObject(with: response.body, options: [.fragmentsAllowed]) {
                return obj
            }
        }
        return String(data: response.body, encoding: .utf8)
    }

    // MARK: Routing table

    private static func buildRoutes() -> [String: EndpointMapping] {
        [
            // Account
            "tgn.account.get_profile": EndpointMapping(method: "GET", pathTemplate: "account/v1/users/{user_id}", body: .none),
            "tgn.account.update_profile": EndpointMapping(method: "PATCH", pathTemplate: "account/v1/users/me", body: .json),
            // Audit
            "tgn.audit.list_events": EndpointMapping(method: "GET", pathTemplate: "audit/v1/events", body: .query),
            // Auth
            "tgn.auth.request_otp": EndpointMapping(method: "POST", pathTemplate: "auth/v1/otp/request", body: .json),
            "tgn.auth.verify_otp": EndpointMapping(method: "POST", pathTemplate: "auth/v1/otp/verify", body: .json),
            "tgn.auth.push_to_app": EndpointMapping(method: "POST", pathTemplate: "auth/v1/push-to-app", body: .json),
            // BidBaas
            "tgn.bidbaas.list_active_auctions": EndpointMapping(method: "GET", pathTemplate: "bidbaas/v1/auctions/active", body: .query),
            "tgn.bidbaas.place_bid": EndpointMapping(method: "POST", pathTemplate: "bidbaas/v1/auctions/{auction_id}/bids", body: .json),
            "tgn.bidbaas.get_auction_details": EndpointMapping(method: "GET", pathTemplate: "bidbaas/v1/auctions/{auction_id}", body: .none),
            // BillPayment
            "tgn.billpayment.list_billers": EndpointMapping(method: "GET", pathTemplate: "billpayment/v1/billers", body: .query),
            "tgn.billpayment.pay_bill": EndpointMapping(method: "POST", pathTemplate: "billpayment/v1/payments", body: .json),
            // Blockchain
            "tgn.blockchain.get_transaction": EndpointMapping(method: "GET", pathTemplate: "blockchain/v1/transactions/{tx_hash}", body: .none),
            "tgn.blockchain.get_address_info": EndpointMapping(method: "GET", pathTemplate: "blockchain/v1/addresses/{address}", body: .none),
            // Butler
            "tgn.butler.log_interaction": EndpointMapping(method: "POST", pathTemplate: "butler/v1/interactions", body: .json),
            "tgn.butler.get_user_context": EndpointMapping(method: "GET", pathTemplate: "butler/v1/users/{user_id}/context", body: .none),
            // CircleAether
            "tgn.circleaether.get_node_status": EndpointMapping(method: "GET", pathTemplate: "circleaether/v1/nodes/{device_id}/status", body: .none),
            "tgn.circleaether.list_nearby_peers": EndpointMapping(method: "GET", pathTemplate: "circleaether/v1/peers/nearby", body: .query),
            // Ecommerce
            "tgn.ecommerce.search_products": EndpointMapping(method: "GET", pathTemplate: "ecommerce/v1/products/search", body: .query),
            "tgn.ecommerce.get_product": EndpointMapping(method: "GET", pathTemplate: "ecommerce/v1/products/{product_id}", body: .none),
            // Electricity
            "tgn.electricity.buy_token": EndpointMapping(method: "POST", pathTemplate: "electricity/v1/tokens", body: .json),
            "tgn.electricity.list_recent_purchases": EndpointMapping(method: "GET", pathTemplate: "electricity/v1/purchases", body: .query),
            // Geo
            "tgn.geo.get_user_location": EndpointMapping(method: "GET", pathTemplate: "geo/v1/users/me/location", body: .none),
            "tgn.geo.geocode_address": EndpointMapping(method: "GET", pathTemplate: "geo/v1/geocode", body: .query),
            // Glocell
            "tgn.glocell.list_products": EndpointMapping(method: "GET", pathTemplate: "glocell/v1/products", body: .query),
            // Incentives
            "tgn.incentives.get_qi_balance": EndpointMapping(method: "GET", pathTemplate: "incentives/v1/qi/balance", body: .none),
            "tgn.incentives.list_active_quests": EndpointMapping(method: "GET", pathTemplate: "incentives/v1/quests/active", body: .query),
            // KiffStore
            "tgn.kiffstore.search_items": EndpointMapping(method: "GET", pathTemplate: "kiffstore/v1/items/search", body: .query),
            // Ledger
            "tgn.ledger.get_account_balance": EndpointMapping(method: "GET", pathTemplate: "ledger/v1/accounts/{account_id}/balance", body: .none),
            "tgn.ledger.list_entries": EndpointMapping(method: "GET", pathTemplate: "ledger/v1/accounts/{account_id}/entries", body: .query),
            // Localization
            "tgn.localization.translate_text": EndpointMapping(method: "POST", pathTemplate: "localization/v1/translate", body: .json),
            "tgn.localization.list_supported_languages": EndpointMapping(method: "GET", pathTemplate: "localization/v1/languages", body: .none),
            // Maps
            "tgn.maps.geocode": EndpointMapping(method: "GET", pathTemplate: "maps/v1/geocode", body: .query),
            "tgn.maps.reverse_geocode": EndpointMapping(method: "GET", pathTemplate: "maps/v1/reverse-geocode", body: .query),
            // MapsData
            "tgn.mapsdata.search_pois": EndpointMapping(method: "GET", pathTemplate: "mapsdata/v1/pois/search", body: .query),
            // Media
            "tgn.media.create_upload_url": EndpointMapping(method: "POST", pathTemplate: "media/v1/uploads", body: .json),
            "tgn.media.get_media": EndpointMapping(method: "GET", pathTemplate: "media/v1/media/{media_id}", body: .none),
            // Messaging
            "tgn.messaging.send_message": EndpointMapping(method: "POST", pathTemplate: "messaging/v1/messages", body: .json),
            "tgn.messaging.list_conversations": EndpointMapping(method: "GET", pathTemplate: "messaging/v1/conversations", body: .query),
            "tgn.messaging.get_messages": EndpointMapping(method: "GET", pathTemplate: "messaging/v1/conversations/{conversation_id}/messages", body: .query),
            // Notification
            "tgn.notification.send_push": EndpointMapping(method: "POST", pathTemplate: "notification/v1/push", body: .json),
            "tgn.notification.list_for_user": EndpointMapping(method: "GET", pathTemplate: "notification/v1/notifications", body: .query),
            // OpSupport
            "tgn.opsupport.create_ticket": EndpointMapping(method: "POST", pathTemplate: "opsupport/v1/tickets", body: .json),
            "tgn.opsupport.get_system_status": EndpointMapping(method: "GET", pathTemplate: "opsupport/v1/status", body: .none),
            // Panik
            "tgn.panik.trigger_sos": EndpointMapping(method: "POST", pathTemplate: "panik/v1/alerts", body: .json),
            "tgn.panik.cancel_sos": EndpointMapping(method: "POST", pathTemplate: "panik/v1/alerts/{alert_id}/cancel", body: .json),
            // Payfast
            "tgn.payfast.create_payment": EndpointMapping(method: "POST", pathTemplate: "payfast/v1/payments", body: .json),
            // Sdpkt
            "tgn.sdpkt.get_balance": EndpointMapping(method: "GET", pathTemplate: "sdpkt/v1/wallet/balance", body: .none),
            "tgn.sdpkt.send_payment": EndpointMapping(method: "POST", pathTemplate: "sdpkt/v1/wallet/transfers", body: .json),
            "tgn.sdpkt.get_transactions": EndpointMapping(method: "GET", pathTemplate: "sdpkt/v1/wallet/transactions", body: .query),
            // ShhMoney
            "tgn.shhmoney.create_discreet_payment": EndpointMapping(method: "POST", pathTemplate: "shhmoney/v1/payments", body: .json),
            // SleptOn
            "tgn.slepton.list_stories": EndpointMapping(method: "GET", pathTemplate: "slepton/v1/stories", body: .query),
            "tgn.slepton.get_story": EndpointMapping(method: "GET", pathTemplate: "slepton/v1/stories/{story_id}", body: .none),
            // SortedClothing
            "tgn.sortedclothing.search_items": EndpointMapping(method: "GET", pathTemplate: "sortedclothing/v1/items/search", body: .query),
            // TagMe
            "tgn.tagme.create_tag": EndpointMapping(method: "POST", pathTemplate: "tagme/v1/tags", body: .json),
            "tgn.tagme.list_nearby_tags": EndpointMapping(method: "GET", pathTemplate: "tagme/v1/tags/nearby", body: .query),
            // Takemehome
            "tgn.takemehome.search_flights": EndpointMapping(method: "GET", pathTemplate: "takemehome/v1/flights/search", body: .query),
            "tgn.takemehome.search_stays": EndpointMapping(method: "GET", pathTemplate: "takemehome/v1/stays/search", body: .query),
            // TheHotList
            "tgn.thehotlist.list_entries": EndpointMapping(method: "GET", pathTemplate: "thehotlist/v1/entries", body: .query),
            // TheJobCenter
            "tgn.thejobcenter.search_jobs": EndpointMapping(method: "GET", pathTemplate: "thejobcenter/v1/jobs/search", body: .query),
            "tgn.thejobcenter.apply": EndpointMapping(method: "POST", pathTemplate: "thejobcenter/v1/jobs/{job_id}/applications", body: .json),
            // ThirdParty
            "tgn.thirdparty.list_integrations": EndpointMapping(method: "GET", pathTemplate: "thirdparty/v1/integrations", body: .none),
            "tgn.thirdparty.invoke_integration": EndpointMapping(method: "POST", pathTemplate: "thirdparty/v1/integrations/{integration_name}/invoke", body: .json),
            // TrustSeal
            "tgn.trustseal.get_status": EndpointMapping(method: "GET", pathTemplate: "trustseal/v1/status", body: .none),
            "tgn.trustseal.start_verification": EndpointMapping(method: "POST", pathTemplate: "trustseal/v1/verifications", body: .json),
            // Wallet
            "tgn.wallet.get_balance": EndpointMapping(method: "GET", pathTemplate: "wallet/v1/balance", body: .query),
            "tgn.wallet.get_transactions": EndpointMapping(method: "GET", pathTemplate: "wallet/v1/transactions", body: .query),
            // WhatWeWant
            "tgn.whatwewant.list_stories": EndpointMapping(method: "GET", pathTemplate: "whatwewant/v1/stories", body: .query),
            "tgn.whatwewant.get_story": EndpointMapping(method: "GET", pathTemplate: "whatwewant/v1/stories/{story_id}", body: .none),
            // Wolverine
            "tgn.wolverine.list_jobs": EndpointMapping(method: "GET", pathTemplate: "wolverine/v1/jobs", body: .query),
        ]
    }
}

// MARK: - ComposioToolBridge

/// Routes tool calls to a Composio MCP server via JSON-RPC 2.0 over HTTP.
/// Discovery hits `GET {serverUri}tools`; invoke posts a `tools/call` request to
/// `tools/{name}/invoke`. Fail-soft: discovery failures return `[]`, invoke
/// failures return a failed `ToolResult`. (C# `ComposioToolBridge`.)
public final class ComposioToolBridge: IToolBridge, @unchecked Sendable {
    private static let defaultServerUri = "https://mcp.composio.dev/"

    private let apiKey: String
    private let serverUri: String  // ends with "/"
    private let transport: any IToolHttpTransport
    private let lock = NSLock()
    private var cachedTools: [ToolDefinition] = []

    /// - Parameters:
    ///   - composioApiKey: sent in the `X-API-Key` header (non-empty).
    ///   - serverUri: base MCP endpoint (defaults to the public Composio URL).
    ///   - transport: injected HTTP leaf.
    public init(composioApiKey: String, serverUri: String? = nil, transport: any IToolHttpTransport) throws {
        let key = composioApiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { throw ToolBuildError.argument("composioApiKey required") }
        self.apiKey = key
        let raw = (serverUri ?? Self.defaultServerUri).trimmingCharacters(in: .whitespacesAndNewlines)
        self.serverUri = raw.hasSuffix("/") ? raw : raw + "/"
        self.transport = transport
    }

    public var availableTools: [ToolDefinition] {
        lock.lock(); defer { lock.unlock() }
        return cachedTools
    }

    public func invoke(_ invocation: ToolInvocation) async throws -> ToolResult {
        let name = invocation.toolName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { throw ToolBuildError.argument("ToolName must not be null or whitespace.") }

        // JSON-RPC 2.0 request envelope.
        var argsJson: [String: Any] = [:]
        for (k, v) in invocation.arguments {
            argsJson[k] = v ?? NSNull()   // v is Any? — nil becomes JSON null
        }
        let requestBody: [String: Any] = [
            "jsonrpc": "2.0",
            "method": "tools/call",
            "id": 1,
            "params": [
                "name": invocation.toolName,
                "arguments": argsJson,
            ] as [String: Any],
        ]
        let jsonBody = (try? JSONSerialization.data(withJSONObject: requestBody, options: [.sortedKeys])) ?? Data("{}".utf8)

        let endpoint = serverUri + "tools/" + Self.escape(invocation.toolName) + "/invoke"

        do {
            let response = try await transport.send(
                method: "POST",
                url: endpoint,
                headers: ["X-API-Key": apiKey, "Content-Type": "application/json", "Accept": "application/json"],
                jsonBody: jsonBody)

            let root = (try? JSONSerialization.jsonObject(with: response.body, options: [.fragmentsAllowed])) as? [String: Any]

            if !response.isSuccess {
                let httpError = "HTTP \(response.statusCode)"
                return ToolResult.failure(toolName: invocation.toolName, error: Self.extractError(root, fallback: httpError))
            }

            // Standard JSON-RPC 2.0 response: { "result": ..., "error": ... }.
            if let root = root {
                if let errNode = root["error"], !(errNode is NSNull) {
                    let msg = (errNode as? [String: Any])?["message"] as? String ?? "\(errNode)"
                    return ToolResult.failure(toolName: invocation.toolName, error: msg)
                }
                if let resultNode = root["result"] {
                    return ToolResult.ok(toolName: invocation.toolName, result: (resultNode is NSNull) ? nil : resultNode)
                }
            }

            // No result / error — success with null payload.
            return ToolResult.ok(toolName: invocation.toolName)
        } catch {
            return ToolResult.failure(toolName: invocation.toolName, error: error.localizedDescription)
        }
    }

    public func getAvailableTools() async throws -> [ToolDefinition] {
        let endpoint = serverUri + "tools"
        do {
            let response = try await transport.send(
                method: "GET",
                url: endpoint,
                headers: ["X-API-Key": apiKey, "Accept": "application/json"],
                jsonBody: nil)

            guard response.isSuccess else { return [] }

            let root = try? JSONSerialization.jsonObject(with: response.body, options: [.fragmentsAllowed])
            let tools = Self.parseToolList(root)
            lock.lock(); cachedTools = tools; lock.unlock()
            return tools
        } catch {
            return []
        }
    }

    // MARK: helpers

    private static func parseToolList(_ root: Any?) -> [ToolDefinition] {
        // Composio may return an array at root, or { "tools": [...] }.
        let toolsArray: [[String: Any]]
        if let arr = root as? [[String: Any]] {
            toolsArray = arr
        } else if let obj = root as? [String: Any], let arr = obj["tools"] as? [[String: Any]] {
            toolsArray = arr
        } else {
            return []
        }

        var result: [ToolDefinition] = []
        result.reserveCapacity(toolsArray.count)
        for item in toolsArray {
            guard let name = item["name"] as? String, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
            let desc = item["description"] as? String ?? ""

            var parameters: [String: ToolParameter] = [:]
            var required: [String] = []

            if let schema = item["inputSchema"] as? [String: Any],
               let props = schema["properties"] as? [String: Any] {
                for (propName, rawProp) in props {
                    let prop = rawProp as? [String: Any] ?? [:]
                    let type = prop["type"] as? String ?? "string"
                    let propDesc = prop["description"] as? String ?? ""
                    parameters[propName] = ToolParameter(type: type, description: propDesc)
                }
                if let req = schema["required"] as? [Any] {
                    for r in req {
                        if let rName = r as? String, !rName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                            required.append(rName)
                        }
                    }
                }
            }

            result.append(ToolDefinition(
                name: name,
                description: desc,
                parameters: parameters,
                requiredParameters: required))
        }
        return result
    }

    private static func extractError(_ body: [String: Any]?, fallback: String) -> String {
        guard let body = body, let e = body["error"] else { return fallback }
        if let eObj = e as? [String: Any], let m = eObj["message"] as? String { return m }
        return "\(e)"
    }

    private static func escape(_ s: String) -> String {
        let allowed = CharacterSet(charactersIn:
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }
}
