// IntegrationHomeAssistant.swift
//
// Port of the CircleAI.Integration.HomeAssistant vertical:
//   • HomeAssistantConnector.cs → HomeAssistantOptions + HomeAssistantConnector
//
// Talks HTTP → the injected `IIntegrationHttpTransport`; the `/api/states`
// entity parse (domain split on the first '.', attribute value stringify,
// friendly_name extraction), the `/api/services/{domain}/{service}` call, and
// the turn_on / turn_off convenience helpers are ported verbatim and asserted
// against `FakeIntegrationHttpTransport` (no real calls).

import Foundation

/// HomeAssistant connector config. Port of the C# `HomeAssistantOptions`.
public struct HomeAssistantOptions: Sendable, Equatable {
    /// HA base URL, e.g. http://homeassistant.local:8123/ — must include a
    /// trailing slash.
    public let baseUrl: URL
    /// Long-lived access token.
    public let accessToken: String

    public init(baseUrl: URL, accessToken: String) {
        self.baseUrl = baseUrl
        self.accessToken = accessToken
    }
}

/// HomeAssistant REST `IHomeAutomationConnector`. Port of the C#
/// `HomeAssistantConnector`.
public final class HomeAssistantConnector: IHomeAutomationConnector, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: HomeAssistantOptions

    public init(opts: HomeAssistantOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
        if http.baseAddress == nil { http.baseAddress = opts.baseUrl }
        if !opts.accessToken.isBlank {
            var headers = http.defaultHeaders
            headers["Authorization"] = "Bearer \(opts.accessToken)"
            http.defaultHeaders = headers
        }
    }

    public var providerId: String { "home-assistant" }
    public var isConfigured: Bool { !opts.accessToken.isBlank }

    public func listEntities() async throws -> [HaEntity] {
        let resp = try await http.send(IntegrationHttpRequest(
            method: .get, url: Self.resolve(opts.baseUrl, "api/states")))
        try resp.ensureSuccess()
        let arr = try IntegrationJson.parseArray(resp.body)

        var list: [HaEntity] = []
        for case let st as [String: Any] in arr {
            let entityId = IntegrationJson.string(st, "entity_id") ?? ""
            if entityId.isEmpty { continue }
            let state = IntegrationJson.string(st, "state") ?? ""
            // domain = entityId.Split('.', 2)[0]
            let domain = entityId.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? entityId
            var attrs: [String: String] = [:]
            var friendly = entityId
            if let attObj = IntegrationJson.object(st, "attributes") {
                for (name, value) in attObj {
                    attrs[name] = IntegrationJson.haAttributeString(value)
                    if name == "friendly_name", IntegrationJson.isJsonString(value) {
                        friendly = (value as? String) ?? entityId
                    }
                }
            }
            list.append(HaEntity(entityId: entityId, friendlyName: friendly, domain: domain, state: state, attributes: attrs))
        }
        return list
    }

    public func callService(domain: String, service: String, data: [IntegrationServiceArg]?) async throws {
        if domain.isBlank { throw IntegrationError.argument("domain required") }
        if service.isBlank { throw IntegrationError.argument("service required") }

        // C#: payload = data ?? new Dictionary(); POST as JSON.
        var payload: [String: Any] = [:]
        for arg in (data ?? []) { payload[arg.name] = arg.value.jsonObject }
        let resp = try await http.send(IntegrationHttpRequest(
            method: .post,
            url: Self.resolve(opts.baseUrl, "api/services/\(IntegrationUri.escapeDataString(domain))/\(IntegrationUri.escapeDataString(service))"),
            body: try IntegrationJson.encode(payload),
            contentType: .json))
        try resp.ensureSuccess()
    }

    /// Convenience: turn an entity on via `homeassistant.turn_on`. Port of the
    /// C# `TurnOnAsync`.
    public func turnOn(entityId: String) async throws {
        try await callService(domain: "homeassistant", service: "turn_on",
                              data: [IntegrationServiceArg("entity_id", .string(entityId))])
    }

    /// Convenience: turn an entity off via `homeassistant.turn_off`. Port of the
    /// C# `TurnOffAsync`.
    public func turnOff(entityId: String) async throws {
        try await callService(domain: "homeassistant", service: "turn_off",
                              data: [IntegrationServiceArg("entity_id", .string(entityId))])
    }

    /// Resolve a relative API path against the HA base URL (which includes a
    /// trailing slash), mirroring `HttpClient`'s base-relative resolution.
    static func resolve(_ base: URL, _ path: String) -> String {
        URL(string: path, relativeTo: base)?.absoluteString ?? (base.absoluteString + path)
    }
}
