/**
 * memory/atoms
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// What the companion remembers, and what it lets go of.
//
// A MEMORY THAT ONLY GROWS IS NOT A MEMORY, it is a log. The interesting half
// of this file is forgetting: what fades, what does not, and why.
//
// THE ONE THAT READS BACKWARDS AND HAS TO BE STATED. `kindFloor` is the
// fraction of retrievability a kind KEEPS no matter how long it sits - a FLOOR,
// not a decay rate. Reading it as a rate and computing `1 - floor` gives a
// plain fact a floor of 1.0, so it never fades at all, and the store grows
// forever while reporting that forgetting works. That bug was found by RUNNING
// the curve rather than reading it, and this comment exists so it is not
// reintroduced by somebody who reads the name and not the formula.
//
// AND THE SECOND: a ruling never fades because it was DECIDED. A decision that
// quietly stops being offered is a decision made twice, and the second time it
// may go the other way.

// ─────────────────────────────────────────────────────────────────────────────
// What an atom is

/** What kind of thing is being remembered. */
export enum AtomKind {
  /** Something that is true. Fades if it is not used. */
  Fact = "fact",
  /** Something somebody decided. Never fades below its floor. */
  Ruling = "ruling",
  /** How somebody likes things. Fades slowly. */
  Preference = "preference",
  /** Who somebody is to somebody else. Holds as hard as a ruling. */
  Relationship = "relationship",
  /** Something that happened, with a time attached. */
  Episode = "episode",
  /** Something being worked towards. */
  Goal = "goal",
  /** A correction. Beats what it corrects, whatever the other's strength. */
  Correction = "correction",
}

/** How a decision turned out. */
export enum DecisionOutcome {
  /** Not yet known. The honest default and a real value. */
  Unknown = "unknown",
  Worked = "worked",
  DidNot = "did-not",
  /** Reversed later. Kept, because the reversal is the useful part. */
  Reversed = "reversed",
}

/** One thing remembered. */
export interface MemoryAtom {
  readonly id: string;
  readonly kind: AtomKind;
  readonly text: string;
  readonly createdAtMs: number;
  readonly lastRecalledAtMs: number;
  /**
   * How long this resists forgetting, in DAYS. Grows each time the atom is
   * recalled, which is the spacing effect: a thing remembered after a long gap
   * is remembered longer next time.
   */
  readonly stabilityDays: number;
  readonly recallCount: number;
  /** Which module or conversation it belongs to. Empty means general. */
  readonly folder: string;
  /** Where it came from, so a wrong memory can be traced to what said it. */
  readonly source: string;
  readonly outcome: DecisionOutcome;
}

/** The starting stability, in days. */
export const INITIAL_STABILITY_DAYS = 90;

export const memoryAtom = (partial: Partial<MemoryAtom> & { id: string; text: string }): MemoryAtom =>
  Object.freeze({
    id: partial.id,
    kind: partial.kind ?? AtomKind.Fact,
    text: partial.text,
    createdAtMs: partial.createdAtMs ?? 0,
    lastRecalledAtMs: partial.lastRecalledAtMs ?? partial.createdAtMs ?? 0,
    stabilityDays: partial.stabilityDays ?? INITIAL_STABILITY_DAYS,
    recallCount: partial.recallCount ?? 0,
    folder: partial.folder ?? "",
    source: partial.source ?? "",
    outcome: partial.outcome ?? DecisionOutcome.Unknown,
  });

/**
 * The forgetting curve.
 *
 * `retrievability = floor + (1 - floor) * exp(-days / stability)`
 *
 * An exponential rather than a step, because memory does not have a cliff -
 * something half-forgotten is still worth offering with less confidence, and a
 * threshold that deletes at a boundary loses it entirely the day before it
 * would have been useful.
 */
export class Forgetting {
  /**
   * The fraction each kind KEEPS forever. A FLOOR, NOT A DECAY RATE.
   *
   * A ruling keeps 0.40 and so can never fade below it; a relationship the
   * same; a preference keeps 0.20; everything else keeps nothing and decays
   * towards zero. Reading these as decay rates inverts the whole table.
   */
  private static readonly floors: Readonly<Map<string, number>> = Object.freeze({
    [AtomKind.Ruling]: 0.4,
    [AtomKind.Relationship]: 0.4,
    [AtomKind.Preference]: 0.2,
    [AtomKind.Correction]: 0.4,
  });

  /** Below this an atom is not offered. It is not deleted - see `shouldDrop`. */
  static readonly OFFER_THRESHOLD = 0.25;

