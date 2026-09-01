package com.bhengubv.circleai.tools.catalog

import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class OAuth2FlowDriverTest {

    private val gmail = ProviderDescriptor(
        providerId = "gmail",
        displayName = "Gmail",
        description = "Mail.",
        homepage = null,
        auth = AuthKind.OAUTH2,
        oauth2 = OAuth2Descriptor(
            authorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
            tokenUrl = "https://oauth2.googleapis.com/token",
            scopes = listOf(
                "https://www.googleapis.com/auth/gmail.readonly",
                "https://www.googleapis.com/auth/gmail.send",
            ),
        ),
    )

    private val payfast = ProviderDescriptor(
        providerId = "payfast",
        displayName = "PayFast",
        description = "Payments.",
        homepage = null,
        auth = AuthKind.API_KEY,
    )

    private fun driver(
        exchange: suspend (String, String, String, String) -> CredentialBundle = { p, u, c, _ ->
            CredentialBundle(p, u, mapOf("access_token" to "exchanged:" + c))
        },
    ): OAuth2FlowDriver {
        val catalog = InMemoryProviderCatalog().apply {
            register(gmail)
            register(payfast)
        }
        return OAuth2FlowDriver(catalog, { "client-for-" + it }, exchange)
    }

    @Test
    fun theAuthorizeUrlCarriesEveryParameterTheProviderNeeds() = runTest {
        val url = driver().start("gmail", "thabo", "circleai://oauth/callback")
        assertTrue(url.startsWith("https://accounts.google.com/o/oauth2/v2/auth?response_type=code"))
        assertContains(url, "&client_id=client-for-gmail")
        assertContains(url, "&redirect_uri=circleai%3A%2F%2Foauth%2Fcallback")
        assertContains(url, "&state=")
    }

    @Test
    fun scopesAreSpaceJoinedThenEncodedAsPercentTwenty() = runTest {
        // Space-joined is the OAuth2 spec, and it must survive as %20 rather than
        // a plus, which is what URLEncoder would have written. A provider that
        // reads the plus literally silently grants the wrong scopes.
        val url = driver().start("gmail", "thabo", "app://cb")
        assertContains(url, "gmail.readonly%20https")
        assertFalse(url.contains("gmail.readonly+https"))
    }

    @Test
    fun aTildeIsUnreservedAndSurvivesUnencoded() = runTest {
        // URLEncoder percent-encodes the tilde; RFC 3986 does not. The other
        // ports keep it, and a redirect_uri that differs by one character is a
        // mismatch at the provider.
        val d = OAuth2FlowDriver(
            InMemoryProviderCatalog().apply { register(gmail) },
            { "cid" },
            { p, u, _, _ -> CredentialBundle(p, u, emptyMap()) },
        )
        val url = d.start("gmail", "thabo", "app://cb/~thabo")
        assertContains(url, "~thabo")
    }

    @Test
    fun unicodeInAClientIdIsUtfEightPercentEncoded() = runTest {
        val d = OAuth2FlowDriver(
            InMemoryProviderCatalog().apply { register(gmail) },
            { "klüb" },
            { p, u, _, _ -> CredentialBundle(p, u, emptyMap()) },
        )
        // u-umlaut is two bytes in UTF-8, so it becomes two percent-escapes.
        assertContains(d.start("gmail", "thabo", "app://cb"), "client_id=kl%C3%BCb")
    }

    @Test
    fun everyStartMintsAFreshState() = runTest {
        // State is the CSRF defence. A constant one defends against nothing.
        val d = driver()
        val a = d.start("gmail", "thabo", "app://cb")
        val b = d.start("gmail", "thabo", "app://cb")
        assertNotEquals(stateOf(a), stateOf(b))
    }

    @Test
    fun theStateIsUrlSafeBase64WithNoPadding() = runTest {
        val state = stateOf(driver().start("gmail", "thabo", "app://cb"))
        // 16 bytes is 22 base64 characters once the two padding equals are gone.
        assertEquals(22, state.length)
        assertFalse(state.contains("="))
        assertFalse(state.contains("+"))
        assertFalse(state.contains("/"))
        assertFalse(state.contains("%"))
    }

    @Test
    fun anUnknownProviderIsRefusedBeforeAnyUrlIsBuilt() = runTest {
        val e = assertFailsWith<ToolsCatalogError.InvalidOperation> {
            driver().start("linear", "thabo", "app://cb")
        }
        assertContains(e.message ?: "", "linear")
    }

    @Test
    fun aProviderThatIsNotOAuthIsRefusedRatherThanGivenAnEmptyUrl() = runTest {
        val e = assertFailsWith<ToolsCatalogError.InvalidOperation> {
            driver().start("payfast", "thabo", "app://cb")
        }
        assertContains(e.message ?: "", "not OAuth2")
    }

    @Test
    fun everyRequiredArgumentIsCheckedOnStart() = runTest {
        val d = driver()
        assertFailsWith<ToolsCatalogError.Argument> { d.start("", "thabo", "app://cb") }
        assertFailsWith<ToolsCatalogError.Argument> { d.start("gmail", " ", "app://cb") }
        assertFailsWith<ToolsCatalogError.Argument> { d.start("gmail", "thabo", "") }
    }

    @Test
    fun completeHandsTheCodeToTheHostExchange() = runTest {
        val bundle = driver().complete("gmail", "thabo", "4/0Aabc", "app://cb")
        assertEquals("exchanged:4/0Aabc", bundle.fields["access_token"])
        assertEquals("gmail", bundle.providerId)
        assertEquals("thabo", bundle.userId)
    }

    @Test
    fun everyRequiredArgumentIsCheckedBeforeTheExchangeRuns() = runTest {
        // Checked FIRST, so a blank code never reaches the network.
        var called = false
        val d = driver { p, u, _, _ ->
            called = true
            CredentialBundle(p, u, emptyMap())
        }
        assertFailsWith<ToolsCatalogError.Argument> { d.complete("", "thabo", "code", "app://cb") }
        assertFailsWith<ToolsCatalogError.Argument> { d.complete("gmail", "", "code", "app://cb") }
        assertFailsWith<ToolsCatalogError.Argument> { d.complete("gmail", "thabo", " ", "app://cb") }
        assertFailsWith<ToolsCatalogError.Argument> { d.complete("gmail", "thabo", "code", "") }
        assertFalse(called)
    }

    @Test
    fun completeDoesNotRequireTheProviderToBeInTheCatalog() = runTest {
        // Deliberate: the exchange leg is the host problem, and a provider list
        // that has been reloaded mid-flow must not strand somebody who is already
        // holding an authorisation code.
        val b = driver().complete("linear", "thabo", "code", "app://cb")
        assertEquals("linear", b.providerId)
    }

    @Test
    fun theBackendIdSaysWhatItIs() {
        assertEquals("oauth2", driver().backendId)
    }

    private fun stateOf(url: String): String = url.substringAfter("&state=")
}

