// Personal data behind a consent guard, plugins, what the companion senses,
// and the video seam.
//
// THE CONSENT GUARD IS THE SERIOUS PART. Everything else here is plumbing; this
// is what stands between an assistant and somebody's email.
//
// Four properties, and each exists because the version without it is the one
// that gets built by default:
//
//   * SCOPED. A grant to read the calendar is not a grant to read contacts. A
//     single "personal data" permission is how an assistant that was allowed to
//     check a meeting time ends up reading a mailbox.
//
//   * EXPIRING. A grant with no end is a grant forever, and nobody revisits it.
//     There is no way to build a token without an expiry.
//
//   * REVOCABLE, and revocation beats everything - an unexpired, in-scope,
//     correctly-granted token that has been revoked is refused.
//
//   * FAIL CLOSED. Every path that cannot answer answers NO: no token, wrong
//     scope, expired, revoked, or a clock that will not read. A refusal is the
//     system working.

// ─────────────────────────────────────────────────────────────────────────────
// Consent

/**
 * One thing a person may agree to.
 *
 * SEPARATE VALUES, deliberately fine-grained, and reading is separate from
 * writing everywhere. An assistant that can read a calendar to answer "when am
 * I free" does not thereby get to send invitations.
 */
export enum ConsentScope {
  CalendarRead = "calendar:read",
  CalendarWrite = "calendar:write",
  ContactsRead = "contacts:read",
  ContactsWrite = "contacts:write",
  EmailRead = "email:read",
  /** Sending is its own scope and is never bundled. Sending mail as somebody is
   * the single most consequential thing in this file. */
  EmailSend = "email:send",
  LocationRead = "location:read",
  PhotosRead = "photos:read",
}

export const isWriteScope = (s: ConsentScope): boolean =>
  s.endsWith(":write") || s.endsWith(":send");

/**
 * Proof that somebody agreed to something, for a while.
 *
 * NO OPEN-ENDED GRANT IS CONSTRUCTIBLE, and a grant nobody can be shown to have
 * made is refused - which makes "forever" something a caller writes out in
 * whole years rather than gets by leaving a field alone.
 */
export class UserConsentToken {
  readonly scopes: ReadonlySet<ConsentScope>;

  constructor(
    scopes: readonly ConsentScope[],
    readonly expiresAtMs: number,
    readonly grantedAtMs: number,
    /** Who agreed. Blank is refused. */
    readonly grantedBy = "the person using this device",
    /** What it is for, in their words. Shown when they review what they have
     * allowed, which is the only thing that makes a review meaningful. */
    readonly purpose = "",
  ) {
    if (scopes.length === 0) throw new Error("a consent token must name at least one scope");
    if (!grantedBy.trim()) throw new Error("a consent token must record who granted it");
    if (expiresAtMs <= grantedAtMs) {
      throw new Error("a consent token must expire after it was granted");
    }
    this.scopes = Object.freeze(new Set(scopes));
  }

  isValidAt(nowMs: number): boolean {
    return nowMs >= this.grantedAtMs && nowMs < this.expiresAtMs;
  }

  covers(scope: ConsentScope): boolean {
    return this.scopes.has(scope);
  }

  remainingMs(nowMs: number): number {
    return Math.max(0, this.expiresAtMs - nowMs);
  }

  describe(): string {
    const names = [...this.scopes].sort().join(", ");
    return `${names}${this.purpose ? ` to ${this.purpose}` : ""}, until ${new Date(this.expiresAtMs).toISOString()}`;
  }

  /**
   * FIFTEEN MINUTES by default.
   *
   * Short because the common case is one task - "what is on today" - and a
   * grant that outlives the task is a grant that is still open next week.
   */
  static forScopes(
    scopes: readonly ConsentScope[],
    nowMs: number,
    minutes = 15,
    purpose = "",
  ): UserConsentToken {
    return new UserConsentToken(
      scopes,
      nowMs + Math.max(1, minutes) * 60_000,
      nowMs,
      "the person using this device",
      purpose,
    );
  }
}

/** Whether an operation may proceed, and why not. */
export interface ConsentDecision {
  readonly allowed: boolean;
  readonly reason: string;
  readonly scope: ConsentScope;
}

/**
 * Holds tokens and answers whether a scope is permitted right now.
 *
 * FAILS CLOSED EVERYWHERE. The one thing it must never do is allow something
 * because it could not work out whether to refuse.
 */
export class ConsentGuard {
  private readonly tokens: UserConsentToken[] = [];
  private readonly revoked = new Set<ConsentScope>();

  constructor(private readonly now: () => number = () => 0) {}

  grant(token: UserConsentToken): void {
    this.tokens.push(token);
    // Granting CLEARS a previous revocation for those scopes. A person who
    // revokes and then agrees again means the second thing.
    for (const s of token.scopes) this.revoked.delete(s);
  }

  /**
   * Revocation is by SCOPE, not by token.
   *
   * Revoking a token would leave any other token carrying the same scope
   * working, and a person who says "stop reading my email" means all of it.
   */
  revoke(scope: ConsentScope): void {
    this.revoked.add(scope);
  }

