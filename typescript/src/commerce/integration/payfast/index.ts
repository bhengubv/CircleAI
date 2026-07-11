// commerce/integration/payfast/index.ts
// Full-parity port of CircleAI.Commerce.Integration.PayFast (C#). C# is the spec.
//
// PayFast integration primitives — a REAL signature builder (MD5 over the
// URL-encoded, ordered field string), ITN validation, and an in-memory webhook
// recorder. HTTP-side callbacks are wired by the host. Plus the static
// CommerceIntegrationPayFastDomainContext.
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   decimal Amount                  → number
//   IReadOnlyDictionary<string,string> → ReadonlyMap<string,string> (ITERATION
//                                     ORDER is load-bearing for the signature —
//                                     Map preserves insertion order, matching the
//                                     "orderedFields" contract)
//   List<PayFastItnPayload> under a lock → array
//
// SIGNATURE PARITY (the important part):
//   C#:  foreach kv: append `key=WebUtility.UrlEncode(value).Replace("%20","+")&`
//        then if Passphrase: append `passphrase=<encoded>` ELSE if trailing '&'
//        trim it. MD5 the UTF-8 bytes, hex, lowercase.
//   .NET WebUtility.UrlEncode leaves `[A-Za-z0-9]` and `- _ . ! * ( )` intact,
//   maps space → '+', and percent-encodes every other char's UTF-8 bytes as
//   uppercase %XX (so `'`→%27, `~`→%7E, `&`→%26, `=`→%3D, `é`→%C3%A9). We
//   reproduce that byte-for-byte in {@link webUtilityUrlEncode}. Verified against
//   the real .NET 10 runtime:
//     {merchant_id:10000100, item_name:"Test Product", amount:"100.00"} (no pass)
//       → "merchant_id=10000100&item_name=Test+Product&amount=100.00"
//       → MD5 a928072455fff91c7bb4238393006983
//     same + passphrase "my secret pass"
//       → …&passphrase=my+secret+pass  → MD5 974e3c4c1f4d3d21a8e357f799e2896c
//     {a:"x&y", b:"c=d", e:"space here"}
//       → "a=x%26y&b=c%3Dd&e=space+here"  → MD5 0b8fc27bd19fa06043bbf42d30da18eb
//     {k:""}  → "k="  → MD5 7de211558c5b2e4d2d6d255f028a1e1a
//
// RecentWebhooks: C# does `_webhooks.AsEnumerable().Reverse().Take(limit)` —
// most-recent-first by insertion order.

import { createHash } from "node:crypto";

/** PayFast merchant configuration. Mirrors C# `PayFastConfig` record. */
export interface PayFastConfig {
  readonly merchantId: string;
  readonly merchantKey: string;
  readonly passphrase: string;
  readonly sandbox: boolean;
}

/** Constructs a {@link PayFastConfig}. */
export function payFastConfig(
  merchantId: string,
  merchantKey: string,
  passphrase: string,
  sandbox: boolean,
): PayFastConfig {
  return { merchantId, merchantKey, passphrase, sandbox };
}

/** An ITN (Instant Transaction Notification) payload. Mirrors `PayFastItnPayload`. */
export interface PayFastItnPayload {
  readonly merchantId: string;
  readonly paymentId: string;
  readonly paymentStatus: string;
  readonly amount: number;
  readonly mPaymentId: string;
  readonly signature: string;
}

/** Constructs a {@link PayFastItnPayload}. */
export function payFastItnPayload(
  merchantId: string,
  paymentId: string,
  paymentStatus: string,
  amount: number,
  mPaymentId: string,
  signature: string,
): PayFastItnPayload {
  return { merchantId, paymentId, paymentStatus, amount, mPaymentId, signature };
}

/** The PayFast board contract. */
export interface IPayFastBoard {
  readonly config: PayFastConfig;
  /**
   * Builds the PayFast MD5 signature over the ordered fields. Iteration order of
   * `orderedFields` is significant and is preserved verbatim.
   */
  signatureFor(orderedFields: ReadonlyMap<string, string>): string;
  verifyItn(p: PayFastItnPayload): boolean;
  recordWebhook(p: PayFastItnPayload): void;
  recentWebhooks(limit?: number): readonly PayFastItnPayload[];
}

