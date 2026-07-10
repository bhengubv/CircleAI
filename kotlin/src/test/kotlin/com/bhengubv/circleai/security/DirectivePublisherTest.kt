// DirectivePublisherTest.kt
//
// Verifies fan-out: every subscriber receives each published directive,
// unsubscribe stops delivery, close is idempotent, and a consumer that
// unsubscribes from within its own callback does not deadlock (snapshot taken
// under lock, callbacks fire outside it).

package com.bhengubv.circleai.security

import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class DirectivePublisherTest {

    private fun directive(node: String = "peer-1") = PeerDirective(
        kind = PeerDirectiveKind.ElevateMonitoring,
        targetNodeId = node,
        trustScore = 0.7,
        threatLevel = PeerThreatLevel.Medium,
        reason = "test",
        duration = null,
        issuedAt = Instant.now(),
    )

    @Test
    fun `all subscribers receive each directive`() {
        val pub = DirectivePublisher()
        val a = AtomicInteger()
        val b = AtomicInteger()
        pub.subscribe { a.incrementAndGet() }
        pub.subscribe { b.incrementAndGet() }

        pub.publish(directive())
        pub.publish(directive())

        assertEquals(2, a.get())
        assertEquals(2, b.get())
        assertEquals(2, pub.subscriberCount)
    }

    @Test
    fun `unsubscribe stops delivery`() {
        val pub = DirectivePublisher()
        val count = AtomicInteger()
        val handle = pub.subscribe { count.incrementAndGet() }

        pub.publish(directive())
        assertEquals(1, count.get())

        handle.close()
        assertEquals(0, pub.subscriberCount)
        pub.publish(directive())
        assertEquals(1, count.get(), "no delivery after unsubscribe")
    }

    @Test
    fun `close is idempotent`() {
        val pub = DirectivePublisher()
        val handle = pub.subscribe { }
        assertEquals(1, pub.subscriberCount)
        handle.close()
        handle.close()
        assertEquals(0, pub.subscriberCount)
    }

    @Test
    fun `received directive carries the published payload`() {
        val pub = DirectivePublisher()
        var received: PeerDirective? = null
        pub.subscribe { received = it }
        pub.publish(directive("node-X"))
        assertTrue(received != null)
        assertEquals("node-X", received!!.targetNodeId)
        assertEquals(PeerDirectiveKind.ElevateMonitoring, received!!.kind)
    }

    @Test
    fun `consumer may unsubscribe from within its own callback without deadlock`() {
        val pub = DirectivePublisher()
        val hits = AtomicInteger()
        lateinit var handle: AutoCloseable
        handle = pub.subscribe {
            hits.incrementAndGet()
            handle.close() // reentrant unsubscribe during fan-out
        }
        pub.publish(directive())
        pub.publish(directive())
        assertEquals(1, hits.get(), "second publish should not reach the unsubscribed consumer")
        assertEquals(0, pub.subscriberCount)
    }
}
