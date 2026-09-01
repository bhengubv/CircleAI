package com.bhengubv.circleai.tools.catalog

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class AesGcmCipherTest {

    private val key = ByteArray(32) { (it * 7 + 3).toByte() }

    @Test
    fun aKeyThatIsNotTwoHundredAndFiftySixBitsIsRefusedUpFront() {
        // Refused at construction, not at the first seal. A store built with a
        // short key and no complaint would encrypt happily and be unreadable by
        // every other port.
        assertFailsWith<ToolsCatalogError.Argument> { AesGcmCredentialCipher(ByteArray(16)) }
        assertFailsWith<ToolsCatalogError.Argument> { AesGcmCredentialCipher(ByteArray(0)) }
        assertFailsWith<ToolsCatalogError.Argument> { AesGcmCredentialCipher(ByteArray(33)) }
    }

    @Test
    fun sealThenOpenReturnsTheOriginalBytes() {
        val c = AesGcmCredentialCipher(key)
        val plain = "sk-live-0123456789".toByteArray()
        assertTrue(c.open(c.seal(plain))!!.contentEquals(plain))
    }

    @Test
    fun theBlobIsNonceThenTagThenCiphertext() {
        // The layout is the interop contract with the C# store. JCE appends its
        // tag; this class moves it to the front, and the length arithmetic below
        // is what says so. Getting it wrong does not fail loudly - it fails as an
        // authentication error on a blob another port wrote correctly.
        val c = AesGcmCredentialCipher(key)
        val blob = c.seal(ByteArray(40) { it.toByte() })
        assertEquals(
            AesGcmCredentialCipher.NONCE_BYTES + AesGcmCredentialCipher.TAG_BYTES + 40,
            blob.size,
        )
    }

    @Test
    fun everySealUsesAFreshNonceSoTheSamePlaintextNeverRepeats() {
        // Reusing a nonce under one key breaks GCM outright. Two seals of the
        // same bytes must not be byte-equal.
        val c = AesGcmCredentialCipher(key)
        val plain = "same".toByteArray()
        val a = c.seal(plain)
        val b = c.seal(plain)
        assertFalse(a.contentEquals(b))
        assertFalse(
            a.copyOfRange(0, AesGcmCredentialCipher.NONCE_BYTES)
                .contentEquals(b.copyOfRange(0, AesGcmCredentialCipher.NONCE_BYTES)),
        )
    }

    @Test
    fun aFlippedBitAnywhereFailsAuthenticationRatherThanReturningGarbage() {
        val c = AesGcmCredentialCipher(key)
        val blob = c.seal("sk-live-0123456789".toByteArray())
        for (i in blob.indices) {
            val bad = blob.copyOf()
            bad[i] = (bad[i].toInt() xor 1).toByte()
            assertNull(c.open(bad), "byte " + i + " was tampered with and still opened")
        }
    }

    @Test
    fun aBlobShorterThanTheHeaderIsNullNotAnIndexError() {
        val c = AesGcmCredentialCipher(key)
        assertNull(c.open(ByteArray(0)))
        assertNull(c.open(ByteArray(27)))
    }

    @Test
    fun anEmptyPlaintextRoundTripsToTheHeaderAloneAndBackToEmpty() {
        val c = AesGcmCredentialCipher(key)
        val blob = c.seal(ByteArray(0))
        assertEquals(
            AesGcmCredentialCipher.NONCE_BYTES + AesGcmCredentialCipher.TAG_BYTES,
            blob.size,
        )
        assertEquals(0, c.open(blob)!!.size)
    }

    @Test
    fun anotherKeyCannotOpenIt() {
        val blob = AesGcmCredentialCipher(key).seal("secret".toByteArray())
        val other = ByteArray(32) { (it * 11 + 1).toByte() }
        assertNull(AesGcmCredentialCipher(other).open(blob))
    }
}

class AesGcmCredentialStoreTest {

    private val key = ByteArray(32) { it.toByte() }

    private fun bundle(provider: String = "gmail", user: String = "thabo") = CredentialBundle(
        providerId = provider,
        userId = user,
        fields = mapOf("access_token" to "ya29.abc", "refresh_token" to "1//zzz"),
        expiresAtUtc = Instant.ofEpochMilli(1_788_000_000_000L),
    )

    @Test
    fun upsertThenGetRoundTripsEveryFieldIncludingTheExpiry() = runTest {
        val store = AesGcmCredentialStore(key)
        store.upsert(bundle())
        val got = store.get("gmail", "thabo")
        assertEquals(bundle(), got)
        assertEquals(Instant.ofEpochMilli(1_788_000_000_000L), got!!.expiresAtUtc)
    }

