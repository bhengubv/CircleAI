// Built-in protection: noticing threats, and the gate in front of doing
// anything about them.
//
// THE WHOLE MODULE IS DEFENSIVE AND SAYS SO IN ITS SHAPE. Everything in the
// awareness half ASSESSES and returns a verdict; nothing in it acts. The only
// path to an action goes through a gate that requires a named person to have
// consented, in scope, unexpired - and every failure mode of that gate denies.
//
// THE DEFAULTS ARE THE DESIGN:
//
//   * A null gate DENIES. A build that forgot to configure one can assess and
//     cannot act. The opposite default - a permissive null - is how a
//     protective feature becomes an offensive one by omission.
//
//   * A null escalation returns FALSE, so nothing believes an alert was raised
//     when none was. Returning true would be worse than doing nothing, because
//     something downstream would stop trying.
//
//   * A consent with a blank granter is REFUSED at construction. A consent
//     nobody can be shown to have given is not a consent.
//
// AND THE ONE THAT MATTERS MOST: an indicator match is EVIDENCE, not a verdict.
// A file that matches a hash and a connection to a listed address are both
// reasons to look, and neither is a reason to act on its own.

// ─────────────────────────────────────────────────────────────────────────────
// What can be noticed

/** What kind of thing an indicator describes. */
export enum IndicatorKind {
  FileHash = "file-hash",
  /** A domain name. Matched on the REGISTRABLE part, so subdomains match. */
  Domain = "domain",
  Ipv4 = "ipv4",
  Ipv4Cidr = "ipv4-cidr",
  Url = "url",
  EmailAddress = "email-address",
  /** A phone number, in E.164. The commonest vector here by a wide margin. */
  PhoneNumber = "phone-number",
}

/** One thing worth noticing. */
export interface ThreatIndicator {
  readonly kind: IndicatorKind;
  /** Normalised at construction, so a lookup cannot miss on spelling. */
  readonly value: string;
  readonly source: string;
  /** 0..1. How much this source is trusted, NOT how bad the thing is. */
  readonly confidence: number;
  readonly note: string;
}

/**
 * Normalises an indicator so lookups cannot miss on spelling.
 *
 * Case, a trailing dot on a domain, a `+` on a phone number - each of these
 * makes two spellings of the same thing, and a corpus that stores one and is
 * asked the other reports clean.
 */
export function normaliseIndicator(kind: IndicatorKind, value: string): string {
  const raw = (value ?? "").trim();
  switch (kind) {
    case IndicatorKind.FileHash:
      return raw.toLowerCase().replace(/^(sha256|sha1|md5):/i, "");
    case IndicatorKind.Domain:
      return raw.toLowerCase().replace(/\.$/, "").replace(/^www\./, "");
    case IndicatorKind.EmailAddress:
      return raw.toLowerCase();
    case IndicatorKind.PhoneNumber:
      // Digits only, with a leading + implied. A number written with spaces,
      // dashes or brackets is the same number.
      return raw.replace(/[^\d]/g, "");
    case IndicatorKind.Url:
      return raw.replace(/\/+$/, "");
    default:
      return raw;
  }
}

export const threatIndicator = (
  kind: IndicatorKind,
  value: string,
  source = "",
  confidence = 1,
  note = "",
): ThreatIndicator =>
  Object.freeze({
    kind,
    value: normaliseIndicator(kind, value),
    source,
    confidence: confidence < 0 ? 0 : confidence > 1 ? 1 : confidence,
    note,
  });

/** An indicator about a network endpoint. */
export interface NetworkIndicator extends ThreatIndicator {
  readonly port: number;
}

/** An indicator about a person's identity being exposed. */
export interface IdentityIndicator extends ThreatIndicator {
  /** Which breach, in the corpus's own words. Never inferred. */
  readonly breachName: string;
  readonly breachDateIso: string;
}

/** Something that was matched. */
export interface IndicatorMatch {
  readonly indicator: ThreatIndicator;
  /** What was being checked when it matched. */
  readonly observed: string;
  /** Carried from the indicator's source, so a low-trust match reads as one. */
  readonly confidence: number;
}

/** A file being looked at. */
export interface FileArtifact {
  readonly path: string;
  readonly sha256: string;
  readonly sizeBytes: number;
  /** From the CONTENT where a host can determine it, not the extension - an
   * extension is what somebody chose to call the file. */
  readonly detectedType: string;
}

/** How sure the assessment is. */
export enum ThreatAwarenessVerdict {
  /** Nothing matched. Not the same as safe, and worded so nobody reads it that
   * way: absence of evidence is what this is. */
  NothingKnown = "nothing-known",
  /** Something matched, weakly or from a low-trust source. Worth a look. */
  WorthChecking = "worth-checking",
  /** A strong match from a trusted source. */
  Concerning = "concerning",
  /** The corpus could not be consulted, so nothing was actually checked. NOT
   * clean - the difference is the whole point of having this value. */
  CouldNotCheck = "could-not-check",
}

