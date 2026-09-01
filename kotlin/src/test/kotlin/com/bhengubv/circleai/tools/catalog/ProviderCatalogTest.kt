package com.bhengubv.circleai.tools.catalog

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class InMemoryProviderCatalogTest {

    private val gmail = ProviderDescriptor(
        providerId = "gmail",
        displayName = "Gmail",
        description = "Read and send mail from a Google account.",
        homepage = "https://mail.google.com",
        auth = AuthKind.OAUTH2,
        tags = listOf("email", "google"),
        capabilities = listOf("mail.read", "mail.send"),
        oauth2 = OAuth2Descriptor(
            authorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth",
            tokenUrl = "https://oauth2.googleapis.com/token",
            scopes = listOf("https://www.googleapis.com/auth/gmail.readonly"),
        ),
    )

    private val slack = ProviderDescriptor(
        providerId = "slack",
        displayName = "Slack",
        description = "Post messages to a workspace.",
        homepage = null,
        auth = AuthKind.BEARER_TOKEN,
        tags = listOf("chat"),
        capabilities = listOf("chat.post"),
    )

    private val payfast = ProviderDescriptor(
        providerId = "payfast",
        displayName = "PayFast",
        description = "South African payment gateway with email receipts.",
        homepage = null,
        auth = AuthKind.API_KEY,
        tags = listOf("payments", "za"),
        capabilities = listOf("payment.create"),
    )

    private fun catalog() = InMemoryProviderCatalog().apply {
        register(slack)
        register(gmail)
        register(payfast)
    }

    @Test
    fun listingIsSortedByIdRegardlessOfRegistrationOrder() = runTest {
        // A stable order matters because this list is what a person scrolls.
        assertContentEquals(
            listOf("gmail", "payfast", "slack"),
            catalog().listProviders().map { it.providerId },
        )
    }

    @Test
    fun lookupIsCaseInsensitive() = runTest {
        val c = catalog()
        assertEquals(gmail, c.getProvider("GMAIL"))
        assertEquals(gmail, c.getProvider("GmAiL"))
    }

    @Test
    fun registeringTheSameIdReplacesRatherThanDuplicating() = runTest {
        val c = catalog()
        c.register(slack.copy(displayName = "Slack (work)"))
        assertEquals(3, c.listProviders().size)
        assertEquals("Slack (work)", c.getProvider("slack")!!.displayName)
    }

    @Test
    fun differentlyCasedIdsAreTheSameProviderNotTwo() = runTest {
        val c = InMemoryProviderCatalog()
        c.register(slack)
        c.register(slack.copy(providerId = "SLACK", displayName = "Shouty"))
        assertEquals(1, c.listProviders().size)
        assertEquals("Shouty", c.getProvider("slack")!!.displayName)
    }

    @Test
    fun anUnknownProviderIsNullNotAnError() = runTest {
        assertNull(catalog().getProvider("linear"))
    }

    @Test
    fun aBlankProviderIdIsRefused() = runTest {
        assertFailsWith<ToolsCatalogError.Argument> { catalog().getProvider("") }
        assertFailsWith<ToolsCatalogError.Argument> { catalog().getProvider("   ") }
    }

    @Test
    fun theNameOutranksATagWhichOutranksTheProse() = runTest {
        // Both match "email": Gmail on a tag, PayFast only in its description.
        // Weighted 2 against 1, so the mail provider comes first when somebody
        // searches for mail.
        val hits = catalog().searchProviders("email")
        assertEquals(listOf("gmail", "payfast"), hits.map { it.providerId })
    }

    @Test
    fun aNameMatchScoresHighestOfAll() = runTest {
        val hits = catalog().searchProviders("slack")
        assertEquals("slack", hits.first().providerId)
    }

    @Test
    fun aCapabilityIsSearchableToo() = runTest {
        // Nobody types "chat.post", but the agent picking a tool does.
        val hits = catalog().searchProviders("chat.post")
        assertEquals(listOf("slack"), hits.map { it.providerId })
    }

    @Test
    fun searchIsCaseInsensitive() = runTest {
        assertEquals("gmail", catalog().searchProviders("GMAIL").first().providerId)
    }

    @Test
    fun aQueryNothingMatchesReturnsEmptyRatherThanEverything() = runTest {
        // A zero score must be FILTERED, not merely sorted last. A search that
        // falls back to the whole catalog is worse than one that admits defeat.
        assertTrue(catalog().searchProviders("quantum").isEmpty())
    }

    @Test
    fun topKCapsTheResults() = runTest {
        assertEquals(1, catalog().searchProviders("email", topK = 1).size)
    }

    @Test
    fun aTopKOfZeroOrLessIsRefused() = runTest {
        assertFailsWith<ToolsCatalogError.Argument> { catalog().searchProviders("email", topK = 0) }
        assertFailsWith<ToolsCatalogError.Argument> { catalog().searchProviders("email", topK = -1) }
    }

    @Test
    fun anEmptyCatalogSearchesAndListsWithoutComplaint() = runTest {
        val c = InMemoryProviderCatalog()
        assertTrue(c.listProviders().isEmpty())
        assertTrue(c.searchProviders("anything").isEmpty())
        assertEquals("in-memory", c.backendId)
    }
}

