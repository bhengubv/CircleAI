/**
 * commerce/integration/xero/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// commerce/integration/xero/index.ts
// Full-parity port of CircleAI.Commerce.Integration.Xero (C#). C# is the spec.
//
// Xero integration primitives: per-user token storage, tenant tracking, and a
// webhook recorder. HTTP plumbing is host-supplied. Plus the static
// CommerceIntegrationXeroDomainContext.
//
// Type mappings (C# → TS):
//   record                       → readonly interface (+ positional factory)
//   DateTimeOffset               → Date
//   ConcurrentDictionary (Ordinal) + per-user List + events List (all locked)
//                                → Map<string,T> + arrays
//
// PARITY NOTES:
//   TokensExpired(user, now) — true when no tokens stored, else now >= ExpiresAtUtc
//   AddTenant                — de-duplicates by TenantId within the user's list
//   TenantsFor               — the user's tenants (insertion order), or empty
//   RecentEvents(limit)      — all events sorted AtUtc descending, take `limit`

/** OAuth token set for a Xero connection. Mirrors C# `XeroTokens` record. */
export interface XeroTokens {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly expiresAtUtc: Date;
  readonly idToken: string;
}

/** Constructs {@link XeroTokens}. */
export function xeroTokens(accessToken: string, refreshToken: string, expiresAtUtc: Date, idToken: string): XeroTokens {
  return { accessToken, refreshToken, expiresAtUtc, idToken };
}

/** A connected Xero tenant/organisation. Mirrors C# `XeroTenant` record. */
export interface XeroTenant {
  readonly tenantId: string;
  readonly tenantName: string;
  readonly tenantType: string;
}

/** Constructs a {@link XeroTenant}. */
export function xeroTenant(tenantId: string, tenantName: string, tenantType: string): XeroTenant {
  return { tenantId, tenantName, tenantType };
}

/** A received Xero webhook event. Mirrors C# `XeroWebhookEvent` record. */
export interface XeroWebhookEvent {
  readonly tenantId: string;
  readonly resourceType: string;
  readonly resourceId: string;
  readonly atUtc: Date;
}

/** Constructs a {@link XeroWebhookEvent}. */
export function xeroWebhookEvent(
  tenantId: string,
  resourceType: string,
  resourceId: string,
  atUtc: Date,
): XeroWebhookEvent {
  return { tenantId, resourceType, resourceId, atUtc };
}

/** The Xero board contract. */
export interface IXeroBoard {
  storeTokens(userId: string, t: XeroTokens): void;
  getTokens(userId: string): XeroTokens | undefined;
  tokensExpired(userId: string, now: Date): boolean;
  addTenant(userId: string, t: XeroTenant): void;
  tenantsFor(userId: string): readonly XeroTenant[];
  recordWebhook(e: XeroWebhookEvent): void;
  recentEvents(limit?: number): readonly XeroWebhookEvent[];
}

/** Deterministic in-memory {@link IXeroBoard}. */
export class InMemoryXeroBoard implements IXeroBoard {
  private readonly tokens = new Map<string, XeroTokens>();
  private readonly tenants = new Map<string, XeroTenant[]>();
  private readonly events: XeroWebhookEvent[] = [];

  storeTokens(userId: string, t: XeroTokens): void {
    if (t == null) throw new Error("t required");
    this.tokens.set(userId, t);
  }

  getTokens(userId: string): XeroTokens | undefined {
    return this.tokens.get(userId);
  }

  tokensExpired(userId: string, now: Date): boolean {
    const t = this.tokens.get(userId);
    if (t === undefined) return true;
    return now.getTime() >= t.expiresAtUtc.getTime();
  }

  addTenant(userId: string, t: XeroTenant): void {
    if (t == null) throw new Error("t required");
    let list = this.tenants.get(userId);
    if (list === undefined) {
      list = [];
      this.tenants.set(userId, list);
    }
    if (!list.some((x) => x.tenantId === t.tenantId)) list.push(t);
  }

  tenantsFor(userId: string): readonly XeroTenant[] {
    const list = this.tenants.get(userId);
    return list !== undefined ? [...list] : [];
  }

  recordWebhook(e: XeroWebhookEvent): void {
    if (e == null) throw new Error("e required");
    this.events.push(e);
  }

  recentEvents(limit = 20): readonly XeroWebhookEvent[] {
    return [...this.events].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit);
  }
}

/**
 * Static domain context for the Commerce.Integration.Xero vertical. Mirrors C#
 * `CommerceIntegrationXeroDomainContext`.
 */
export const CommerceIntegrationXeroDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Commerce.Integration.Xero] You are a Xero accounting platform expert. " +
    "Help with Xero chart of accounts, invoice creation, bank feeds, reconciliation workflows, " +
    "Xero reporting, and API integration troubleshooting. Reference Xero HQ documentation for accuracy. " +
    "Compliance: SARS, IFRS for SMEs, Xero data handling standards.",
  complianceFlags: ["SARS", "IFRS", "Xero_Data_Standards", "POPIA"] as readonly string[],
  suggestedTools: ["xero_api", "spreadsheet", "document_editor"] as readonly string[],
} as const;
