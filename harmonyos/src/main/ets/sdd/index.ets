/**
 * sdd/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// sdd/index.ts
//
// Full-parity port of CircleAI.SDD (C#). C# is the exact spec.
//
// Spec-Driven Development contracts: ISpecificationStore / ISpecificationValidator
// / ISpecToScaffold, the Specification / SpecValidationResult / ScaffoldedProject
// records, deterministic in-memory implementations, and the Null* defaults.
//
// Type mappings (C# → TS):
//   record                                            → readonly interface (+ factory)
//   string? Schema                                     → string | null
//   IReadOnlyDictionary<string, ReadOnlyMemory<byte>>  → ReadonlyMap<string, Uint8Array>
//   IReadOnlyDictionary<string,string>?                → Readonly<Map<string, string>> | null
//   JsonDocument.Parse                                 → JSON.parse
//   ValueTask<T>                                        → Promise<T>

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A specification. Mirrors C# `Specification`. */
export interface Specification {
  readonly specId: string;
  readonly title: string;
  readonly body: string;
  readonly schema: string | null;
  readonly metadata: Readonly<Map<string, string>> | null;
}

/** Constructs a {@link Specification}. */
export function specification(
  specId: string,
  title: string,
  body: string,
  schema: string | null,
  metadata: Readonly<Map<string, string>> | null = null,
): Specification {
  return { specId, title, body, schema, metadata };
}

/** A spec-validation result. Mirrors C# `SpecValidationResult`. */
export interface SpecValidationResult {
  readonly isValid: boolean;
  readonly errors: readonly string[];
}

/** Constructs a {@link SpecValidationResult}. */
export function specValidationResult(isValid: boolean, errors: readonly string[]): SpecValidationResult {
  return { isValid, errors };
}

/** A scaffolded project. Mirrors C# `ScaffoldedProject`. */
export interface ScaffoldedProject {
  readonly projectId: string;
  readonly files: ReadonlyMap<string, Uint8Array>;
}

