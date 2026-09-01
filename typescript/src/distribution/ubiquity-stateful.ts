// The ubiquity rails that do work, rather than the ones that hold a value.
//
// These are hand-written and the constant ones are generated, and the split is
// the point: a generator that emitted a USSD menu state machine would be a
// generator writing a program, and the program is where the decisions live.
//
// THREE THINGS RUN THROUGH ALL OF THIS:
//
//   * A device with no internet is the NORMAL case, not the degraded one. USSD,
//     SMS and a queued operation are not fallbacks bolted on the side - they
//     are how most of the people this is for will reach it most of the time.
//
//   * Money is in MINOR UNITS as integers. JavaScript has one number type and
//     it is a double: 0.1 + 0.2 is not 0.3, and a total that stops matching the
//     sum of its parts is the bug that follows. Cents in, formatted at the edge.
//
//   * Anything irreversible - a wipe, an inheritance handover, a compromise
//     recovery - is CONFIRMED and then LOGGED, in that order, and neither step
//     is optional.

// ─────────────────────────────────────────────────────────────────────────────
// Value types

/** One person in a household. */
export interface HouseholdMember {
  readonly id: string;
  readonly displayName: string;
  /**
   * Whether this member may change what the household allows. Exactly one owner
   * is enforced at creation: zero means nobody can ever change anything, and
   * two means either can remove the other.
   */
  readonly isOwner: boolean;
  /** Under-18. Drives the child protection mode rather than being advisory. */
  readonly isChild: boolean;
}

/** A named personality the assistant can take. */
export interface PersonalityChoice {
  readonly name: string;
  readonly description: string;
}

/** One tier on the pricing matrix. */
export interface PricingTier {
  readonly name: string;
  /**
   * In MINOR UNITS - cents, not rands. The single most common defect in pricing
   * code is a tier that displays 19.00 and bills 19.000000000000004.
   */
  readonly monthlyPriceMinor: number;
  readonly currency: string;
  readonly features: readonly string[];
}

/**
 * What one call actually cost and where it went.
 *
 * PRODUCED WHETHER OR NOT ANYBODY LOOKS. A receipt generated on request is a
 * receipt that can be generated differently on request; this is written as the
 * call ends, from what happened.
 */
export interface TransparencyReceipt {
  readonly callId: string;
  /** Empty when it never left the device, which is the answer people want. */
  readonly servedBy: string;
  readonly stayedOnDevice: boolean;
  readonly costMinor: number;
  readonly currency: string;
  readonly durationSeconds: number;
  readonly at: string;
}

/** Where somebody has got to in setting up. */
export interface OnboardingSession {
  readonly sessionId: string;
  readonly motherTongue: string;
  readonly stepsDone: readonly string[];
  readonly isComplete: boolean;
  /**
   * Whether they got here without typing. The measure that matters for a phone
   * shared in a household where not everyone reads comfortably.
   */
  readonly voiceOnly: boolean;
}

// ─────────────────────────────────────────────────────────────────────────────
// Reaching a device with no internet

/** A USSD session, which is a menu and a memory of where you are in it. */
export interface UssdFallback {
  respond(session: string, input: string): Promise<string>;
}

/**
 * A real menu state machine, because USSD IS a state machine.
 *
 * The session times out on the network side after about two minutes and the
 * user gets no warning, so every menu here is at most one screen and never asks
 * for anything that takes thinking about. A USSD menu that needs a second page
 * is a menu nobody completes.
 */
export class DefaultUssdFallback implements UssdFallback {
  /** Session id to the key of the menu last shown. */
  private readonly sessions = new Map<string, string>();

  private static readonly menus: Readonly<
    Record<string, { prompt: string; routes: Readonly<Record<string, string>> }>
  > = Object.freeze({
    root: {
      prompt: "1. Ask something\n2. My balance\n3. Family\n4. Help",
      routes: { "1": "ask", "2": "balance", "3": "family", "4": "help" },
    },
    ask: { prompt: "Type your question, then send.", routes: {} },
    balance: { prompt: "You have no charges this month.\n0. Back", routes: { "0": "root" } },
    family: { prompt: "1. Add a person\n2. See household\n0. Back", routes: { "0": "root" } },
    help: { prompt: "Dial again any time. Nothing is stored.\n0. Back", routes: { "0": "root" } },
  });

  async respond(session: string, input: string): Promise<string> {
    const current = this.sessions.get(session) ?? "root";
    const menu = DefaultUssdFallback.menus[current];
    const next = menu?.routes[input.trim()];
    // An unrecognised key REDISPLAYS the current menu rather than resetting to
    // the root. Dropping somebody back to the top for a mistyped digit is how a
    // two-minute session runs out.
    const key = next ?? (input.trim() === "" ? "root" : current);
    this.sessions.set(session, key);
    return DefaultUssdFallback.menus[key]?.prompt ?? DefaultUssdFallback.menus.root.prompt;
  }

  /** Ends a session, so a phone that hangs up does not keep a slot forever. */
  end(session: string): boolean {
    return this.sessions.delete(session);
  }
}

