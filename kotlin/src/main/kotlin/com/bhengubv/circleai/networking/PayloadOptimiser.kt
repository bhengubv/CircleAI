// PayloadOptimiser.kt
//
// The compress/decompress seam for anything crossing a radio.
//
// A SEAM, NOT AN ALGORITHM. What is worth compressing depends entirely on the
// link: over Wi-Fi Direct at 50 payloads a second the CPU cost is the expensive
// part, and over BLE at 9 a second the bytes are. A host picks; this contract is
// what lets it.
//
// Ported from src/CircleAI.Networking/IPayloadOptimiser.cs.

package com.bhengubv.circleai.networking

interface IPayloadOptimiser {
    /** A stable name, so a receiver knows what it is being handed. */
    val optimiserId: String

    /**
     * Returns the payload to send. An implementation that decides compression is
     * not worth it MUST return the payload unchanged rather than a wrapper —
     * round-tripping an unhelpful compression costs both ends and gains nothing.
     */
    fun optimise(payload: NetworkPayload): NetworkPayload

    /** The inverse. Must be safe to call on a payload that was never optimised:
     *  a mesh carries traffic from peers running older builds. */
    fun decompress(payload: NetworkPayload): NetworkPayload
}
