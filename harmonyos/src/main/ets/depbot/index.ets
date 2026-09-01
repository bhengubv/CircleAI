/**
 * depbot/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// depbot/index.ts
//
// Full-parity port of CircleAI.DepBot (C#). C# is the exact spec.
//
// Dependency analyzer + updater: IDependencyAnalyzer / IDependencyUpdater,
// the Dependency / DependencyUpdate records, a real filesystem analyzer that
// scans package.json / requirements.txt / Cargo.toml / *.csproj manifests, a
// text-rewrite updater, and the Null* defaults.
//
// The C# implementations read/write manifest files on disk — ported with
// node:fs (a local, deterministic operation).
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   string? LatestVersion                 → string | null
//   IReadOnlyList<T>                       → readonly T[]
//   ValueTask<T>                          → Promise<T>

import { Files as fs } from "../platform";
import { Path as path } from "../platform";

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A declared dependency. Mirrors C# `Dependency`. */
export interface Dependency {
  readonly ecosystem: string;
  readonly name: string;
  readonly currentVersion: string;
  readonly latestVersion: string | null;
}

/** Constructs a {@link Dependency}. */
export function dependency(
  ecosystem: string,
  name: string,
  currentVersion: string,
  latestVersion: string | null,
): Dependency {
  return { ecosystem, name, currentVersion, latestVersion };
}

/** A proposed dependency update. Mirrors C# `DependencyUpdate`. */
export interface DependencyUpdate {
  readonly ecosystem: string;
  readonly name: string;
  readonly fromVersion: string;
  readonly toVersion: string;
  readonly isBreaking: boolean;
}

/** Constructs a {@link DependencyUpdate}. */
export function dependencyUpdate(
  ecosystem: string,
  name: string,
  fromVersion: string,
  toVersion: string,
  isBreaking: boolean,
): DependencyUpdate {
  return { ecosystem, name, fromVersion, toVersion, isBreaking };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Dependency analyzer. Mirrors C# `IDependencyAnalyzer`. */
export interface IDependencyAnalyzer {
  readonly backendId: string;
  scanAsync(repoPath: string): Promise<readonly Dependency[]>;
}

/** Dependency updater. Mirrors C# `IDependencyUpdater`. */
export interface IDependencyUpdater {
  readonly backendId: string;
  proposeUpdatesAsync(repoPath: string): Promise<readonly DependencyUpdate[]>;
  applyUpdateAsync(repoPath: string, update: DependencyUpdate): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Filesystem implementations
// ─────────────────────────────────────────────────────────────────────────────

/** Scans a repo for declared dependencies. Mirrors C# `FilesystemDependencyAnalyzer`. */
export class FilesystemDependencyAnalyzer implements IDependencyAnalyzer {
  get backendId(): string {
    return "filesystem";
  }

  async scanAsync(repoPath: string): Promise<readonly Dependency[]> {
    if (repoPath == null || repoPath.trim().length === 0) throw new Error("repoPath required");
    if (!(await isDirectory(repoPath))) throw new Error(`directory not found: ${repoPath}`);

    const results: Dependency[] = [];

    // npm / yarn
    for (const pkg of await findFiles(repoPath, (n) => n === "package.json")) {
      if (pkg.includes("node_modules")) continue;
      try {
        const doc = JSON.parse(await fs.readFile(pkg, "utf8")) as Object;
        if (isObject(doc)) {
          for (const key of ["dependencies", "devDependencies"]) {
            const section = doc[key];
            if (!isObject(section)) continue;
            for (const [name, ver] of Object.entries(section)) {
              results.push(dependency("npm", name, typeof ver === "string" ? ver : "", null));
            }
          }
        }
      } catch {
        /* skip malformed file */
      }
    }

    // Python — requirements.txt
    for (const req of await findFiles(repoPath, (n) => n === "requirements.txt")) {
      const text = await fs.readFile(req, "utf8");
      for (const rawLine of text.split(/\r\n|\r|\n/)) {
        const line = rawLine.trim();
        if (line.length === 0 || line.startsWith("#")) continue;
        const match = /^([A-Za-z0-9_.\-]+)\s*([=<>!~]=?)?\s*([0-9.A-Za-z_\-]+)?/.exec(line);
        if (!match) continue;
        results.push(dependency("pypi", match[1], match[3] ?? "", null));
      }
    }

    // Rust — Cargo.toml [dependencies]
    for (const toml of await findFiles(repoPath, (n) => n === "Cargo.toml")) {
      if (toml.includes("target")) continue;
      let inDepsSection = false;
      const text = await fs.readFile(toml, "utf8");
      for (const rawLine of text.split(/\r\n|\r|\n/)) {
        const line = rawLine.trim();
        if (line.startsWith("[")) {
          inDepsSection = line.toLowerCase() === "[dependencies]";
          continue;
        }
        if (!inDepsSection || line.length === 0 || line.startsWith("#")) continue;
        const match = /^([A-Za-z0-9_\-]+)\s*=\s*"([^"]+)"/.exec(line);
        if (!match) continue;
        results.push(dependency("cargo", match[1], match[2], null));
      }
    }

    // .NET — *.csproj <PackageReference Include="X" Version="Y" />
    for (const csproj of await findFiles(repoPath, (n) => n.toLowerCase().endsWith(".csproj"))) {
      const text = await fs.readFile(csproj, "utf8");
      const rx = /<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"/g;
      for (const m of text.matchAll(rx)) {
        results.push(dependency("nuget", m[1], m[2], null));
      }
    }

    return results;
  }
}