  static kindFloor(kind: AtomKind): number {
    return Forgetting.floors[kind] ?? 0;
  }

  static retrievability(atom: MemoryAtom, nowMs: number): number {
    const elapsedDays = Math.max(0, (nowMs - atom.lastRecalledAtMs) / (24 * 60 * 60 * 1000));
    const stability = Math.max(1, atom.stabilityDays);
    const base = Math.exp(-elapsedDays / stability);
    const floor = Forgetting.kindFloor(atom.kind);
    return floor + (1 - floor) * base;
  }

  /**
   * Recalling STRENGTHENS, and by more when the gap was long.
   *
   * The spacing effect: something remembered after a long gap is remembered
   * longer next time, and something re-read immediately is not learned at all.
   * A flat multiplier would make a hundred rapid recalls as valuable as one
   * after a month, which is the opposite of true.
   */
  static strengthen(atom: MemoryAtom, nowMs: number): MemoryAtom {
    const r = Forgetting.retrievability(atom, nowMs);
    // The harder it was to retrieve, the more the recall is worth - so the
    // multiplier is largest when retrievability was LOW.
    const gain = 1 + 2 * (1 - r);
    return memoryAtom({
      ...atom,
      lastRecalledAtMs: nowMs,
      stabilityDays: Math.min(3650, atom.stabilityDays * gain),
      recallCount: atom.recallCount + 1,
    });
  }

  /**
   * Whether an atom may be REMOVED, which is a much higher bar than not
   * offering it.
   *
   * A ruling and a relationship are never dropped whatever their retrievability
   * says, because their floor guarantees they never reach zero - and a
   * correction is never dropped because it exists to override something else
   * that is still there.
   */
  static shouldDrop(atom: MemoryAtom, nowMs: number): boolean {
    if (Forgetting.kindFloor(atom.kind) > 0) return false;
    if (atom.kind === AtomKind.Goal) return false;
    // A YEAR below the threshold, not a moment. Something that dipped last week
    // and has not been needed since may still be needed tomorrow.
    const elapsedDays = (nowMs - atom.lastRecalledAtMs) / (24 * 60 * 60 * 1000);
    return Forgetting.retrievability(atom, nowMs) < 0.05 && elapsedDays > 365;
  }
}

/** How much a module's memory is kept. */
export enum MemoryRetention {
  /** Nothing is kept past the conversation. */
  None = "none",
  /** Kept for a session and then dropped. */
  Session = "session",
  /** Kept and allowed to fade. The normal setting. */
  Fading = "fading",
  /** Kept indefinitely. Only for things somebody explicitly pinned. */
  Pinned = "pinned",
}

/** Where an atom lives. */
export interface MemoryFolder {
  readonly name: string;
  readonly retention: MemoryRetention;
  /** A cap, so one talkative module cannot fill the store. Zero means no cap. */
  readonly maxAtoms: number;
}

export const memoryFolder = (
  name: string,
  retention: MemoryRetention = MemoryRetention.Fading,
  maxAtoms = 0,
): MemoryFolder => Object.freeze({ name, retention, maxAtoms });

// ─────────────────────────────────────────────────────────────────────────────
// Getting atoms out of what was said

/** Something that might be worth remembering. */
export interface AtomCandidate {
  readonly text: string;
  readonly kind: AtomKind;
  /** 0..1. Below the learner's floor it is discarded rather than stored
   * weakly - a store full of maybes is worse than a small store of facts. */
  readonly confidence: number;
  readonly source: string;
}

/** Pulls candidates out of text. */
export interface AtomExtractor {
  extract(text: string, source?: string): readonly AtomCandidate[];
}

/**
 * Finds the phrases that signal something worth keeping.
 *
 * RULES, NOT A MODEL, and deliberately so: this runs on every turn, and a model
 * call per turn is a battery cost per turn. The rules are conservative - they
 * miss things, and what they find is nearly always right, which is the correct
 * trade for something that writes to a person's memory.
 */
export class CueExtractor implements AtomExtractor {
  /** Phrases that introduce a decision. */
  private static readonly rulingCues = [
    "we decided", "let's go with", "from now on", "the rule is", "always",
    "never", "i've decided", "we agreed",
  ];
  /** Phrases that introduce a preference. */
  private static readonly preferenceCues = [
    "i prefer", "i like", "i don't like", "i hate", "i'd rather", "please always",
    "please don't",
  ];
  /** Phrases that introduce a correction. */
  private static readonly correctionCues = [
    "actually", "no, ", "that's wrong", "i meant", "correction", "not quite",
  ];
  /** Phrases that introduce a relationship. */
  private static readonly relationshipCues = [
    "my wife", "my husband", "my mother", "my father", "my son", "my daughter",
    "my brother", "my sister", "my boss", "my partner", "my friend",
  ];

