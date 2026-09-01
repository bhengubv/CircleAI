/**
 * dev-tools/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// dev-tools/index.ts
//
// Full-parity port of CircleAI.DevTools (C#). C# is the exact spec.
//
// The dev-tools replacement surface: ICodeEditor / IInlineSuggester /
// IAgentShell / IPatchPlanner / IRefactorTool, the FileEdit / InlineSuggestion /
// AgentTurn / PatchPlan / RefactorRequest records, real filesystem-backed
// implementations, and the Null* defaults.
//
// The C# implementations read/write files on disk — ported with node:fs (a
// local, deterministic operation).
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   float Confidence                      → number (Math.fround at float sites)
//   IReadOnlyList<FileEdit>               → readonly FileEdit[]
//   InlineSuggestion?                     → InlineSuggestion | null
//   ValueTask<T>                          → Promise<T>

import { Files as fs } from "../platform";
import { Path as path } from "../platform";

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** One text-buffer edit. Mirrors C# `FileEdit`. */
export interface FileEdit {
  readonly path: string;
  readonly rangeStart: number;
  readonly rangeEnd: number;
  readonly replacement: string;
}

/** Constructs a {@link FileEdit}. */
export function fileEdit(path: string, rangeStart: number, rangeEnd: number, replacement: string): FileEdit {
  return { path, rangeStart, rangeEnd, replacement };
}

/** A ghost-text suggestion. Mirrors C# `InlineSuggestion`. */
export interface InlineSuggestion {
  readonly text: string;
  readonly confidence: number;
}

/** Constructs an {@link InlineSuggestion}. */
export function inlineSuggestion(text: string, confidence: number): InlineSuggestion {
  return { text, confidence: Math.fround(confidence) };
}

/** One agent-shell turn. Mirrors C# `AgentTurn`. */
export interface AgentTurn {
  readonly turnId: string;
  readonly userPrompt: string;
  readonly response: string;
  readonly edits: readonly FileEdit[];
}

/** Constructs an {@link AgentTurn}. */
export function agentTurn(turnId: string, userPrompt: string, response: string, edits: readonly FileEdit[]): AgentTurn {
  return { turnId, userPrompt, response, edits };
}

/** A multi-file patch plan. Mirrors C# `PatchPlan`. */
export interface PatchPlan {
  readonly goal: string;
  readonly steps: readonly string[];
  readonly proposedEdits: readonly FileEdit[];
}

/** Constructs a {@link PatchPlan}. */
export function patchPlan(goal: string, steps: readonly string[], proposedEdits: readonly FileEdit[]): PatchPlan {
  return { goal, steps, proposedEdits };
}

/** A refactor request. Mirrors C# `RefactorRequest`. */
export interface RefactorRequest {
  readonly description: string;
  readonly targetPaths: readonly string[];
}

