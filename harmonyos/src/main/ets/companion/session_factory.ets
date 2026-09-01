/**
 * companion/session_factory
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/session_factory.ts
//
// CompanionSessionFactory.cs — creates per-identity, per-surface Companion
// sessions with backing services resolved from DI. Callers use only the factory;
// they never construct a CompanionSession directly.
//
// C# resolves optional services from an IServiceProvider. TypeScript has no DI
// container, so the factory takes its required collaborators (the chat
// generator, episodic store, and recall the TS CompanionSession needs) as
// constructor dependencies, plus an optional IIdentityProvider used to resolve a
// richer display name / preferred language — exactly the role the C#
// IIdentityProvider plays. Any additional session options (persona, affect,
// goals, encoder, beliefs, embedder) are passed through a defaults hook so the
// factory stays the single construction point.

import { InterfaceKind } from "./index";
import type { ICompanionSession } from "./index";
import { CompanionSession } from "./session";
import type { CompanionSessionOptions } from "./session";
import type { IChatGenerator } from "../inference/index";
import type { IEpisodicMemoryStore } from "../memory/index";
import type { IRecall } from "../memory/recall";
import { randomUUID } from "../platform";

/** Subset of CircleAI.Identity the factory reads (display name + language). */
export interface ResolvedIdentity {
  readonly displayName: string;
  readonly preferredLanguage: string | null;
}

/** Mirrors CircleAI.Identity.IIdentityProvider (the bit the factory uses). */
export interface IIdentityProvider {
  getCurrentIdentityAsync(signal?: AbortSignal): Promise<ResolvedIdentity | null>;
}

/**
 * Contract for creating per-identity, per-surface Companion sessions.
 * Mirrors CircleAI.Companion.ICompanionSessionFactory.
 */
export interface ICompanionSessionFactory {
  /**
   * Create a new session for the given identity and interface surface,
   * resolving available backing services.
   */
  createAsync(
    identityId: string,
    surface: InterfaceKind,
    signal?: AbortSignal,
  ): Promise<ICompanionSession>;
}

/** Required backing services the TS CompanionSession is built from. */
export interface CompanionSessionFactoryDeps {
  readonly generator: IChatGenerator;
  readonly episodic: IEpisodicMemoryStore;
  readonly recall: IRecall;
  /** Optional identity provider — resolves a richer display name / language. */
  readonly identity?: IIdentityProvider | null;
  /**
   * Optional hook to supply extra per-session options (persona, affect, goals,
   * encoder, beliefs, embedder). Called with the base options the factory has
   * already resolved (session id, identity, surface, display name, language).
   */
  readonly optionsFactory?: (base: CompanionSessionOptions) => CompanionSessionOptions;
}

/** Default ICompanionSessionFactory. Mirrors CircleAI.Companion.CompanionSessionFactory. */
export class CompanionSessionFactory implements ICompanionSessionFactory {
  private readonly deps: CompanionSessionFactoryDeps;

  constructor(deps: CompanionSessionFactoryDeps) {
    if (deps == null) throw new Error("deps required");
    if (deps.generator == null) throw new Error("generator required");
    if (deps.episodic == null) throw new Error("episodic required");
    if (deps.recall == null) throw new Error("recall required");
    this.deps = deps;
  }

  async createAsync(
    identityId: string,
    surface: InterfaceKind,
    signal?: AbortSignal,
  ): Promise<ICompanionSession> {
    if (!identityId || identityId.trim().length === 0) throw new Error("identityId required");

    // Try to resolve a rich display name from the identity store.
    let displayName = identityId;
    let preferredLanguage: string | null = null;

    const identity = this.deps.identity;
    if (identity != null) {
      const resolved = await identity.getCurrentIdentityAsync(signal);
      if (resolved != null) {
        displayName = resolved.displayName;
        preferredLanguage = resolved.preferredLanguage;
      }
    }

    const base: CompanionSessionOptions = {
      sessionId: randomUUID(),
      identityId,
      interface: surface,
      displayName,
      preferredLanguage,
    };
    const opts = this.deps.optionsFactory ? this.deps.optionsFactory(base) : base;

    return new CompanionSession(this.deps.generator, this.deps.episodic, this.deps.recall, opts);
  }
}