/** Constructs a {@link ScaffoldedProject}. */
export function scaffoldedProject(projectId: string, files: ReadonlyMap<string, Uint8Array>): ScaffoldedProject {
  return { projectId, files };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Persistent specification store. Mirrors C# `ISpecificationStore`. */
export interface ISpecificationStore {
  readonly backendId: string;
  upsertAsync(spec: Specification): Promise<void>;
  getAsync(specId: string): Promise<Specification | null>;
  listAsync(): Promise<readonly Specification[]>;
}

/** Specification validator. Mirrors C# `ISpecificationValidator`. */
export interface ISpecificationValidator {
  readonly backendId: string;
  validateAsync(spec: Specification): Promise<SpecValidationResult>;
}

/** Spec → scaffolded project. Mirrors C# `ISpecToScaffold`. */
export interface ISpecToScaffold {
  readonly backendId: string;
  scaffoldAsync(spec: Specification, targetLanguage: string): Promise<ScaffoldedProject>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory specification store. Mirrors C# `InMemorySpecificationStore`. */
export class InMemorySpecificationStore implements ISpecificationStore {
  private readonly items = new Map<string, Specification>();

  get backendId(): string {
    return "in-memory";
  }

  async upsertAsync(spec: Specification): Promise<void> {
    if (spec == null) throw new Error("spec required");
    if (spec.specId == null || spec.specId.trim().length === 0) throw new Error("SpecId required");
    this.items.set(spec.specId, spec);
  }

  async getAsync(specId: string): Promise<Specification | null> {
    if (specId == null || specId.trim().length === 0) throw new Error("specId required");
    return this.items.get(specId) ?? null;
  }

  async listAsync(): Promise<readonly Specification[]> {
    return [...this.items.values()];
  }
}

/** JSON-shape validator. Mirrors C# `JsonShapeSpecificationValidator`. */
export class JsonShapeSpecificationValidator implements ISpecificationValidator {
  get backendId(): string {
    return "json-shape";
  }

  async validateAsync(spec: Specification): Promise<SpecValidationResult> {
    if (spec == null) throw new Error("spec required");
    const errors: string[] = [];
    if (spec.title == null || spec.title.trim().length === 0) errors.push("Title is required.");
    if (spec.body == null || spec.body.trim().length === 0) errors.push("Body is required.");
    if (spec.schema != null && spec.schema.trim().length > 0) {
      try {
        const root = JSON.parse(spec.schema) as Object;
        if (!isObject(root)) errors.push("Schema must be a JSON object.");
        else if (!("type" in root)) errors.push("Schema must declare a top-level 'type'.");
      } catch (ex) {
        errors.push(`Schema is not valid JSON: ${(ex as Error).message}`);
      }
    }
    return specValidationResult(errors.length === 0, errors);
  }
}

/** Spec → minimal compilable project. Mirrors C# `HelloWorldSpecToScaffold`. */
export class HelloWorldSpecToScaffold implements ISpecToScaffold {
  get backendId(): string {
    return "hello-world";
  }

  async scaffoldAsync(spec: Specification, targetLanguage: string): Promise<ScaffoldedProject> {
    if (spec == null) throw new Error("spec required");
    if (targetLanguage == null || targetLanguage.trim().length === 0) throw new Error("targetLanguage required");

    const files = new Map<string, Uint8Array>();
    const lang = targetLanguage.toLowerCase();
    const name = sanitizeName(spec.specId);

    switch (lang) {
      case "csharp":
      case "c#":
        files.set("Program.cs", bytes(`Console.WriteLine("${name}: ${escapeText(spec.title)}");\n`));
        files.set(
          `${name}.csproj`,
          bytes(
            '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>\n</Project>\n',
          ),
        );
        files.set("README.md", bytes(`# ${escapeText(spec.title)}\n\n${escapeText(spec.body)}\n`));
        break;
      case "typescript":
      case "ts":
        files.set("index.ts", bytes(`console.log("${name}: ${escapeText(spec.title)}");\n`));
        files.set(
          "package.json",
          bytes(`{"name":"${name}","version":"0.1.0","main":"index.ts","scripts":{"start":"ts-node index.ts"}}\n`),
        );
        files.set("tsconfig.json", bytes('{"compilerOptions":{"strict":true,"target":"ES2022","module":"commonjs"}}\n'));
        files.set("README.md", bytes(`# ${escapeText(spec.title)}\n\n${escapeText(spec.body)}\n`));
        break;
      case "python":
      case "py":
        files.set(
          "main.py",
          bytes(`def main():\n    print("${name}: ${escapeText(spec.title)}")\n\nif __name__ == "__main__":\n    main()\n`),
        );
        files.set("pyproject.toml", bytes(`[project]\nname = "${name}"\nversion = "0.1.0"\nrequires-python = ">=3.10"\n`));
        files.set("README.md", bytes(`# ${escapeText(spec.title)}\n\n${escapeText(spec.body)}\n`));
        break;
      default:
        throw new Error(
          `Language '${targetLanguage}' is not supported by HelloWorldSpecToScaffold (csharp / typescript / python).`,
        );
    }

    return scaffoldedProject(`${name}-${lang}`, files);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

/** Fail-safe {@link ISpecificationStore}. */
export class NullSpecificationStore implements ISpecificationStore {
  static readonly instance = new NullSpecificationStore();
  get backendId(): string {
    return "null";
  }
  async upsertAsync(): Promise<void> {
    /* no-op */
  }
  async getAsync(): Promise<Specification | null> {
    return null;
  }
  async listAsync(): Promise<readonly Specification[]> {
    return [];
  }
}

/** Fail-safe {@link ISpecificationValidator} — always invalid. */
export class NullSpecificationValidator implements ISpecificationValidator {
  static readonly instance = new NullSpecificationValidator();
  get backendId(): string {
    return "null";
  }
  async validateAsync(): Promise<SpecValidationResult> {
    return specValidationResult(false, ["No real validator wired."]);
  }
}

/** Fail-safe {@link ISpecToScaffold} — empty scaffold. */
export class NullSpecToScaffold implements ISpecToScaffold {
  static readonly instance = new NullSpecToScaffold();
  get backendId(): string {
    return "null";
  }
  async scaffoldAsync(): Promise<ScaffoldedProject> {
    return scaffoldedProject(EMPTY_GUID, new Map<string, Uint8Array>());
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

function isObject(v: Object): v is Map<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function sanitizeName(id: string): string {
  if (id == null || id.trim().length === 0) return "project";
  let s = "";
  for (const ch of id) {
    if (/[A-Za-z0-9]/.test(ch) || ch === "_" || ch === "-") s += ch;
  }
  return s.length === 0 ? "project" : s;
}

function escapeText(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\n");
}

function bytes(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}