/** Text-rewrite updater. Mirrors C# `TextRewriteDependencyUpdater`. */
export class TextRewriteDependencyUpdater implements IDependencyUpdater {
  get backendId(): string {
    return "text-rewrite";
  }

  async proposeUpdatesAsync(repoPath: string): Promise<readonly DependencyUpdate[]> {
    // Surfaces nothing without a registry (mirrors C#) — hosts feed a richer
    // update list through the same interface.
    if (repoPath == null || repoPath.trim().length === 0) throw new Error("repoPath required");
    return [];
  }

  async applyUpdateAsync(repoPath: string, update: DependencyUpdate): Promise<void> {
    if (update == null) throw new Error("update required");
    if (repoPath == null || repoPath.trim().length === 0) throw new Error("repoPath required");
    if (!(await isDirectory(repoPath))) throw new Error(`directory not found: ${repoPath}`);

    switch (update.ecosystem.toLowerCase()) {
      case "nuget":
        for (const csproj of await findFiles(repoPath, (n) => n.toLowerCase().endsWith(".csproj"))) {
          const text = await fs.readFile(csproj, "utf8");
          const pattern = new RegExp(`<PackageReference\\s+Include="${escapeRegex(update.name)}"\\s+Version="[^"]+"`, "g");
          const replacement = `<PackageReference Include="${update.name}" Version="${update.toVersion}"`;
          const updated = text.replace(pattern, replacement);
          if (updated !== text) await fs.writeFile(csproj, updated, "utf8");
        }
        break;

      case "npm":
        for (const pkg of await findFiles(repoPath, (n) => n === "package.json")) {
          if (pkg.includes("node_modules")) continue;
          const json = await fs.readFile(pkg, "utf8");
          const pattern = new RegExp(`"${escapeRegex(update.name)}"\\s*:\\s*"[^"]+"`, "g");
          const replacement = `"${update.name}": "${update.toVersion}"`;
          await fs.writeFile(pkg, json.replace(pattern, replacement), "utf8");
        }
        break;

      case "pypi":
        for (const req of await findFiles(repoPath, (n) => n === "requirements.txt")) {
          const lines = (await fs.readFile(req, "utf8")).split(/\r\n|\r|\n/);
          for (let i = 0; i < lines.length; i++) {
            const line = lines[i].trim();
            if (line.startsWith("#") || line.length === 0) continue;
            const m = new RegExp(`^${escapeRegex(update.name)}\\s*[=<>!~]=?\\s*[0-9.A-Za-z_\\-]+`).exec(line);
            if (m) lines[i] = `${update.name}==${update.toVersion}`;
          }
          await fs.writeFile(req, lines.join("\n"), "utf8");
        }
        break;
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link IDependencyAnalyzer}. */
export class NullDependencyAnalyzer implements IDependencyAnalyzer {
  static readonly instance = new NullDependencyAnalyzer();
  get backendId(): string {
    return "null";
  }
  async scanAsync(): Promise<readonly Dependency[]> {
    return [];
  }
}

/** Fail-safe {@link IDependencyUpdater}. */
export class NullDependencyUpdater implements IDependencyUpdater {
  static readonly instance = new NullDependencyUpdater();
  get backendId(): string {
    return "null";
  }
  async proposeUpdatesAsync(): Promise<readonly DependencyUpdate[]> {
    return [];
  }
  async applyUpdateAsync(): Promise<void> {
    /* no-op */
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

function isObject(v: Object): v is Map<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

async function isDirectory(p: string): Promise<boolean> {
  try {
    return (await fs.stat(p)).isDirectory();
  } catch {
    return false;
  }
}

/** Recursively finds files whose basename matches the predicate. */
async function findFiles(root: string, matchName: (name: string) => boolean): Promise<string[]> {
  const out: string[] = [];
  async function walk(dir: string): Promise<void> {
    let entries;
    try {
      entries = await fs.readdir(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) await walk(full);
      else if (e.isFile() && matchName(e.name)) out.push(full);
    }
  }
  await walk(root);
  return out;
}
