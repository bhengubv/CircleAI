// PersonalContacts.kt
//
// Kotlin port of CircleAI.Personal.Contacts — the C# reference is the EXACT
// spec. Provider-neutral contacts contract + a consent-enforcing null adapter.
//
// Covers (C# file -> Kotlin type):
//   Contact.cs             -> Contact
//   IContactsAdapter.cs    -> IContactsAdapter
//   NullContactsAdapter.cs -> NullContactsAdapter
//
// Fidelity notes: `record`->`data class`; `Guid`->`UUID`;
// `DateTimeOffset`->`Instant`; `Task<T>`->`suspend fun`; nullable lookup ->
// Kotlin `?`. Both methods require [ConsentScope.ContactsRead].

package com.bhengubv.circleai.personal.contacts

import com.bhengubv.circleai.personal.ConsentGuard
import com.bhengubv.circleai.personal.ConsentScope
import com.bhengubv.circleai.personal.UserConsentToken
import java.time.Instant
import java.util.UUID

/**
 * A user contact normalised across providers.
 *
 * @property phoneNumbers Known phone numbers in E.164 form.
 * @property relationship User-tagged relationship label ("spouse", "colleague"), or null.
 * @property lastInteractionAt UTC time of the most recent interaction Circle has observed.
 */
data class Contact(
    val id: UUID,
    val externalId: String,
    val displayName: String,
    val emails: List<String>,
    val phoneNumbers: List<String>,
    val relationship: String?,
    val lastInteractionAt: Instant,
)

/**
 * Contract for a contacts adapter. Concrete implementations bind to a specific
 * provider (Google People, Microsoft Graph, iOS Contacts, …) and ship in
 * separate packages.
 */
interface IContactsAdapter {
    /**
     * Searches the user's contacts by free-text query (display name, email,
     * phone). Requires [ConsentScope.ContactsRead].
     */
    suspend fun search(query: String, consent: UserConsentToken): List<Contact>

    /**
     * Fetches a single contact by provider-native id, or null if not found.
     * Requires [ConsentScope.ContactsRead].
     */
    suspend fun getByExternalId(externalId: String, consent: UserConsentToken): Contact?
}

/**
 * A contacts adapter that holds no entries. Searches return empty; lookups
 * return null. Enforces the consent contract on every call.
 */
class NullContactsAdapter : IContactsAdapter {
    override suspend fun search(query: String, consent: UserConsentToken): List<Contact> {
        ConsentGuard.require(consent, ConsentScope.ContactsRead)
        return emptyList()
    }

    override suspend fun getByExternalId(externalId: String, consent: UserConsentToken): Contact? {
        ConsentGuard.require(consent, ConsentScope.ContactsRead)
        return null
    }
}
