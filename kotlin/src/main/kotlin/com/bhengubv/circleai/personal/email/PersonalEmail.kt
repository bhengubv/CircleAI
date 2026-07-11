// PersonalEmail.kt
//
// Kotlin port of CircleAI.Personal.Email — the C# reference is the EXACT spec.
// Provider-neutral email contract + a consent-enforcing null adapter. This
// package never sends mail: drafting is supported, but send crosses a trust
// boundary handled outside this contract.
//
// Covers (C# file -> Kotlin type):
//   EmailMessage.cs     -> EmailMessage
//   IEmailAdapter.cs    -> IEmailAdapter
//   NullEmailAdapter.cs -> NullEmailAdapter
//
// Fidelity notes: `record`->`data class`; `Guid`->`UUID`;
// `DateTimeOffset`->`Instant`; `Task<T>`->`suspend fun`; nullable lookup ->
// Kotlin `?`. Reads require [ConsentScope.EmailRead]; drafting requires
// [ConsentScope.EmailDraft]. The null adapter's `draftReply` returns a fresh
// UUID each call (nothing is persisted), matching C#.

package com.bhengubv.circleai.personal.email

import com.bhengubv.circleai.personal.ConsentGuard
import com.bhengubv.circleai.personal.ConsentScope
import com.bhengubv.circleai.personal.UserConsentToken
import java.time.Instant
import java.util.UUID

/**
 * A user email message normalised across providers.
 *
 * @property bodyPlain Plain-text body. HTML is intentionally not exposed
 *   through the interface — UI layers assemble rich content separately.
 * @property labels Provider labels / folders / categories.
 */
data class EmailMessage(
    val id: UUID,
    val externalId: String,
    val from: String,
    val to: List<String>,
    val cc: List<String>,
    val subject: String,
    val bodyPlain: String,
    val receivedAt: Instant,
    val isUnread: Boolean,
    val labels: List<String>,
)

/**
 * Contract for an email adapter. Concrete implementations bind to a specific
 * provider (Gmail, Microsoft Graph, IMAP, …) and ship in separate packages.
 *
 * This package never sends mail. Drafting is supported via [draftReply], but
 * actual send crosses a trust boundary and is handled outside this contract.
 */
interface IEmailAdapter {
    /**
     * Lists the most recent [count] messages in the user's inbox, ordered
     * most-recent-first. Requires [ConsentScope.EmailRead].
     */
    suspend fun listRecent(count: Int, consent: UserConsentToken): List<EmailMessage>

    /**
     * Fetches a single message by its provider-native id, or null if not found.
     * Requires [ConsentScope.EmailRead].
     */
    suspend fun getById(externalId: String, consent: UserConsentToken): EmailMessage?

    /**
     * Creates a draft reply to the referenced message. The draft is saved in
     * the user's drafts folder; this method DOES NOT send. Requires
     * [ConsentScope.EmailDraft]. Returns the Circle identifier of the new draft.
     */
    suspend fun draftReply(toExternalId: String, bodyPlain: String, consent: UserConsentToken): UUID
}

/**
 * An email adapter that holds no messages. Reads return empty / null;
 * [draftReply] returns a fresh [UUID] each call (the "draft" is not persisted
 * anywhere). All methods enforce the consent contract before returning.
 */
class NullEmailAdapter : IEmailAdapter {
    override suspend fun listRecent(count: Int, consent: UserConsentToken): List<EmailMessage> {
        ConsentGuard.require(consent, ConsentScope.EmailRead)
        return emptyList()
    }

    override suspend fun getById(externalId: String, consent: UserConsentToken): EmailMessage? {
        ConsentGuard.require(consent, ConsentScope.EmailRead)
        return null
    }

    override suspend fun draftReply(
        toExternalId: String,
        bodyPlain: String,
        consent: UserConsentToken,
    ): UUID {
        ConsentGuard.require(consent, ConsentScope.EmailDraft)
        return UUID.randomUUID()
    }
}
