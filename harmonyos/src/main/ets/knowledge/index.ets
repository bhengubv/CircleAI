/**
 * knowledge/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// knowledge/index.ts
//
// Full-parity port of CircleAI.Knowledge (C#). C# is the exact spec.
//
// Markdown-on-disk knowledge notes: YAML frontmatter + markdown body.
//   • YamlFrontmatter          — minimal flat key→value YAML reader/writer.
//   • KnowledgeNote            — a note record + ToFileText / ParseFile round-trip.
//   • IKnowledgeStore          — persistence contract (get / save / delete / stream).
//   • FileSystemKnowledgeStore — one .md file per note (atomic write-then-rename).
//   • MarkdownEpisodicMemoryStore — IEpisodicMemoryStore backed by IKnowledgeStore.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   Guid                                  → string (UUID "N"/"D" form via helpers)
//   DateTimeOffset                        → Date (ToString("O") → toISOString())
//   IReadOnlyDictionary<string,string>    → ReadonlyMap<string, string>
//   IReadOnlyList<string>                 → readonly string[]
//   IAsyncEnumerable<T>                   → AsyncIterable<T> (async *)
//   CancellationToken                     → optional AbortSignal
//   SemaphoreSlim per key                 → per-key promise-chain mutex
//   float[] embedding                     → number[] (Math.fround at float sites)
//
// The C# FileSystemKnowledgeStore/JsonPersonaProvider read/write real files —
// ported with node:fs, matching this tree's FilesystemCodeEditor convention.

import { Files as fs } from "../platform";
import { Path as path } from "../platform";
import { randomUUID } from "../platform";
import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from "../memory/index";

// ─────────────────────────────────────────────────────────────────────────────
// Guid helpers — mirror C# Guid.ToString("N"/"D") + Guid.TryParse.
// ─────────────────────────────────────────────────────────────────────────────

const GUID_D_RX =
  /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const GUID_N_RX = /^[0-9a-fA-F]{32}$/;

/** C# Guid.ToString("N") — 32 hex digits, no dashes, lower-case. */
function guidToN(id: string): string {
  return id.replace(/-/g, "").toLowerCase();
}

/** C# Guid.ToString("D") — canonical dashed lower-case form. */
function guidToD(id: string): string {
  const n = guidToN(id);
  return (
    n.slice(0, 8) +
    "-" +
    n.slice(8, 12) +
    "-" +
    n.slice(12, 16) +
    "-" +
    n.slice(16, 20) +
    "-" +
    n.slice(20, 32)
  );
}

/** C# Guid.TryParse — accepts "N" and "D" forms; returns null when invalid. */
function tryParseGuid(raw: string): string | null {
  if (raw == null) return null;
  const t = raw.trim();
  if (GUID_D_RX.test(t)) return guidToD(t);
  if (GUID_N_RX.test(t)) return guidToD(t);
  return null;
}

/** The all-zero Guid (Guid.Empty). */
const GUID_EMPTY = "00000000-0000-0000-0000-000000000000";

// ─────────────────────────────────────────────────────────────────────────────
// YamlFrontmatter — minimal flat key→value YAML block reader/writer.
// Mirrors C# internal static YamlFrontmatter. Exported here (TS has no
// assembly-internal visibility) but not re-exported from the package barrel.
// ─────────────────────────────────────────────────────────────────────────────

const YAML_DELIMITER = "---";

/**
 * Parses and writes minimal YAML frontmatter blocks of the form
 * `---\nkey: value\n---\n(body)`. Only flat string→string mappings are
 * supported. Nested keys, flow-style structures, anchors, and lists are
 * rejected so the format stays predictable across implementations.
 */
export class YamlFrontmatter {
  private constructor() {
    /* static-only */
  }