class NullImplementationsTest {

    @Test
    fun theNullCatalogKnowsNothingAndSaysSo() = runTest {
        val c = NullProviderCatalog.instance
        assertEquals("null", c.backendId)
        assertTrue(c.listProviders().isEmpty())
        assertNull(c.getProvider("gmail"))
        assertTrue(c.searchProviders("gmail").isEmpty())
    }

    @Test
    fun theNullCredentialStoreSwallowsWritesAndHasNothingToRead() = runTest {
        val s = NullCredentialStore.instance
        assertEquals("null", s.backendId)
        s.upsert(CredentialBundle("gmail", "thabo", mapOf("t" to "1")))
        assertNull(s.get("gmail", "thabo"))
        s.delete("gmail", "thabo")
    }

    @Test
    fun theNullOAuthDriverSendsNobodyAnywhereAndRefusesToFinish() = runTest {
        val d = NullOAuth2FlowDriver.instance
        assertEquals("about:blank", d.start("gmail", "thabo", "app://cb"))
        // Refusing loudly here is the point: a silent empty bundle would look
        // like a successful authorisation that granted nothing.
        assertFailsWith<ToolsCatalogError.InvalidOperation> {
            d.complete("gmail", "thabo", "code", "app://cb")
        }
    }

    @Test
    fun theNullQuotaGuardDeniesEverything() = runTest {
        // FAIL CLOSED, and note this is the opposite of the sliding-window guard,
        // where no policy means unlimited. An unwired quota layer must not hand
        // out unlimited calls against somebody paid API.
        val g = NullQuotaGuard.instance
        assertEquals(false, g.tryAcquire("gmail", "thabo"))
        g.setPolicy(QuotaPolicy("gmail", "thabo", 1000, 4, 60))
        assertEquals(false, g.tryAcquire("gmail", "thabo"))
        assertNull(g.getPolicy("gmail", "thabo"))
    }

    @Test
    fun theNullNamespaceStoreHoldsNothing() = runTest {
        val s = NullToolNamespaceStore.instance
        assertEquals("null", s.backendId)
        s.upsert(ToolNamespace("ns", "thabo", listOf("gmail")))
        assertNull(s.get("ns"))
        assertTrue(s.listForUser("thabo").isEmpty())
    }
}

class InMemoryToolNamespaceStoreTest {

    @Test
    fun upsertThenGetRoundTrips() = runTest {
        val s = InMemoryToolNamespaceStore()
        val ns = ToolNamespace("work", "thabo", listOf("gmail", "slack"))
        s.upsert(ns)
        assertEquals(ns, s.get("work"))
        assertEquals("in-memory", s.backendId)
    }

    @Test
    fun listForUserReturnsOnlyThatUserNamespaces() = runTest {
        // The whole reason the type exists: one person tool list must not leak
        // into another list.
        val s = InMemoryToolNamespaceStore()
        s.upsert(ToolNamespace("work", "thabo", listOf("gmail")))
        s.upsert(ToolNamespace("home", "thabo", listOf("slack")))
        s.upsert(ToolNamespace("shop", "nomsa", listOf("payfast")))
        assertEquals(setOf("work", "home"), s.listForUser("thabo").map { it.namespaceId }.toSet())
        assertEquals(listOf("shop"), s.listForUser("nomsa").map { it.namespaceId })
        assertTrue(s.listForUser("someone-else").isEmpty())
    }

    @Test
    fun namespaceIdsAreCaseSensitiveUnlikeProviderIds() = runTest {
        // Deliberate asymmetry with the catalog: a provider id is a well-known
        // name a person may type, a namespace id is a key the app generates.
        val s = InMemoryToolNamespaceStore()
        s.upsert(ToolNamespace("work", "thabo", listOf("gmail")))
        assertNull(s.get("WORK"))
    }

    @Test
    fun upsertReplacesById() = runTest {
        val s = InMemoryToolNamespaceStore()
        s.upsert(ToolNamespace("work", "thabo", listOf("gmail")))
        s.upsert(ToolNamespace("work", "thabo", listOf("gmail", "slack")))
        assertEquals(2, s.get("work")!!.providerIds.size)
        assertEquals(1, s.listForUser("thabo").size)
    }

    @Test
    fun blankIdsAreRefusedOnEveryPath() = runTest {
        val s = InMemoryToolNamespaceStore()
        assertFailsWith<ToolsCatalogError.Argument> { s.upsert(ToolNamespace(" ", "thabo", emptyList())) }
        assertFailsWith<ToolsCatalogError.Argument> { s.get("") }
        assertFailsWith<ToolsCatalogError.Argument> { s.listForUser("   ") }
    }
}