/** Constructs a {@link RefactorRequest}. */
export function refactorRequest(description: string, targetPaths: readonly string[]): RefactorRequest {
  return { description, targetPaths };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Read / write text buffers. Mirrors C# `ICodeEditor`. */
export interface ICodeEditor {
  readonly backendId: string;
  readAsync(path: string): Promise<string>;
  applyAsync(edits: readonly FileEdit[]): Promise<void>;
  saveAsync(path: string): Promise<void>;
}

/** Tab-completion / ghost-text suggester. Mirrors C# `IInlineSuggester`. */
export interface IInlineSuggester {
  readonly backendId: string;
  suggestAsync(path: string, line: number, column: number, contextBefore: string): Promise<InlineSuggestion | null>;
}

/** Agent-shell loop. Mirrors C# `IAgentShell`. */
export interface IAgentShell {
  readonly backendId: string;
  runTurnAsync(userPrompt: string): Promise<AgentTurn>;
  historyAsync(limit?: number): Promise<readonly AgentTurn[]>;
}

/** Multi-file patch planner. Mirrors C# `IPatchPlanner`. */
export interface IPatchPlanner {
  readonly backendId: string;
  planAsync(goal: string): Promise<PatchPlan>;
  applyAsync(plan: PatchPlan): Promise<void>;
}

/** Cross-file refactor primitives. Mirrors C# `IRefactorTool`. */
export interface IRefactorTool {
  readonly backendId: string;
  proposeAsync(request: RefactorRequest): Promise<readonly FileEdit[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Filesystem implementations
// ─────────────────────────────────────────────────────────────────────────────

/** Filesystem editor — applies FileEdits by offset. Mirrors C# `FilesystemCodeEditor`. */
export class FilesystemCodeEditor implements ICodeEditor {
  get backendId(): string {
    return "filesystem";
  }

  async readAsync(p: string): Promise<string> {
    if (p == null || p.trim().length === 0) throw new Error("path required");
    return fs.readFile(p, "utf8");
  }

  async applyAsync(edits: readonly FileEdit[]): Promise<void> {
    if (edits == null) throw new Error("edits required");
    for (const [file, group] of groupBy(edits, (e) => e.path)) {
      const text = await fs.readFile(file, "utf8");
      // Apply highest-offset edits first so earlier offsets stay valid.
      const ordered = [...group].sort((a, b) => b.rangeStart - a.rangeStart);
      let buf = text;
      for (const e of ordered) {
        if (e.rangeStart < 0 || e.rangeEnd > buf.length || e.rangeEnd < e.rangeStart) {
          throw new Error(`Invalid edit range ${e.rangeStart}..${e.rangeEnd} for ${e.path}`);
        }
        buf = buf.slice(0, e.rangeStart) + e.replacement + buf.slice(e.rangeEnd);
      }
      await fs.writeFile(file, buf, "utf8");
    }
  }

  async saveAsync(): Promise<void> {
    /* no-op — applyAsync already persists */
  }
}

const IDENTIFIER_RX = /[A-Za-z_][A-Za-z0-9_]*/g;

/** Inline suggester from file identifier vocabulary. Mirrors C# `TokenContextInlineSuggester`. */
export class TokenContextInlineSuggester implements IInlineSuggester {
  get backendId(): string {
    return "token-context";
  }

  async suggestAsync(
    p: string,
    _line: number,
    _column: number,
    contextBefore: string,
  ): Promise<InlineSuggestion | null> {
    if (p == null || p.trim().length === 0) throw new Error("path required");
    if (contextBefore == null) throw new Error("contextBefore required");

    const partial = extractPartialAtCursor(contextBefore);
    if (partial.length < 2) return null;

    const fileText = (await fileExists(p)) ? await fs.readFile(p, "utf8") : contextBefore;
    const freq = new Map<string, number>();
    for (const m of fileText.matchAll(IDENTIFIER_RX)) {
      const v = m[0];
      if (v.startsWith(partial) && v.length > partial.length) {
        freq.set(v, (freq.get(v) ?? 0) + 1);
      }
    }
    if (freq.size === 0) return null;
    // Highest frequency, then shortest identifier (matches C# ThenBy length).
    let best: { key: string; count: number } | null = null;
    for (const [key, count] of freq) {
      if (best === null || count > best.count || (count === best.count && key.length < best.key.length)) {
        best = { key, count };
      }
    }
    const b = best as { key: string; count: number };
    const completion = b.key.substring(partial.length);
    const confidence = Math.min(1.0, b.count / 10.0);
    return inlineSuggestion(completion, confidence);
  }
}

/** Executor signature for the agent shell (mirrors the C# Func). */
export type AgentExecutor = (prompt: string) => Promise<AgentTurn>;

/** Agent shell with turn history + built-in echo executor. Mirrors C# `InMemoryAgentShell`. */
export class InMemoryAgentShell implements IAgentShell {
  private readonly executor: AgentExecutor;
  private readonly history: AgentTurn[] = [];
  private seq = 0;

  constructor(executor?: AgentExecutor) {
    this.executor = executor ?? InMemoryAgentShell.builtInExecutor;
  }

  get backendId(): string {
    return "in-memory";
  }

  async runTurnAsync(userPrompt: string): Promise<AgentTurn> {
    if (userPrompt == null) throw new Error("userPrompt required");
    const t = await this.executor(userPrompt);
    const turn = t.turnId == null || t.turnId.length === 0 ? { ...t, turnId: `turn-${++this.seq}` } : t;
    this.history.push(turn);
    return turn;
  }

  async historyAsync(limit = 50): Promise<readonly AgentTurn[]> {
    if (limit <= 0) throw new Error("limit out of range");
    // Newest `limit`, returned oldest-first (mirrors C# Reverse/Take/Reverse).
    return this.history.slice(Math.max(0, this.history.length - limit));
  }

  private static async builtInExecutor(prompt: string): Promise<AgentTurn> {
    const trimmed = prompt.trim();
    let response: string;
    if (/^read /i.test(trimmed)) response = `Reading ${trimmed.substring(5)} ...`;
    else if (/^write /i.test(trimmed)) response = `Writing ${trimmed.substring(6)} ...`;
    else if (trimmed.includes("?")) response = "Acknowledged the question; need more context to give a useful answer.";
    else response = `Acknowledged: ${trimmed}.`;
    return agentTurn("", prompt, response, []);
  }
}

const RENAME_RX = /^rename\s+(\S+)\s+to\s+(\S+)(?:\s+in\s+(.+))?$/i;
const REMOVE_RX = /^remove\s+line\s+(\d+)\s+from\s+(.+)$/i;
const APPEND_RX = /^append\s+(.+?)\s+to\s+(.+)$/i;

/** Patch planner that parses goal text into real FileEdits. Mirrors C# `PatternMatchPatchPlanner`. */
export class PatternMatchPatchPlanner implements IPatchPlanner {
  private readonly editor: ICodeEditor;

  constructor(editor: ICodeEditor) {
    if (editor == null) throw new Error("editor required");
    this.editor = editor;
  }

  get backendId(): string {
    return "pattern-match";
  }

  async planAsync(goal: string): Promise<PatchPlan> {
    if (goal == null || goal.trim().length === 0) throw new Error("goal required");

    const rename = RENAME_RX.exec(goal);
    if (rename) {
      const oldName = rename[1];
      const newName = rename[2];
      const scope = rename[3] !== undefined ? rename[3] : Files.root();
      const edits = await computeRenameEdits(scope, oldName, newName);
      return patchPlan(goal, [`Rename '${oldName}' -> '${newName}' across ${edits.length} location(s)`], edits);
    }
    const remove = REMOVE_RX.exec(goal);
    if (remove) {
      const lineNo = parseInt(remove[1], 10);
      const p = remove[2].trim();
      const edits = await computeRemoveLineEdits(p, lineNo);
      return patchPlan(goal, [`Remove line ${lineNo} from ${p}`], edits);
    }
    const append = APPEND_RX.exec(goal);
    if (append) {
      const text = trimQuotes(append[1].trim());
      const p = append[2].trim();
      const len = (await fileExists(p)) ? (await fs.readFile(p, "utf8")).length : 0;
      const edits: FileEdit[] = [fileEdit(p, len, len, text)];
      return patchPlan(goal, [`Append to ${p}`], edits);
    }
    return patchPlan(goal, ["no recognised intent"], []);
  }

  async applyAsync(plan: PatchPlan): Promise<void> {
    if (plan == null) throw new Error("plan required");
    return this.editor.applyAsync(plan.proposedEdits);
  }
}

/** Regex refactor tool — Rename + ExtractConstant. Mirrors C# `RegexRefactorTool`. */
export class RegexRefactorTool implements IRefactorTool {
  get backendId(): string {
    return "regex";
  }

  async proposeAsync(request: RefactorRequest): Promise<readonly FileEdit[]> {
    if (request == null) throw new Error("request required");
    if (request.targetPaths == null) throw new Error("targetPaths required");
    const description = (request.description ?? "").trim();
    if (/^rename /i.test(description)) {
      const m = /^rename\s+(\S+)\s+to\s+(\S+)/i.exec(description);
      if (!m) return [];
      return renameInFiles(request.targetPaths, m[1], m[2]);
    }
    if (/^extract /i.test(description)) {
      const m = /^extract\s+constant\s+from\s+"([^"]+)"\s+as\s+(\S+)/i.exec(description);
      if (!m) return [];
      return extractConstant(request.targetPaths, m[1], m[2]);
    }
    return [];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

/** Fail-safe {@link ICodeEditor}. */
export class NullCodeEditor implements ICodeEditor {
  static readonly instance = new NullCodeEditor();
  get backendId(): string {
    return "null";
  }
  async readAsync(): Promise<string> {
    return "";
  }
  async applyAsync(): Promise<void> {
    /* no-op */
  }
  async saveAsync(): Promise<void> {
    /* no-op */
  }
}

/** Fail-safe {@link IInlineSuggester}. */
export class NullInlineSuggester implements IInlineSuggester {
  static readonly instance = new NullInlineSuggester();
  get backendId(): string {
    return "null";
  }
  async suggestAsync(): Promise<InlineSuggestion | null> {
    return null;
  }
}

/** Fail-safe {@link IAgentShell}. */
export class NullAgentShell implements IAgentShell {
  static readonly instance = new NullAgentShell();
  get backendId(): string {
    return "null";
  }
  async runTurnAsync(prompt: string): Promise<AgentTurn> {
    return agentTurn(EMPTY_GUID, prompt, "", []);
  }
  async historyAsync(): Promise<readonly AgentTurn[]> {
    return [];
  }
}

/** Fail-safe {@link IPatchPlanner}. */
export class NullPatchPlanner implements IPatchPlanner {
  static readonly instance = new NullPatchPlanner();
  get backendId(): string {
    return "null";
  }
  async planAsync(goal: string): Promise<PatchPlan> {
    return patchPlan(goal, [], []);
  }
  async applyAsync(): Promise<void> {
    /* no-op */
  }
}

/** Fail-safe {@link IRefactorTool}. */
export class NullRefactorTool implements IRefactorTool {
  static readonly instance = new NullRefactorTool();
  get backendId(): string {
    return "null";
  }
  async proposeAsync(): Promise<readonly FileEdit[]> {
    return [];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

function extractPartialAtCursor(contextBefore: string): string {
  let i = contextBefore.length;
  while (i > 0) {
    const ch = contextBefore[i - 1];
    if (/[A-Za-z0-9]/.test(ch) || ch === "_") i--;
    else break;
  }
  return contextBefore.substring(i);
}

async function computeRenameEdits(scope: string, oldName: string, newName: string): Promise<FileEdit[]> {
  const isDir = await isDirectory(scope);
  const isFile = await fileExists(scope);
  if (!isDir && !isFile) throw new Error(`directory not found: ${scope}`);
  const files = isFile ? [scope] : await enumerateCsFiles(scope);
  const edits: FileEdit[] = [];
  const sep = path.sep;
  for (const f of files) {
    if (f.includes(`${sep}obj${sep}`)) continue;
    if (f.includes(`${sep}bin${sep}`)) continue;
    const text = await fs.readFile(f, "utf8");
    const rx = new RegExp(`\\b${escapeRegex(oldName)}\\b`, "g");
    for (const m of text.matchAll(rx)) {
      const idx = m.index ?? 0;
      edits.push(fileEdit(f, idx, idx + m[0].length, newName));
    }
  }
  return edits;
}

async function computeRemoveLineEdits(p: string, lineNo: number): Promise<FileEdit[]> {
  if (!(await fileExists(p))) throw new Error(`file not found: ${p}`);
  const text = await fs.readFile(p, "utf8");
  let current = 1;
  for (let i = 0; i < text.length; i++) {
    if (current === lineNo) {
      const end = text.indexOf("\n", i);
      const rangeEnd = end < 0 ? text.length : end + 1;
      return [fileEdit(p, i, rangeEnd, "")];
    }
    if (text[i] === "\n") current++;
  }
  return [];
}

async function renameInFiles(paths: readonly string[], oldName: string, newName: string): Promise<FileEdit[]> {
  const edits: FileEdit[] = [];
  for (const p of paths) {
    if (!(await fileExists(p))) continue;
    const text = await fs.readFile(p, "utf8");
    const rx = new RegExp(`\\b${escapeRegex(oldName)}\\b`, "g");
    for (const m of text.matchAll(rx)) {
      const idx = m.index ?? 0;
      edits.push(fileEdit(p, idx, idx + m[0].length, newName));
    }
  }
  return edits;
}

async function extractConstant(paths: readonly string[], literal: string, constantName: string): Promise<FileEdit[]> {
  const edits: FileEdit[] = [];
  const quoted = '"' + literal + '"';
  for (const p of paths) {
    if (!(await fileExists(p))) continue;
    const text = await fs.readFile(p, "utf8");
    const first = text.indexOf(quoted);
    if (first < 0) continue;
    const classIdx = text.indexOf("class ");
    if (classIdx < 0) continue;
    const brace = text.indexOf("{", classIdx);
    if (brace < 0) continue;
    const insertion = `\n    private const string ${constantName} = ${quoted};\n`;
    edits.push(fileEdit(p, brace + 1, brace + 1, insertion));
    for (let idx = first; idx >= 0; idx = text.indexOf(quoted, idx + 1)) {
      edits.push(fileEdit(p, idx, idx + quoted.length, constantName));
    }
  }
  return edits;
}

/** Groups edits by path, preserving first-seen key order (LINQ GroupBy). */
function groupBy<T>(items: readonly T[], keyOf: (t: T) => string): Map<string, T[]> {
  const m = new Map<string, T[]>();
  for (const it of items) {
    const k = keyOf(it);
    const l = m.get(k) ?? [];
    l.push(it);
    m.set(k, l);
  }
  return m;
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function trimQuotes(s: string): string {
  return s.replace(/^"+|"+$/g, "");
}

async function fileExists(p: string): Promise<boolean> {
  try {
    return (await fs.stat(p)).isFile();
  } catch {
    return false;
  }
}

async function isDirectory(p: string): Promise<boolean> {
  try {
    return (await fs.stat(p)).isDirectory();
  } catch {
    return false;
  }
}

async function enumerateCsFiles(root: string): Promise<string[]> {
  const out: string[] = [];
  async function walk(dir: string): Promise<void> {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) await walk(full);
      else if (e.isFile() && path.extname(full).toLowerCase() === ".cs") out.push(full);
    }
  }
  await walk(root);
  return out;
}
