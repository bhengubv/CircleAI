// companion/herjarvis/crypto_delegation.ts
//
// EcdsaCryptoDelegation — HerJarvisRealImplementations.cs #22. Issues and
// verifies short-lived delegation credentials, signed with ECDSA over the
// NIST P-256 curve and SHA-256, over a canonical
// `issuer|subjectId|scope|expires:O` payload.
//
// C# uses System.Security.Cryptography.ECDsa (NamedCurves.nistP256), whose
// SignData default output is the IEEE P1363 fixed-size (r‖s) encoding. Node's
// crypto verifies/signs DER by default, so we pass `dsaEncoding: 'ieee-p1363'`
// to match the C# on the wire. Since issue+verify share one keypair inside the
// instance, the signature never crosses to C#; the encoding choice keeps the
// implementation faithful rather than for cross-language byte-matching.
//
// The keypair is an injected dependency (a host can pass its own PEM keys — the
// equivalent of the C# `ECDsa? key` constructor parameter); the default
// generates a fresh P-256 keypair, exactly like `ECDsa.Create(...)`.

import { createSign, createVerify, generateKeyPairSync, type KeyObject } from "node:crypto";
import { toRoundTripUtc } from "./stores.js";
import type { ICryptoDelegation, DelegationCredential } from "./contracts.js";

export interface EcdsaKeyPair {
  readonly privateKey: KeyObject;
  readonly publicKey: KeyObject;
}

/** Generate a fresh NIST P-256 (prime256v1) keypair — matches ECDsa.Create. */
export function generateP256KeyPair(): EcdsaKeyPair {
  const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  return { privateKey, publicKey };
}

/**
 * Issues and verifies delegation credentials. `issue` signs a canonical payload
 * with the private key; `verify` checks issuer, expiry, and the signature with
 * the public key. Rejects a credential from a different issuer, an expired one,
 * an empty signature, or a malformed base64 signature — all before the crypto
 * check, exactly like the C#.
 */
export class EcdsaCryptoDelegation implements ICryptoDelegation {
  private readonly issuer: string;
  private readonly keys: EcdsaKeyPair;

  constructor(issuer = "circleai-companion", keys?: EcdsaKeyPair) {
    if (!issuer || issuer.trim().length === 0) throw new Error("issuer required");
    this.issuer = issuer;
    this.keys = keys ?? generateP256KeyPair();
  }

  issue(subjectId: string, scope: string, lifetimeMs: number): DelegationCredential {
    if (!subjectId || subjectId.trim().length === 0) throw new Error("subjectId required");
    if (!scope || scope.trim().length === 0) throw new Error("scope required");
    if (lifetimeMs <= 0) throw new Error("lifetime out of range");
    const expires = new Date(Date.now() + lifetimeMs);
    const payload = this.canonical(subjectId, scope, expires);
    const signer = createSign("SHA256");
    signer.update(Buffer.from(payload, "utf8"));
    signer.end();
    const sig = signer.sign({ key: this.keys.privateKey, dsaEncoding: "ieee-p1363" });
    return {
      issuer: this.issuer,
      subjectId,
      scope,
      expiresAtUtc: expires,
      signature: sig.toString("base64"),
    };
  }

  verify(credential: DelegationCredential): boolean {
    if (credential == null) throw new Error("credential required");
    if (credential.issuer !== this.issuer) return false;
    if (credential.expiresAtUtc.getTime() <= Date.now()) return false;
    if (!credential.signature || credential.signature.length === 0) return false;
    let sig: Buffer;
    try {
      sig = Buffer.from(credential.signature, "base64");
      // Buffer.from silently drops invalid chars; guard against a decode that
      // clearly is not a P-256 P1363 signature (64 bytes) to mirror the C#
      // FormatException path rejecting non-base64 input.
      if (sig.length === 0) return false;
    } catch {
      return false;
    }
    const payload = this.canonical(credential.subjectId, credential.scope, credential.expiresAtUtc);
    const verifier = createVerify("SHA256");
    verifier.update(Buffer.from(payload, "utf8"));
    verifier.end();
    try {
      return verifier.verify({ key: this.keys.publicKey, dsaEncoding: "ieee-p1363" }, sig);
    } catch {
      return false;
    }
  }

  /** `$"{issuer}|{subjectId}|{scope}|{expiresAtUtc:O}"`. */
  private canonical(subjectId: string, scope: string, expiresAtUtc: Date): string {
    return `${this.issuer}|${subjectId}|${scope}|${toRoundTripUtc(expiresAtUtc)}`;
  }
}