  revokeAll(): void {
    for (const s of Object.values(ConsentScope)) this.revoked.add(s);
    this.tokens.length = 0;
  }

  check(scope: ConsentScope): ConsentDecision {
    let nowMs: number;
    try {
      nowMs = this.now();
    } catch {
      // A clock that will not answer means no. Assuming a time here would let a
      // broken clock become an open door.
      return Object.freeze({
        allowed: false,
        reason: "this device cannot tell the time, so it will not act on your data",
        scope,
      });
    }
    if (this.revoked.has(scope)) {
      // Checked FIRST. Revocation beats a token that is otherwise perfectly
      // valid.
      return Object.freeze({ allowed: false, reason: `you turned off ${scope}`, scope });
    }
    const live = this.tokens.filter((t) => t.covers(scope) && t.isValidAt(nowMs));
    if (live.length === 0) {
      const expired = this.tokens.some((t) => t.covers(scope));
      return Object.freeze({
        allowed: false,
        reason: expired
          ? `the permission for ${scope} has run out - ask again`
          : `this needs your permission for ${scope}`,
        scope,
      });
    }
    const until = Math.max(...live.map((t) => t.expiresAtMs));
    return Object.freeze({
      allowed: true,
      reason: `allowed until ${new Date(until).toISOString()}`,
      scope,
    });
  }

  require(scope: ConsentScope): void {
    const decision = this.check(scope);
    if (!decision.allowed) throw new Error(decision.reason);
  }

