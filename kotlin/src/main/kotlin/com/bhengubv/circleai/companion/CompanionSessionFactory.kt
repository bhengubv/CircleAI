// CompanionSessionFactory.kt
//
// Kotlin port of CircleAI.Companion.CompanionSessionFactory — the C# reference
// (CompanionSessionFactory.cs) is the EXACT spec. Creates per-identity,
// per-surface Companion sessions; callers only ever need the factory and never
// construct a session directly.
//
// The C# factory pulls every optional backing service out of an IServiceProvider
// at CreateAsync time. Kotlin/DI is constructor injection, so the equivalent is
// to inject the (required) core services + (optional) enrichments into the
// factory and stamp them onto each session. The rich display-name / preferred-
// language resolution from an optional IIdentityProvider is preserved 1:1. The
// produced session is the existing brain.CompanionSession (an ICompanionSession).

package com.bhengubv.circleai.companion

import com.bhengubv.circleai.companion.brain.CompanionMemoryEncoder
import com.bhengubv.circleai.companion.brain.CompanionSession
import com.bhengubv.circleai.companion.brain.CompanionSessionOptions
import com.bhengubv.circleai.companion.brain.Embedder
import com.bhengubv.circleai.companion.brain.SelfBeliefStore
import com.bhengubv.circleai.identity.IIdentityProvider
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.brain.IEpisodicStore
import com.bhengubv.circleai.memory.brain.IRecall
import java.util.UUID

/** Contract for creating per-identity, per-surface Companion sessions. */
interface ICompanionSessionFactory {
    /**
     * Creates a new [ICompanionSession] for [identityId] on the [interfaceKind]
     * surface, resolving a rich display name / preferred language when an
     * identity provider is available.
     */
    suspend fun createAsync(identityId: String, interfaceKind: InterfaceKind): ICompanionSession
}

/**
 * Default factory. The core services (generator/episodic/recall) are required;
 * the enrichments (encoder/beliefs/embedder) and the identity provider are
 * optional — mirroring the optional-service resolution in the C# factory.
 */
class CompanionSessionFactory(
    private val generator: IChatGenerator,
    private val episodic: IEpisodicStore,
    private val recall: IRecall,
    private val identity: IIdentityProvider? = null,
    private val encoder: CompanionMemoryEncoder? = null,
    private val beliefs: SelfBeliefStore? = null,
    private val embedder: Embedder? = null,
) : ICompanionSessionFactory {

    override suspend fun createAsync(identityId: String, interfaceKind: InterfaceKind): ICompanionSession {
        require(identityId.isNotBlank()) { "identityId required" }

        // Try to resolve a rich display name from the identity store.
        var displayName = identityId
        var preferredLang: String? = null

        val provider = identity
        if (provider != null) {
            val resolved = provider.getCurrentIdentityAsync()
            if (resolved != null) {
                displayName = resolved.displayName
                preferredLang = resolved.preferredLanguage
            }
        }

        return CompanionSession(
            generator = generator,
            episodic = episodic,
            recall = recall,
            opts = CompanionSessionOptions(
                sessionId = UUID.randomUUID().toString(),
                identityId = identityId,
                interfaceKind = interfaceKind,
                displayName = displayName,
                preferredLanguage = preferredLang,
                encoder = encoder,
                beliefs = beliefs,
                embedder = embedder,
            ),
        )
    }
}
