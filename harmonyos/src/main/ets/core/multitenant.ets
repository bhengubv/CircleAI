/**
 * core/multitenant
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// core/multitenant.ts
//
// Port of CircleAI.Core.MultiTenant:
//   • ICircleAITenantContext   (ICircleAITenantContext.cs)
//   • NullTenantContext        (throws on read — the safe default)
//   • SingleTenantContext      (explicit single-tenant)
//
// Ambient tenant context. Implementations resolve the current tenant from
// whatever signal the host uses. There is no safe default for "which tenant is
// this request for", so the default NullTenantContext throws on access rather
// than silently falling open (which causes cross-tenant data leaks).

/**
 * Ambient tenant context. Multi-tenant wrappers around CircleAI's stateful
 * stores read {@link currentTenantId} to scope the on-disk root directory.
 */
export interface ICircleAITenantContext {
  /**
   * The tenant identifier for the current request / unit of work. Throws if no
   * tenant is in scope — multi-tenant code paths must NEVER silently fall back
   * to a default.
   *
   * @throws Error when no tenant is in scope.
   */
  readonly currentTenantId: string;

  /** True when a tenant is currently in scope. Use to gate optional behaviour. */
  readonly hasTenant: boolean;
}

/**
 * Default {@link ICircleAITenantContext} — throws on any read.
 *
 * The throw is intentional: it makes "I forgot to wire tenant resolution" a
 * load-time error rather than a silent data-leak at runtime. For a genuine
 * single-tenant deployment, use {@link SingleTenantContext} instead.
 */
export class NullTenantContext implements ICircleAITenantContext {
  /** Shared singleton instance. */
  static readonly instance: NullTenantContext = new NullTenantContext();

  /** @throws Error always. */
  get currentTenantId(): string {
    throw new Error(
      "No CircleAI tenant context is in scope. Register a concrete ICircleAITenantContext " +
        "(e.g. SingleTenantContext, or your own ClaimsPrincipal-backed resolver) before " +
        "using multi-tenant-aware components.",
    );
  }

  get hasTenant(): boolean {
    return false;
  }
}

/**
 * Explicit single-tenant context. Returns a fixed tenant id for every read.
 * Use this when the deployment genuinely has one tenant and the throwing
 * default would just be ceremony.
 */
export class SingleTenantContext implements ICircleAITenantContext {
  /** Construct with the fixed tenant id. */
  constructor(tenantId: string) {
    if (tenantId === null || tenantId === undefined || tenantId.trim() === "")
      throw new Error("tenantId is required");
    this.currentTenantId = tenantId;
  }

  readonly currentTenantId: string;

  get hasTenant(): boolean {
    return true;
  }
}
