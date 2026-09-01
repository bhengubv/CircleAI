/**
 * personality/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// personality/index.ts
//
// Full-parity port of CircleAI.Personality (C#). C# is the exact spec.
//
// The user-DECLARED persona artefact (distinct from CircleAI.Memory.PersonaState,
// which is the AI's LEARNED model of the user):
//   • Persona / FormalityRange / PrivacyLevel   (Persona.cs)
//   • IPersonaProvider                          (IPersonaProvider.cs)
//   • JsonPersonaProvider                       (JsonPersonaProvider.cs)
//   • IPersonaConflictResolver                  (IPersonaConflictResolver.cs)
//   • DeclaredWinsResolver / LearnedWinsResolver
//   • PersonaPromptBuilder                      (PersonaPromptBuilder.cs)
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ Create factory)
//   Guid                                  → string (UUID)
//   DateTimeOffset                        → Date (ToString("O") → toISOString())
//   IReadOnlyList<string>                 → readonly string[]
//   enum PrivacyLevel                     → TS enum (Strict/Balanced/Open)
//   System.Text.Json string escaping      → JSON.stringify (prompt-injection defence)
//   File.Move (atomic)                    → fs.rename after write-to-tmp
//   SemaphoreSlim per userId              → per-key promise-chain mutex

import { Files as fs } from "../platform";
import { Path as path } from "../platform";
import { randomUUID } from "../platform";
import type { PersonaState } from "../memory/index";

// ─────────────────────────────────────────────────────────────────────────────
// PrivacyLevel — declared privacy posture. Mirrors C# `PrivacyLevel` enum.
// ─────────────────────────────────────────────────────────────────────────────

/** Declared privacy posture. Mirrors C# `PrivacyLevel` enum (ordinals 0..2). */
export enum PrivacyLevel {
  /** Minimum retention, no proactive surfacing, no third-party calls without prompt. */
  Strict = 0,
  /** Default. Reasonable retention, helpful proactive prompts. */
  Balanced = 1,
  /** Maximum retention, willing to share personal context across surfaces. */
  Open = 2,
}

// ─────────────────────────────────────────────────────────────────────────────
// FormalityRange — declared bounds on conversational formality.
// ─────────────────────────────────────────────────────────────────────────────

/** Declared bounds on conversational formality. Mirrors C# `FormalityRange` record. */
export interface FormalityRange {
  /** Lowest acceptable formality. Allowed: "casual" | "neutral" | "formal". */
  readonly floor: string;
  /** Highest acceptable formality. Allowed: "casual" | "neutral" | "formal". */
  readonly ceiling: string;
}

/** Constructs a {@link FormalityRange}. */
export function formalityRange(floor: string, ceiling: string): FormalityRange {
  return { floor, ceiling };
}

// ─────────────────────────────────────────────────────────────────────────────
// Persona — user-declared identity artefact. Mirrors C# `Persona` record.
// ─────────────────────────────────────────────────────────────────────────────

/** User-declared persona artefact. Mirrors C# `Persona` record. */
export interface Persona {
  /** Stable identifier for the persona document. */
  readonly id: string;
  /** User's preferred display name. */
  readonly displayName: string;
  /** Free-form pronouns (e.g. "she/her"). May be null. */
  readonly pronouns: string | null;
  /** Free-form identity tags (e.g. "parent", "vegan", "isiZulu learner"). */
  readonly identityTags: readonly string[];
  /** Stated values the assistant should respect (e.g. "privacy", "family"). */
  readonly values: readonly string[];
  /** Topics the assistant must refuse or avoid. */
  readonly taboos: readonly string[];
  /** IETF BCP-47 locale. */
  readonly preferredLocale: string;
  /** Optional preferred voice tag (e.g. "warm-female"). */
  readonly voicePreference: string | null;
  /** Declared formality range. */
  readonly formality: FormalityRange;
  /** Declared privacy posture. */
  readonly privacy: PrivacyLevel;
  /** UTC time of initial creation. */
  readonly createdAt: Date;
  /** UTC time of the last modification. */
  readonly updatedAt: Date;
}

