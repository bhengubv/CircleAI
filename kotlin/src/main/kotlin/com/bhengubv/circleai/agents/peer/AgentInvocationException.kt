// AgentInvocationException.kt
//
// Kotlin port of CircleAI.Agents.Peer/AgentInvocationException.cs.
//
// Raised by IAgentPeerProtocol.invoke when a peer Declines the invocation or
// otherwise fails to return a Response envelope.

package com.bhengubv.circleai.agents.peer

/**
 * Thrown when a peer declines an [AgentMessageKind.INVOKE] or returns an error
 * response.
 *
 * @property peerUhid The peer that declined or errored, if known.
 * @property declineMessage The decline envelope returned by the peer, if any.
 */
class AgentInvocationException : RuntimeException {
    val peerUhid: String?
    val declineMessage: AgentMessage?

    constructor(message: String) : super(message) {
        peerUhid = null
        declineMessage = null
    }

    constructor(message: String, peerUhid: String) : super(message) {
        this.peerUhid = peerUhid
        this.declineMessage = null
    }

    constructor(message: String, peerUhid: String, declineMessage: AgentMessage) : super(message) {
        this.peerUhid = peerUhid
        this.declineMessage = declineMessage
    }

    constructor(message: String, cause: Throwable) : super(message, cause) {
        peerUhid = null
        declineMessage = null
    }
}
