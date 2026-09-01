package com.bhengubv.circleai.security.antibodies

import java.time.Instant
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The gate. Deny by default is the whole design, so most of this is about the
 * ways an antibody must NOT run.
 */
class AntibodiesGateTest {

    private val now: Instant = Instant.ofEpochSecond(1_782_896_400)

    private fun threat(reason: String = "User reported a suspicious attachment") =
        DefensiveThreatContext.raise(reason, DefensiveThreatSeverity.ELEVATED, "user", now)!!

    private fun request(
        cap: AntibodyCapability = AntibodyCapability.FILE_REPUTATION_AWARENESS,
        t: DefensiveThreatContext = threat(),
    ) = AuthorizedUseRequest(UUID.randomUUID(), cap, t, "warn the user", now)

    @Test fun `the default gate cannot grant anything`() {
        val d = NullAuthorizedUseGate.requestAuthorization(request())
        assertFalse(d.granted)
        assertEquals(NullAuthorizedUseGate.DENIAL_REASON, d.reason)
        assertNull(d.expiresAtUtc)
    }

    @Test fun `the default gate denies every capability`() {
        for (cap in AntibodyCapability.entries) {
            assertFalse(NullAuthorizedUseGate.requestAuthorization(request(cap)).granted, cap.displayName)
        }
    }

    @Test fun `no consent means no authorization`() {
        val gate = ExplicitConsentAuthorizedUseGate(InMemoryAuthorizedUseConsentStore()) { now }
        val d = gate.requestAuthorization(request())
        assertFalse(d.granted)
        assertTrue(d.reason.contains("denied by default"))
    }

    @Test fun `an active consent authorizes and carries its expiry`() {
        val store = InMemoryAuthorizedUseConsentStore()
        val consent = AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "Nandi", "one file", 3600.0, now)!!
        store.record(consent)

        val d = ExplicitConsentAuthorizedUseGate(store) { now }.requestAuthorization(request())
        assertTrue(d.granted)
        assertEquals(consent.expiresAtUtc, d.expiresAtUtc)
        assertTrue(d.reason.contains("Nandi"))
    }

    // Consent for one capability must not unlock the others.
    @Test fun `consent does not spread to other capabilities`() {
        val store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "u", "s", 3600.0, now)!!)
        val gate = ExplicitConsentAuthorizedUseGate(store) { now }

        assertTrue(gate.requestAuthorization(
            request(AntibodyCapability.FILE_REPUTATION_AWARENESS)).granted)
        assertFalse(gate.requestAuthorization(
            request(AntibodyCapability.NETWORK_INDICATOR_AWARENESS)).granted)
    }

    // An expired consent is exactly as good as no consent.
    @Test fun `an expired consent authorizes nothing`() {
        val store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "u", "s", 60.0, now)!!)
        val later = now.plusSeconds(120)

        assertFalse(ExplicitConsentAuthorizedUseGate(store) { later }
            .requestAuthorization(request()).granted)
    }

    @Test fun `a consent is not active before it was granted`() {
        val c = AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "u", "s", 60.0, now)!!
        assertFalse(c.isActiveFor(AntibodyCapability.FILE_REPUTATION_AWARENESS, now.minusSeconds(1)))
        assertTrue(c.isActiveFor(AntibodyCapability.FILE_REPUTATION_AWARENESS, now))
        // Half-open: dead the instant it expires.
        assertFalse(c.isActiveFor(AntibodyCapability.FILE_REPUTATION_AWARENESS, c.expiresAtUtc))
    }

    @Test fun `revoking takes the authorization away immediately`() {
        val store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "u", "s", 3600.0, now)!!)
        val gate = ExplicitConsentAuthorizedUseGate(store) { now }
        assertTrue(gate.requestAuthorization(request()).granted)

        store.revoke(AntibodyCapability.FILE_REPUTATION_AWARENESS)
        assertFalse(gate.requestAuthorization(request()).granted)
    }

    @Test fun `revoke all clears everything`() {
        val store = InMemoryAuthorizedUseConsentStore()
        for (cap in AntibodyCapability.entries) {
            store.record(AuthorizedUseConsent.grant(cap, "u", "s", 3600.0, now)!!)
        }
        store.revokeAll()
        val gate = ExplicitConsentAuthorizedUseGate(store) { now }
        for (cap in AntibodyCapability.entries) {
            assertFalse(gate.requestAuthorization(request(cap)).granted, cap.displayName)
        }
    }

    // Consent ALONE is not enough. Without a named threat this is a capability
    // being used just to check, which is what the module exists to refuse.
    @Test fun `consent without a threat still denies`() {
        val store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, "u", "s", 3600.0, now)!!)
        val gate = ExplicitConsentAuthorizedUseGate(store) { now }

        val empty = DefensiveThreatContext("   ", DefensiveThreatSeverity.ELEVATED, "u", now, UUID.randomUUID())
        val d = gate.requestAuthorization(request(AntibodyCapability.FILE_REPUTATION_AWARENESS, empty))
        assertFalse(d.granted)
        assertTrue(d.reason.contains("only under a defined threat"))
    }

    @Test fun `a threat needs a reason and somebody raising it`() {
        assertNull(DefensiveThreatContext.raise("  ", DefensiveThreatSeverity.HIGH, "u"))
        assertNull(DefensiveThreatContext.raise("real", DefensiveThreatSeverity.HIGH, " "))
        assertNotNull(DefensiveThreatContext.raise("real", DefensiveThreatSeverity.HIGH, "u"))
    }

    // A consent with no end is not a consent.
    @Test fun `a consent needs a positive duration and a granter`() {
        val cap = AntibodyCapability.FILE_REPUTATION_AWARENESS
        assertNull(AuthorizedUseConsent.grant(cap, "u", "s", 0.0))
        assertNull(AuthorizedUseConsent.grant(cap, "u", "s", -60.0))
        assertNull(AuthorizedUseConsent.grant(cap, " ", "s", 60.0))
        assertNull(AuthorizedUseConsent.grant(cap, "u", " ", 60.0))
    }

    @Test fun `a request needs a justification`() {
        assertNull(AuthorizedUseRequest.of(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, threat(), "  "))
        assertNotNull(AuthorizedUseRequest.of(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, threat(), "warn the user"))
    }

    @Test fun `a decision carries the request it answers`() {
        val r = request()
        val denied = AuthorizationDecision.deny(r, "no", now)
        assertEquals(r.requestId, denied.requestId)
        assertEquals(r.capability, denied.capability)
        assertNull(denied.expiresAtUtc)
    }
}