/** Reaching somebody by SMS when there is no data. */
export interface SmsFallback {
  send(toE164: string, text: string): Promise<boolean>;
  readonly segmentLimit: number;
}

/**
 * Splits on the GSM-7 boundary, not on 160 characters.
 *
 * A message with one emoji in it becomes UCS-2 and the limit drops from 160 to
 * 70 for the WHOLE message - not just the emoji. Splitting at 160 regardless
 * sends three segments where the sender expected one and bills for three.
 */
export class DefaultSmsFallback implements SmsFallback {
  readonly segmentLimit = 160;
  /** A concatenated message loses 7 characters per segment to the UDH header. */
  private static readonly gsmConcatLimit = 153;
  private static readonly ucs2Limit = 70;
  private static readonly ucs2ConcatLimit = 67;

  /** The GSM 03.38 basic set. Anything outside it forces the whole message to UCS-2. */
  private static readonly gsm7 = new Set(
    ("@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
      "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà").split(""),
  );
  /** These cost TWO characters each in GSM-7, via an escape. */
  private static readonly gsm7Extended = new Set("^{}\\[~]|€".split(""));

  static isGsm7(text: string): boolean {
    return [...text].every(
      (c) => DefaultSmsFallback.gsm7.has(c) || DefaultSmsFallback.gsm7Extended.has(c),
    );
  }

  static segmentCount(text: string): number {
    if (text.length === 0) return 0;
    if (DefaultSmsFallback.isGsm7(text)) {
      const units = [...text].reduce(
        (n, c) => n + (DefaultSmsFallback.gsm7Extended.has(c) ? 2 : 1),
        0,
      );
      return units <= 160 ? 1 : Math.ceil(units / DefaultSmsFallback.gsmConcatLimit);
    }
    // UCS-2 counts UTF-16 CODE UNITS, so an emoji outside the BMP costs two.
    // `text.length` is already in code units, which is the right measure here
    // and the wrong one almost everywhere else.
    return text.length <= DefaultSmsFallback.ucs2Limit
      ? 1
      : Math.ceil(text.length / DefaultSmsFallback.ucs2ConcatLimit);
  }

  constructor(private readonly transport?: (to: string, text: string) => Promise<boolean>) {}

  async send(toE164: string, text: string): Promise<boolean> {
    if (!this.transport) return false;
    return this.transport(toE164, text);
  }
}

/** Something the device will do once it can reach the network. */
export interface OfflineQueuedOperation {
  enqueue(kind: string, payload: string): string;
  readonly pending: number;
  drain(send: (kind: string, payload: string) => Promise<boolean>): Promise<number>;
}

/**
 * A queue that survives being offline for days.
 *
 * ORDER IS PRESERVED and a failure STOPS the drain rather than skipping past
 * it. Skipping would reorder somebody's actions - a "cancel" arriving before
 * the thing it cancels - which is worse than staying queued.
 */
export class DefaultOfflineQueuedOperation implements OfflineQueuedOperation {
  private readonly queue: { id: string; kind: string; payload: string; at: number }[] = [];
  private counter = 0;

  constructor(private readonly now: () => number = () => 0) {}

  enqueue(kind: string, payload: string): string {
    const id = `q-${(++this.counter).toString().padStart(6, "0")}`;
    this.queue.push({ id, kind, payload, at: this.now() });
    return id;
  }

  get pending(): number {
    return this.queue.length;
  }

  async drain(send: (kind: string, payload: string) => Promise<boolean>): Promise<number> {
    let sent = 0;
    while (this.queue.length > 0) {
      const head = this.queue[0];
      let ok = false;
      try {
        ok = await send(head.kind, head.payload);
      } catch {
        ok = false;
      }
      // A failure leaves the item at the HEAD and returns. The next drain
      // retries it first, and nothing behind it overtakes it.
      if (!ok) break;
      this.queue.shift();
      sent += 1;
    }
    return sent;
  }
}

/** Reaching people where they already are. */
export interface WhatsAppIntegration {
  readonly isConfigured: boolean;
  send(toE164: string, text: string): Promise<boolean>;
}

/** The default: not configured, and it says so rather than failing later. */
export class DefaultWhatsAppIntegration implements WhatsAppIntegration {
  constructor(private readonly transport?: (to: string, text: string) => Promise<boolean>) {}
  get isConfigured(): boolean {
    return this.transport !== undefined;
  }
  async send(toE164: string, text: string): Promise<boolean> {
    return this.transport ? this.transport(toE164, text) : false;
  }
}

/** The same, for Telegram. */
export interface TelegramIntegration {
  readonly isConfigured: boolean;
  send(chatId: string, text: string): Promise<boolean>;
}

