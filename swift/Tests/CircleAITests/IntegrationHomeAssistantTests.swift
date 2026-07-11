// IntegrationHomeAssistantTests.swift
//
// Exercises the HomeAssistant connector against FakeIntegrationHttpTransport:
// /api/states entity parse (domain split, friendly_name, attribute stringify),
// service calls, and the turn_on / turn_off helpers. Mirrors
// src/CircleAI.Integration.HomeAssistant/.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationHomeAssistantTests: XCTestCase {

    private func ha(_ http: IIntegrationHttpTransport, token: String = "llt") -> HomeAssistantConnector {
        HomeAssistantConnector(
            opts: HomeAssistantOptions(baseUrl: URL(string: "http://homeassistant.local:8123/")!, accessToken: token),
            http: http)
    }

    func testProviderIdAndConfigured() {
        XCTAssertEqual(ha(FakeIntegrationHttpTransport()).providerId, "home-assistant")
        XCTAssertTrue(ha(FakeIntegrationHttpTransport()).isConfigured)
        XCTAssertFalse(ha(FakeIntegrationHttpTransport(), token: "  ").isConfigured)
    }

    func testConstructorSetsBearerHeader() {
        let http = FakeIntegrationHttpTransport()
        _ = ha(http, token: "secret")
        XCTAssertEqual(http.defaultHeaders["Authorization"], "Bearer secret")
    }

    func testListEntitiesParsesStatesArray() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/api/states", json: """
        [
          {"entity_id":"light.kitchen","state":"on",
           "attributes":{"friendly_name":"Kitchen Light","brightness":255,"supported":true}},
          {"entity_id":"sensor.temp","state":"21.4","attributes":{"unit_of_measurement":"°C"}},
          {"entity_id":"","state":"ignored"}
        ]
        """)
        let entities = try await ha(http).listEntities()
        XCTAssertEqual(entities.count, 2) // empty entity_id skipped
        let kitchen = try XCTUnwrap(entities.first { $0.entityId == "light.kitchen" })
        XCTAssertEqual(kitchen.domain, "light")
        XCTAssertEqual(kitchen.friendlyName, "Kitchen Light")
        XCTAssertEqual(kitchen.state, "on")
        XCTAssertEqual(kitchen.attributes["brightness"], "255") // number → text
        XCTAssertEqual(kitchen.attributes["supported"], "true") // bool → "true"

        let sensor = try XCTUnwrap(entities.first { $0.entityId == "sensor.temp" })
        XCTAssertEqual(sensor.domain, "sensor")
        XCTAssertEqual(sensor.friendlyName, "sensor.temp") // no friendly_name → entity id
    }

    func testCallServiceSerialisesOrderedArgs() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.post, urlContains: "/api/services/light/turn_on", json: "[]")
        try await ha(http).callService(
            domain: "light", service: "turn_on",
            data: [
                IntegrationServiceArg("entity_id", .string("light.kitchen")),
                IntegrationServiceArg("brightness", .int(128)),
            ])
        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(req.method, .post)
        let body = try IntegrationJson.parseObject(req.body)
        XCTAssertEqual(IntegrationJson.string(body, "entity_id"), "light.kitchen")
        XCTAssertEqual(IntegrationJson.int(body, "brightness"), 128)
    }

    func testCallServiceValidatesDomainAndService() async {
        let c = ha(FakeIntegrationHttpTransport())
        do { try await c.callService(domain: " ", service: "x", data: nil); XCTFail() }
        catch IntegrationError.argument {} catch { XCTFail("wrong \(error)") }
        do { try await c.callService(domain: "d", service: " ", data: nil); XCTFail() }
        catch IntegrationError.argument {} catch { XCTFail("wrong \(error)") }
    }

    func testCallServiceWithNilDataSendsEmptyObject() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.post, urlContains: "/api/services/homeassistant/restart", json: "[]")
        try await ha(http).callService(domain: "homeassistant", service: "restart", data: nil)
        let body = try IntegrationJson.parseObject(try XCTUnwrap(http.lastRequest).body)
        XCTAssertTrue(body.isEmpty)
    }

    func testTurnOnAndTurnOffUseHomeassistantDomain() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.post, where: { $0.contains("/api/services/homeassistant/turn_on") }, respond: { _ in .json("[]") })
        http.on(.post, where: { $0.contains("/api/services/homeassistant/turn_off") }, respond: { _ in .json("[]") })

        try await ha(http).turnOn(entityId: "switch.fan")
        var body = try IntegrationJson.parseObject(try XCTUnwrap(http.lastRequest).body)
        XCTAssertEqual(IntegrationJson.string(body, "entity_id"), "switch.fan")
        XCTAssertTrue(http.lastRequest?.url.contains("/homeassistant/turn_on") ?? false)

        try await ha(http).turnOff(entityId: "switch.fan")
        body = try IntegrationJson.parseObject(try XCTUnwrap(http.lastRequest).body)
        XCTAssertEqual(IntegrationJson.string(body, "entity_id"), "switch.fan")
        XCTAssertTrue(http.lastRequest?.url.contains("/homeassistant/turn_off") ?? false)
    }

    func testResolveBuildsUrlUnderBase() {
        let url = HomeAssistantConnector.resolve(URL(string: "http://homeassistant.local:8123/")!, "api/states")
        XCTAssertEqual(url, "http://homeassistant.local:8123/api/states")
    }
}
