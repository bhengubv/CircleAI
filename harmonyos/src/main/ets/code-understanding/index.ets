/**
 * code-understanding/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// code-understanding/index.ts
//
// Full-parity port of CircleAI.CodeUnderstanding (C#). C# is the exact spec.
//
// Code-understanding contracts: ICodeIndexer / ICodeSearch / ISymbolGraph,
// the CodeSymbol / CodeMatch / SymbolEdge records, a real filesystem indexer +
// index-backed search + in-memory symbol graph, and the Null* defaults.
//
// The C# FilesystemCodeIndexer walks the repo filesystem — ported with node:fs
// (a local, deterministic operation). Symbol-graph edges are host-populated.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   float Score                           → number (Math.fround at float sites)
//   IReadOnlyList<T>                       → readonly T[]
//   ValueTask<T>                          → Promise<T>

import { Files as fs } from "../platform";
import { Path as path } from "../platform";

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A source symbol. Mirrors C# `CodeSymbol`. */
export interface CodeSymbol {
  readonly path: string;
  readonly line: number;
  readonly name: string;
  readonly kind: string;
}

/** Constructs a {@link CodeSymbol}. */
export function codeSymbol(path: string, line: number, name: string, kind: string): CodeSymbol {
  return { path, line, name, kind };
}

/** A search match. Mirrors C# `CodeMatch`. */
export interface CodeMatch {
  readonly path: string;
  readonly line: number;
  readonly snippet: string;
  readonly score: number;
}

/** Constructs a {@link CodeMatch}. */
export function codeMatch(path: string, line: number, snippet: string, score: number): CodeMatch {
  return { path, line, snippet, score: Math.fround(score) };
}

/** An edge between two symbols. Mirrors C# `SymbolEdge`. */
export interface SymbolEdge {
  readonly from: CodeSymbol;
  readonly to: CodeSymbol;
  readonly kind: string;
}