  extract(text: string, source = ""): readonly AtomCandidate[] {
    const out: AtomCandidate[] = [];
    for (const sentence of text.split(/(?<=[.!?])\s+/)) {
      const lower = sentence.toLowerCase();
      const trimmed = sentence.trim();
      if (!trimmed) continue;

      // Order matters: a correction that also contains a preference cue is a
      // CORRECTION, because it is overriding something already stored.
      if (CueExtractor.correctionCues.some((c) => lower.startsWith(c) || lower.includes(` ${c}`))) {
        out.push(Object.freeze({ text: trimmed, kind: AtomKind.Correction, confidence: 0.75, source }));
        continue;
      }
      if (CueExtractor.rulingCues.some((c) => lower.includes(c))) {
        out.push(Object.freeze({ text: trimmed, kind: AtomKind.Ruling, confidence: 0.8, source }));
        continue;
      }
      if (CueExtractor.preferenceCues.some((c) => lower.includes(c))) {
        out.push(Object.freeze({ text: trimmed, kind: AtomKind.Preference, confidence: 0.7, source }));
        continue;
      }
      if (CueExtractor.relationshipCues.some((c) => lower.includes(c))) {
        out.push(Object.freeze({ text: trimmed, kind: AtomKind.Relationship, confidence: 0.7, source }));
      }
    }
    return Object.freeze(out);
  }
}

/** What a learning pass did. */
export interface LearnReport {
  readonly considered: number;
  readonly stored: number;
  readonly merged: number;
  readonly discarded: number;
  /** Why things were discarded, so a quiet learner can be diagnosed rather
   * than assumed broken. */
  readonly reasons: readonly string[];
}

/** Where atoms are kept. */
export interface AtomStore {
  put(atom: MemoryAtom): void;
  get(id: string): MemoryAtom | undefined;
  all(): readonly MemoryAtom[];
  remove(id: string): boolean;
}

/**
 * Turns candidates into stored atoms.
 *
 * IT MERGES RATHER THAN DUPLICATING. Somebody who says the same thing twice
 * should end up with one stronger memory, not two identical ones that both
 * surface in every recall.
 */
export class AtomLearner {
  /** Below this a candidate is discarded. A store full of maybes is worse than
   * a small store of facts. */
  static readonly CONFIDENCE_FLOOR = 0.6;

  private counter = 0;

  constructor(
    private readonly store: AtomStore,
    private readonly now: () => number = () => 0,
  ) {}

  /** Normalised for comparison: case, punctuation and spacing dropped. */
  private static key(text: string): string {
    return text
      .toLowerCase()
      .replace(/[^\p{L}\p{N}\s]/gu, "")
      .split(/\s+/)
      .filter(Boolean)
      .join(" ");
  }