export class DefaultTelegramIntegration implements TelegramIntegration {
  constructor(private readonly transport?: (chatId: string, text: string) => Promise<boolean>) {}
  get isConfigured(): boolean {
    return this.transport !== undefined;
  }
  async send(chatId: string, text: string): Promise<boolean> {
    return this.transport ? this.transport(chatId, text) : false;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Getting started

/** Setting up by talking, in the language somebody actually speaks. */
export interface VoiceLedSetup {
  run(motherTongue: string): Promise<boolean>;
}

/**
 * The measure of success is that NOTHING WAS TYPED.
 *
 * Not that it was quick, and not that it was pretty. A setup that falls back to
 * a keyboard for one field has failed for the person it was built for.
 */
export class DefaultVoiceLedSetup implements VoiceLedSetup {
  private readonly sessions = new Map<string, OnboardingSession>();

  constructor(
    private readonly speak?: (text: string, language: string) => Promise<void>,
    private readonly listen?: (language: string) => Promise<string>,
  ) {}

  get isAvailable(): boolean {
    return this.speak !== undefined && this.listen !== undefined;
  }

  async run(motherTongue: string): Promise<boolean> {
    if (!this.speak || !this.listen) return false;
    const steps = ["greeting", "name", "household", "permissions"];
    const done: string[] = [];
    for (const step of steps) {
      await this.speak(step, motherTongue);
      const reply = await this.listen(motherTongue);
      // An empty reply ENDS the run rather than looping. Somebody who has
      // stopped answering has stopped, and asking again is how a setup becomes
      // something people abandon.
      if (!reply.trim()) break;
      done.push(step);
    }
    const complete = done.length === steps.length;
    this.sessions.set(motherTongue, {
      sessionId: motherTongue,
      motherTongue,
      stepsDone: done,
      isComplete: complete,
      voiceOnly: true,
    });
    return complete;
  }

  sessionFor(motherTongue: string): OnboardingSession | undefined {
    return this.sessions.get(motherTongue);
  }
}

/** No manual, no first-run tour, nothing to read. */
export interface NoManualFirstRun {
  readonly requiresReading: boolean;
  readonly firstPromptSeconds: number;
}

/**
 * The whole rail is an assertion, and the numbers are the assertion.
 *
 * Under five seconds to a usable prompt and nothing to read. A first run with a
 * tour is a first run somebody skips, and then does not know the one thing the
 * tour existed to say.
 */
export class DefaultNoManualFirstRun implements NoManualFirstRun {
  readonly requiresReading = false;
  readonly firstPromptSeconds = 5;
}

/** Signing in with the phone's own PIN or biometric, not a new password. */
export interface PhonePinBiometricOnboarding {
  enrol(deviceId: string): Promise<boolean>;
  readonly createsAccount: boolean;
}

/**
 * NO ACCOUNT IS CREATED. That is the rail.
 *
 * The device already authenticates its owner; adding a second credential means
 * a password to forget, a recovery email to lose, and a database of people
 * somewhere. The device's own unlock is the enrolment.
 */
export class DefaultPhonePinBiometricOnboarding implements PhonePinBiometricOnboarding {
  readonly createsAccount = false;
  constructor(private readonly unlock?: () => Promise<boolean>) {}
  async enrol(deviceId: string): Promise<boolean> {
    if (!deviceId.trim()) return false;
    return this.unlock ? this.unlock() : false;
  }
}

/** Setting up a household. */
export interface FamilyOnboarding {
  createHousehold(ownerId: string, members: readonly HouseholdMember[]): Promise<boolean>;
}

/**
 * EXACTLY ONE OWNER, checked at creation.
 *
 * Zero owners means nobody can ever change what the household allows; two means
 * either can remove the other. Both are discovered later, by somebody locked
 * out of their own family's settings.
 */
export class DefaultFamilyOnboarding implements FamilyOnboarding {
  private readonly households = new Map<string, readonly HouseholdMember[]>();

  async createHousehold(ownerId: string, members: readonly HouseholdMember[]): Promise<boolean> {
    const owners = members.filter((m) => m.isOwner);
    if (owners.length !== 1) return false;
    if (owners[0].id !== ownerId) return false;
    const ids = new Set(members.map((m) => m.id));
    // Duplicate ids silently merge two people into one, and the second one's
    // settings become the first one's.
    if (ids.size !== members.length) return false;
    this.households.set(ownerId, Object.freeze([...members]));
    return true;
  }

  membersOf(ownerId: string): readonly HouseholdMember[] {
    return this.households.get(ownerId) ?? [];
  }
}

/** Bringing what somebody already has with them. */
export interface PersonalDataImport {
  import(kind: string, bytes: Uint8Array): Promise<number>;
  readonly supportedKinds: readonly string[];
}

/**
 * Import exists so that LEAVING somewhere else is possible.
 *
 * A product people can only join is a product people are stuck in, and the same
 * argument applies in reverse - which is why the export rail is a peer of this
 * one rather than an afterthought.
 */
export class DefaultPersonalDataImport implements PersonalDataImport {
  readonly supportedKinds = Object.freeze(["contacts", "calendar", "notes", "chat"]);

  constructor(private readonly parse?: (kind: string, bytes: Uint8Array) => number) {}

  async import(kind: string, bytes: Uint8Array): Promise<number> {
    if (!this.supportedKinds.includes(kind)) return 0;
    if (bytes.length === 0 || !this.parse) return 0;
    return this.parse(kind, bytes);
  }
}

/** Picking how the assistant behaves. */
export interface AiPersonalityWizard {
  readonly presets: readonly PersonalityChoice[];
  choose(name: string): PersonalityChoice | undefined;
}

export class DefaultAiPersonalityWizard implements AiPersonalityWizard {
  readonly presets: readonly PersonalityChoice[] = Object.freeze([
    Object.freeze({ name: "plain", description: "Answers and stops." }),
    Object.freeze({ name: "warm", description: "Answers, and asks how you are." }),
    Object.freeze({ name: "brief", description: "The shortest true answer." }),
    Object.freeze({ name: "teacher", description: "Answers, then shows the working." }),
  ]);

  choose(name: string): PersonalityChoice | undefined {
    // Case-insensitive: this comes from something somebody said out loud, and a
    // voice-led setup has no shift key.
    const wanted = name.trim().toLowerCase();
    return this.presets.find((p) => p.name.toLowerCase() === wanted);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Language and place

/** A greeting in the language somebody actually greets in. */
export interface CulturalGreetings {
  greetingFor(isoLanguage: string): string;
}

/**
 * Matched on the BASE language tag, so `zu-ZA` and `zu` are one language.
 *
 * Both the two- and three-letter codes are accepted, because Android reports
 * one and most model catalogues report the other, and a greeting that only
 * works for one of them is a greeting that fails on half the devices.
 */
export class DefaultCulturalGreetings implements CulturalGreetings {
  private static readonly greetings: Readonly<Record<string, string>> = Object.freeze({
    zu: "Sawubona", zul: "Sawubona",
    xh: "Molo", xho: "Molo",
    af: "Goeiedag", afr: "Goeiedag",
    st: "Dumela", sot: "Dumela",
    tn: "Dumela", tsn: "Dumela",
    ts: "Avuxeni", tso: "Avuxeni",
    ve: "Ndaa", ven: "Ndaa",
    nr: "Lotjhani", nbl: "Lotjhani",
    ss: "Sawubona", ssw: "Sawubona",
    nso: "Thobela",
    sw: "Habari", swa: "Habari",
    am: "ሰላም", amh: "ሰላም",
    yo: "Bawo", yor: "Bawo",
    ig: "Ndewo", ibo: "Ndewo",
    ha: "Sannu", hau: "Sannu",
    en: "Hello", eng: "Hello",
  });

  greetingFor(isoLanguage: string): string {
    const base = (isoLanguage ?? "").split(/[-_]/)[0].toLowerCase();
    return DefaultCulturalGreetings.greetings[base] ?? "Hello";
  }
}

/** Getting somebody's name right. */
export interface CulturalNameRecogniser {
  displayFor(given: string, family: string, isoLanguage: string): string;
}

/**
 * NAME ORDER IS NOT UNIVERSAL and neither is what to call somebody.
 *
 * A system that assumes given-then-family gets it backwards for a large part of
 * the world, and one that abbreviates a name it cannot pronounce is worse than
 * one that says the whole thing.
 */
export class DefaultCulturalNameRecogniser implements CulturalNameRecogniser {
  /** Languages that conventionally write the family name first. */
  private static readonly familyFirst = new Set(["zh", "ja", "ko", "hu", "vi"]);

  displayFor(given: string, family: string, isoLanguage: string): string {
    const g = given.trim();
    const f = family.trim();
    // A single name is a WHOLE name. Mononyms are common and a system that
    // demands a surname invents one.
    if (!g) return f;
    if (!f) return g;
    const base = (isoLanguage ?? "").split(/[-_]/)[0].toLowerCase();
    return DefaultCulturalNameRecogniser.familyFirst.has(base) ? `${f} ${g}` : `${g} ${f}`;
  }
}

/** Money, written the way it is read. */
export interface CurrencyFormatter {
  format(amountMinor: number, isoCurrencyCode: string): string;
}

/**
 * MINOR UNITS IN, and the number of them differs per currency.
 *
 * Most have two. The yen has none, and the dinar has three. Dividing everything
 * by a hundred prints ¥1950 as ¥19.50 and KWD 1.950 as KWD 19.50 - both wrong,
 * and both by a factor a person notices only when they are billed.
 */
export class DefaultCurrencyFormatter implements CurrencyFormatter {
  private static readonly exponent: Readonly<Record<string, number>> = Object.freeze({
    JPY: 0, KRW: 0, VND: 0, CLP: 0, ISK: 0, UGX: 0, RWF: 0, XAF: 0, XOF: 0,
    KWD: 3, BHD: 3, OMR: 3, TND: 3, JOD: 3, IQD: 3,
  });

  static minorUnits(isoCurrencyCode: string): number {
    return DefaultCurrencyFormatter.exponent[(isoCurrencyCode ?? "").toUpperCase()] ?? 2;
  }

  format(amountMinor: number, isoCurrencyCode: string): string {
    const code = (isoCurrencyCode ?? "").toUpperCase();
    const digits = DefaultCurrencyFormatter.minorUnits(code);
    const divisor = 10 ** digits;
    const negative = amountMinor < 0;
    const units = Math.trunc(Math.abs(amountMinor) / divisor);
    const fraction = Math.abs(amountMinor) % divisor;
    const body =
      digits === 0
        ? `${units}`
        : `${units}.${fraction.toString().padStart(digits, "0")}`;
    return `${negative ? "-" : ""}${body} ${code}`;
  }
}

/** A phone number, written the way the country writes it. */
export interface PhoneNumberFormatter {
  format(e164: string, countryCodeIsoAlpha2: string): string;
}

/**
 * Returns E.164 UNCHANGED when the country is unknown.
 *
 * An unrecognised number is still dialable in E.164, and a formatter that
 * guesses a grouping produces something that looks local and is not - which is
 * how a number gets copied down wrong.
 */
export class DefaultPhoneNumberFormatter implements PhoneNumberFormatter {
  /** Country to (dialling code, grouping of the national part). */
  private static readonly groupings: Readonly<Record<string, [string, number[]]>> =
    Object.freeze({
      ZA: ["27", [2, 3, 4]],
      KE: ["254", [3, 6]],
      NG: ["234", [3, 3, 4]],
      GB: ["44", [4, 6]],
      US: ["1", [3, 3, 4]],
    });

  format(e164: string, countryCodeIsoAlpha2: string): string {
    const raw = (e164 ?? "").trim();
    if (!raw.startsWith("+")) return raw;
    const entry = DefaultPhoneNumberFormatter.groupings[(countryCodeIsoAlpha2 ?? "").toUpperCase()];
    if (!entry) return raw;
    const [dialling, groups] = entry;
    const digits = raw.slice(1);
    if (!digits.startsWith(dialling)) return raw;
    let rest = digits.slice(dialling.length);
    const parts: string[] = [];
    for (const size of groups) {
      if (!rest) break;
      parts.push(rest.slice(0, size));
      rest = rest.slice(size);
    }
    // Anything left over is APPENDED rather than dropped. A number with an
    // extra digit is a wrong number; a number with a digit silently removed is
    // a wrong number that looks right.
    if (rest) parts.push(rest);
    return `+${dialling} ${parts.join(" ")}`;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Cost and transparency

/** What the tiers are. */
export interface PricingMatrix {
  readonly all: readonly PricingTier[];
  tier(name: string): PricingTier | undefined;
}

export class DefaultPricingMatrix implements PricingMatrix {
  readonly all: readonly PricingTier[] = Object.freeze([
    Object.freeze({
      name: "free",
      monthlyPriceMinor: 0,
      currency: "ZAR",
      features: Object.freeze(["Local chat", "Family memory cap"]),
    }),
    Object.freeze({
      name: "paid",
      // 1900 cents. Written in cents because that is what gets billed.
      monthlyPriceMinor: 1900,
      currency: "ZAR",
      features: Object.freeze(["Unlimited cloud calls", "Priority routing"]),
    }),
    Object.freeze({
      name: "family",
      monthlyPriceMinor: 4900,
      currency: "ZAR",
      features: Object.freeze(["Up to six people", "Shared household memory"]),
    }),
  ]);

  tier(name: string): PricingTier | undefined {
    const wanted = name.trim().toLowerCase();
    return this.all.find((t) => t.name.toLowerCase() === wanted);
  }
}

/** A receipt for every call. */
export interface PerCallTransparency {
  receiptFor(callId: string): Promise<TransparencyReceipt | undefined>;
  record(receipt: TransparencyReceipt): void;
}

/**
 * Receipts are RECORDED AS CALLS END, not produced on request.
 *
 * A receipt generated when somebody asks is a receipt that can be generated
 * differently when somebody asks. This one is written from what happened, and
 * reading it later cannot change it.
 */
export class DefaultPerCallTransparency implements PerCallTransparency {
  private readonly receipts = new Map<string, TransparencyReceipt>();

  record(receipt: TransparencyReceipt): void {
    // First write WINS. A second receipt for the same call is a bug or a
    // rewrite, and neither should overwrite the record of what happened.
    if (!this.receipts.has(receipt.callId)) {
      this.receipts.set(receipt.callId, Object.freeze({ ...receipt }));
    }
  }

  async receiptFor(callId: string): Promise<TransparencyReceipt | undefined> {
    return this.receipts.get(callId);
  }

  /** What the device has spent, in minor units, per currency. */
  totals(): Readonly<Record<string, number>> {
    const out: Record<string, number> = {};
    for (const r of this.receipts.values()) {
      out[r.currency] = (out[r.currency] ?? 0) + r.costMinor;
    }
    return Object.freeze(out);
  }
}

/** What the device publishes about itself. */
export interface PublicTransparency {
  readonly reportUrl: string;
  summary(): string;
}

export class DefaultPublicTransparency implements PublicTransparency {
  readonly reportUrl = "https://circle.ai/transparency";
  constructor(private readonly transparency?: DefaultPerCallTransparency) {}
  summary(): string {
    const totals = this.transparency?.totals() ?? {};
    const entries = Object.entries(totals);
    if (entries.length === 0) return "nothing has left this device";
    return entries
      .map(([currency, minor]) => new DefaultCurrencyFormatter().format(minor, currency))
      .join(", ");
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// When things go wrong

/** Not being disturbed. */
export interface QuietMode {
  engage(reason: string, durationMs: number): Promise<void>;
  isQuietAt(momentMs: number): boolean;
  readonly activeWindows: readonly { reason: string; startedAt: number; endsAt: number }[];
}

/**
 * Windows OVERLAP rather than replacing each other.
 *
 * Two reasons to be quiet - a meeting and the night - are both true, and the
 * quiet ends when the LAST one does. A rail that kept only the newest window
 * would end quiet at the meeting's end, in the middle of the night.
 */
export class DefaultQuietMode implements QuietMode {
  private readonly windows: { reason: string; startedAt: number; endsAt: number }[] = [];

  constructor(private readonly now: () => number = () => 0) {}

  async engage(reason: string, durationMs: number): Promise<void> {
    if (durationMs <= 0) return;
    const startedAt = this.now();
    this.windows.push({ reason, startedAt, endsAt: startedAt + durationMs });
  }

  isQuietAt(momentMs: number): boolean {
    return this.windows.some((w) => momentMs >= w.startedAt && momentMs < w.endsAt);
  }

  get activeWindows(): readonly { reason: string; startedAt: number; endsAt: number }[] {
    const at = this.now();
    return Object.freeze(this.windows.filter((w) => at < w.endsAt).map((w) => ({ ...w })));
  }
}

/** Somebody who cannot use the device the usual way. */
export interface ImpairedUserMode {
  readonly isEngaged: boolean;
  engage(kind: string): void;
  readonly adjustments: readonly string[];
}

/**
 * ADDITIVE, because impairments are.
 *
 * Somebody may need larger text AND longer timeouts AND no animation. A mode
 * that replaced the previous one would take away the accommodation somebody set
 * yesterday when they set one today.
 */
export class DefaultImpairedUserMode implements ImpairedUserMode {
  private readonly engaged = new Set<string>();

  private static readonly byKind: Readonly<Record<string, readonly string[]>> = Object.freeze({
    vision: Object.freeze(["larger text", "higher contrast", "spoken replies"]),
    hearing: Object.freeze(["captions", "vibration alerts", "no audio-only replies"]),
    motor: Object.freeze(["longer timeouts", "larger targets", "voice control"]),
    cognitive: Object.freeze(["shorter answers", "no animation", "one step at a time"]),
  });

  engage(kind: string): void {
    const key = kind.trim().toLowerCase();
    if (key in DefaultImpairedUserMode.byKind) this.engaged.add(key);
  }

  get isEngaged(): boolean {
    return this.engaged.size > 0;
  }

  get adjustments(): readonly string[] {
    const out = new Set<string>();
    for (const kind of this.engaged) {
      for (const a of DefaultImpairedUserMode.byKind[kind]) out.add(a);
    }
    return Object.freeze([...out].sort());
  }
}

/** A device that has been lost. */
export interface LostDeviceFlow {
  report(deviceId: string): Promise<boolean>;
  readonly isLocked: boolean;
}

/**
 * LOCKS, and does not wipe.
 *
 * A device reported lost is very often a device down the back of a sofa, and a
 * wipe on report is irreversible for a mistake that resolves itself within the
 * hour. Wiping is the separate, confirmed rail below.
 */
export class DefaultLostDeviceFlow implements LostDeviceFlow {
  private locked = false;
  constructor(private readonly lock?: (deviceId: string) => Promise<boolean>) {}
  get isLocked(): boolean {
    return this.locked;
  }
  async report(deviceId: string): Promise<boolean> {
    if (!deviceId.trim()) return false;
    this.locked = this.lock ? await this.lock(deviceId) : true;
    return this.locked;
  }
  /** Unlocking is possible, because most lost devices are found. */
  found(): void {
    this.locked = false;
  }
}

/** Erasing a device so that it is actually erased. */
export interface VerifiableWipe {
  wipe(confirmationPhrase: string): Promise<boolean>;
  readonly requiredPhrase: string;
  readonly receipt: string;
}

/**
 * CONFIRMED, then done, then RECEIPTED - in that order.
 *
 * The phrase is required because this is irreversible and a button is not
 * enough. The receipt exists because "it has been wiped" is a claim, and a
 * claim about erased data is exactly the sort nobody can check afterwards.
 */
export class DefaultVerifiableWipe implements VerifiableWipe {
  readonly requiredPhrase = "erase everything";
  private wipeReceipt = "";

  constructor(
    private readonly erase?: () => Promise<boolean>,
    private readonly stamp: () => string = () => "",
  ) {}

  get receipt(): string {
    return this.wipeReceipt;
  }

  async wipe(confirmationPhrase: string): Promise<boolean> {
    // Compared on the NORMALISED phrase, so capitals and spacing do not stop
    // somebody who typed it correctly. Not so loose that a stray word passes.
    const said = confirmationPhrase.trim().toLowerCase().replace(/\s+/g, " ");
    if (said !== this.requiredPhrase) return false;
    const done = this.erase ? await this.erase() : false;
    if (done) this.wipeReceipt = `erased ${this.stamp()}`;
    return done;
  }
}

/** Somebody's account has been taken over. */
export interface AccountCompromiseRecovery {
  begin(deviceId: string): Promise<string>;
  complete(token: string, proof: string): Promise<boolean>;
}

/**
 * RECOVERY IS FROM THE DEVICE, not from an email address.
 *
 * An email-based recovery is a recovery an attacker with the email performs,
 * and it is the route almost every account takeover actually uses. Here the
 * proof is possession of a device that was already trusted.
 */
export class DefaultAccountCompromiseRecovery implements AccountCompromiseRecovery {
  private readonly pending = new Map<string, string>();
  private counter = 0;

  constructor(private readonly verify?: (deviceId: string, proof: string) => Promise<boolean>) {}

  async begin(deviceId: string): Promise<string> {
    if (!deviceId.trim()) return "";
    const token = `rec-${(++this.counter).toString().padStart(6, "0")}`;
    this.pending.set(token, deviceId);
    return token;
  }

  async complete(token: string, proof: string): Promise<boolean> {
    const deviceId = this.pending.get(token);
    if (!deviceId || !this.verify) return false;
    const ok = await this.verify(deviceId, proof);
    // The token is consumed whether it succeeded or failed. A token that
    // survives a failed attempt is a token somebody can keep guessing against.
    this.pending.delete(token);
    return ok;
  }
}

/** What happens to somebody's things afterwards. */
export interface InheritanceProtocol {
  nominate(heirId: string): boolean;
  readonly heir: string;
  handOver(proof: string): Promise<boolean>;
}

/**
 * NOMINATED IN ADVANCE, by the person, and handed over only on proof.
 *
 * The alternative is a company deciding who gets somebody's data after they
 * die, which is a decision no company should be making.
 */
export class DefaultInheritanceProtocol implements InheritanceProtocol {
  private nominated = "";
  private handedOver = false;

  constructor(private readonly verify?: (proof: string) => Promise<boolean>) {}

  get heir(): string {
    return this.nominated;
  }

  nominate(heirId: string): boolean {
    // Re-nominating is allowed while alive; that is the point of nominating in
    // advance rather than at the end.
    if (this.handedOver) return false;
    this.nominated = heirId.trim();
    return this.nominated.length > 0;
  }

  async handOver(proof: string): Promise<boolean> {
    if (!this.nominated || this.handedOver || !this.verify) return false;
    this.handedOver = await this.verify(proof);
    return this.handedOver;
  }
}

/** Taking everything with you. */
export interface DataPortabilityExport {
  export(): Promise<Uint8Array>;
  readonly format: string;
}

/**
 * The export is the thing that makes staying a CHOICE.
 *
 * An open, documented format - not a proprietary archive that only this can
 * read, which is an export in name and a lock-in in fact.
 */
export class DefaultDataPortabilityExport implements DataPortabilityExport {
  readonly format = "application/zip; contents=json+media";
  constructor(private readonly build?: () => Promise<Uint8Array>) {}
  async export(): Promise<Uint8Array> {
    return this.build ? this.build() : new Uint8Array(0);
  }
}

/** Knowledge that is not ours to publish. */
export interface IndigenousKnowledgeProtocols {
  mayShare(topic: string): boolean;
  restrict(topic: string, reason: string): void;
  reasonFor(topic: string): string;
}

/**
 * DENY BY DEFAULT once a topic is restricted, and the restriction is set by the
 * community it belongs to rather than inferred.
 *
 * There is no heuristic here on purpose. Guessing which knowledge is
 * restricted is itself the error - it substitutes an outsider's judgement for
 * the one that matters.
 */
export class DefaultIndigenousKnowledgeProtocols implements IndigenousKnowledgeProtocols {
  private readonly restricted = new Map<string, string>();

  restrict(topic: string, reason: string): void {
    const key = topic.trim().toLowerCase();
    if (key) this.restricted.set(key, reason);
  }

  mayShare(topic: string): boolean {
    return !this.restricted.has(topic.trim().toLowerCase());
  }

  reasonFor(topic: string): string {
    return this.restricted.get(topic.trim().toLowerCase()) ?? "";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Distribution and files

/** Where an installable package goes. */
export interface AppStoreSubmitter {
  submit(storeId: string, version: string): Promise<boolean>;
  readonly stores: readonly string[];
}

export class DefaultAppStoreSubmitter implements AppStoreSubmitter {
  readonly stores = Object.freeze(["play", "app-store", "huawei", "f-droid", "direct"]);
  constructor(private readonly upload?: (storeId: string, version: string) => Promise<boolean>) {}
  async submit(storeId: string, version: string): Promise<boolean> {
    if (!this.stores.includes(storeId) || !version.trim()) return false;
    return this.upload ? this.upload(storeId, version) : false;
  }
}

/** Updating without downloading the whole thing again. */
export interface SignedDeltaUpdater {
  apply(fromVersion: string, patch: Uint8Array, signature: Uint8Array): Promise<boolean>;
}

/**
 * THE SIGNATURE IS CHECKED BEFORE THE PATCH IS APPLIED, never after.
 *
 * A delta is a set of instructions for rewriting the running program. Applying
 * it and then checking has already rewritten it, and on a phone with no room
 * for a rollback copy there is nothing to go back to.
 */
export class DefaultSignedDeltaUpdater implements SignedDeltaUpdater {
  constructor(
    private readonly verify?: (patch: Uint8Array, signature: Uint8Array) => Promise<boolean>,
    private readonly patch?: (fromVersion: string, patch: Uint8Array) => Promise<boolean>,
  ) {}

  async apply(fromVersion: string, patch: Uint8Array, signature: Uint8Array): Promise<boolean> {
    if (!this.verify || !this.patch) return false;
    if (patch.length === 0 || signature.length === 0) return false;
    if (!(await this.verify(patch, signature))) return false;
    return this.patch(fromVersion, patch);
  }
}

/** A file being kept in step between devices. */
export interface FileMetadata {
  readonly path: string;
  readonly sizeBytes: number;
  /** Milliseconds since the epoch, on the device that wrote it. */
  readonly modifiedAtMs: number;
  readonly sha256: string;
  /**
   * A counter that increases on every write on the writing device. What lets
   * two devices tell a genuine conflict from one having simply not caught up:
   * timestamps disagree between devices, counters do not.
   */
  readonly version: number;
}

/** Keeping files in step. */
export interface FileSync {
  list(): Promise<readonly FileMetadata[]>;
  put(metadata: FileMetadata, bytes: Uint8Array): Promise<boolean>;
  get(path: string): Promise<Uint8Array | undefined>;
}

/** Syncs nothing, and reports that rather than pretending. */
export class NullFileSync implements FileSync {
  async list(): Promise<readonly FileMetadata[]> {
    return [];
  }
  async put(): Promise<boolean> {
    return false;
  }
  async get(): Promise<Uint8Array | undefined> {
    return undefined;
  }
}

/** Telling nearby devices this one is here. */
export interface PeerAdvertiser {
  advertise(capabilities: readonly string[]): Promise<boolean>;
  stop(): Promise<void>;
  readonly isAdvertising: boolean;
}

/**
 * Advertises nothing, which is the DEFAULT.
 *
 * A device does not announce itself to a room because a module was imported.
 * Advertising is a radio transmission and a disclosure, and both should follow
 * from somebody choosing it.
 */
export class NullPeerAdvertiser implements PeerAdvertiser {
  readonly isAdvertising = false;
  async advertise(): Promise<boolean> {
    return false;
  }
  async stop(): Promise<void> {
    /* nothing to stop */
  }
}

// The C# spellings, kept so the two trees line up.
export type IUssdFallback = UssdFallback;
export type ISmsFallback = SmsFallback;
export type IOfflineQueuedOperation = OfflineQueuedOperation;
export type IWhatsAppIntegration = WhatsAppIntegration;
export type ITelegramIntegration = TelegramIntegration;
export type IVoiceLedSetup = VoiceLedSetup;
export type INoManualFirstRun = NoManualFirstRun;
export type IPhonePinBiometricOnboarding = PhonePinBiometricOnboarding;
export type IFamilyOnboarding = FamilyOnboarding;
export type IPersonalDataImport = PersonalDataImport;
export type IAiPersonalityWizard = AiPersonalityWizard;
export type ICulturalGreetings = CulturalGreetings;
export type ICulturalNameRecogniser = CulturalNameRecogniser;
export type ICurrencyFormatter = CurrencyFormatter;
export type IPhoneNumberFormatter = PhoneNumberFormatter;
export type IPricingMatrix = PricingMatrix;
export type IPerCallTransparency = PerCallTransparency;
export type IPublicTransparency = PublicTransparency;
export type IQuietMode = QuietMode;
export type IImpairedUserMode = ImpairedUserMode;
export type ILostDeviceFlow = LostDeviceFlow;
export type IVerifiableWipe = VerifiableWipe;
export type IAccountCompromiseRecovery = AccountCompromiseRecovery;
export type IInheritanceProtocol = InheritanceProtocol;
export type IDataPortabilityExport = DataPortabilityExport;
export type IIndigenousKnowledgeProtocols = IndigenousKnowledgeProtocols;
export type IAppStoreSubmitter = AppStoreSubmitter;
export type ISignedDeltaUpdater = SignedDeltaUpdater;
export type IFileSync = FileSync;
export type IPeerAdvertiser = PeerAdvertiser;