/** What an assessment found. */
export interface ThreatAwarenessResult {
  readonly verdict: ThreatAwarenessVerdict;
  readonly matches: readonly IndicatorMatch[];
  /** Written for a PERSON. This is shown on a screen, not logged. */
  readonly explanation: string;
  /** What they can do. Empty when there is nothing - which is worth saying. */
  readonly suggestion: string;
}

export const threatAwarenessResult = (
  partial: Partial<ThreatAwarenessResult> = {},
): ThreatAwarenessResult =>
  Object.freeze({
    verdict: partial.verdict ?? ThreatAwarenessVerdict.NothingKnown,
    matches: Object.freeze([...(partial.matches ?? [])]),
    explanation: partial.explanation ?? "",
    suggestion: partial.suggestion ?? "",
  });

// ─────────────────────────────────────────────────────────────────────────────
// The corpus

/**
 * The indicators this device knows about.
 *
 * LOCAL. There is no lookup service here and there will not be one: asking a
 * server whether a file is malicious tells that server what files somebody has,
 * and asking about a phone number tells it who they are talking to. The corpus
 * is downloaded and consulted on the device.
 */
export interface LocalIndicatorCorpus {
  readonly isLoaded: boolean;
  readonly count: number;
  lookup(kind: IndicatorKind, value: string): ThreatIndicator | undefined;
}

/**
 * Knows nothing, and says CouldNotCheck rather than NothingKnown.
 *
 * The distinction is the entire reason this class is not just an empty map: a
 * device with no corpus has not checked anything, and reporting "nothing known"
 * would be a clean bill of health it has no basis for.
 */
export class EmptyIndicatorCorpus implements LocalIndicatorCorpus {
  readonly isLoaded = false;
  readonly count = 0;
  lookup(): ThreatIndicator | undefined {
    return undefined;
  }
}

/** A corpus held in memory. */
export class InMemoryIndicatorCorpus implements LocalIndicatorCorpus {
  private readonly byKind = new Map<IndicatorKind, Map<string, ThreatIndicator>>();

  constructor(indicators: readonly ThreatIndicator[] = []) {
    for (const indicator of indicators) this.add(indicator);
  }

  get isLoaded(): boolean {
    return true;
  }

  get count(): number {
    let n = 0;
    for (const map of this.byKind.values()) n += map.size;
    return n;
  }

  add(indicator: ThreatIndicator): void {
    let map = this.byKind.get(indicator.kind);
    if (!map) {
      map = new Map();
      this.byKind.set(indicator.kind, map);
    }
    const existing = map.get(indicator.value);
    // The HIGHER-confidence one wins. A weak source must not overwrite a strong
    // one just by being loaded second.
    if (!existing || indicator.confidence > existing.confidence) {
      map.set(indicator.value, indicator);
    }
  }

