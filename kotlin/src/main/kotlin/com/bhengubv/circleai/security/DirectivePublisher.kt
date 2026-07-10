// DirectivePublisher.kt
//
// Kotlin port of src/CircleAI.Security/DirectivePublisher.cs.
//
// Fan-out publisher for PeerDirectives.
//
// Keeps a list of IPeerDirectiveConsumer subscriptions and fans every published
// directive out to all current subscribers. Concurrent subscribe, unsubscribe,
// and publish operations are all thread-safe: a snapshot of the subscriber list
// is taken under the lock, then callbacks fire OUTSIDE the lock so a consumer
// that (un)subscribes from within its own callback cannot self-deadlock.

package com.bhengubv.circleai.security

import java.util.concurrent.atomic.AtomicBoolean

/**
 * Manages [IPeerDirectiveConsumer] subscriptions and fans published
 * [PeerDirective] instances out to all subscribers.
 */
class DirectivePublisher {

    private val lock = Any()
    private val consumers = ArrayList<IPeerDirectiveConsumer>()

    /**
     * Subscribes [consumer] to receive directives. Close the returned handle to
     * unsubscribe. Idempotent close.
     */
    fun subscribe(consumer: IPeerDirectiveConsumer): AutoCloseable {
        synchronized(lock) { consumers.add(consumer) }
        return SubscriptionHandle(this, consumer)
    }

    /**
     * Publishes [directive] to all current subscribers. A snapshot is taken
     * under the lock; callbacks fire outside it.
     */
    fun publish(directive: PeerDirective) {
        val snapshot = synchronized(lock) { consumers.toList() }
        for (c in snapshot) {
            c.onDirective(directive)
        }
    }

    /** Number of currently active subscribers. Useful in tests. */
    val subscriberCount: Int
        get() = synchronized(lock) { consumers.size }

    private fun unsubscribe(consumer: IPeerDirectiveConsumer) {
        synchronized(lock) { consumers.remove(consumer) }
    }

    private class SubscriptionHandle(
        private val publisher: DirectivePublisher,
        private val consumer: IPeerDirectiveConsumer,
    ) : AutoCloseable {
        private val disposed = AtomicBoolean(false)

        override fun close() {
            if (disposed.compareAndSet(false, true)) {
                publisher.unsubscribe(consumer)
            }
        }
    }
}