  learn(candidates: readonly AtomCandidate[], folder = ""): LearnReport {
    let stored = 0;
    let merged = 0;
    let discarded = 0;
    const reasons: string[] = [];
    const nowMs = this.now();
    const existing = new Map(this.store.all().map((a) => [AtomLearner.key(a.text), a]));

    for (const candidate of candidates) {
      if (candidate.confidence < AtomLearner.CONFIDENCE_FLOOR) {
        discarded += 1;
        reasons.push(`"${candidate.text.slice(0, 40)}" was not certain enough`);
        continue;
      }
      const key = AtomLearner.key(candidate.text);
      const found = existing.get(key);
      if (found) {
        // Merging STRENGTHENS rather than replacing, so a repeated statement
        // becomes a firmer memory rather than a fresh weak one.
        this.store.put(Forgetting.strengthen(found, nowMs));
        merged += 1;
        continue;
      }
      this.counter += 1;
      const atom = memoryAtom({
        id: `a-${this.counter.toString().padStart(6, "0")}`,
        kind: candidate.kind,
        text: candidate.text,
        createdAtMs: nowMs,
        lastRecalledAtMs: nowMs,
        folder,
        source: candidate.source,
      });
      this.store.put(atom);
      existing.set(key, atom);
      stored += 1;
    }
    return Object.freeze({
      considered: candidates.length,
      stored,
      merged,
      discarded,
      reasons: Object.freeze(reasons),
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Getting atoms back

/** What is going on, for recall to work with. */
export interface Situation {
  readonly text: string;
  readonly folder: string;
  readonly atMs: number;
  /** Who is present, if known. Recall is narrowed to what is appropriate in
   * front of them - a household device is used in front of other people. */
  readonly presentSpeakerIds: readonly string[];
}

/** How much recall may spend. */
export interface RecallBudget {
  /** At most this many atoms reach a prompt. Small on purpose: more context is
   * not more relevance, and every extra atom competes for attention with what
   * was actually asked. */
  readonly maxAtoms: number;
  readonly maxCharacters: number;
  /** Below this retrievability an atom is not offered at all. */
  readonly floor: number;
}

export const recallBudget = (partial: Partial<RecallBudget> = {}): RecallBudget =>
  Object.freeze({
    maxAtoms: partial.maxAtoms ?? 5,
    maxCharacters: partial.maxCharacters ?? 800,
    floor: partial.floor ?? Forgetting.OFFER_THRESHOLD,
  });

/** What recall found. */
export interface RecallResult {
  readonly atoms: readonly MemoryAtom[];
  /** Ready to put in a prompt. EMPTY when nothing cleared the floor - a heading
   * with nothing under it tells a model there is nothing to remember about this
   * person, which is worse than saying nothing. */
  readonly promptText: string;
  readonly consideredCount: number;
}

/**
 * Chooses what to bring back.
 *
 * RANKED ON BOTH strength and relevance. Strength alone surfaces the same
 * favourite fact forever; relevance alone surfaces whatever happens to share a
 * word with the question.
 */
export class Recall {
  constructor(
    private readonly store: AtomStore,
    private readonly now: () => number = () => 0,
  ) {}

  private static terms(text: string): Set<string> {
    return new Set(
      text
        .toLowerCase()
        .split(/[^\p{L}\p{N}]+/u)
        .filter((w) => w.length > 2),
    );
  }

  recall(situation: Situation, budget: RecallBudget = recallBudget()): RecallResult {
    const nowMs = situation.atMs || this.now();
    const wanted = Recall.terms(situation.text);
    const all = this.store.all();

    const scored = all
      .filter((a) => !situation.folder || !a.folder || a.folder === situation.folder)
      .map((atom) => {
        const strength = Forgetting.retrievability(atom, nowMs);
        const words = Recall.terms(atom.text);
        let shared = 0;
        for (const w of wanted) if (words.has(w)) shared += 1;
        const relevance = wanted.size > 0 ? shared / wanted.size : 0;
        // A CORRECTION outranks everything of the same relevance, because it
        // exists to override something that is still in the store.
        const bonus = atom.kind === AtomKind.Correction ? 0.15 : 0;
        return { atom, strength, score: strength * 0.4 + relevance * 0.6 + bonus };
      })
      .filter((s) => s.strength >= budget.floor)
      .sort((a, b) => b.score - a.score);

    const chosen: MemoryAtom[] = [];
    let characters = 0;
    for (const { atom } of scored) {
      if (chosen.length >= budget.maxAtoms) break;
      if (characters + atom.text.length > budget.maxCharacters) continue;
      chosen.push(atom);
      characters += atom.text.length;
    }

    // The chosen atoms are STRENGTHENED, because being recalled is what recall
    // means - and without this the spacing effect never fires and everything
    // decays at the same rate whether it is used or not.
    for (const atom of chosen) this.store.put(Forgetting.strengthen(atom, nowMs));

    return Object.freeze({
      atoms: Object.freeze(chosen),
      promptText: chosen.length
        ? `worth remembering:\n${chosen.map((a) => `- ${a.text}`).join("\n")}`
        : "",
      consideredCount: all.length,
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Wear

/** How often something has been reached for. */
export interface MemoryTrace {
  readonly atomId: string;
  readonly recallCount: number;
  readonly lastRecalledAtMs: number;
  /** How often it was recalled and then NOT used in the answer. High wear with
   * low use means recall keeps offering something unhelpful. */
  readonly offeredNotUsed: number;
}

/**
 * Watches which memories earn their place.
 *
 * THE USEFUL SIGNAL IS OFFERED-BUT-NOT-USED. An atom that surfaces in every
 * recall and never influences an answer is noise that is crowding out something
 * better, and nothing else in the system can see that.
 */
export class MemoryWear {
  private readonly traces = new Map<string, MemoryTrace>();

  offered(atomId: string, nowMs: number): void {
    const existing = this.traces.get(atomId);
    this.traces.set(
      atomId,
      Object.freeze({
        atomId,
        recallCount: (existing?.recallCount ?? 0) + 1,
        lastRecalledAtMs: nowMs,
        offeredNotUsed: (existing?.offeredNotUsed ?? 0) + 1,
      }),
    );
  }

  /** Called when an atom actually influenced an answer. */
  used(atomId: string): void {
    const existing = this.traces.get(atomId);
    if (!existing) return;
    this.traces.set(
      atomId,
      Object.freeze({ ...existing, offeredNotUsed: Math.max(0, existing.offeredNotUsed - 1) }),
    );
  }

  trace(atomId: string): MemoryTrace | undefined {
    return this.traces.get(atomId);
  }

  /** Atoms that keep being offered and never help. Enough samples to mean
   * something, so one unlucky turn does not condemn a memory. */
  noisy(minOffers = 5): readonly string[] {
    return Object.freeze(
      [...this.traces.values()]
        .filter((t) => t.recallCount >= minOffers && t.offeredNotUsed >= minOffers)
        .map((t) => t.atomId),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Stores

/** Atoms in memory. */
export class InMemoryAtomStore implements AtomStore {
  private readonly atoms = new Map<string, MemoryAtom>();
  put(atom: MemoryAtom): void {
    this.atoms.set(atom.id, atom);
  }
  get(id: string): MemoryAtom | undefined {
    return this.atoms.get(id);
  }
  all(): readonly MemoryAtom[] {
    return Object.freeze([...this.atoms.values()]);
  }
  remove(id: string): boolean {
    return this.atoms.delete(id);
  }
}

/** One row as it is written. */
export interface AtomRecord {
  readonly id: string;
  readonly kind: string;
  readonly text: string;
  readonly createdAt: string;
  readonly lastRecalledAt: string;
  readonly stabilityDays: number;
  readonly recallCount: number;
  readonly folder: string;
  readonly source: string;
  readonly outcome: string;
}

/**
 * A running record of what was learned and forgotten.
 *
 * APPEND-ONLY, and separate from the store. When somebody asks why the
 * companion thinks something, the store says what it thinks and only the log
 * says how it came to.
 */
export class AtomLog {
  private readonly entries: { atMs: number; action: string; atomId: string; note: string }[] = [];

  record(atMs: number, action: string, atomId: string, note = ""): void {
    this.entries.push({ atMs, action, atomId, note });
  }

  /** Everything about one atom, oldest first. The answer to "why do you think
   * that?" */
  historyOf(atomId: string): readonly { atMs: number; action: string; note: string }[] {
    return Object.freeze(
      this.entries.filter((e) => e.atomId === atomId).map(({ atMs, action, note }) => ({ atMs, action, note })),
    );
  }

  get count(): number {
    return this.entries.length;
  }
}

/**
 * Atoms in SQLite.
 *
 * PARAMETERISED, ALWAYS. Every value here came from something somebody said,
 * and a store built by concatenating strings into SQL can be rewritten by
 * saying the right sentence out loud.
 */
export class SqliteAtomStore implements AtomStore {
  static readonly SCHEMA = Object.freeze([
    "CREATE TABLE IF NOT EXISTS atoms (" +
      " id TEXT PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL," +
      " created_at INTEGER NOT NULL, last_recalled_at INTEGER NOT NULL," +
      " stability_days REAL NOT NULL, recall_count INTEGER NOT NULL," +
      " folder TEXT NOT NULL DEFAULT '', source TEXT NOT NULL DEFAULT ''," +
      " outcome TEXT NOT NULL DEFAULT 'unknown')",
    // Indexed on folder AND last_recalled_at together: recall filters by folder
    // and orders by recency, and two separate indexes let the planner use only
    // one of them.
    "CREATE INDEX IF NOT EXISTS atoms_folder_recency ON atoms (folder, last_recalled_at DESC)",
  ]);

  private readonly cache = new Map<string, MemoryAtom>();

  constructor(private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][]) {}

  initialise(): boolean {
    if (!this.execute) return false;
    for (const statement of SqliteAtomStore.SCHEMA) this.execute(statement, []);
    return true;
  }

  put(atom: MemoryAtom): void {
    this.cache.set(atom.id, atom);
    this.execute?.(
      "INSERT OR REPLACE INTO atoms" +
        " (id, kind, text, created_at, last_recalled_at, stability_days," +
        "  recall_count, folder, source, outcome)" +
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
      [
        atom.id, atom.kind, atom.text, atom.createdAtMs, atom.lastRecalledAtMs,
        atom.stabilityDays, atom.recallCount, atom.folder, atom.source, atom.outcome,
      ],
    );
  }

  get(id: string): MemoryAtom | undefined {
    return this.cache.get(id);
  }

  all(): readonly MemoryAtom[] {
    return Object.freeze([...this.cache.values()]);
  }

  remove(id: string): boolean {
    const had = this.cache.delete(id);
    this.execute?.("DELETE FROM atoms WHERE id = ?", [id]);
    return had;
  }
}

/** Episodes in SQLite. */
export class SqliteEpisodicStore {
  static readonly SCHEMA = Object.freeze([
    "CREATE TABLE IF NOT EXISTS episodes (" +
      " id TEXT PRIMARY KEY, text TEXT NOT NULL, at INTEGER NOT NULL," +
      " folder TEXT NOT NULL DEFAULT '')",
    "CREATE INDEX IF NOT EXISTS episodes_at ON episodes (at DESC)",
  ]);

  constructor(private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][]) {}

  initialise(): boolean {
    if (!this.execute) return false;
    for (const statement of SqliteEpisodicStore.SCHEMA) this.execute(statement, []);
    return true;
  }

  add(id: string, text: string, atMs: number, folder = ""): boolean {
    if (!this.execute || !id) return false;
    this.execute("INSERT OR REPLACE INTO episodes (id, text, at, folder) VALUES (?, ?, ?, ?)", [
      id, text, atMs, folder,
    ]);
    return true;
  }

  /** BETWEEN two times, because "what happened on Tuesday" is the question
   * people actually ask - not "the last twenty things". */
  between(fromMs: number, toMs: number): Object[][] {
    if (!this.execute) return [];
    return this.execute(
      "SELECT id, text, at FROM episodes WHERE at >= ? AND at <= ? ORDER BY at",
      [fromMs, toMs],
    );
  }
}

/** Goals in SQLite. */
export class SqliteGoalStore {
  static readonly SCHEMA = Object.freeze([
    "CREATE TABLE IF NOT EXISTS goals (" +
      " id TEXT PRIMARY KEY, text TEXT NOT NULL, due_at INTEGER," +
      " progress REAL NOT NULL DEFAULT 0, is_done INTEGER NOT NULL DEFAULT 0)",
  ]);

  constructor(private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][]) {}

  initialise(): boolean {
    if (!this.execute) return false;
    for (const statement of SqliteGoalStore.SCHEMA) this.execute(statement, []);
    return true;
  }

  put(id: string, text: string, dueAtMs: number | null, progress = 0, isDone = false): boolean {
    if (!this.execute || !id) return false;
    // NULL rather than 0 for no deadline. Zero is a real time - the epoch - and
    // a goal with no deadline stored as 0 shows as fifty-six years overdue.
    this.execute(
      "INSERT OR REPLACE INTO goals (id, text, due_at, progress, is_done) VALUES (?, ?, ?, ?, ?)",
      [id, text, dueAtMs, progress, isDone ? 1 : 0],
    );
    return true;
  }

  open(): Object[][] {
    if (!this.execute) return [];
    // `due_at IS NULL` sorts LAST rather than first, which is what an undated
    // goal deserves - it is not urgent, it is undated.
    return this.execute(
      "SELECT id, text, due_at, progress FROM goals WHERE is_done = 0" +
        " ORDER BY due_at IS NULL, due_at",
      [],
    );
  }
}

/** Affect readings, as JSON on disk. */
export class JsonAffectStore {
  private readings: { label: string; confidence: number; atMs: number }[] = [];

  constructor(
    private readonly read?: () => string,
    private readonly write?: (json: string) => void,
    /** Older than this is dropped on save. Affect is a reading about a moment
     * and a year-old one says nothing about now - keeping it is a record of
     * somebody's moods for no purpose. */
    private readonly keepDays = 30,
  ) {}

  load(): number {
    if (!this.read) return 0;
    try {
      const parsed = JSON.parse(this.read());
      this.readings = Array.isArray(parsed) ? parsed : [];
    } catch {
      // A file that will not parse is treated as EMPTY rather than fatal.
      // Refusing to start because a cache is corrupt is the wrong trade for
      // something this unimportant.
      this.readings = [];
    }
    return this.readings.length;
  }

  add(label: string, confidence: number, atMs: number): void {
    this.readings.push({ label, confidence, atMs });
  }

  save(nowMs: number): boolean {
    if (!this.write) return false;
    const cutoff = nowMs - this.keepDays * 24 * 60 * 60 * 1000;
    this.readings = this.readings.filter((r) => r.atMs >= cutoff);
    this.write(JSON.stringify(this.readings));
    return true;
  }

  recent(sinceMs: number): readonly { label: string; confidence: number; atMs: number }[] {
    return Object.freeze(this.readings.filter((r) => r.atMs >= sinceMs));
  }
}

/** How the companion presents itself, as JSON on disk. */
export class JsonPersonaStore {
  private persona: Map<string, unknown> = {};

  constructor(
    private readonly read?: () => string,
    private readonly write?: (json: string) => void,
  ) {}

  load(): boolean {
    if (!this.read) return false;
    try {
      const parsed = JSON.parse(this.read());
      this.persona = parsed && typeof parsed === "object" ? parsed : {};
      return true;
    } catch {
      this.persona = {};
      return false;
    }
  }

  set(key: string, value: Object): void {
    this.persona[key] = value;
  }

  get(key: string): Object {
    return this.persona[key];
  }

  save(): boolean {
    if (!this.write) return false;
    this.write(JSON.stringify(this.persona));
    return true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// A module's own memory

/** Memory scoped to one module. */
export interface ModuleMemoryContract {
  readonly folder: MemoryFolder;
  remember(text: string, kind?: AtomKind): void;
  recall(about: string, budget?: RecallBudget): RecallResult;
  forget(id: string): boolean;
}

/**
 * One module's slice of memory.
 *
 * SCOPED, so the health module cannot read the business module's memories by
 * accident. A single shared pool is simpler and it is how a question about an
 * invoice ends up with somebody's medication in the prompt.
 */
export class ModuleMemory implements ModuleMemoryContract {
  private counter = 0;

  constructor(
    readonly folder: MemoryFolder,
    private readonly store: AtomStore,
    private readonly now: () => number = () => 0,
  ) {}

  remember(text: string, kind: AtomKind = AtomKind.Fact): void {
    if (this.folder.retention === MemoryRetention.None) return;
    if (this.folder.maxAtoms > 0) {
      const mine = this.store.all().filter((a) => a.folder === this.folder.name);
      if (mine.length >= this.folder.maxAtoms) {
        // The WEAKEST goes, not the oldest. An old memory that is still
        // recalled weekly is worth more than a recent one nobody has needed.
        const nowMs = this.now();
        const weakest = mine.reduce((a, b) =>
          Forgetting.retrievability(b, nowMs) < Forgetting.retrievability(a, nowMs) ? b : a,
        );
        this.store.remove(weakest.id);
      }
    }
    this.counter += 1;
    const nowMs = this.now();
    this.store.put(
      memoryAtom({
        id: `${this.folder.name}-${this.counter.toString().padStart(6, "0")}`,
        kind,
        text,
        createdAtMs: nowMs,
        lastRecalledAtMs: nowMs,
        folder: this.folder.name,
      }),
    );
  }

  recall(about: string, budget: RecallBudget = recallBudget()): RecallResult {
    return new Recall(this.store, this.now).recall(
      Object.freeze({
        text: about,
        folder: this.folder.name,
        atMs: this.now(),
        presentSpeakerIds: [],
      }),
      budget,
    );
  }

  forget(id: string): boolean {
    const atom = this.store.get(id);
    // A module can only forget its OWN memories. Otherwise "forget that" in one
    // context deletes something from another.
    if (!atom || atom.folder !== this.folder.name) return false;
    return this.store.remove(id);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The service

/** What was carried to a hook. */
export interface HookPayload {
  readonly event: string;
  readonly text: string;
  readonly folder: string;
  readonly atMs: number;
}

/** The memory the companion uses. */
export interface MemoryServiceContract {
  observe(text: string, folder?: string, source?: string): LearnReport;
  recall(situation: Situation, budget?: RecallBudget): RecallResult;
  sweep(nowMs: number): number;
  readonly atomCount: number;
}

/** Remembering, recalling, and letting go. */
export class MemoryService implements MemoryServiceContract {
  private readonly learner: AtomLearner;
  private readonly recaller: Recall;

  constructor(
    private readonly store: AtomStore = new InMemoryAtomStore(),
    private readonly extractor: AtomExtractor = new CueExtractor(),
    private readonly now: () => number = () => 0,
    private readonly log: AtomLog = new AtomLog(),
  ) {
    this.learner = new AtomLearner(store, now);
    this.recaller = new Recall(store, now);
  }

  get atomCount(): number {
    return this.store.all().length;
  }

  observe(text: string, folder = "", source = ""): LearnReport {
    const report = this.learner.learn(this.extractor.extract(text, source), folder);
    this.log.record(this.now(), "observe", "", `${report.stored} stored, ${report.merged} merged`);
    return report;
  }

  recall(situation: Situation, budget: RecallBudget = recallBudget()): RecallResult {
    return this.recaller.recall(situation, budget);
  }

  /**
   * Removes what has genuinely gone, and returns how many.
   *
   * SEPARATE FROM RECALL and never automatic on a read path: a sweep that ran
   * during a recall would delete things in the middle of answering a question,
   * and a slow sweep would make every answer slow.
   */
  sweep(nowMs: number): number {
    let dropped = 0;
    for (const atom of this.store.all()) {
      if (Forgetting.shouldDrop(atom, nowMs)) {
        this.store.remove(atom.id);
        this.log.record(nowMs, "forget", atom.id, "faded past recovery");
        dropped += 1;
      }
    }
    return dropped;
  }
}

/** What a sync run did. */
export interface SyncReport {
  readonly sent: number;
  readonly received: number;
  readonly conflicts: number;
  /** Resolved by keeping BOTH, never by picking one silently. */
  readonly keptBoth: number;
}

/**
 * Memory between a person's own devices.
 *
 * BETWEEN THEIR DEVICES ONLY, over the mesh, never through a server. A memory
 * store that syncs through somebody else's computer is a copy of somebody's
 * life on somebody else's computer, whatever the encryption story is.
 *
 * A CONFLICT KEEPS BOTH. Two devices that learned different things about the
 * same subject have both learned something, and picking one by timestamp
 * discards a real memory because a clock was wrong.
 */
export class MemorySync {
  constructor(
    private readonly store: AtomStore,
    private readonly send?: (atoms: readonly MemoryAtom[]) => Promise<readonly MemoryAtom[]>,
  ) {}

  async sync(sinceMs: number): Promise<SyncReport> {
    if (!this.send) {
      return Object.freeze({ sent: 0, received: 0, conflicts: 0, keptBoth: 0 });
    }
    const mine = this.store.all().filter((a) => a.lastRecalledAtMs >= sinceMs);
    const theirs = await this.send(mine);

    const byId = new Map(this.store.all().map((a) => [a.id, a]));
    let conflicts = 0;
    let keptBoth = 0;
    let received = 0;
    for (const incoming of theirs) {
      const existing = byId.get(incoming.id);
      if (!existing) {
        this.store.put(incoming);
        received += 1;
        continue;
      }
      if (existing.text === incoming.text) {
        // The same memory on both sides: keep the STRONGER, which is the one
        // that has been used more.
        this.store.put(
          existing.stabilityDays >= incoming.stabilityDays ? existing : incoming,
        );
        continue;
      }
      conflicts += 1;
      // Both kept, under distinct ids. The suffix makes the duplicate visible
      // to a person, which is who should resolve it.
      this.store.put(memoryAtom({ ...incoming, id: `${incoming.id}-b` }));
      keptBoth += 1;
    }
    return Object.freeze({ sent: mine.length, received, conflicts, keptBoth });
  }
}

/**
 * Affect readings paired with whether anybody was actually speaking.
 *
 * A voice-derived reading taken while nobody is speaking is reading the room's
 * air conditioning, and pairing the two is what stops an empty room being
 * recorded as a calm person.
 */
export class AffectStateVadExtensions {
  static isTrustworthy(source: string, speechPresent: boolean, confidence: number): boolean {
    if (source === "voice" && !speechPresent) return false;
    return confidence >= 0.5;
  }

  /**
   * Combines readings, weighted by confidence, with DISAGREEMENT lowering the
   * result.
   *
   * Two sources that disagree are less informative than one that is sure, and
   * averaging them into a confident middle is the standard way to turn two weak
   * signals into one wrong strong one.
   */
  static combine(
    readings: readonly { label: string; confidence: number }[],
  ): { label: string; confidence: number } {
    const usable = readings.filter((r) => r.label && r.label !== "uncertain");
    if (usable.length === 0) return { label: "uncertain", confidence: 0 };
    const totals = new Map<string, number>();
    for (const r of usable) totals.set(r.label, (totals.get(r.label) ?? 0) + r.confidence);
    const [label, weight] = [...totals.entries()].reduce((a, b) => (b[1] > a[1] ? b : a));
    const all = [...totals.values()].reduce((a, b) => a + b, 0);
    const agreement = weight / all;
    return { label, confidence: Math.min(1, weight / usable.length) * agreement };
  }
}

/** Wires the memory service. */
export class MemoryServiceCollectionExtensions {
  static addMemory(
    store: AtomStore = new InMemoryAtomStore(),
    now: () => number = () => 0,
  ): MemoryService {
    return new MemoryService(store, new CueExtractor(), now);
  }
}

// The C# spellings, kept so the two trees line up.
export type IAtomStore = AtomStore;
export type IAtomExtractor = AtomExtractor;
export type IMemoryService = MemoryServiceContract;
export type IModuleMemory = ModuleMemoryContract;