class SlidingWindowQuotaGuardTest {

    private class Clock(var millis: Long = 1_788_000_000_000L) {
        fun now(): Long = millis
        fun advanceSeconds(s: Long) { millis += s * 1000L }
    }

    private fun policy(
        daily: Int = 1000,
        concurrent: Int = 100,
        perMinute: Int = 100,
    ) = QuotaPolicy("gmail", "thabo", daily, concurrent, perMinute)

    @Test
    fun noPolicyMeansUNLIMITEDnotDenied() = runTest {
        // The opposite of NullQuotaGuard, and on purpose: a provider nobody has
        // budgeted for still works. Fail-closed lives in the null object.
        val g = SlidingWindowQuotaGuard()
        repeat(500) { assertTrue(g.tryAcquire("gmail", "thabo")) }
    }

    @Test
    fun setPolicyThenGetPolicyRoundTripsPerPair() = runTest {
        val g = SlidingWindowQuotaGuard()
        g.setPolicy(policy())
        assertEquals(policy(), g.getPolicy("gmail", "thabo"))
        // Another user on the same provider is a different budget.
        assertNull(g.getPolicy("gmail", "nomsa"))
        assertEquals("sliding-window", g.backendId)
    }

    @Test
    fun thePerMinuteCapStopsTheBurst() = runTest {
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(perMinute = 3))
        repeat(3) { assertTrue(g.tryAcquire("gmail", "thabo")) }
        assertFalse(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun theWindowSLIDESratherThanResettingOnTheMinute() = runTest {
        // A fixed bucket would let a caller spend the whole cap at 59 seconds and
        // the whole cap again at 61. A sliding window does not.
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(perMinute = 3))
        repeat(3) { assertTrue(g.tryAcquire("gmail", "thabo")) }
        assertFalse(g.tryAcquire("gmail", "thabo"))

        c.advanceSeconds(59)
        assertFalse(g.tryAcquire("gmail", "thabo"))

        // Now the first three have aged out of the window together.
        c.advanceSeconds(2)
        assertTrue(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun callsAgeOutOneAtATimeNotAllAtOnce() = runTest {
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(perMinute = 2))
        assertTrue(g.tryAcquire("gmail", "thabo"))
        c.advanceSeconds(30)
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertFalse(g.tryAcquire("gmail", "thabo"))

        // 61s after the first call: it has expired, the second has not.
        c.advanceSeconds(31)
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertFalse(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun theDailyBudgetOutlivesThePerMinuteWindow() = runTest {
        // The daily count is taken over the SAME list, so a budget larger than
        // the per-minute cap is only reachable across minutes - and a spend that
        // walks slowly still runs out.
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(daily = 3, perMinute = 100))
        repeat(3) { assertTrue(g.tryAcquire("gmail", "thabo")) }
        assertFalse(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun maxConcurrentIsHeldUntilReleaseIsCalled() = runTest {
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(concurrent = 2))
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertFalse(g.tryAcquire("gmail", "thabo"))

        g.release("gmail", "thabo")
        assertTrue(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun releaseNeverDrivesTheCountBelowZero() = runTest {
        // An unbalanced release must not bank free slots for later.
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(concurrent = 1))
        repeat(5) { g.release("gmail", "thabo") }
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertFalse(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun aRefusedCallDoesNotSPENDtheBudgetItWasRefusedBy() = runTest {
        // A denied acquire must not append to the call list, or a caller who
        // retries in a loop pushes their own window out forever.
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(policy(perMinute = 1))
        assertTrue(g.tryAcquire("gmail", "thabo"))
        repeat(20) { assertFalse(g.tryAcquire("gmail", "thabo")) }

        c.advanceSeconds(61)
        assertTrue(g.tryAcquire("gmail", "thabo"))
    }

    @Test
    fun budgetsDoNotBleedBetweenUsersOrProviders() = runTest {
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        g.setPolicy(QuotaPolicy("gmail", "thabo", 1000, 100, 1))
        g.setPolicy(QuotaPolicy("gmail", "nomsa", 1000, 100, 1))
        g.setPolicy(QuotaPolicy("slack", "thabo", 1000, 100, 1))

        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertFalse(g.tryAcquire("gmail", "thabo"))
        assertTrue(g.tryAcquire("gmail", "nomsa"))
        assertTrue(g.tryAcquire("slack", "thabo"))
    }

    @Test
    fun aPolicySetAfterTheFactAppliesToTheCallsAlreadyRecorded() = runTest {
        // Calls are recorded only while a policy exists, so tightening the budget
        // mid-flight starts from a clean window rather than retroactively.
        val c = Clock()
        val g = SlidingWindowQuotaGuard(c::now)
        repeat(10) { g.tryAcquire("gmail", "thabo") }
        g.setPolicy(policy(perMinute = 2))
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertTrue(g.tryAcquire("gmail", "thabo"))
        assertFalse(g.tryAcquire("gmail", "thabo"))
    }
}
