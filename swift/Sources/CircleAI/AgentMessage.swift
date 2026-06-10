// AgentMessage.swift
//
// AgentMessage with auto-synthesised correlation ID.

import Foundation

public enum AgentMessageKind: Int, Sendable {
    case discover = 0, greet, capabilityQuery, invoke, response, decline, heartbeat
}

public struct AgentMessage: Sendable, Equatable {
    public let id: UUID
    public let kind: AgentMessageKind
    public let fromUhid: String
    public let toUhid: String
    public let contentType: String
    public let payload: Data
    public let signature: Data
    public let sentAt: Date
    public let correlationId: String

    public init(
        id: UUID,
        kind: AgentMessageKind,
        fromUhid: String,
        toUhid: String,
        contentType: String,
        payload: Data,
        signature: Data,
        sentAt: Date,
        correlationId: String
    ) {
        self.id = id; self.kind = kind; self.fromUhid = fromUhid; self.toUhid = toUhid
        self.contentType = contentType; self.payload = payload; self.signature = signature
        self.sentAt = sentAt; self.correlationId = correlationId
    }

    public static func create(
        kind: AgentMessageKind,
        fromUhid: String,
        toUhid: String,
        contentType: String,
        payload: Data,
        signature: Data,
        correlationId: String? = nil
    ) -> AgentMessage {
        let cid: String
        if let c = correlationId, !c.isEmpty {
            cid = c
        } else {
            var bytes = [UInt8](repeating: 0, count: 16)
            for i in 0..<16 { bytes[i] = UInt8.random(in: 0...255) }
            cid = bytes.map { String(format: "%02x", $0) }.joined()
        }
        return AgentMessage(
            id: UUID(),
            kind: kind,
            fromUhid: fromUhid,
            toUhid: toUhid,
            contentType: contentType,
            payload: payload,
            signature: signature,
            sentAt: Date(),
            correlationId: cid
        )
    }
}