/** Deterministic in-memory {@link IPayFastBoard} with a real signature builder. */
export class InMemoryPayFastBoard implements IPayFastBoard {
  readonly config: PayFastConfig;
  private readonly webhooks: PayFastItnPayload[] = [];

  constructor(cfg: PayFastConfig) {
    if (cfg == null) throw new Error("cfg required");
    this.config = cfg;
  }

  signatureFor(orderedFields: ReadonlyMap<string, string>): string {
    if (orderedFields == null) throw new Error("orderedFields required");
    let s = "";
    for (const [key, value] of orderedFields) {
      s += `${key}=${webUtilityUrlEncode(value).replace(/%20/g, "+")}&`;
    }
    if (this.config.passphrase !== null && this.config.passphrase !== undefined && this.config.passphrase.length > 0) {
      s += `passphrase=${webUtilityUrlEncode(this.config.passphrase).replace(/%20/g, "+")}`;
    } else if (s.length > 0 && s[s.length - 1] === "&") {
      s = s.slice(0, -1);
    }
    return createHash("md5").update(Buffer.from(s, "utf8")).digest("hex").toLowerCase();
  }

  verifyItn(p: PayFastItnPayload): boolean {
    if (p == null) throw new Error("p required");
    return p.merchantId === this.config.merchantId;
  }

  recordWebhook(p: PayFastItnPayload): void {
    if (p == null) throw new Error("p required");
    this.webhooks.push(p);
  }

  recentWebhooks(limit = 20): readonly PayFastItnPayload[] {
    // AsEnumerable().Reverse().Take(limit): most-recent-first.
    return [...this.webhooks].reverse().slice(0, limit);
  }
}

/**
 * Reproduces .NET `System.Net.WebUtility.UrlEncode` byte-for-byte for the PayFast
 * signature: `[A-Za-z0-9]` and `- _ . ! * ( )` pass through unescaped, space
 * becomes `+`, and every other character is percent-encoded as its UTF-8 bytes
 * in uppercase hex.
 *
 * NOTE this deliberately differs from `encodeURIComponent`, which leaves
 * `! ' ( ) * ~` unescaped and never emits `+` for space. Here `'`→%27 and
 * `~`→%7E, matching .NET.
 */
export function webUtilityUrlEncode(value: string): string {
  let out = "";
  const bytes = Buffer.from(value, "utf8");
  for (const b of bytes) {
    if (isUrlSafe(b)) {
      out += String.fromCharCode(b);
    } else if (b === 0x20) {
      out += "+";
    } else {
      out += "%" + b.toString(16).toUpperCase().padStart(2, "0");
    }
  }
  return out;
}

/** The .NET WebUtility.UrlEncode "safe" byte test: alphanumerics + `-_.!*()`. */
function isUrlSafe(b: number): boolean {
  // 0-9
  if (b >= 0x30 && b <= 0x39) return true;
  // A-Z
  if (b >= 0x41 && b <= 0x5a) return true;
  // a-z
  if (b >= 0x61 && b <= 0x7a) return true;
  switch (b) {
    case 0x2d: // -
    case 0x5f: // _
    case 0x2e: // .
    case 0x21: // !
    case 0x2a: // *
    case 0x28: // (
    case 0x29: // )
      return true;
    default:
      return false;
  }
}

/**
 * Static domain context for the Commerce.Integration.PayFast vertical. Mirrors
 * C# `CommerceIntegrationPayFastDomainContext`.
 */
export const CommerceIntegrationPayFastDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Commerce.Integration.PayFast] You are a PayFast payment gateway integration expert. " +
    "Help with PayFast ITN (Instant Transaction Notification) webhook handling, payment flow debugging, " +
    "refund processing, subscription billing, split payments, and PCI-DSS compliance guidance. " +
    "Compliance: PCI-DSS, POPIA, PASA, Consumer Protection Act.",
  complianceFlags: ["PCI_DSS", "POPIA", "PASA", "Consumer_Protection_Act"] as readonly string[],
  suggestedTools: ["payfast_api", "webhook_debugger", "document_editor"] as readonly string[],
} as const;