  lookup(kind: IndicatorKind, value: string): ThreatIndicator | undefined {
    const normalised = normaliseIndicator(kind, value);
    const direct = this.byKind.get(kind)?.get(normalised);
    if (direct) return direct;
    if (kind !== IndicatorKind.Domain) return undefined;
    // A domain indicator covers its SUBDOMAINS. A listed `example.com` should
    // match `login.example.com`, which is where the interesting ones live.
    const map = this.byKind.get(IndicatorKind.Domain);
    if (!map) return undefined;
    const parts = normalised.split(".");
    for (let i = 1; i < parts.length - 1; i++) {
      const parent = parts.slice(i).join(".");
      const found = map.get(parent);
      if (found) return found;
    }
    return undefined;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Assessment - which never acts

/** Assesses a file. */
export interface FileThreatAwareness {
  assess(file: FileArtifact): ThreatAwarenessResult;
}

/** Assesses a network endpoint. */
export interface NetworkThreatAwareness {
  assess(host: string, port?: number): ThreatAwarenessResult;
}

/** Assesses whether an identity is exposed. */
export interface BreachExposureAwareness {
  assess(emailOrPhone: string): ThreatAwarenessResult;
}

/**
 * The shared assessment logic.
 *
 * ONE MATCH IS NEVER CONCERNING ON ITS OWN unless the source is trusted. The
 * threshold is here rather than at each call site so it cannot drift between
 * the file path and the network path, which is exactly how a system ends up
 * strict about one and permissive about the other.
 */
abstract class AwarenessAssessorBase {
  /** Above this, a single match is Concerning. Below, it is Worth checking. */
  protected static readonly TRUSTED = 0.8;

  constructor(protected readonly corpus: LocalIndicatorCorpus) {}

  protected settle(matches: readonly IndicatorMatch[], subject: string): ThreatAwarenessResult {
    if (!this.corpus.isLoaded) {
      return threatAwarenessResult({
        verdict: ThreatAwarenessVerdict.CouldNotCheck,
        explanation: "this device has no threat list, so nothing was checked",
        suggestion: "connect to Wi-Fi so the list can be downloaded",
      });
    }
    if (matches.length === 0) {
      return threatAwarenessResult({
        verdict: ThreatAwarenessVerdict.NothingKnown,
        explanation: `nothing on this device's list matches ${subject}`,
      });
    }
    const best = matches.reduce((a, b) => (b.confidence > a.confidence ? b : a));
    const concerning = best.confidence >= AwarenessAssessorBase.TRUSTED || matches.length > 1;
    return threatAwarenessResult({
      verdict: concerning
        ? ThreatAwarenessVerdict.Concerning
        : ThreatAwarenessVerdict.WorthChecking,
      matches,
      explanation: concerning
        ? `${subject} matches something known to be harmful`
        : `${subject} matches something worth checking`,
      suggestion: concerning
        ? "do not open it, and do not enter anything into it"
        : "have a look before you go further",
    });
  }
}

/** Assesses a file against the corpus. */
export class FileThreatAwarenessAssessor
  extends AwarenessAssessorBase
  implements FileThreatAwareness
{
  constructor(corpus: LocalIndicatorCorpus = new EmptyIndicatorCorpus()) {
    super(corpus);
  }

  assess(file: FileArtifact): ThreatAwarenessResult {
    const matches: IndicatorMatch[] = [];
    if (file.sha256) {
      const found = this.corpus.lookup(IndicatorKind.FileHash, file.sha256);
      if (found) {
        matches.push(Object.freeze({ indicator: found, observed: file.sha256, confidence: found.confidence }));
      }
    }
    // The NAME is not matched against anything. A file called `invoice.pdf.exe`
    // is suspicious to a person and matching on names produces false positives
    // that teach people to ignore warnings.
    return this.settle(matches, file.path ? `that file` : "that file");
  }
}

/** Assesses a network endpoint against the corpus. */
export class NetworkThreatAwarenessAssessor
  extends AwarenessAssessorBase
  implements NetworkThreatAwareness
{
  constructor(corpus: LocalIndicatorCorpus = new EmptyIndicatorCorpus()) {
    super(corpus);
  }

  assess(host: string, port = 0): ThreatAwarenessResult {
    const matches: IndicatorMatch[] = [];
    const asIp = Ipv4Cidr.isIpv4(host);
    const kind = asIp ? IndicatorKind.Ipv4 : IndicatorKind.Domain;
    const found = this.corpus.lookup(kind, host);
    if (found) {
      matches.push(Object.freeze({ indicator: found, observed: host, confidence: found.confidence }));
    }
    return this.settle(matches, host || "that connection");
  }
}

/** Assesses whether an identity appears in a known breach. */
export class BreachExposureAssessor
  extends AwarenessAssessorBase
  implements BreachExposureAwareness
{
  constructor(corpus: LocalIndicatorCorpus = new EmptyIndicatorCorpus()) {
    super(corpus);
  }

  assess(emailOrPhone: string): ThreatAwarenessResult {
    const kind = emailOrPhone.includes("@") ? IndicatorKind.EmailAddress : IndicatorKind.PhoneNumber;
    const found = this.corpus.lookup(kind, emailOrPhone);
    const matches = found
      ? [Object.freeze({ indicator: found, observed: emailOrPhone, confidence: found.confidence })]
      : [];
    const result = this.settle(matches, "that address");
    if (matches.length === 0) return result;
    // The suggestion for a breach is DIFFERENT from the generic one, because
    // the action is different: change a password, not avoid a file.
    return threatAwarenessResult({
      ...result,
      suggestion: "change that password anywhere you have reused it",
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The gate

/** What a defensive action can do. */
export enum AntibodyCapability {
  /** Look at a file or a connection. Read-only, and the least of them. */
  Inspect = "inspect",
  /** Stop a connection. Affects this device only. */
  BlockConnection = "block-connection",
  /** Move a file somewhere it will not run. Reversible on purpose. */
  QuarantineFile = "quarantine-file",
  /** Tell a person something is happening. */
  NotifyOwner = "notify-owner",
  /** Raise an alarm beyond this device. The only one that reaches anybody
   * else, and the one that needs the most consent. */
  EscalateSos = "escalate-sos",
}

/** How bad something is. */
export enum ThreatSeverity {
  Informational = 0,
  Low = 1,
  Medium = 2,
  High = 3,
  Critical = 4,
}

/** What is happening, for the gate to weigh. */
export interface DefensiveThreatContext {
  readonly severity: ThreatSeverity;
  readonly summary: string;
  readonly matches: readonly IndicatorMatch[];
  readonly atMs: number;
}

/** A request to do something about a threat. */
export interface AuthorizedUseRequest {
  readonly capability: AntibodyCapability;
  readonly context: DefensiveThreatContext;
  /** Which device this affects. Never another device: nothing here reaches off
   * this machine except an escalation to its owner. */
  readonly deviceId: string;
  readonly reason: string;
}

/**
 * Somebody's agreement to a capability, for a while.
 *
 * NO OPEN-ENDED CONSENT IS CONSTRUCTIBLE. `expiresAtMs` has no default and an
 * expiry that is not after the grant is refused, so "forever" is something a
 * caller has to write out rather than get by leaving a field alone.
 */
export class AuthorizedUseConsent {
  readonly capabilities: ReadonlySet<AntibodyCapability>;

  constructor(
    capabilities: readonly AntibodyCapability[],
    readonly expiresAtMs: number,
    readonly grantedAtMs: number,
    /** Who agreed. Blank is REFUSED: a consent nobody can be shown to have
     * given is not a consent. */
    readonly grantedBy: string,
    readonly purpose = "",
  ) {
    if (capabilities.length === 0) {
      throw new Error("a consent must name at least one capability");
    }
    if (!grantedBy.trim()) {
      throw new Error("a consent must record who granted it");
    }
    if (expiresAtMs <= grantedAtMs) {
      throw new Error("a consent must expire after it was granted");
    }
    this.capabilities = Object.freeze(new Set(capabilities));
  }

  isValidAt(nowMs: number): boolean {
    return nowMs >= this.grantedAtMs && nowMs < this.expiresAtMs;
  }

  covers(capability: AntibodyCapability): boolean {
    return this.capabilities.has(capability);
  }

  describe(): string {
    const names = [...this.capabilities].sort().join(", ");
    return `${names}${this.purpose ? ` to ${this.purpose}` : ""}, granted by ${this.grantedBy}`;
  }
}

/** Whether an action may proceed. */
export interface AuthorizationDecision {
  readonly allowed: boolean;
  /** ALWAYS populated, including on allow. A decision without a reason is a
   * decision nobody can review, and these decisions act on somebody's device. */
  readonly reason: string;
  readonly capability: AntibodyCapability;
}

export const authorizationDecision = (
  allowed: boolean,
  reason: string,
  capability: AntibodyCapability,
): AuthorizationDecision => Object.freeze({ allowed, reason, capability });

/** Holds consents. */
export interface AuthorizedUseConsentStore {
  grant(consent: AuthorizedUseConsent): void;
  revoke(capability: AntibodyCapability): void;
  active(nowMs: number): readonly AuthorizedUseConsent[];
  isRevoked(capability: AntibodyCapability): boolean;
}

/** The default store. */
export class InMemoryAuthorizedUseConsentStore implements AuthorizedUseConsentStore {
  private readonly consents: AuthorizedUseConsent[] = [];
  private readonly revoked = new Set<AntibodyCapability>();

  grant(consent: AuthorizedUseConsent): void {
    this.consents.push(consent);
    // Granting CLEARS a previous revocation for those capabilities. Somebody
    // who revokes and then agrees again means the second thing.
    for (const c of consent.capabilities) this.revoked.delete(c);
  }

  /**
   * Revocation is by CAPABILITY, not by consent.
   *
   * Revoking one consent would leave any other consent carrying the same
   * capability working, and somebody who says "stop doing that" means all of
   * it.
   */
  revoke(capability: AntibodyCapability): void {
    this.revoked.add(capability);
  }

  isRevoked(capability: AntibodyCapability): boolean {
    return this.revoked.has(capability);
  }

  active(nowMs: number): readonly AuthorizedUseConsent[] {
    return Object.freeze(this.consents.filter((c) => c.isValidAt(nowMs)));
  }
}

/** Decides whether a defensive action may proceed. */
export interface AuthorizedUseGate {
  authorize(request: AuthorizedUseRequest): AuthorizationDecision;
}

/**
 * Denies everything.
 *
 * THE DEFAULT, and the most important line in this file. A build that forgot to
 * configure a gate can assess and cannot act. A permissive null gate is how a
 * protective feature becomes an offensive one by omission.
 */
export class NullAuthorizedUseGate implements AuthorizedUseGate {
  authorize(request: AuthorizedUseRequest): AuthorizationDecision {
    return authorizationDecision(
      false,
      "no authorisation gate is configured, so nothing will be acted on",
      request.capability,
    );
  }
}

/**
 * Allows only what somebody has explicitly consented to.
 *
 * FAILS CLOSED at every branch: no consent, wrong capability, expired, revoked,
 * or a clock that will not answer. The one thing it must never do is allow
 * something because it could not work out whether to refuse.
 */
export class ExplicitConsentAuthorizedUseGate implements AuthorizedUseGate {
  /**
   * Escalation needs BOTH consent and a severity that warrants it. A consent to
   * escalate is not a consent to escalate about anything - it is the only
   * capability that reaches another person, and a low-severity alarm at 3am
   * teaches somebody to ignore the next one.
   */
  static readonly ESCALATION_FLOOR = ThreatSeverity.High;

  constructor(
    private readonly store: AuthorizedUseConsentStore = new InMemoryAuthorizedUseConsentStore(),
    private readonly now: () => number = () => 0,
  ) {}

  authorize(request: AuthorizedUseRequest): AuthorizationDecision {
    const { capability } = request;
    if (this.store.isRevoked(capability)) {
      // Checked FIRST. Revocation beats a consent that is otherwise perfectly
      // valid.
      return authorizationDecision(false, `you turned off ${capability}`, capability);
    }

    let nowMs: number;
    try {
      nowMs = this.now();
    } catch {
      // A clock that will not answer means no. Assuming a time here would let a
      // broken clock become an open door.
      return authorizationDecision(
        false,
        "this device cannot tell the time, so it will not act",
        capability,
      );
    }

    const live = this.store.active(nowMs).filter((c) => c.covers(capability));
    if (live.length === 0) {
      return authorizationDecision(
        false,
        `nobody has agreed to ${capability} on this device`,
        capability,
      );
    }
    if (
      capability === AntibodyCapability.EscalateSos &&
      request.context.severity < ExplicitConsentAuthorizedUseGate.ESCALATION_FLOOR
    ) {
      return authorizationDecision(
        false,
        "this is not serious enough to raise an alarm with anybody",
        capability,
      );
    }
    if (!request.deviceId.trim()) {
      // An action with no device named is an action with no scope, and the
      // whole point of the scope is that it is this device.
      return authorizationDecision(false, "no device was named", capability);
    }
    return authorizationDecision(
      true,
      `${live[0].grantedBy} agreed to ${capability}`,
      capability,
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Blocklists

/**
 * An IPv4 range, and matching against it.
 *
 * PREFIX ARITHMETIC IS DONE UNSIGNED. JavaScript's bitwise operators produce a
 * SIGNED 32-bit result, so `192.168.0.1` comes out negative and a naive
 * comparison against a mask fails for the entire upper half of the address
 * space - which is where the interesting addresses are. `>>> 0` after every
 * step is the fix.
 */
export class Ipv4Cidr {
  readonly network: number;
  readonly mask: number;

  constructor(
    readonly prefix: string,
    readonly bits: number,
  ) {
    if (bits < 0 || bits > 32) throw new Error(`${bits} is not a usable prefix length`);
    // A /0 mask must be 0, and `-1 << 32` is `-1` in JavaScript rather than 0,
    // because the shift count is taken modulo 32. Special-cased rather than
    // discovered later by a /0 that matches nothing.
    this.mask = bits === 0 ? 0 : (0xffffffff << (32 - bits)) >>> 0;
    this.network = (Ipv4Cidr.toNumber(prefix) & this.mask) >>> 0;
  }

  static isIpv4(text: string): boolean {
    const parts = (text ?? "").split(".");
    return (
      parts.length === 4 &&
      parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) >= 0 && Number(p) <= 255)
    );
  }

  static toNumber(address: string): number {
    const parts = address.split(".").map(Number);
    if (parts.length !== 4) return 0;
    return (((parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | parts[3]) >>> 0);
  }

  static parse(text: string): Ipv4Cidr | undefined {
    const [prefix, bits] = (text ?? "").trim().split("/");
    if (!Ipv4Cidr.isIpv4(prefix)) return undefined;
    // A bare address is a /32 - one host. Defaulting to /0 would make a single
    // listed address match the entire internet.
    const length = bits === undefined ? 32 : Number(bits);
    if (!Number.isInteger(length) || length < 0 || length > 32) return undefined;
    return new Ipv4Cidr(prefix, length);
  }

  contains(address: string): boolean {
    if (!Ipv4Cidr.isIpv4(address)) return false;
    return ((Ipv4Cidr.toNumber(address) & this.mask) >>> 0) === this.network;
  }

  toString(): string {
    return `${this.prefix}/${this.bits}`;
  }
}

/** One line of a blocklist, parsed. */
export interface ParsedIndicator {
  readonly kind: IndicatorKind;
  readonly value: string;
  readonly comment: string;
}

/**
 * Reads the blocklist formats that actually exist.
 *
 * Hosts files, plain lists, and lists with comments. A parser that only handles
 * one silently reads a hosts file's `0.0.0.0` column as the indicator and
 * blocks nothing except the address 0.0.0.0.
 */
export class BlocklistParser {
  static parseLine(line: string): ParsedIndicator | undefined {
    const withoutComment = line.split("#")[0].trim();
    const comment = line.includes("#") ? line.slice(line.indexOf("#") + 1).trim() : "";
    if (!withoutComment) return undefined;

    const fields = withoutComment.split(/\s+/);
    // A hosts-file line is `0.0.0.0 bad.example`. The indicator is the SECOND
    // field; reading the first blocks the sinkhole address instead of the site.
    const value =
      fields.length >= 2 && (fields[0] === "0.0.0.0" || fields[0] === "127.0.0.1")
        ? fields[1]
        : fields[0];
    if (!value || value === "localhost") return undefined;

    if (value.includes("/") && Ipv4Cidr.parse(value)) {
      return Object.freeze({ kind: IndicatorKind.Ipv4Cidr, value, comment });
    }
    if (Ipv4Cidr.isIpv4(value)) {
      return Object.freeze({ kind: IndicatorKind.Ipv4, value, comment });
    }
    if (/^https?:\/\//i.test(value)) {
      return Object.freeze({ kind: IndicatorKind.Url, value, comment });
    }
    if (value.includes("@")) {
      return Object.freeze({ kind: IndicatorKind.EmailAddress, value, comment });
    }
    if (/^[a-f0-9]{32,64}$/i.test(value)) {
      return Object.freeze({ kind: IndicatorKind.FileHash, value, comment });
    }
    if (value.includes(".")) {
      return Object.freeze({ kind: IndicatorKind.Domain, value, comment });
    }
    return undefined;
  }

  static parse(text: string): ParsedIndicator[] {
    const out: ParsedIndicator[] = [];
    for (const line of text.split(/\r?\n/)) {
      const parsed = BlocklistParser.parseLine(line);
      if (parsed) out.push(parsed);
    }
    return out;
  }
}

/** Where indicators come from. */
export interface IndicatorSource {
  readonly name: string;
  readonly isLoaded: boolean;
  match(kind: IndicatorKind, value: string): IndicatorMatch | undefined;
}

/** A source backed by a parsed blocklist. */
export class BlocklistIndicatorSource implements IndicatorSource {
  private readonly corpus = new InMemoryIndicatorCorpus();
  private readonly ranges: Ipv4Cidr[] = [];
  private loaded = false;

  constructor(
    readonly name: string,
    /** How much this list is trusted. A community list is not a vendor feed and
     * should not produce the same verdict. */
    private readonly confidence = 0.7,
  ) {}

  get isLoaded(): boolean {
    return this.loaded;
  }

  load(text: string): number {
    let count = 0;
    for (const parsed of BlocklistParser.parse(text)) {
      if (parsed.kind === IndicatorKind.Ipv4Cidr) {
        const range = Ipv4Cidr.parse(parsed.value);
        if (range) {
          this.ranges.push(range);
          count += 1;
        }
        continue;
      }
      this.corpus.add(threatIndicator(parsed.kind, parsed.value, this.name, this.confidence, parsed.comment));
      count += 1;
    }
    this.loaded = true;
    return count;
  }

  match(kind: IndicatorKind, value: string): IndicatorMatch | undefined {
    const direct = this.corpus.lookup(kind, value);
    if (direct) {
      return Object.freeze({ indicator: direct, observed: value, confidence: direct.confidence });
    }
    if (kind !== IndicatorKind.Ipv4) return undefined;
    // CIDR ranges are checked after exact addresses, because an exact entry
    // usually carries a better comment than the range that also covers it.
    const range = this.ranges.find((r) => r.contains(value));
    return range
      ? Object.freeze({
          indicator: threatIndicator(IndicatorKind.Ipv4Cidr, range.toString(), this.name, this.confidence),
          observed: value,
          confidence: this.confidence,
        })
      : undefined;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Watching

/** Which way traffic was going. */
export enum ThreatDirection {
  /** Something reached in. Usually the more serious of the two. */
  Inbound = "inbound",
  /** This device reached out - which, for malware, is the interesting one. */
  Outbound = "outbound",
}

/** One connection this device made or received. */
export interface NetworkObservation {
  readonly host: string;
  readonly port: number;
  readonly direction: ThreatDirection;
  readonly atMs: number;
  /** Which app, where a host can tell. Empty rather than guessed. */
  readonly processName: string;
}

/** Where observations come from. */
export interface NetworkObservationFeed {
  readonly isAvailable: boolean;
  subscribe(handler: (observation: NetworkObservation) => void): () => void;
}

/** How bad, and what kind. */
export enum ThreatCategory {
  Network = "network",
  File = "file",
  Identity = "identity",
  /** Somebody being manipulated rather than something being exploited. The
   * commonest category by a wide margin, and the one no scanner catches. */
  SocialEngineering = "social-engineering",
}

/** Something worth telling somebody about. */
export interface ThreatSignal {
  readonly category: ThreatCategory;
  readonly severity: ThreatSeverity;
  readonly summary: string;
  readonly matches: readonly IndicatorMatch[];
  readonly atMs: number;
}

export const threatSignal = (partial: Partial<ThreatSignal> = {}): ThreatSignal =>
  Object.freeze({
    category: partial.category ?? ThreatCategory.Network,
    severity: partial.severity ?? ThreatSeverity.Informational,
    summary: partial.summary ?? "",
    matches: Object.freeze([...(partial.matches ?? [])]),
    atMs: partial.atMs ?? 0,
  });

/** Somewhere a signal goes. */
export interface ThreatSink {
  accept(signal: ThreatSignal): void;
}

/** Accepts and discards. The default: noticing is not reporting. */
export class NullThreatSink implements ThreatSink {
  accept(): void {
    /* nothing is reported anywhere by default */
  }
}

/** Wraps a function as a sink. */
export class DelegateThreatSink implements ThreatSink {
  constructor(private readonly handler: (signal: ThreatSignal) => void) {}
  accept(signal: ThreatSignal): void {
    this.handler(signal);
  }
}

/**
 * Sends a signal to several sinks.
 *
 * A THROWING SINK MUST NOT STOP THE OTHERS. One sink writing to a full disk
 * should not prevent the one that shows a person a warning.
 */
export class CompositeThreatSink implements ThreatSink {
  constructor(private readonly sinks: readonly ThreatSink[]) {}
  accept(signal: ThreatSignal): void {
    for (const sink of this.sinks) {
      try {
        sink.accept(signal);
      } catch {
        continue;
      }
    }
  }
}

/** Watches for threats. */
export interface ThreatMonitor {
  readonly isRunning: boolean;
  start(): void;
  stop(): void;
}

/** Watches network observations against blocklists. */
export class BlocklistThreatMonitor implements ThreatMonitor {
  private unsubscribe?: () => void;

  constructor(
    private readonly feed: NetworkObservationFeed,
    private readonly sources: readonly IndicatorSource[],
    private readonly sink: ThreatSink = new NullThreatSink(),
  ) {}

  get isRunning(): boolean {
    return this.unsubscribe !== undefined;
  }

  start(): void {
    if (this.isRunning || !this.feed.isAvailable) return;
    this.unsubscribe = this.feed.subscribe((observation) => this.consider(observation));
  }

  stop(): void {
    this.unsubscribe?.();
    this.unsubscribe = undefined;
  }

  private consider(observation: NetworkObservation): void {
    const kind = Ipv4Cidr.isIpv4(observation.host) ? IndicatorKind.Ipv4 : IndicatorKind.Domain;
    const matches = this.sources
      .map((s) => s.match(kind, observation.host))
      .filter((m): m is IndicatorMatch => m !== undefined);
    if (matches.length === 0) return;
    const best = matches.reduce((a, b) => (b.confidence > a.confidence ? b : a));
    // An OUTBOUND connection to a listed host is treated more seriously than an
    // inbound one: inbound is the internet knocking, which happens constantly;
    // outbound means something on this device chose to go there.
    const severity =
      observation.direction === ThreatDirection.Outbound && best.confidence >= 0.8
        ? ThreatSeverity.High
        : best.confidence >= 0.8
          ? ThreatSeverity.Medium
          : ThreatSeverity.Low;
    this.sink.accept(
      threatSignal({
        category: ThreatCategory.Network,
        severity,
        summary:
          observation.direction === ThreatDirection.Outbound
            ? `something on this device connected to ${observation.host}`
            : `${observation.host} connected to this device`,
        matches,
        atMs: observation.atMs,
      }),
    );
  }
}

/** Raises an alarm beyond this device. */
export interface SosEscalation {
  readonly isAvailable: boolean;
  escalate(signal: ThreatSignal): Promise<boolean>;
}

/**
 * Escalates nothing, and returns FALSE.
 *
 * False rather than true is the whole point: nothing downstream should believe
 * an alert was raised when none was. Returning true would be worse than doing
 * nothing, because something else would stop trying.
 */
export class NullSosEscalation implements SosEscalation {
  readonly isAvailable = false;
  async escalate(): Promise<boolean> {
    return false;
  }
}

/** Wraps a function as an escalation. */
export class DelegateSosEscalation implements SosEscalation {
  constructor(private readonly handler: (signal: ThreatSignal) => Promise<boolean>) {}
  readonly isAvailable = true;
  async escalate(signal: ThreatSignal): Promise<boolean> {
    return this.handler(signal);
  }
}

/**
 * A sink that escalates, but only through the gate.
 *
 * THE GATE IS ASKED EVERY TIME, not once at construction. A consent expires
 * while a device is running, and a sink that cached its answer would keep
 * escalating for hours after somebody's agreement ran out.
 */
export class SosThreatSink implements ThreatSink {
  constructor(
    private readonly escalation: SosEscalation,
    private readonly gate: AuthorizedUseGate = new NullAuthorizedUseGate(),
    private readonly deviceId = "",
  ) {}

  accept(signal: ThreatSignal): void {
    const decision = this.gate.authorize({
      capability: AntibodyCapability.EscalateSos,
      context: {
        severity: signal.severity,
        summary: signal.summary,
        matches: signal.matches,
        atMs: signal.atMs,
      },
      deviceId: this.deviceId,
      reason: signal.summary,
    });
    if (!decision.allowed) return;
    // Fire and forget, deliberately: an escalation that blocks the monitor
    // means one slow alarm stops every subsequent observation being examined.
    void this.escalation.escalate(signal);
  }
}

/** A sink that pokes a watchdog, so a stalled monitor is noticed. */
export class WatchdogThreatSink implements ThreatSink {
  private lastAtMs = 0;

  constructor(
    private readonly inner: ThreatSink,
    private readonly now: () => number = () => 0,
  ) {}

  /** How long without a signal before the monitor is presumed stalled. */
  static readonly STALE_MS = 15 * 60 * 1000;

  accept(signal: ThreatSignal): void {
    this.lastAtMs = this.now();
    this.inner.accept(signal);
  }

  /**
   * Quiet is NOT the same as healthy.
   *
   * A monitor that has crashed and one that has seen nothing look identical
   * from outside, and the whole reason to watch is that the first one is
   * silent in exactly the way the second one is.
   */
  isStale(): boolean {
    return this.lastAtMs > 0 && this.now() - this.lastAtMs > WatchdogThreatSink.STALE_MS;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The whole thing

/** How the defensive module is configured. */
export interface DefenseOptions {
  /** OFF. Watching a network is a decision, not a default. */
  readonly enabled: boolean;
  readonly blocklistUrls: readonly string[];
  readonly refreshHours: number;
  /** Escalation is off separately from watching, because they are different
   * agreements: noticing something is not telling anybody about it. */
  readonly escalationEnabled: boolean;
}

export const defenseOptions = (partial: Partial<DefenseOptions> = {}): DefenseOptions =>
  Object.freeze({
    enabled: partial.enabled ?? false,
    blocklistUrls: Object.freeze([...(partial.blocklistUrls ?? [])]),
    refreshHours: partial.refreshHours ?? 24,
    escalationEnabled: partial.escalationEnabled ?? false,
  });

/** Defence that runs while the device does. */
export interface AutonomicDefense {
  readonly isRunning: boolean;
  start(): void;
  stop(): void;
  readonly lastSignal?: ThreatSignal;
}

/**
 * The always-on sentinel.
 *
 * IT ONLY WATCHES. Every action it could take goes through the gate, and with
 * no gate configured it takes none - so the worst a misconfigured deployment
 * does is notice things and tell nobody.
 */
export class AlwaysOnDefenseSentinel implements AutonomicDefense {
  private running = false;
  private last?: ThreatSignal;

  constructor(
    private readonly monitors: readonly ThreatMonitor[],
    private readonly options: DefenseOptions = defenseOptions(),
  ) {}

  get isRunning(): boolean {
    return this.running;
  }

  get lastSignal(): ThreatSignal | undefined {
    return this.last;
  }

  observe(signal: ThreatSignal): void {
    this.last = signal;
  }

  start(): void {
    if (!this.options.enabled || this.running) return;
    for (const monitor of this.monitors) monitor.start();
    this.running = true;
  }

  stop(): void {
    for (const monitor of this.monitors) monitor.stop();
    this.running = false;
  }
}

/** The system that assesses and, through the gate, acts. */
export interface DefensiveAntibodySystemContract {
  assessFile(file: FileArtifact): ThreatAwarenessResult;
  assessHost(host: string, port?: number): ThreatAwarenessResult;
  assessIdentity(emailOrPhone: string): ThreatAwarenessResult;
  act(request: AuthorizedUseRequest): AuthorizationDecision;
}

/**
 * Assessment, and a single door to action.
 *
 * The assessors are free to run because they only look. `act` is the one method
 * that can change anything, and it does nothing on its own - it asks the gate
 * and returns the decision. Whether the caller then does something is the
 * caller's business, and the DECISION is the record of whether it was allowed.
 */
export class DefensiveAntibodySystem implements DefensiveAntibodySystemContract {
  constructor(
    private readonly files: FileThreatAwareness = new FileThreatAwarenessAssessor(),
    private readonly network: NetworkThreatAwareness = new NetworkThreatAwarenessAssessor(),
    private readonly breaches: BreachExposureAwareness = new BreachExposureAssessor(),
    private readonly gate: AuthorizedUseGate = new NullAuthorizedUseGate(),
  ) {}

  assessFile(file: FileArtifact): ThreatAwarenessResult {
    return this.files.assess(file);
  }

  assessHost(host: string, port = 0): ThreatAwarenessResult {
    return this.network.assess(host, port);
  }

  assessIdentity(emailOrPhone: string): ThreatAwarenessResult {
    return this.breaches.assess(emailOrPhone);
  }

  act(request: AuthorizedUseRequest): AuthorizationDecision {
    return this.gate.authorize(request);
  }
}

/** Assembles the defensive module a host has agreed to. */
export class DefenseModule {
  static build(
    options: DefenseOptions,
    feed: NetworkObservationFeed,
    sources: readonly IndicatorSource[],
    sink: ThreatSink = new NullThreatSink(),
  ): AutonomicDefense {
    if (!options.enabled) {
      // Returns a sentinel that will not start, rather than throwing. A
      // disabled module is a normal configuration, not an error.
      return new AlwaysOnDefenseSentinel([], options);
    }
    return new AlwaysOnDefenseSentinel(
      [new BlocklistThreatMonitor(feed, sources, sink)],
      options,
    );
  }
}

// The C# spellings, kept so the two trees line up.
export type ILocalIndicatorCorpus = LocalIndicatorCorpus;
export type IFileThreatAwareness = FileThreatAwareness;
export type INetworkThreatAwareness = NetworkThreatAwareness;
export type IBreachExposureAwareness = BreachExposureAwareness;
export type IAuthorizedUseGate = AuthorizedUseGate;
export type IAuthorizedUseConsentStore = AuthorizedUseConsentStore;
export type IDefensiveAntibodySystem = DefensiveAntibodySystemContract;
export type IIndicatorSource = IndicatorSource;
export type IThreatMonitor = ThreatMonitor;
export type IThreatSink = ThreatSink;
export type ISosEscalation = SosEscalation;
export type INetworkObservationFeed = NetworkObservationFeed;
export type IAutonomicDefense = AutonomicDefense;