    @Test
    fun aBundleWithNoExpiryComesBackWithNoExpiry() = runTest {
        val store = AesGcmCredentialStore(key)
        store.upsert(CredentialBundle("slack", "nomsa", mapOf("token" to "xoxb-1")))
        assertNull(store.get("slack", "nomsa")!!.expiresAtUtc)
    }

    @Test
    fun theSecretIsNotSittingInTheStoreInTheClear() = runTest {
        // The whole point of the class. If the token can be found by scanning the
        // stored bytes, encryption at rest is decoration.
        val probe = RecordingCipher()
        val store = AesGcmCredentialStore(probe)
        store.upsert(bundle())
        assertFalse(String(probe.lastSealed!!, Charsets.ISO_8859_1).contains("ya29.abc"))
    }

    @Test
    fun anUnknownPairIsNullNotAnError() = runTest {
        assertNull(AesGcmCredentialStore(key).get("gmail", "nobody"))
    }

    @Test
    fun deleteRemovesItAndDeletingTwiceIsFine() = runTest {
        val store = AesGcmCredentialStore(key)
        store.upsert(bundle())
        store.delete("gmail", "thabo")
        assertNull(store.get("gmail", "thabo"))
        store.delete("gmail", "thabo")
    }

    @Test
    fun upsertReplacesRatherThanAccumulating() = runTest {
        val store = AesGcmCredentialStore(key)
        store.upsert(bundle())
        store.upsert(CredentialBundle("gmail", "thabo", mapOf("access_token" to "ya29.NEW")))
        assertEquals("ya29.NEW", store.get("gmail", "thabo")!!.fields["access_token"])
        assertNull(store.get("gmail", "thabo")!!.fields["refresh_token"])
    }

    @Test
    fun oneUserCredentialIsNotAnotherEvenOnTheSameProvider() = runTest {
        val store = AesGcmCredentialStore(key)
        store.upsert(bundle(user = "thabo"))
        store.upsert(CredentialBundle("gmail", "nomsa", mapOf("access_token" to "ya29.nomsa")))
        assertEquals("ya29.abc", store.get("gmail", "thabo")!!.fields["access_token"])
        assertEquals("ya29.nomsa", store.get("gmail", "nomsa")!!.fields["access_token"])
        assertNotEquals(store.get("gmail", "thabo"), store.get("gmail", "nomsa"))
    }

    @Test
    fun aBlankProviderOrUserIsRefusedOnEveryReadPath() = runTest {
        val store = AesGcmCredentialStore(key)
        assertFailsWith<ToolsCatalogError.Argument> { store.get("", "thabo") }
        assertFailsWith<ToolsCatalogError.Argument> { store.get("  ", "thabo") }
        assertFailsWith<ToolsCatalogError.Argument> { store.get("gmail", "") }
        assertFailsWith<ToolsCatalogError.Argument> { store.delete("", "thabo") }
        assertFailsWith<ToolsCatalogError.Argument> { store.delete("gmail", " ") }
    }

    @Test
    fun aCipherThatCannotOpenTheBlobReadsAsAbsent() = runTest {
        // Not a throw. A credential that will not decrypt is one the caller has
        // to re-authorise, and that path is the same as never having had one.
        val store = AesGcmCredentialStore(RefusingCipher())
        store.upsert(bundle())
        assertNull(store.get("gmail", "thabo"))
    }

    @Test
    fun plaintextThatIsNotAValidBundleAlsoReadsAsAbsent() = runTest {
        val store = AesGcmCredentialStore(GarbageCipher())
        store.upsert(bundle())
        assertNull(store.get("gmail", "thabo"))
    }

    @Test
    fun theBackendIdSaysWhatIsBehindIt() {
        assertEquals("aes-gcm", AesGcmCredentialStore(key).backendId)
    }

    private class RecordingCipher : CredentialCipher {
        var lastSealed: ByteArray? = null
        private val inner = AesGcmCredentialCipher(ByteArray(32) { 9 })
        override fun seal(plaintext: ByteArray): ByteArray =
            inner.seal(plaintext).also { lastSealed = it }
        override fun open(combined: ByteArray): ByteArray? = inner.open(combined)
    }

    private class RefusingCipher : CredentialCipher {
        override fun seal(plaintext: ByteArray): ByteArray = plaintext
        override fun open(combined: ByteArray): ByteArray? = null
    }

    private class GarbageCipher : CredentialCipher {
        override fun seal(plaintext: ByteArray): ByteArray = plaintext
        override fun open(combined: ByteArray): ByteArray = "not json at all".toByteArray()
    }
}