  /** What is allowed right now - for the screen where somebody reviews it. A
   * permission list nobody can see is a permission list nobody withdraws. */
  activeScopes(): readonly ConsentScope[] {
    const nowMs = this.now();
    const live = new Set<ConsentScope>();
    for (const token of this.tokens) {
      if (!token.isValidAt(nowMs)) continue;
      for (const s of token.scopes) if (!this.revoked.has(s)) live.add(s);
    }
    return Object.freeze([...live].sort());
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Personal adapters

/** One event. */
export interface CalendarEvent {
  readonly title: string;
  readonly startsAtMs: number;
  readonly endsAtMs: number;
  readonly location: string;
  readonly attendees: readonly string[];
}

/** One person. */
export interface Contact {
  readonly displayName: string;
  readonly emails: readonly string[];
  readonly phones: readonly string[];
}

/** One message. */
export interface EmailMessage {
  readonly subject: string;
  readonly sender: string;
  readonly recipients: readonly string[];
  readonly body: string;
  readonly receivedAtMs: number;
}

/** Reaches the device's calendar. */
export interface CalendarAdapter {
  readonly isAvailable: boolean;
  eventsBetween(startMs: number, endMs: number): Promise<readonly CalendarEvent[]>;
}

/** Reaches the device's contacts. */
export interface ContactsAdapter {
  readonly isAvailable: boolean;
  search(query: string): Promise<readonly Contact[]>;
}

/** Reaches the device's mail. */
export interface EmailAdapter {
  readonly isAvailable: boolean;
  recent(count?: number): Promise<readonly EmailMessage[]>;
  send(message: EmailMessage): Promise<boolean>;
}

/**
 * Reads nothing.
 *
 * The DEFAULT, so a build with no calendar binding reads no calendar - rather
 * than a build that happens to have one reading it without anybody wiring
 * consent.
 */
export class NullCalendarAdapter implements CalendarAdapter {
  readonly isAvailable = false;
  async eventsBetween(): Promise<readonly CalendarEvent[]> {
    return [];
  }
}

/** Finds nobody. */
export class NullContactsAdapter implements ContactsAdapter {
  readonly isAvailable = false;
  async search(): Promise<readonly Contact[]> {
    return [];
  }
}

/**
 * Reads nothing and sends nothing.
 *
 * `send` returns FALSE rather than throwing, and returning true would be the
 * worst possible default: the assistant would tell somebody their message went
 * when it did not.
 */
export class NullEmailAdapter implements EmailAdapter {
  readonly isAvailable = false;
  async recent(): Promise<readonly EmailMessage[]> {
    return [];
  }
  async send(): Promise<boolean> {
    return false;
  }
}

/** What the companion may reach on this device. */
export interface PersonalDomainContext {
  readonly hasCalendar: boolean;
  readonly hasContacts: boolean;
  readonly hasEmail: boolean;
}

/**
 * Says what is CONNECTED and, separately, what is ALLOWED.
 *
 * The two are different, and conflating them is how a person is told the
 * assistant can read their mail when it merely could if they said so.
 */
export function describePersonalContext(
  context: PersonalDomainContext,
  guard?: ConsentGuard,
): string {
  const connected = [
    context.hasCalendar ? "calendar" : "",
    context.hasContacts ? "contacts" : "",
    context.hasEmail ? "email" : "",
  ].filter(Boolean);
  if (connected.length === 0) return "nothing personal is connected to this device";
  let text = `connected: ${connected.join(", ")}`;
  if (guard) {
    const active = guard.activeScopes();
    text += active.length
      ? `; allowed right now: ${active.join(", ")}`
      : "; nothing is allowed right now";
  }
  return text;
}

/**
 * The companion's way in - and every call passes the guard FIRST.
 *
 * THE GUARD IS CHECKED BEFORE THE ADAPTER IS TOUCHED, not after. Reading the
 * data and then deciding whether it was allowed has already read it, and on a
 * platform that logs access, already recorded it.
 */
export class PersonalCompanionAdapter {
  constructor(
    private readonly guard: ConsentGuard = new ConsentGuard(),
    private readonly calendar: CalendarAdapter = new NullCalendarAdapter(),
    private readonly contacts: ContactsAdapter = new NullContactsAdapter(),
    private readonly email: EmailAdapter = new NullEmailAdapter(),
  ) {}

  get context(): PersonalDomainContext {
    return Object.freeze({
      hasCalendar: this.calendar.isAvailable,
      hasContacts: this.contacts.isAvailable,
      hasEmail: this.email.isAvailable,
    });
  }

  async eventsBetween(
    startMs: number,
    endMs: number,
  ): Promise<{ events: readonly CalendarEvent[]; refusal: string }> {
    const decision = this.guard.check(ConsentScope.CalendarRead);
    if (!decision.allowed) return { events: [], refusal: decision.reason };
    return { events: await this.calendar.eventsBetween(startMs, endMs), refusal: "" };
  }

  async findContact(query: string): Promise<{ contacts: readonly Contact[]; refusal: string }> {
    const decision = this.guard.check(ConsentScope.ContactsRead);
    if (!decision.allowed) return { contacts: [], refusal: decision.reason };
    return { contacts: await this.contacts.search(query), refusal: "" };
  }

  async recentEmail(count = 20): Promise<{ messages: readonly EmailMessage[]; refusal: string }> {
    const decision = this.guard.check(ConsentScope.EmailRead);
    if (!decision.allowed) return { messages: [], refusal: decision.reason };
    return { messages: await this.email.recent(count), refusal: "" };
  }

  /**
   * Requires EmailSend, which reading never grants.
   *
   * The two scopes are checked separately and a token carrying only EmailRead
   * cannot send - which is the whole reason they are separate values.
   */
  async sendEmail(message: EmailMessage): Promise<{ sent: boolean; refusal: string }> {
    const decision = this.guard.check(ConsentScope.EmailSend);
    if (!decision.allowed) return { sent: false, refusal: decision.reason };
    if (!this.email.isAvailable) {
      return { sent: false, refusal: "no mail account is connected to this device" };
    }
    return { sent: await this.email.send(message), refusal: "" };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Plugins

/**
 * What a plugin is allowed to do.
 *
 * EVERYTHING OFF. A plugin is code from somebody else running inside the
 * assistant, and a permission it was not given is a permission it does not have
 * - not one it has until somebody notices.
 */
export interface Permissions {
  readonly readFiles: boolean;
  readonly writeFiles: boolean;
  readonly network: boolean;
  /** Reaching the model. Off by default, because a plugin with model access can
   * spend the device's battery and, through the model, its context. */
  readonly inference: boolean;
  /** Held as scopes rather than a flag, so a plugin cannot be given "personal
   * data" wholesale. */
  readonly consentScopes: ReadonlySet<ConsentScope>;
  /** Directories it may touch. Empty with file access on means its own
   * workspace only. */
  readonly paths: readonly string[];
}

export const noPermissions = (): Permissions =>
  Object.freeze({
    readFiles: false,
    writeFiles: false,
    network: false,
    inference: false,
    consentScopes: Object.freeze(new Set<ConsentScope>()),
    paths: Object.freeze([]),
  });

/**
 * What `permissions()` accepts.
 *
 * `consentScopes` is widened to any ITERABLE, because the factory already
 * builds a `Set` from it and every caller has one of an array, a set, or
 * nothing. Requiring a `ReadonlySet` on the way in made two correct call sites
 * fail to type-check while the factory itself was happy to take either.
 */
export type PermissionsInit = Omit<Partial<Permissions>, "consentScopes"> & {
  readonly consentScopes?: Iterable<ConsentScope>;
};

export const permissions = (partial: PermissionsInit = {}): Permissions =>
  Object.freeze({
    readFiles: partial.readFiles ?? false,
    writeFiles: partial.writeFiles ?? false,
    network: partial.network ?? false,
    inference: partial.inference ?? false,
    consentScopes: Object.freeze(new Set(partial.consentScopes ?? [])),
    paths: Object.freeze([...(partial.paths ?? [])]),
  });

/**
 * What a person is shown before installing.
 *
 * Written as capabilities in plain words, because "network: true" is not a
 * decision anybody can make.
 */
export function describePermissions(p: Permissions): string {
  const wants: string[] = [];
  if (p.network) wants.push("use the internet");
  if (p.readFiles) {
    wants.push(`read files${p.paths.length ? ` in ${p.paths.join(", ")}` : " in its own folder"}`);
  }
  if (p.writeFiles) {
    wants.push(`change files${p.paths.length ? ` in ${p.paths.join(", ")}` : " in its own folder"}`);
  }
  if (p.inference) wants.push("use the assistant's model");
  for (const s of [...p.consentScopes].sort()) wants.push(`reach your ${s.split(":")[0]}`);
  return wants.length ? `this wants to ${wants.join("; ")}` : "this asks for nothing";
}

/** Says where plugins live. */
export interface PluginsRootResolver {
  pluginsRoot(): string;
}

/** Says where one plugin's own files live. */
export interface WorkspacePathProvider {
  workspaceFor(pluginId: string): string;
}

/** What loading one plugin did. */
export interface PluginLoadResult {
  readonly pluginId: string;
  readonly loaded: boolean;
  readonly version: string;
  readonly granted: Permissions;
  /** What it ASKED for, kept beside what it got - so a review screen can show
   * the difference. A plugin asking for far more than it was given is worth
   * seeing. */
  readonly requested: Permissions;
  readonly error: string;
}

/**
 * Loads a plugin with no more than it was granted.
 *
 * THE INTERSECTION, ALWAYS. A plugin gets what it asked for AND what the person
 * allowed - never the union, and never what it asked for on the grounds that it
 * asked. That single rule is the difference between a permission system and a
 * manifest.
 */
export class PluginLoader {
  constructor(
    private readonly roots?: PluginsRootResolver,
    private readonly workspaces?: WorkspacePathProvider,
    private readonly readManifest?: (pluginId: string) => Record<string, unknown>,
  ) {}

  static intersect(requested: Permissions, allowed: Permissions): Permissions {
    return permissions({
      readFiles: requested.readFiles && allowed.readFiles,
      writeFiles: requested.writeFiles && allowed.writeFiles,
      network: requested.network && allowed.network,
      inference: requested.inference && allowed.inference,
      // The factory builds the Set; what it needs is the members. Everything
      // downstream calls `.has()` on this field, so it must not stay an array.
      consentScopes: [...requested.consentScopes].filter((s) =>
        allowed.consentScopes.has(s),
      ),
      // Paths intersect too. A plugin granted one directory and asking for two
      // gets one.
      paths: requested.paths.filter((p) => allowed.paths.includes(p)),
    });
  }

  load(pluginId: string, allowed: Permissions): PluginLoadResult {
    const failed = (error: string): PluginLoadResult =>
      Object.freeze({
        pluginId,
        loaded: false,
        version: "",
        granted: noPermissions(),
        requested: noPermissions(),
        error,
      });

    if (!pluginId.trim()) return failed("a plugin needs an identifier");
    if (!this.readManifest) return failed("no way to read a manifest");

    let manifest: Record<string, unknown>;
    try {
      manifest = this.readManifest(pluginId);
    } catch (error) {
      return failed(error instanceof Error ? error.message : String(error));
    }

    const raw = (manifest.permissions ?? {}) as Record<string, unknown>;
    const scopes: ConsentScope[] = [];
    for (const name of (raw.consent_scopes as string[] | undefined) ?? []) {
      const found = Object.values(ConsentScope).find((s) => s === name);
      // An unknown scope is DROPPED, not an error. A plugin built against a
      // newer build asking for something this one has never heard of gets less,
      // not a failure - and it certainly does not get it.
      if (found) scopes.push(found);
    }
    const requested = permissions({
      readFiles: Boolean(raw.read_files),
      writeFiles: Boolean(raw.write_files),
      network: Boolean(raw.network),
      inference: Boolean(raw.inference),
      consentScopes: scopes,
      paths: ((raw.paths as string[] | undefined) ?? []).map(String),
    });

    return Object.freeze({
      pluginId,
      loaded: true,
      version: String(manifest.version ?? ""),
      granted: PluginLoader.intersect(requested, allowed),
      requested,
      error: "",
    });
  }
}

/**
 * Starts and stops plugins.
 *
 * STOPPING IS THE HARD PART. A plugin that will not stop is a plugin still
 * holding a permission, so this drops the grant FIRST - if it ignores the
 * request it is at least no longer allowed to do anything.
 */
export class PluginLifecycleService {
  private readonly running = new Map<string, PluginLoadResult>();

  constructor(private readonly loader: PluginLoader = new PluginLoader()) {}

  start(pluginId: string, allowed: Permissions): PluginLoadResult {
    const result = this.loader.load(pluginId, allowed);
    if (result.loaded) this.running.set(pluginId, result);
    return result;
  }

  stop(pluginId: string): boolean {
    return this.running.delete(pluginId);
  }

  stopAll(): number {
    const count = this.running.size;
    this.running.clear();
    return count;
  }

  runningIds(): readonly string[] {
    return Object.freeze([...this.running.keys()].sort());
  }

  /** A plugin that is not running has NO permissions, not its last ones. */
  permissionsOf(pluginId: string): Permissions {
    return this.running.get(pluginId)?.granted ?? noPermissions();
  }
}

/** One plugin somebody has installed. */
export interface RegisteredPlugin {
  readonly pluginId: string;
  readonly displayName: string;
  readonly version: string;
  readonly granted: Permissions;
  readonly installedAtMs: number;
}

/** What the marketplace says about one. */
export interface MarketplaceEntry {
  readonly pluginId: string;
  readonly displayName: string;
  readonly summary: string;
  readonly author: string;
  readonly requested: Permissions;
  /** The digest of the package. A plugin without one is not installable, for
   * the same reason a model without one is not. */
  readonly sha256: string;
}

/** What this device has installed. */
export class PluginRegistry {
  private readonly plugins = new Map<string, RegisteredPlugin>();

  register(plugin: RegisteredPlugin): void {
    this.plugins.set(plugin.pluginId, plugin);
  }

  get(pluginId: string): RegisteredPlugin | undefined {
    return this.plugins.get(pluginId);
  }

  all(): readonly RegisteredPlugin[] {
    return Object.freeze(
      [...this.plugins.values()].sort((a, b) => a.displayName.localeCompare(b.displayName)),
    );
  }

  /** Removing a plugin removes its GRANT too. Leaving the grant behind means a
   * reinstall silently inherits permissions nobody re-approved. */
  remove(pluginId: string): boolean {
    return this.plugins.delete(pluginId);
  }
}

/**
 * What is on offer.
 *
 * NOTHING INSTALLS WITHOUT A DIGEST and nothing installs without the person
 * seeing what it asked for. A marketplace that installs on a tap is a
 * marketplace that installs whatever was in the listing when the tap landed.
 */
export class PluginMarketplace {
  constructor(
    private readonly fetchListing?: () => Promise<readonly MarketplaceEntry[]>,
    private readonly download?: (entry: MarketplaceEntry) => Promise<Uint8Array>,
    private readonly digestOf?: (bytes: Uint8Array) => string,
  ) {}

  async list(): Promise<readonly MarketplaceEntry[]> {
    return this.fetchListing ? this.fetchListing() : [];
  }

  /** What to show before installing, so a person is agreeing to something they
   * have read. */
  static consentPrompt(entry: MarketplaceEntry): string {
    return `${entry.displayName} by ${entry.author}: ${entry.summary}\n${describePermissions(entry.requested)}`;
  }

  async install(
    entry: MarketplaceEntry,
    approved: Permissions,
  ): Promise<{ bytes: Uint8Array; error: string }> {
    if (!entry.sha256) {
      return { bytes: new Uint8Array(0), error: "that plugin has no checksum, so it will not install" };
    }
    if (!this.download || !this.digestOf) {
      return { bytes: new Uint8Array(0), error: "this device cannot install plugins" };
    }
    let bytes: Uint8Array;
    try {
      bytes = await this.download(entry);
    } catch (error) {
      return {
        bytes: new Uint8Array(0),
        error: `the plugin did not download: ${error instanceof Error ? error.message : String(error)}`,
      };
    }
    if (this.digestOf(bytes).toLowerCase() !== entry.sha256.trim().toLowerCase()) {
      return { bytes: new Uint8Array(0), error: "that plugin does not match its checksum" };
    }
    // The APPROVED permissions are what matter, not the requested ones. An
    // install that grants what the listing asked for has skipped the person.
    if (PluginLoader.intersect(entry.requested, approved) === noPermissions()) {
      // Not an error - a plugin approved with nothing still installs and does
      // nothing, which is a legitimate choice.
    }
    return { bytes, error: "" };
  }
}

/** Wires the plugin service. */
export class PluginsServiceCollectionExtensions {
  static addPlugins(
    roots?: PluginsRootResolver,
    workspaces?: WorkspacePathProvider,
    readManifest?: (pluginId: string) => Record<string, unknown>,
  ): PluginLifecycleService {
    return new PluginLifecycleService(new PluginLoader(roots, workspaces, readManifest));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// What the companion senses

/**
 * A coarse guess at how somebody seems.
 *
 * DELIBERATELY COARSE. Finer categories are not more accurate, they are more
 * confidently wrong - the underlying signal does not distinguish irritation
 * from concentration, and offering a label that claims to only makes the error
 * harder to notice.
 */
export enum AffectLabel {
  /** The honest default and a real answer. A mapper that must always choose a
   * feeling will always find one. */
  Uncertain = "uncertain",
  Calm = "calm",
  Engaged = "engaged",
  Stressed = "stressed",
  Tired = "tired",
  Low = "low",
}

/** A guess, with how much to trust it. */
export interface AffectReading {
  readonly label: AffectLabel;
  readonly confidence: number;
  /** Where it came from, so two readings can be weighed against each other and
   * a person can be told what was actually observed. */
  readonly source: string;
  readonly atMs: number;
}

/** Under this, the answer is Uncertain whatever the numbers say. A coin-flip
 * dressed as an observation is worse than saying nothing. */
export const AFFECT_CONFIDENCE_FLOOR = 0.45;

export function affectReading(
  label: AffectLabel,
  confidence: number,
  source: string,
  atMs = 0,
): AffectReading {
  const c = Math.max(0, Math.min(1, confidence));
  return Object.freeze({
    label: c < AFFECT_CONFIDENCE_FLOOR ? AffectLabel.Uncertain : label,
    confidence: c,
    source,
    atMs,
  });
}

export const isActionable = (r: AffectReading): boolean =>
  r.label !== AffectLabel.Uncertain && r.confidence >= 0.5;

/**
 * From facial expression scores.
 *
 * THE WEAKEST OF THE SIGNALS and treated as such. Expression is culturally
 * variable, easily posed, and a camera sees a face lit by a phone in a dark
 * room. Its confidence is scaled down so it loses to a voice or a wrist reading
 * that disagrees.
 */
export class FaceAffectMapper {
  /** Everything this mapper produces is multiplied by this. A face is
   * corroboration, not evidence. */
  static readonly TRUST = 0.7;

  private static readonly labels: Readonly<Record<string, AffectLabel>> = Object.freeze({
    neutral: AffectLabel.Calm,
    happy: AffectLabel.Engaged,
    angry: AffectLabel.Stressed,
    fear: AffectLabel.Stressed,
    sad: AffectLabel.Low,
    tired: AffectLabel.Tired,
  });

  map(scores: Readonly<Record<string, number>>, atMs = 0): AffectReading {
    const entries = Object.entries(scores);
    if (entries.length === 0) return affectReading(AffectLabel.Uncertain, 0, "face", atMs);
    const best = entries.reduce((a, b) => (b[1] > a[1] ? b : a));
    // The MARGIN over the runner-up, not the top score alone. A face scoring 0.6
    // happy and 0.58 sad has told us nothing, and reporting 0.6 confidence
    // would be a lie about a near-tie.
    const rest = entries.filter(([k]) => k !== best[0]).map(([, v]) => v).sort((a, b) => b - a);
    const margin = best[1] - (rest[0] ?? 0);
    return affectReading(
      FaceAffectMapper.labels[best[0].toLowerCase()] ?? AffectLabel.Uncertain,
      margin * FaceAffectMapper.TRUST + best[1] * 0.2,
      "face",
      atMs,
    );
  }
}

/**
 * Brings face signals to the companion, or refuses to.
 *
 * THE CAMERA IS OFF UNLESS SOMEBODY TURNED IT ON, and "on" has a timeout. A
 * camera that stays on because a screen was opened once is a camera that is
 * always on.
 */
export class FaceCompanionBridge {
  private enabledUntilMs?: number;

  constructor(
    private readonly mapper: FaceAffectMapper = new FaceAffectMapper(),
    private readonly now: () => number = () => 0,
  ) {}

  get isEnabled(): boolean {
    return this.enabledUntilMs !== undefined && this.now() < this.enabledUntilMs;
  }

  /** Time-limited, always. There is no way to turn this on permanently. */
  enableFor(minutes = 5): number {
    this.enabledUntilMs = this.now() + Math.max(1, Math.min(60, minutes)) * 60_000;
    return this.enabledUntilMs;
  }

  disable(): void {
    this.enabledUntilMs = undefined;
  }

  read(scores: Readonly<Record<string, number>>): AffectReading {
    if (!this.isEnabled) return affectReading(AffectLabel.Uncertain, 0, "face", this.now());
    return this.mapper.map(scores, this.now());
  }
}

/** Who the voice belongs to, probably. */
export interface SpeakerIdentity {
  readonly speakerId: string;
  readonly confidence: number;
  /** True when the voice matched nobody enrolled. NOT the same as low
   * confidence in a match: an unknown speaker is a fact, a weak match is a
   * doubt. */
  readonly isUnknown: boolean;
}

/**
 * Matches a voice against enrolled embeddings.
 *
 * ONLY EMBEDDINGS ARE HELD, never audio. An embedding cannot be played back,
 * which is the difference between a device that recognises a household and one
 * that has recorded it.
 */
export class OnnxSpeakerIdentityAdapter {
  /** Cosine similarity. Strict enough that similar voices in one household do
   * not cross over: mistaking one family member for another is worse than
   * asking. */
  static readonly THRESHOLD = 0.72;

  private readonly enrolled = new Map<string, number[]>();

  constructor(private readonly embed?: (audio: readonly number[]) => number[]) {}

  get isAvailable(): boolean {
    return this.embed !== undefined;
  }

  static cosine(a: readonly number[], b: readonly number[]): number {
    if (a.length === 0 || a.length !== b.length) return 0;
    let dot = 0;
    let na = 0;
    let nb = 0;
    for (let i = 0; i < a.length; i++) {
      dot += a[i] * b[i];
      na += a[i] * a[i];
      nb += b[i] * b[i];
    }
    return na === 0 || nb === 0 ? 0 : dot / (Math.sqrt(na) * Math.sqrt(nb));
  }

  /**
   * Averages SEVERAL samples into one template.
   *
   * One sample enrols the room and the microphone as much as the voice, and the
   * person then fails to be recognised anywhere else in the house.
   */
  enrol(speakerId: string, samples: readonly (readonly number[])[]): boolean {
    if (!this.embed || samples.length < 2) return false;
    const vectors = samples.map((s) => this.embed!(s));
    const width = Math.min(...vectors.map((v) => v.length));
    this.enrolled.set(
      speakerId,
      Array.from({ length: width }, (_, i) => vectors.reduce((n, v) => n + v[i], 0) / vectors.length),
    );
    return true;
  }

  identify(audio: readonly number[]): SpeakerIdentity {
    if (!this.embed || this.enrolled.size === 0) {
      return Object.freeze({ speakerId: "", confidence: 0, isUnknown: true });
    }
    const live = this.embed(audio);
    let bestId = "";
    let bestScore = 0;
    for (const [speakerId, template] of this.enrolled) {
      const score = OnnxSpeakerIdentityAdapter.cosine(live, template);
      if (score > bestScore) {
        bestId = speakerId;
        bestScore = score;
      }
    }
    return bestScore < OnnxSpeakerIdentityAdapter.THRESHOLD
      ? Object.freeze({ speakerId: "", confidence: bestScore, isUnknown: true })
      : Object.freeze({ speakerId: bestId, confidence: bestScore, isUnknown: false });
  }

  /** Enrolment must be undoable, or it is not consent. */
  forget(speakerId: string): boolean {
    return this.enrolled.delete(speakerId);
  }
}

/**
 * Affect from the voice itself, not the words.
 *
 * PROSODY ONLY - pace, pitch and energy. It never looks at what was said, which
 * is what lets it run without the transcript and without keeping one.
 */
export class OnnxSpeechEmotionSensor {
  private static readonly labels: Readonly<Record<string, AffectLabel>> = Object.freeze({
    neutral: AffectLabel.Calm,
    happy: AffectLabel.Engaged,
    angry: AffectLabel.Stressed,
    sad: AffectLabel.Low,
    tired: AffectLabel.Tired,
  });

  constructor(private readonly infer?: (audio: readonly number[]) => Record<string, number>) {}

  get isAvailable(): boolean {
    return this.infer !== undefined;
  }

  sense(audio: readonly number[], speechPresent = true, atMs = 0): AffectReading {
    // No speech means no reading. Inferring emotion from silence reads the
    // room's air conditioning.
    if (!this.infer || !speechPresent || audio.length === 0) {
      return affectReading(AffectLabel.Uncertain, 0, "voice", atMs);
    }
    const entries = Object.entries(this.infer(audio));
    if (entries.length === 0) return affectReading(AffectLabel.Uncertain, 0, "voice", atMs);
    const best = entries.reduce((a, b) => (b[1] > a[1] ? b : a));
    return affectReading(
      OnnxSpeechEmotionSensor.labels[best[0].toLowerCase()] ?? AffectLabel.Uncertain,
      best[1],
      "voice",
      atMs,
    );
  }
}

/**
 * How the companion sounds, for one person, on one device.
 *
 * A VOICE IS A CHOICE AND NOT AN IDENTITY. The same companion sounds different
 * on two devices if the person wanted that, and nothing about who they are
 * depends on it.
 */
export class NeuronVoice {
  constructor(
    readonly voiceId = "",
    readonly language = "",
    readonly rate = 1,
    readonly pitch = 1,
    /** What to fall back to when the chosen voice is not installed. Falling back
     * to silence is how a device becomes mute after a factory reset. */
    readonly fallbackVoiceId = "",
  ) {
    if (rate < 0.5 || rate > 2) throw new Error("a speaking rate outside 0.5-2.0 is not intelligible");
    if (pitch < 0.5 || pitch > 2) throw new Error("a pitch outside 0.5-2.0 is not a voice");
  }

  /** The chosen voice, the fallback, or the first installed - in that order.
   * Never empty when anything at all is installed. */
  resolve(installed: readonly string[]): string {
    for (const candidate of [this.voiceId, this.fallbackVoiceId]) {
      if (candidate && installed.includes(candidate)) return candidate;
    }
    return installed[0] ?? "";
  }
}

/**
 * Facts as subject-predicate-object, on disk.
 *
 * PARAMETERISED, ALWAYS. Every value here came from something somebody said,
 * and a graph built by concatenating strings into SQL is a graph anybody can
 * rewrite by saying the right sentence.
 */
export class SqliteKnowledgeGraph {
  static readonly SCHEMA = Object.freeze([
    "CREATE TABLE IF NOT EXISTS facts (" +
      " subject TEXT NOT NULL, predicate TEXT NOT NULL, object TEXT NOT NULL," +
      " confidence REAL NOT NULL DEFAULT 1.0, at TEXT NOT NULL," +
      " PRIMARY KEY (subject, predicate, object))",
    // An index on the OBJECT as well as the subject: "who works at Circle" is
    // asked as often as "where does she work", and without it the second is a
    // full scan.
    "CREATE INDEX IF NOT EXISTS facts_object ON facts (object)",
  ]);

  constructor(private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][]) {}

  initialise(): boolean {
    if (!this.execute) return false;
    for (const statement of SqliteKnowledgeGraph.SCHEMA) this.execute(statement, []);
    return true;
  }

  assertFact(subject: string, predicate: string, object: string, confidence = 1, atIso = ""): boolean {
    if (!this.execute || !subject || !predicate || !object) return false;
    this.execute(
      "INSERT OR REPLACE INTO facts (subject, predicate, object, confidence, at) VALUES (?, ?, ?, ?, ?)",
      [subject, predicate, object, Math.max(0, Math.min(1, confidence)), atIso],
    );
    return true;
  }

  about(subject: string): unknown[][] {
    if (!this.execute) return [];
    return this.execute(
      "SELECT predicate, object, confidence FROM facts WHERE subject = ? ORDER BY confidence DESC",
      [subject],
    );
  }

  /** Forgetting everything about somebody has to be ONE call. A person who asks
   * to be forgotten should not depend on the caller enumerating predicates
   * correctly. */
  forget(subject: string): boolean {
    if (!this.execute) return false;
    this.execute("DELETE FROM facts WHERE subject = ? OR object = ?", [subject, subject]);
    return true;
  }
}

/**
 * Passages plus the links between them.
 *
 * THE LINKS ARE THE POINT. Retrieval by similarity alone returns whatever is
 * phrased like the question; following links from what matched returns what is
 * actually related to it, which is how a recall answers about a person rather
 * than about a wording.
 */
export class SqliteHippoRagStore {
  static readonly SCHEMA = Object.freeze([
    "CREATE TABLE IF NOT EXISTS passages (id TEXT PRIMARY KEY, text TEXT NOT NULL, at TEXT NOT NULL)",
    "CREATE TABLE IF NOT EXISTS links (" +
      " from_id TEXT NOT NULL, to_id TEXT NOT NULL, weight REAL NOT NULL," +
      " PRIMARY KEY (from_id, to_id))",
  ]);

  constructor(private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][]) {}

  initialise(): boolean {
    if (!this.execute) return false;
    for (const statement of SqliteHippoRagStore.SCHEMA) this.execute(statement, []);
    return true;
  }

  add(passageId: string, text: string, atIso = ""): boolean {
    if (!this.execute || !passageId) return false;
    this.execute("INSERT OR REPLACE INTO passages (id, text, at) VALUES (?, ?, ?)", [
      passageId, text, atIso,
    ]);
    return true;
  }

  /**
   * Links are DIRECTED and stored once each way by the caller.
   *
   * Storing one direction and reading it both ways makes the weight mean two
   * different things, and a link that is strong one way is often weak the other
   * - a name recalls a meeting far better than a meeting recalls a name.
   */
  link(fromId: string, toId: string, weight = 1): boolean {
    if (!this.execute || fromId === toId) return false;
    this.execute("INSERT OR REPLACE INTO links (from_id, to_id, weight) VALUES (?, ?, ?)", [
      fromId, toId, weight,
    ]);
    return true;
  }

  neighbours(passageId: string, limit = 8): unknown[][] {
    if (!this.execute) return [];
    return this.execute(
      "SELECT to_id, weight FROM links WHERE from_id = ? ORDER BY weight DESC LIMIT ?",
      [passageId, Math.max(1, limit)],
    );
  }
}

/**
 * Turning what was remembered into something worth saying.
 *
 * THE HARD PART IS LEAVING THINGS OUT. A companion that recites everything it
 * knows about somebody every time they speak is unusable and slightly
 * frightening; one that mentions the right single thing is the whole product.
 */
export class CompanionRecallExtensions {
  /** At most this many remembered items reach a prompt. Small on purpose - more
   * context is not more relevance. */
  static readonly MAX_ITEMS = 5;

  /**
   * Ranked by stored strength AND overlap with what was asked.
   *
   * Strength alone surfaces the same favourite fact forever; overlap alone
   * surfaces whatever happens to share a word.
   */
  static rank(
    items: readonly { text: string; strength: number }[],
    queryTerms: readonly string[],
  ): { text: string; score: number }[] {
    const terms = new Set(queryTerms.filter((t) => t.length > 2).map((t) => t.toLowerCase()));
    return items
      .map(({ text, strength }) => {
        const words = new Set(
          text.split(/\s+/).map((w) => w.replace(/[.,!?]/g, "").toLowerCase()),
        );
        let shared = 0;
        for (const t of terms) if (words.has(t)) shared += 1;
        const overlap = terms.size > 0 ? shared / terms.size : 0;
        return { text, score: strength * 0.5 + overlap * 0.5 };
      })
      .sort((a, b) => b.score - a.score);
  }

  /**
   * Returns EMPTY when nothing clears the floor.
   *
   * An empty string rather than a heading with nothing under it: a prompt that
   * says "what I remember:" followed by nothing tells the model there is
   * nothing to remember about this person, which is worse than saying nothing.
   */
  static toPrompt(
    items: readonly { text: string; strength: number }[],
    queryTerms: readonly string[] = [],
    floor = 0.25,
  ): string {
    const ranked = CompanionRecallExtensions.rank(items, queryTerms)
      .filter((r) => r.score >= floor)
      .slice(0, CompanionRecallExtensions.MAX_ITEMS);
    if (ranked.length === 0) return "";
    return `worth remembering:\n${ranked.map((r) => `- ${r.text}`).join("\n")}`;
  }
}

/** Wires the companion's sensors. */
export class CompanionServiceCollectionExtensions {
  private readonly registered = new Map<string, unknown>();

  add(name: string, service: unknown): CompanionServiceCollectionExtensions {
    this.registered.set(name, service);
    return this;
  }

  get(name: string): unknown {
    return this.registered.get(name);
  }

  build(): Readonly<Record<string, unknown>> {
    return Object.freeze(Object.fromEntries(this.registered));
  }
}

// The C# spellings, kept so the two trees line up.
export type ICalendarAdapter = CalendarAdapter;
export type IContactsAdapter = ContactsAdapter;
export type IEmailAdapter = EmailAdapter;
export type IPluginsRootResolver = PluginsRootResolver;
export type IWorkspacePathProvider = WorkspacePathProvider;