  /**
   * Renders `frontmatter` into a YAML block followed by `body`. If
   * `frontmatter` is empty the block is still emitted (empty delimited block)
   * so the format stays uniform. Preserves insertion order.
   */
  static write(frontmatter: ReadonlyMap<string, string>, body: string): string {
    if (frontmatter == null) throw new Error("frontmatter required");
    if (body == null) throw new Error("body required");

    let sb = "";
    sb += YAML_DELIMITER + "\n";
    for (const [key, value] of frontmatter) {
      YamlFrontmatter.validateKey(key);
      sb += key;
      sb += ": ";
      sb += YamlFrontmatter.encodeValue(value);
      sb += "\n";
    }
    sb += YAML_DELIMITER + "\n";
    sb += body;
    return sb;
  }

  /**
   * Parses `text` into a frontmatter map and a body string. Throws when the
   * document is malformed.
   */
  static read(text: string): { frontmatter: Map<string, string>; body: string } {
    if (text == null) throw new Error("text required");

    // Normalise line endings.
    let normalised = text.replace(/\r\n/g, "\n").replace(/\r/g, "\n");

    if (!normalised.startsWith(YAML_DELIMITER + "\n"))
      throw new Error("Frontmatter must start with '---' on its own line.");

    // Locate the closing delimiter.
    const searchStart = YAML_DELIMITER.length + 1;
    const closingMarker = "\n" + YAML_DELIMITER + "\n";
    const closingIdx = normalised.indexOf(closingMarker, searchStart);
    if (closingIdx < 0)
      throw new Error("Missing closing '---' line for frontmatter block.");

    const yaml = normalised.substring(searchStart, closingIdx);
    const body = normalised.substring(closingIdx + closingMarker.length);

    const dict = new Map<string, string>();
    for (const rawLine of yaml.split("\n")) {
      if (rawLine.trim().length === 0) continue;

      // Reject indented lines — that would imply nesting.
      if (rawLine[0] === " " || rawLine[0] === "\t")
        throw new Error("Nested YAML is not supported.");

      // Reject list markers.
      if (rawLine.startsWith("- ")) throw new Error("YAML lists are not supported.");

      const colon = rawLine.indexOf(":");
      if (colon <= 0) throw new Error(`Malformed YAML line: '${rawLine}'.`);

      const key = rawLine.substring(0, colon).trim();
      const rest =
        colon + 1 < rawLine.length ? YamlFrontmatter.trimStart(rawLine.substring(colon + 1)) : "";

      YamlFrontmatter.validateKey(key);

      // Reject obvious nested-structure starters on the value side.
      if (rest.startsWith("{") || rest.startsWith("["))
        throw new Error("Flow-style YAML structures are not supported.");

      dict.set(key, YamlFrontmatter.decodeValue(rest));
    }

    return { frontmatter: dict, body };
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static validateKey(key: string): void {
    if (key == null || key.trim().length === 0) throw new Error("YAML key cannot be empty.");

    for (const ch of key) {
      // Allow alnum, underscore, dash, dot. Anything else is suspicious.
      if (!(YamlFrontmatter.isLetterOrDigit(ch) || ch === "_" || ch === "-" || ch === "."))
        throw new Error(`Invalid character '${ch}' in YAML key '${key}'.`);
    }
  }

  /**
   * Encodes a value for output. Values that contain reserved characters
   * (colon, hash, newline, quote, backslash) are wrapped in double quotes
   * with the standard YAML escape sequences applied.
   */
  private static encodeValue(value: string): string {
    if (value == null) return "";
    if (value.length === 0) return '""';

    let needsQuoting = false;
    for (const ch of value) {
      if (
        ch === ":" ||
        ch === "#" ||
        ch === "\n" ||
        ch === "\r" ||
        ch === "\t" ||
        ch === '"' ||
        ch === "\\" ||
        ch === "'" ||
        ch === "{" ||
        ch === "["
      ) {
        needsQuoting = true;
        break;
      }
    }

    // Leading/trailing whitespace also requires quoting.
    if (!needsQuoting && (value[0] === " " || value[value.length - 1] === " ")) needsQuoting = true;

    if (!needsQuoting) return value;

    let sb = '"';
    for (const ch of value) {
      switch (ch) {
        case "\\":
          sb += "\\\\";
          break;
        case '"':
          sb += '\\"';
          break;
        case "\n":
          sb += "\\n";
          break;
        case "\r":
          sb += "\\r";
          break;
        case "\t":
          sb += "\\t";
          break;
        default:
          sb += ch;
          break;
      }
    }
    sb += '"';
    return sb;
  }

  /**
   * Decodes a YAML scalar from its on-disk form. Handles both bare and
   * double-quoted forms with the matching escape set produced by encodeValue.
   */
  private static decodeValue(raw: string): string {
    if (raw.length === 0) return "";

    // Strip a single trailing inline comment ('  # comment') — only when the
    // value is NOT quoted; quoted values keep their content verbatim.
    if (raw[0] !== '"' && raw[0] !== "'") {
      const hashIdx = raw.indexOf(" #");
      if (hashIdx >= 0) raw = YamlFrontmatter.trimEnd(raw.substring(0, hashIdx));
      return raw;
    }

    // Single-quoted form is intentionally rejected — we never emit it.
    if (raw[0] === "'") throw new Error("Single-quoted YAML scalars are not supported.");

    if (raw.length < 2 || raw[raw.length - 1] !== '"')
      throw new Error("Unterminated double-quoted YAML scalar.");

    const inner = raw.substring(1, raw.length - 1);
    let sb = "";
    for (let i = 0; i < inner.length; i++) {
      const ch = inner[i];
      if (ch !== "\\") {
        sb += ch;
        continue;
      }

      if (i + 1 >= inner.length) throw new Error("Trailing backslash in YAML scalar.");

      const next = inner[++i];
      switch (next) {
        case "\\":
          sb += "\\";
          break;
        case '"':
          sb += '"';
          break;
        case "n":
          sb += "\n";
          break;
        case "r":
          sb += "\r";
          break;
        case "t":
          sb += "\t";
          break;
        default:
          throw new Error(`Unsupported YAML escape '\\${next}'.`);
      }
    }
    return sb;
  }

  private static isLetterOrDigit(ch: string): boolean {
    return /[\p{L}\p{Nd}]/u.test(ch);
  }

  /** C# string.TrimStart() over ASCII/Unicode whitespace. */
  private static trimStart(s: string): string {
    return s.replace(/^\s+/u, "");
  }

  /** C# string.TrimEnd(). */
  private static trimEnd(s: string): string {
    return s.replace(/\s+$/u, "");
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// KnowledgeNote — markdown knowledge entry (frontmatter + body). Mirrors C#
// `KnowledgeNote` record incl. ToFileText / ParseFile.
// ─────────────────────────────────────────────────────────────────────────────

const NOTE_TITLE_KEY = "title";
const NOTE_CREATED_KEY = "created_at";
const NOTE_UPDATED_KEY = "updated_at";
const NOTE_ID_KEY = "id";
const NOTE_TAGS_KEY = "tags";

/** A markdown knowledge note. Mirrors C# `KnowledgeNote` record. */
export interface KnowledgeNote {
  /** Stable identifier — also the file stem on disk. */
  readonly id: string;
  /** Display title of the note. */
  readonly title: string;
  /** Markdown body content. */
  readonly bodyMarkdown: string;
  /** Arbitrary flat key-value metadata. Stored as YAML. */
  readonly frontmatter: ReadonlyMap<string, string>;
  /** Free-form tags for retrieval. */
  readonly tags: readonly string[];
  /** UTC creation time. */
  readonly createdAt: Date;
  /** UTC modification time. */
  readonly updatedAt: Date;
}

/** Constructs a {@link KnowledgeNote}. Mirrors the C# positional record ctor. */
export function knowledgeNote(
  id: string,
  title: string,
  bodyMarkdown: string,
  frontmatter: ReadonlyMap<string, string>,
  tags: readonly string[],
  createdAt: Date,
  updatedAt: Date,
): KnowledgeNote {
  return { id, title, bodyMarkdown, frontmatter, tags, createdAt, updatedAt };
}

/** Serialises `note` to its on-disk text form. Mirrors C# `KnowledgeNote.ToFileText`. */
export function knowledgeNoteToFileText(note: KnowledgeNote): string {
  // Merge well-known fields with the user-supplied frontmatter. The well-known
  // fields win — they are the canonical metadata.
  const merged = new Map<string, string>();
  for (const [k, v] of note.frontmatter) merged.set(k, v);
  merged.set(NOTE_ID_KEY, guidToD(note.id));
  merged.set(NOTE_TITLE_KEY, note.title);
  merged.set(NOTE_CREATED_KEY, note.createdAt.toISOString());
  merged.set(NOTE_UPDATED_KEY, note.updatedAt.toISOString());
  merged.set(NOTE_TAGS_KEY, note.tags.join(","));

  return YamlFrontmatter.write(merged, note.bodyMarkdown);
}

/** Parses the on-disk text form into a {@link KnowledgeNote}. Mirrors C# `KnowledgeNote.ParseFile`. */
export function parseKnowledgeNoteFile(text: string): KnowledgeNote {
  if (text == null) throw new Error("text required");
  const { frontmatter, body } = YamlFrontmatter.read(text);

  const idRaw = frontmatter.get(NOTE_ID_KEY);
  const id = idRaw != null ? tryParseGuid(idRaw) : null;
  if (id == null) throw new Error("Knowledge note frontmatter missing or invalid 'id'.");

  const title = frontmatter.get(NOTE_TITLE_KEY) ?? "";

  const created = parseNoteTimestamp(frontmatter, NOTE_CREATED_KEY);
  const updated = parseNoteTimestamp(frontmatter, NOTE_UPDATED_KEY);

  const rawTags = frontmatter.get(NOTE_TAGS_KEY);
  const tags =
    rawTags != null && rawTags.trim().length > 0
      ? rawTags
          .split(",")
          .map((t) => t.trim())
          .filter((t) => t.length > 0)
      : [];

  // Strip the well-known keys from the user-visible frontmatter view.
  const userFrontmatter = new Map<string, string>();
  for (const [k, v] of frontmatter) {
    if (
      k === NOTE_ID_KEY ||
      k === NOTE_TITLE_KEY ||
      k === NOTE_CREATED_KEY ||
      k === NOTE_UPDATED_KEY ||
      k === NOTE_TAGS_KEY
    )
      continue;
    userFrontmatter.set(k, v);
  }

  return knowledgeNote(id, title, body, userFrontmatter, tags, created, updated);
}

function parseNoteTimestamp(map: ReadonlyMap<string, string>, key: string): Date {
  const raw = map.get(key);
  if (raw == null || raw.trim().length === 0) return new Date();
  const parsed = new Date(raw);
  return Number.isNaN(parsed.getTime()) ? new Date() : parsed;
}

// ─────────────────────────────────────────────────────────────────────────────
// IKnowledgeStore — persistent store for KnowledgeNote documents.
// ─────────────────────────────────────────────────────────────────────────────

/** Persistent store for {@link KnowledgeNote} documents. Mirrors C# `IKnowledgeStore`. */
export interface IKnowledgeStore {
  /** Loads the note with the given id. Returns `null` when none exists. */
  getAsync(id: string, signal?: AbortSignal): Promise<KnowledgeNote | null>;
  /** Persists `note`. The returned record may differ (e.g. refreshed updatedAt). */
  saveAsync(note: KnowledgeNote, signal?: AbortSignal): Promise<KnowledgeNote>;
  /** Deletes the note with the given id. No-op if it does not exist. */
  deleteAsync(id: string, signal?: AbortSignal): Promise<void>;
  /** Streams notes carrying `tag` in their tags collection. */
  searchByTagAsync(tag: string, signal?: AbortSignal): AsyncIterable<KnowledgeNote>;
  /** Streams every note currently stored. */
  enumerateAllAsync(signal?: AbortSignal): AsyncIterable<KnowledgeNote>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-key async mutex — parity for the C# SemaphoreSlim(1,1)-per-Guid gate.
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
// FileSystemKnowledgeStore — one .md file per note under a configured root.
// Atomic write-then-rename; per-Guid mutex. Mirrors C# `FileSystemKnowledgeStore`.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * File-system {@link IKnowledgeStore}. Each note is stored as
 * `{rootDirectory}/{id-no-dashes}.md`. Single-process safe (per-Guid mutex +
 * atomic write-then-rename); NOT multi-replica safe.
 */
export class FileSystemKnowledgeStore implements IKnowledgeStore {
  readonly componentName = "FileSystemKnowledgeStore";

  private readonly rootDirectory: string;
  private readonly locks = new KeyedMutex();
  private readonly ready: Promise<void>;

  /**
   * Creates a new store rooted at `rootDirectory`. The directory is created if
   * it does not already exist.
   */
  constructor(rootDirectory: string) {
    if (rootDirectory == null || rootDirectory.trim().length === 0)
      throw new Error("rootDirectory required");
    this.rootDirectory = rootDirectory;
    this.ready = fs.mkdir(this.rootDirectory, { recursive: true }).then(() => undefined);
  }

  async getAsync(id: string, _signal?: AbortSignal): Promise<KnowledgeNote | null> {
    await this.ready;
    const p = this.notePath(id);
    if (!(await fileExists(p))) return null;

    return this.locks.run(guidToN(id), async () => {
      const text = await fs.readFile(p, "utf8");
      return parseKnowledgeNoteFile(text);
    });
  }

  async saveAsync(note: KnowledgeNote, _signal?: AbortSignal): Promise<KnowledgeNote> {
    if (note == null) throw new Error("note required");
    await this.ready;

    const refreshed: KnowledgeNote = { ...note, updatedAt: new Date() };
    const target = this.notePath(refreshed.id);
    const tmp = target + "." + guidToN(randomUUID()) + ".tmp";

    return this.locks.run(guidToN(refreshed.id), async () => {
      try {
        // Write to tmp first so a crash mid-write never corrupts the canonical file.
        await fs.writeFile(tmp, knowledgeNoteToFileText(refreshed), "utf8");
        await fs.rename(tmp, target);
        return refreshed;
      } catch (e) {
        try {
          if (await fileExists(tmp)) await fs.unlink(tmp);
        } catch {
          /* best-effort */
        }
        throw e;
      }
    });
  }

  async deleteAsync(id: string, _signal?: AbortSignal): Promise<void> {
    await this.ready;
    const p = this.notePath(id);
    await this.locks.run(guidToN(id), async () => {
      if (await fileExists(p)) await fs.unlink(p);
    });
  }

  async *searchByTagAsync(tag: string, signal?: AbortSignal): AsyncIterable<KnowledgeNote> {
    if (tag == null || tag.trim().length === 0) throw new Error("tag required");
    for await (const note of this.enumerateAllAsync(signal)) {
      if (note.tags.some((t) => t.toLowerCase() === tag.toLowerCase())) yield note;
    }
  }

  async *enumerateAllAsync(signal?: AbortSignal): AsyncIterable<KnowledgeNote> {
    await this.ready;
    let files: string[];
    try {
      files = (await fs.readdir(this.rootDirectory)).filter((f) => f.endsWith(".md"));
    } catch {
      return;
    }

    for (const file of files) {
      if (signal?.aborted) throw new DOMException("Aborted", "AbortError");
      let note: KnowledgeNote;
      try {
        const text = await fs.readFile(path.join(this.rootDirectory, file), "utf8");
        note = parseKnowledgeNoteFile(text);
      } catch {
        // Skip notes that are not in our format (e.g. a stray README.md).
        continue;
      }
      yield note;
    }
  }

  private notePath(id: string): string {
    return path.join(this.rootDirectory, guidToN(id) + ".md");
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// MarkdownEpisodicMemoryStore — IEpisodicMemoryStore backed by IKnowledgeStore.
// Mirrors C# `MarkdownEpisodicMemoryStore`.
// ─────────────────────────────────────────────────────────────────────────────

const EPISODE_ID_KEY = "episode_id";
const RECORDED_AT_KEY = "recorded_at";
const APP_CONTEXT_KEY = "app_context";
const EMBEDDING_KEY = "embedding";
const EMBEDDING_DIMS_KEY = "embedding_dims";
const TAG_PREFIX = "tag_";

/**
 * Markdown-on-disk implementation of {@link IEpisodicMemoryStore}. Backed by an
 * {@link IKnowledgeStore}; each {@link EpisodicMemoryEntry} is persisted as one
 * {@link KnowledgeNote}.
 */
export class MarkdownEpisodicMemoryStore implements IEpisodicMemoryStore {
  private readonly store: IKnowledgeStore;

  /** Creates a new episodic store backed by `store`. */
  constructor(store: IKnowledgeStore) {
    if (store == null) throw new Error("store required");
    this.store = store;
  }

  async addAsync(entry: EpisodicMemoryEntry): Promise<void> {
    if (entry == null) throw new Error("entry required");
    const note = MarkdownEpisodicMemoryStore.toNote(entry);
    await this.store.saveAsync(note);
  }

  async searchAsync(
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly EpisodicMemoryEntry[]> {
    const snapshot: EpisodicMemoryEntry[] = [];
    for await (const note of this.store.enumerateAllAsync()) {
      snapshot.push(MarkdownEpisodicMemoryStore.fromNote(note));
    }

    if (queryEmbedding == null || queryEmbedding.length === 0) {
      return snapshot
        .slice()
        .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
        .slice(0, topK);
    }

    return snapshot
      .filter((e) => e.embedding != null && e.embedding.length === queryEmbedding.length)
      .map((e) => ({ entry: e, score: cosineSimilarity(queryEmbedding, e.embedding as number[]) }))
      .sort((a, b) => b.score - a.score)
      .slice(0, topK)
      .map((x) => x.entry);
  }

  async getRecentAsync(count = 10): Promise<readonly EpisodicMemoryEntry[]> {
    const snapshot: EpisodicMemoryEntry[] = [];
    for await (const note of this.store.enumerateAllAsync()) {
      snapshot.push(MarkdownEpisodicMemoryStore.fromNote(note));
    }
    return snapshot
      .slice()
      .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
      .slice(0, count);
  }

  async countAsync(): Promise<number> {
    let n = 0;
    for await (const _ of this.store.enumerateAllAsync()) n++;
    return n;
  }

  async pruneOlderThanAsync(cutoff: Date): Promise<number> {
    const doomed: string[] = [];
    for await (const note of this.store.enumerateAllAsync()) {
      const entry = MarkdownEpisodicMemoryStore.fromNote(note);
      if (entry.recordedAtUtc.getTime() < cutoff.getTime()) doomed.push(note.id);
    }
    for (const id of doomed) {
      await this.store.deleteAsync(id);
    }
    return doomed.length;
  }

  // ── EpisodicMemoryEntry <-> KnowledgeNote ─────────────────────────────────

  /** Maps an {@link EpisodicMemoryEntry} to its {@link KnowledgeNote} representation. */
  static toNote(entry: EpisodicMemoryEntry): KnowledgeNote {
    const frontmatter = new Map<string, string>();
    frontmatter.set(EPISODE_ID_KEY, guidToD(entry.id));
    frontmatter.set(RECORDED_AT_KEY, entry.recordedAtUtc.toISOString());
    if (entry.appContext != null && entry.appContext.trim().length > 0)
      frontmatter.set(APP_CONTEXT_KEY, entry.appContext);

    if (entry.embedding != null && entry.embedding.length > 0) {
      // Encode the embedding as base64 of the raw float32[] bytes (little-endian).
      const buf = Buffer.alloc(entry.embedding.length * 4);
      for (let i = 0; i < entry.embedding.length; i++)
        buf.writeFloatLE(Math.fround(entry.embedding[i]), i * 4);
      frontmatter.set(EMBEDDING_KEY, buf.toString("base64"));
      frontmatter.set(EMBEDDING_DIMS_KEY, String(entry.embedding.length));
    }

    const tags: string[] = [];
    if (entry.tags != null) {
      for (const [k, v] of Object.entries(entry.tags)) {
        frontmatter.set(TAG_PREFIX + k, v);
        tags.push(k);
      }
    }

    const body = "## User\n\n" + entry.userText + "\n\n" + "## Assistant\n\n" + entry.assistantText;

    return knowledgeNote(
      entry.id === GUID_EMPTY ? randomUUID() : entry.id,
      truncateForTitle(entry.userText),
      body,
      frontmatter,
      tags,
      entry.recordedAtUtc,
      entry.recordedAtUtc,
    );
  }

  /** Inverse of {@link MarkdownEpisodicMemoryStore.toNote}. */
  static fromNote(note: KnowledgeNote): EpisodicMemoryEntry {
    let episodeId = note.id;
    const raw = note.frontmatter.get(EPISODE_ID_KEY);
    if (raw != null) {
      const parsed = tryParseGuid(raw);
      if (parsed != null) episodeId = parsed;
    }

    let recordedAt = note.createdAt;
    const rawWhen = note.frontmatter.get(RECORDED_AT_KEY);
    if (rawWhen != null) {
      const when = new Date(rawWhen);
      if (!Number.isNaN(when.getTime())) recordedAt = when;
    }

    const appContext = note.frontmatter.get(APP_CONTEXT_KEY) ?? undefined;

    let embedding: number[] | undefined;
    const b64 = note.frontmatter.get(EMBEDDING_KEY);
    if (b64 != null && b64.trim().length > 0) {
      try {
        const bytes = Buffer.from(b64, "base64");
        const dims = Math.floor(bytes.length / 4);
        const out = new Array<number>(dims);
        for (let i = 0; i < dims; i++) out[i] = Math.fround(bytes.readFloatLE(i * 4));
        embedding = out;
      } catch {
        embedding = undefined;
      }
    }

    const { user: userText, assistant: assistantText } = splitBody(note.bodyMarkdown);

    let tagsOut: Record<string, string> | undefined;
    for (const [k, v] of note.frontmatter) {
      if (!k.startsWith(TAG_PREFIX)) continue;
      tagsOut ??= {};
      tagsOut[k.substring(TAG_PREFIX.length)] = v;
    }

    return {
      id: episodeId,
      recordedAtUtc: recordedAt,
      userText,
      assistantText,
      appContext,
      embedding,
      tags: tagsOut,
    };
  }
}

function splitBody(body: string): { user: string; assistant: string } {
  if (body == null || body.length === 0) return { user: "", assistant: "" };

  const normal = body.replace(/\r\n/g, "\n");
  const userMarker = "## User\n\n";
  const assistantMarker = "\n\n## Assistant\n\n";

  const userIdx = normal.indexOf(userMarker);
  const assistantIdx = normal.indexOf(assistantMarker);

  if (userIdx < 0 || assistantIdx <= userIdx) return { user: normal, assistant: "" };

  const userText = normal.substring(userIdx + userMarker.length, assistantIdx);
  const assistantText = normal.substring(assistantIdx + assistantMarker.length);

  return { user: userText, assistant: assistantText };
}

function truncateForTitle(source: string): string {
  if (source == null || source.trim().length === 0) return "(untitled)";
  const single = source.replace(/\n/g, " ").replace(/\r/g, " ").trim();
  return single.length <= 64 ? single : single.substring(0, 64);
}

/** Dot-product "cosine" — matches C# `MarkdownEpisodicMemoryStore.CosineSimilarity`. */
function cosineSimilarity(a: number[], b: number[]): number {
  let dot = Math.fround(0);
  for (let i = 0; i < a.length; i++) dot = Math.fround(dot + Math.fround(a[i] * b[i]));
  return dot;
}

// ── local fs helper ──────────────────────────────────────────────────────────

async function fileExists(p: string): Promise<boolean> {
  try {
    await fs.access(p);
    return true;
  } catch {
    return false;
  }
}
