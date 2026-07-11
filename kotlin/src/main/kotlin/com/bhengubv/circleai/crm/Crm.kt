// Crm.kt
//
// Kotlin port of CircleAI.CRM (Contracts.cs + InMemoryCrm.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory CRM: a contact store with name/email substring
// search, a deal pipeline indexed by stage, and a per-contact activity log.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ValueTask`/`ValueTask<T>` async members -> `suspend fun`.
//   * Stores keyed with `StringComparer.Ordinal` -> a `ConcurrentHashMap`.
//   * `SearchAsync` filters by FullName/Email substring (OrdinalIgnoreCase),
//     orders by FullName (OrdinalIgnoreCase), takes topK.
//   * `ListByStageAsync` filters by Stage (OrdinalIgnoreCase), orders by Value DESC.
//   * `ReadForContactAsync` returns newest-first, capped at `limit`.
//   * Null implementations are fail-open no-op singletons ("null" backend).

package com.bhengubv.circleai.crm

import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A CRM contact. Mirrors C# `Contact`. */
data class Contact(
    val contactId: String,
    val fullName: String,
    val email: String?,
    val phone: String?,
    val companyId: String?,
)

/** A company record. Mirrors C# `Company`. */
data class Company(val companyId: String, val name: String, val industry: String?)

/** A sales deal. Mirrors C# `Deal`. */
data class Deal(
    val dealId: String,
    val companyId: String,
    val name: String,
    val value: BigDecimal,
    val currency: String,
    val stage: String,
)

/** A timeline activity attached to a contact. Mirrors C# `Activity`. */
data class Activity(
    val activityId: String,
    val contactId: String,
    val kind: String,
    val body: String,
    val atUtc: Instant,
)

/** Contact persistence + search. Mirrors C# `IContactStore`. */
interface IContactStore {
    val backendId: String
    suspend fun upsertAsync(c: Contact)
    suspend fun getAsync(id: String): Contact?
    suspend fun searchAsync(query: String, topK: Int = 20): List<Contact>
}

/** Deal persistence + stage lookup. Mirrors C# `IDealPipeline`. */
interface IDealPipeline {
    val backendId: String
    suspend fun upsertAsync(d: Deal)
    suspend fun getAsync(id: String): Deal?
    suspend fun listByStageAsync(stage: String): List<Deal>
}

/** Per-contact activity log. Mirrors C# `IActivityLog`. */
interface IActivityLog {
    val backendId: String
    suspend fun appendAsync(a: Activity)
    suspend fun readForContactAsync(contactId: String, limit: Int = 100): List<Activity>
}

// =====================================================================
// In-memory implementations (InMemoryCrm.cs)
// =====================================================================

/** In-memory [IContactStore] with substring name/email search. Mirrors C# `InMemoryContactStore`. */
class InMemoryContactStore : IContactStore {
    private val items = ConcurrentHashMap<String, Contact>()
    override val backendId: String get() = "in-memory"

    override suspend fun upsertAsync(c: Contact) {
        if (c.contactId.isBlank()) throw IllegalArgumentException("ContactId required")
        items[c.contactId] = c
    }

    override suspend fun getAsync(id: String): Contact? {
        if (id.isBlank()) throw IllegalArgumentException("id required")
        return items[id]
    }

    override suspend fun searchAsync(query: String, topK: Int): List<Contact> {
        if (topK <= 0) throw IllegalArgumentException("topK must be positive")
        return items.values
            .filter {
                it.fullName.contains(query, ignoreCase = true) ||
                    (it.email?.contains(query, ignoreCase = true) ?: false)
            }
            .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.fullName })
            .take(topK)
    }
}

/** In-memory [IDealPipeline] indexed by stage. Mirrors C# `InMemoryDealPipeline`. */
class InMemoryDealPipeline : IDealPipeline {
    private val items = ConcurrentHashMap<String, Deal>()
    override val backendId: String get() = "in-memory"

    override suspend fun upsertAsync(d: Deal) {
        if (d.dealId.isBlank()) throw IllegalArgumentException("DealId required")
        items[d.dealId] = d
    }

    override suspend fun getAsync(id: String): Deal? = items[id]

    override suspend fun listByStageAsync(stage: String): List<Deal> {
        if (stage.isBlank()) throw IllegalArgumentException("stage required")
        return items.values
            .filter { it.stage.equals(stage, ignoreCase = true) }
            .sortedByDescending { it.value }
    }
}

/** In-memory [IActivityLog], newest-first per contact. Mirrors C# `InMemoryActivityLog`. */
class InMemoryActivityLog : IActivityLog {
    private val byContact = ConcurrentHashMap<String, MutableList<Activity>>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    override suspend fun appendAsync(a: Activity) {
        if (a.contactId.isBlank()) throw IllegalArgumentException("ContactId required")
        synchronized(lock) {
            byContact.getOrPut(a.contactId) { mutableListOf() }.add(a)
        }
    }

    override suspend fun readForContactAsync(contactId: String, limit: Int): List<Activity> {
        if (contactId.isBlank()) throw IllegalArgumentException("contactId required")
        synchronized(lock) {
            val list = byContact[contactId] ?: return emptyList()
            return list.sortedByDescending { it.atUtc }.take(limit)
        }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** Fail-open no-op [IContactStore]. Mirrors C# `NullContactStore`. */
class NullContactStore private constructor() : IContactStore {
    override val backendId: String get() = "null"
    override suspend fun upsertAsync(c: Contact) {}
    override suspend fun getAsync(id: String): Contact? = null
    override suspend fun searchAsync(query: String, topK: Int): List<Contact> = emptyList()

    companion object {
        val Instance = NullContactStore()
    }
}

/** Fail-open no-op [IDealPipeline]. Mirrors C# `NullDealPipeline`. */
class NullDealPipeline private constructor() : IDealPipeline {
    override val backendId: String get() = "null"
    override suspend fun upsertAsync(d: Deal) {}
    override suspend fun getAsync(id: String): Deal? = null
    override suspend fun listByStageAsync(stage: String): List<Deal> = emptyList()

    companion object {
        val Instance = NullDealPipeline()
    }
}

/** Fail-open no-op [IActivityLog]. Mirrors C# `NullActivityLog`. */
class NullActivityLog private constructor() : IActivityLog {
    override val backendId: String get() = "null"
    override suspend fun appendAsync(a: Activity) {}
    override suspend fun readForContactAsync(contactId: String, limit: Int): List<Activity> = emptyList()

    companion object {
        val Instance = NullActivityLog()
    }
}