/** Constructs a {@link Persona}. Mirrors the C# positional record ctor. */
export function persona(
  id: string,
  displayName: string,
  pronouns: string | null,
  identityTags: readonly string[],
  values: readonly string[],
  taboos: readonly string[],
  preferredLocale: string,
  voicePreference: string | null,
  formality: FormalityRange,
  privacy: PrivacyLevel,
  createdAt: Date,
  updatedAt: Date,
): Persona {
  return {
    id,
    displayName,
    pronouns,
    identityTags,
    values,
    taboos,
    preferredLocale,
    voicePreference,
    formality,
    privacy,
    createdAt,
    updatedAt,
  };
}

/**
 * Creates a new {@link Persona} with sensible defaults: balanced privacy, no
 * taboos or values, formality range "casual".."formal", timestamps stamped to
 * now. Mirrors C# `Persona.Create`.
 */
export function createPersona(displayName: string, locale: string): Persona {
  if (displayName == null || displayName.trim().length === 0)
    throw new Error("displayName required");
  if (locale == null || locale.trim().length === 0) throw new Error("locale required");

  const now = new Date();
  return persona(
    randomUUID(),
    displayName,
    null,
    [],
    [],
    [],
    locale,
    null,
    formalityRange("casual", "formal"),
    PrivacyLevel.Balanced,
    now,
    now,
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// IPersonaProvider — stores user-declared Persona documents.
// ─────────────────────────────────────────────────────────────────────────────

/** Persists and retrieves user-declared {@link Persona} documents. Mirrors C# `IPersonaProvider`. */
export interface IPersonaProvider {
  /** Loads the persona for `userId`. Returns `null` when none has been saved. */
  getAsync(userId: string, signal?: AbortSignal): Promise<Persona | null>;
  /** Persists `persona` for `userId` (refreshes updatedAt) and returns the saved record. */
  saveAsync(userId: string, persona: Persona, signal?: AbortSignal): Promise<Persona>;
  /** Whether a persona is currently stored for `userId`. */
  existsAsync(userId: string, signal?: AbortSignal): Promise<boolean>;
  /** Streams every persona currently stored (user-driven export). */
  exportAllAsync(signal?: AbortSignal): AsyncIterable<Persona>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Persona JSON serialisation — mirrors the System.Text.Json shape used by
// JsonPersonaProvider (PascalCase properties, PrivacyLevel as its enum name).
// ─────────────────────────────────────────────────────────────────────────────

function personaToJson(p: Persona): string {
  // WriteIndented=true, JsonStringEnumConverter, WhenWritingNull.
  const obj: Record<string, unknown> = {
    Id: p.id,
    DisplayName: p.displayName,
    IdentityTags: p.identityTags,
    Values: p.values,
    Taboos: p.taboos,
    PreferredLocale: p.preferredLocale,
    Formality: { Floor: p.formality.floor, Ceiling: p.formality.ceiling },
    Privacy: PrivacyLevel[p.privacy],
    CreatedAt: p.createdAt.toISOString(),
    UpdatedAt: p.updatedAt.toISOString(),
  };
  // WhenWritingNull — only emit nullable members when non-null.
  if (p.pronouns != null) obj.Pronouns = p.pronouns;
  if (p.voicePreference != null) obj.VoicePreference = p.voicePreference;
  return JSON.stringify(obj, null, 2);
}

function personaFromJson(text: string): Persona | null {
  const raw = JSON.parse(text) as Record<string, unknown> | null;
  if (raw == null) return null;

  const fm = (raw.Formality ?? {}) as Record<string, unknown>;
  const privacyRaw = raw.Privacy;
  let privacy = PrivacyLevel.Balanced;
  if (typeof privacyRaw === "string") {
    const mapped = (PrivacyLevel as unknown as Record<string, number>)[privacyRaw];
    if (mapped != null) privacy = mapped;
  } else if (typeof privacyRaw === "number") {
    privacy = privacyRaw as PrivacyLevel;
  }

  return persona(
    (raw.Id as string) ?? randomUUID(),
    (raw.DisplayName as string) ?? "",
    (raw.Pronouns as string | undefined) ?? null,
    (raw.IdentityTags as string[] | undefined) ?? [],
    (raw.Values as string[] | undefined) ?? [],
    (raw.Taboos as string[] | undefined) ?? [],
    (raw.PreferredLocale as string) ?? "",
    (raw.VoicePreference as string | undefined) ?? null,
    formalityRange((fm.Floor as string) ?? "casual", (fm.Ceiling as string) ?? "formal"),
    privacy,
    parseDate(raw.CreatedAt),
    parseDate(raw.UpdatedAt),
  );
}

function parseDate(raw: unknown): Date {
  if (typeof raw === "string") {
    const d = new Date(raw);
    if (!Number.isNaN(d.getTime())) return d;
  }
  return new Date();
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-key async mutex — parity for the C# SemaphoreSlim(1,1)-per-userId gate.
// ─────────────────────────────────────────────────────────────────────────────

class KeyedMutex {
  private readonly chains = new Map<string, Promise<unknown>>();

  async run<T>(key: string, action: () => Promise<T>): Promise<T> {
    const prev = this.chains.get(key) ?? Promise.resolve();
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.chains.set(
      key,
      prev.then(() => gate),
    );
    await prev.catch(() => {
      /* previous holder's failure must not block this one */
    });
    try {
      return await action();
    } finally {
      release();
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// JsonPersonaProvider — one {userId}.persona.json per persona under a root dir.
// Mirrors C# `JsonPersonaProvider`.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * File-system {@link IPersonaProvider} that stores each persona as a JSON
 * document under a configured root directory. Single-process safe (per-userId
 * mutex + atomic write-then-rename); NOT multi-replica safe.
 */
export class JsonPersonaProvider implements IPersonaProvider {
  readonly componentName = "JsonPersonaProvider";

  private readonly rootDirectory: string;
  private readonly locks = new KeyedMutex();
  private readonly ready: Promise<void>;

  /**
   * Creates a new provider rooted at `rootDirectory`. The directory is created
   * if it does not already exist.
   */
  constructor(rootDirectory: string) {
    if (rootDirectory == null || rootDirectory.trim().length === 0)
      throw new Error("rootDirectory required");
    this.rootDirectory = rootDirectory;
    this.ready = fs.mkdir(this.rootDirectory, { recursive: true }).then(() => undefined);
  }

  async getAsync(userId: string, _signal?: AbortSignal): Promise<Persona | null> {
    if (userId == null || userId.trim().length === 0) throw new Error("userId required");
    await this.ready;
    const p = this.personaPath(userId);
    if (!(await fileExists(p))) return null;

    return this.locks.run(userId, async () => {
      const text = await fs.readFile(p, "utf8");
      return personaFromJson(text);
    });
  }

  async saveAsync(userId: string, p: Persona, _signal?: AbortSignal): Promise<Persona> {
    if (userId == null || userId.trim().length === 0) throw new Error("userId required");
    if (p == null) throw new Error("persona required");
    await this.ready;

    const refreshed: Persona = { ...p, updatedAt: new Date() };
    const target = this.personaPath(userId);
    const tmp = target + "." + guidN(randomUUID()) + ".tmp";

    return this.locks.run(userId, async () => {
      try {
        await fs.writeFile(tmp, personaToJson(refreshed), "utf8");
        await fs.rename(tmp, target);
        return refreshed;
      } catch (e) {
        try {
          await fs.unlink(tmp);
        } catch {
          /* best effort */
        }
        throw e;
      }
    });
  }

  async existsAsync(userId: string, _signal?: AbortSignal): Promise<boolean> {
    if (userId == null || userId.trim().length === 0) throw new Error("userId required");
    await this.ready;
    return fileExists(this.personaPath(userId));
  }

  async *exportAllAsync(signal?: AbortSignal): AsyncIterable<Persona> {
    await this.ready;
    let files: string[];
    try {
      files = (await fs.readdir(this.rootDirectory)).filter((f) => f.endsWith(".persona.json"));
    } catch {
      return;
    }

    for (const file of files) {
      if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
      let parsed: Persona | null;
      try {
        const text = await fs.readFile(path.join(this.rootDirectory, file), "utf8");
        parsed = personaFromJson(text);
      } catch {
        // Skip corrupted records during export rather than failing the whole stream.
        continue;
      }
      if (parsed != null) yield parsed;
    }
  }

  private personaPath(userId: string): string {
    // Sanitise the userId into a safe filename stem. Mirrors C#
    // string.Join("_", userId.Split(Path.GetInvalidFileNameChars())): on Windows
    // the invalid set is the double-quote, < > | : * ? backslash and forward
    // slash, plus control chars 0x00-0x1F. Space and hyphen are NOT invalid and
    // are preserved verbatim.
    let safe = userId.replace(/[\u0000-\u001f"<>|:*?\\/]/g, "_");
    if (safe.trim().length === 0) safe = "default";
    return path.join(this.rootDirectory, safe + ".persona.json");
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// IPersonaConflictResolver — reconciles declared Persona with learned PersonaState.
// ─────────────────────────────────────────────────────────────────────────────

/** Reconciles a user-declared {@link Persona} with the AI's learned PersonaState. Mirrors C# `IPersonaConflictResolver`. */
export interface IPersonaConflictResolver {
  /** Resolves disagreement between `declared` and `learned`. Deterministic; never mutates inputs. */
  resolve(declared: Persona, learned: PersonaState): Persona;
}

/**
 * Default resolver: the declared persona's bounds are hard limits. The learned
 * formality is clamped to the declared range. Mirrors C# `DeclaredWinsResolver`.
 */
export class DeclaredWinsResolver implements IPersonaConflictResolver {
  resolve(declared: Persona, learned: PersonaState): Persona {
    if (declared == null) throw new Error("declared required");
    if (learned == null) throw new Error("learned required");

    // Clamp the learned formality into the declared range.
    const clamped = DeclaredWinsResolver.clampFormality(learned.formality, declared.formality);
    if (clamped === learned.formality) {
      // Learned was within bounds — no adjustment to surface.
      return declared;
    }

    // Learned drifted outside declared bounds — surface the clamped value by
    // replacing the floor or ceiling so future projections respect it.
    let range: FormalityRange;
    switch (clamped) {
      case "casual":
        range = formalityRange("casual", declared.formality.ceiling);
        break;
      case "formal":
        range = formalityRange(declared.formality.floor, "formal");
        break;
      default:
        range = declared.formality;
        break;
    }

    return { ...declared, formality: range };
  }

  private static clampFormality(learned: string, range: FormalityRange): string {
    const learnedRank = DeclaredWinsResolver.rank(learned);
    const floorRank = DeclaredWinsResolver.rank(range.floor);
    const ceilingRank = DeclaredWinsResolver.rank(range.ceiling);

    // If declared range is inverted, treat declared as fixed at floor.
    if (floorRank > ceilingRank) return range.floor;

    if (learnedRank < floorRank) return range.floor;
    if (learnedRank > ceilingRank) return range.ceiling;
    return learned;
  }

  private static rank(formality: string): number {
    switch (formality) {
      case "casual":
        return 0;
      case "neutral":
        return 1;
      case "formal":
        return 2;
      default:
        return 1; // unknown values rank as neutral
    }
  }
}

/**
 * Alternative resolver: the learned PersonaState overrides the declared
 * Persona. Intended for "privacy mode off". Mirrors C# `LearnedWinsResolver`.
 */
export class LearnedWinsResolver implements IPersonaConflictResolver {
  resolve(declared: Persona, learned: PersonaState): Persona {
    if (declared == null) throw new Error("declared required");
    if (learned == null) throw new Error("learned required");

    // Pass through — identity, taboos, and values stay intact; the learned
    // formality/locale/verbosity are applied separately by the prompt builder.
    return declared;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// PersonaPromptBuilder — renders a Persona into a system-prompt hint block.
// Mirrors C# `PersonaPromptBuilder`.
// ─────────────────────────────────────────────────────────────────────────────

/** Builds the system-prompt block describing a {@link Persona}. Mirrors C# `PersonaPromptBuilder`. */
export class PersonaPromptBuilder {
  private constructor() {
    /* static-only */
  }

  /**
   * Renders `p` into a compact system-prompt hint, or an empty string when the
   * persona is effectively default (display name only). Mirrors C#
   * `PersonaPromptBuilder.BuildSystemHint`.
   */
  static buildSystemHint(p: Persona): string {
    if (p == null) throw new Error("persona required");

    if (PersonaPromptBuilder.isEffectivelyDefault(p)) return "";

    let sb = "";
    sb += "[Persona]";

    sb += "\nYou are speaking with ";
    sb += PersonaPromptBuilder.quote(p.displayName);
    sb += ".";

    if (p.pronouns != null && p.pronouns.trim().length > 0) {
      sb += " They identify as ";
      sb += PersonaPromptBuilder.quote(p.pronouns);
      sb += ".";
    }

    sb += "\nThey prefer responses in ";
    sb += PersonaPromptBuilder.quote(p.preferredLocale);
    sb += ", tone between ";
    sb += PersonaPromptBuilder.quote(p.formality.floor);
    sb += " and ";
    sb += PersonaPromptBuilder.quote(p.formality.ceiling);
    sb += ".";

    if (p.identityTags.length > 0) {
      sb += "\nIdentity tags: ";
      sb += PersonaPromptBuilder.quoteList(p.identityTags);
      sb += ".";
    }

    if (p.values.length > 0) {
      sb += "\nTheir declared values: ";
      sb += PersonaPromptBuilder.quoteList(p.values);
      sb += ".";
    }

    if (p.taboos.length > 0) {
      sb += "\nAvoid: ";
      sb += PersonaPromptBuilder.quoteList(p.taboos);
      sb += ".";
    }

    if (p.voicePreference != null && p.voicePreference.trim().length > 0) {
      sb += "\nPreferred voice tag: ";
      sb += PersonaPromptBuilder.quote(p.voicePreference);
      sb += ".";
    }

    if (p.privacy === PrivacyLevel.Strict) {
      sb +=
        "\nPrivacy: strict — minimize stored signals, do not surface personal context proactively, and never share personal context across surfaces without explicit prompt.";
    } else if (p.privacy === PrivacyLevel.Open) {
      sb += "\nPrivacy: open — the user has authorised broader retention and proactive surfacing.";
    }

    return sb;
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  /** True when the persona contains no information beyond the create() defaults. */
  private static isEffectivelyDefault(p: Persona): boolean {
    return (
      (p.pronouns == null || p.pronouns.trim().length === 0) &&
      p.identityTags.length === 0 &&
      p.values.length === 0 &&
      p.taboos.length === 0 &&
      (p.voicePreference == null || p.voicePreference.trim().length === 0) &&
      p.privacy === PrivacyLevel.Balanced &&
      p.formality.floor === "casual" &&
      p.formality.ceiling === "formal"
    );
  }

  /**
   * JSON-encodes `value` into a quoted literal — the prompt-injection defence:
   * any embedded quote, newline, or directive is rendered as inert text inside
   * a quoted string. Mirrors C# `PersonaPromptBuilder.Quote` (UnsafeRelaxed
   * escaping — no HTML entity encoding).
   */
  private static quote(value: string): string {
    return JSON.stringify(value ?? "");
  }

  private static quoteList(items: readonly string[]): string {
    if (items.length === 0) return "";
    return items.map((i) => PersonaPromptBuilder.quote(i)).join(", ");
  }
}

// ── local helpers ─────────────────────────────────────────────────────────────

function guidN(id: string): string {
  return id.replace(/-/g, "").toLowerCase();
}

async function fileExists(p: string): Promise<boolean> {
  try {
    await fs.access(p);
    return true;
  } catch {
    return false;
  }
}