/** Constructs a {@link SymbolEdge}. */
export function symbolEdge(from: CodeSymbol, to: CodeSymbol, kind: string): SymbolEdge {
  return { from, to, kind };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Code indexer. Mirrors C# `ICodeIndexer`. */
export interface ICodeIndexer {
  readonly backendId: string;
  indexAsync(repoPath: string): Promise<void>;
  countSymbolsAsync(repoPath: string): Promise<number>;
}

/** Code search. Mirrors C# `ICodeSearch`. */
export interface ICodeSearch {
  readonly backendId: string;
  searchAsync(query: string, topK?: number): Promise<readonly CodeMatch[]>;
  semanticSearchAsync(query: string, topK?: number): Promise<readonly CodeMatch[]>;
}

/** Symbol graph. Mirrors C# `ISymbolGraph`. */
export interface ISymbolGraph {
  readonly backendId: string;
  callersOfAsync(s: CodeSymbol): Promise<readonly SymbolEdge[]>;
  calleesOfAsync(s: CodeSymbol): Promise<readonly SymbolEdge[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Filesystem indexer
// ─────────────────────────────────────────────────────────────────────────────

interface LangRule {
  readonly ext: string;
  readonly declRx: RegExp;
  readonly kind: string;
}

// Mirrors the C# lookbehind regexes; JS supports lookbehind (ES2018+).
const LANGUAGES: readonly LangRule[] = [
  { ext: ".cs", declRx: /(?<=\b(?:class|interface|record|enum|struct)\s+)(\w+)/g, kind: "csharp" },
  { ext: ".cs", declRx: /(?<=\b(?:public|private|internal|protected|static)\s+\w+\s+)(\w+)\s*\(/g, kind: "csharp-method" },
  { ext: ".ts", declRx: /(?<=\b(?:class|interface|type|enum)\s+)(\w+)/g, kind: "ts" },
  { ext: ".js", declRx: /(?<=\b(?:class|function)\s+)(\w+)/g, kind: "js" },
  { ext: ".py", declRx: /(?<=^\s*(?:def|class)\s+)(\w+)/gm, kind: "python" },
  { ext: ".go", declRx: /(?<=^\s*func\s+(?:\(\w+\s+\*?\w+\)\s+)?)(\w+)/gm, kind: "go" },
];

const SOURCE_EXTS = new Set([".cs", ".ts", ".js", ".py", ".go"]);

/** Walks a repo and extracts declarations. Mirrors C# `FilesystemCodeIndexer`. */
export class FilesystemCodeIndexer implements ICodeIndexer {
  /** repoPath → symbols (internal; consumed by IndexBackedCodeSearch). */
  readonly index = new Map<string, CodeSymbol[]>();

  get backendId(): string {
    return "filesystem";
  }

  async indexAsync(repoPath: string): Promise<void> {
    if (repoPath == null || repoPath.trim().length === 0) throw new Error("repoPath required");
    if (!(await isDirectory(repoPath))) throw new Error(`directory not found: ${repoPath}`);

    const symbols: CodeSymbol[] = [];
    for (const filePath of await enumerateSourceFiles(repoPath)) {
      const content = await fs.readFile(filePath, "utf8");
      const lines = content.split(/\r\n|\r|\n/);
      const ext = path.extname(filePath).toLowerCase();
      for (let i = 0; i < lines.length; i++) {
        for (const lang of LANGUAGES) {
          if (lang.ext !== ext) continue;
          const rx = perLineRegex(lang.declRx);
          for (const m of lines[i].matchAll(rx)) {
            if (m[1] !== undefined) symbols.push(codeSymbol(filePath, i + 1, m[1], lang.kind));
          }
        }
      }
    }
    this.index.set(repoPath, symbols);
  }

  async countSymbolsAsync(repoPath: string): Promise<number> {
    const l = this.index.get(repoPath);
    return l ? l.length : 0;
  }
}

/** Index-backed search (substring over indexed symbol names). Mirrors C# `IndexBackedCodeSearch`. */
export class IndexBackedCodeSearch implements ICodeSearch {
  private readonly indexer: FilesystemCodeIndexer;

  constructor(indexer: FilesystemCodeIndexer) {
    if (indexer == null) throw new Error("indexer required");
    this.indexer = indexer;
  }

  get backendId(): string {
    return "index-backed";
  }

  async searchAsync(query: string, topK = 10): Promise<readonly CodeMatch[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK out of range");
    const q = query.toLowerCase();
    const out: CodeMatch[] = [];
    for (const list of this.indexer.index.values()) {
      for (const s of list) {
        if (s.name.toLowerCase().includes(q)) {
          out.push(codeMatch(s.path, s.line, `${s.kind} ${s.name}`, 1));
          if (out.length >= topK) return out;
        }
      }
    }
    return out;
  }

  async semanticSearchAsync(query: string, topK = 10): Promise<readonly CodeMatch[]> {
    // No real embedding; substring fallback (mirrors C#).
    return this.searchAsync(query, topK);
  }
}

/** Host-populated in-memory symbol graph. Mirrors C# `InMemorySymbolGraph`. */
export class InMemorySymbolGraph implements ISymbolGraph {
  private readonly edges: SymbolEdge[] = [];

  get backendId(): string {
    return "in-memory";
  }

  link(from: CodeSymbol, to: CodeSymbol, kind = "calls"): void {
    if (from == null) throw new Error("from required");
    if (to == null) throw new Error("to required");
    this.edges.push(symbolEdge(from, to, kind));
  }

  async callersOfAsync(s: CodeSymbol): Promise<readonly SymbolEdge[]> {
    return this.edges.filter((e) => e.to.name === s.name);
  }

  async calleesOfAsync(s: CodeSymbol): Promise<readonly SymbolEdge[]> {
    return this.edges.filter((e) => e.from.name === s.name);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link ICodeIndexer}. */
export class NullCodeIndexer implements ICodeIndexer {
  static readonly instance = new NullCodeIndexer();
  get backendId(): string {
    return "null";
  }
  async indexAsync(): Promise<void> {
    /* no-op */
  }
  async countSymbolsAsync(): Promise<number> {
    return 0;
  }
}

/** Fail-safe {@link ICodeSearch}. */
export class NullCodeSearch implements ICodeSearch {
  static readonly instance = new NullCodeSearch();
  get backendId(): string {
    return "null";
  }
  async searchAsync(): Promise<readonly CodeMatch[]> {
    return [];
  }
  async semanticSearchAsync(): Promise<readonly CodeMatch[]> {
    return [];
  }
}

/** Fail-safe {@link ISymbolGraph}. */
export class NullSymbolGraph implements ISymbolGraph {
  static readonly instance = new NullSymbolGraph();
  get backendId(): string {
    return "null";
  }
  async callersOfAsync(): Promise<readonly SymbolEdge[]> {
    return [];
  }
  async calleesOfAsync(): Promise<readonly SymbolEdge[]> {
    return [];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

/** Fresh RegExp with the global flag reset (matchAll requires /g). */
function perLineRegex(rx: RegExp): RegExp {
  return new RegExp(rx.source, rx.flags.includes("g") ? rx.flags : rx.flags + "g");
}

async function isDirectory(p: string): Promise<boolean> {
  try {
    return (await fs.stat(p)).isDirectory();
  } catch {
    return false;
  }
}

/** Recursively enumerates .cs/.ts/.js/.py/.go files, skipping obj/bin/node_modules. */
async function enumerateSourceFiles(root: string): Promise<string[]> {
  const out: string[] = [];
  const sep = path.sep;
  async function walk(dir: string): Promise<void> {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) {
        await walk(full);
      } else if (e.isFile()) {
        const ext = path.extname(full).toLowerCase();
        if (!SOURCE_EXTS.has(ext)) continue;
        if (full.includes(`${sep}obj${sep}`)) continue;
        if (full.includes(`${sep}bin${sep}`)) continue;
        if (full.includes(`${sep}node_modules${sep}`)) continue;
        out.push(full);
      }
    }
  }
  await walk(root);
  return out;
}
