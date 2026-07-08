// hosting/tool_catalog.ts
//
// Port of the CircleAI.Hosting.Tools surface:
//   • IToolDescriptor.cs — ToolDescriptor + ToolExecutionResult
//   • IToolCatalog.cs     — IToolCatalog + IToolProvider + IToolExecutor
//   • InMemoryToolCatalog.cs — keyword-substring catalog + importFromAsync
//
// Search scoring (name 5 / tags 3 / desc 2), stable name-ordered listing, and
// case-insensitive provider filtering all match the C# reference exactly.

/**
 * Describes one tool callable by an LLM. Data-only. Mirrors
 * CircleAI.Hosting.Tools.ToolDescriptor.
 */
export interface ToolDescriptor {
  readonly name: string;
  readonly description: string;
  readonly provider: string;
  /** JSON Schema for the argument object. Empty string when arg-less. */
  readonly jsonSchema?: string;
  /** "none" | "oauth2" | "api-key" | "host". */
  readonly authScheme?: string;
  readonly tags?: readonly string[];
  readonly examples?: readonly string[];
}

/**
 * Result of one tool execution. Mirrors
 * CircleAI.Hosting.Tools.ToolExecutionResult.
 */
export interface ToolExecutionResult {
  readonly success: boolean;
  readonly result?: unknown;
  readonly error?: string | null;
  readonly durationMs?: number;
}

/**
 * The searchable registry of every tool the host knows about. Mirrors
 * CircleAI.Hosting.Tools.IToolCatalog.
 */
export interface IToolCatalog {
  /** How many tools are currently registered. */
  readonly count: number;
  /** Register or replace one tool. Idempotent for same name. */
  upsertAsync(descriptor: ToolDescriptor): Promise<void>;
  /** Remove a tool by name. Idempotent. Returns whether it existed. */
  removeAsync(name: string): Promise<boolean>;
  /** Get exactly one descriptor by name, or null when unknown. */
  getAsync(name: string): Promise<ToolDescriptor | null>;
  /** Enumerate every registered descriptor (stable name order). */
  list(): readonly ToolDescriptor[];
  /** Free-form keyword-substring search over name + description + tags. */
  search(query: string, topK?: number): readonly ToolDescriptor[];
  /** Filter by provider id (exact match, case-insensitive). */
  listByProvider(provider: string): readonly ToolDescriptor[];
}

/**
 * A source of tools — vendored integrations, MCP server, AetherNet peer, etc.
 * Mirrors CircleAI.Hosting.Tools.IToolProvider.
 */
export interface IToolProvider {
  /** Stable provider id, e.g. "local" / "composio" / "mcp". */
  readonly providerId: string;
  /** Discover every tool this provider exposes. */
  discoverAsync(): Promise<readonly ToolDescriptor[]>;
  /** Cheap availability probe. */
  isAvailableAsync(): Promise<boolean>;
}

/**
 * Sandboxed execution surface. Mirrors
 * CircleAI.Hosting.Tools.IToolExecutor.
 */
export interface IToolExecutor {
  /** Execute one tool call from a model-emitted JSON arguments string. */
  executeAsync(
    tool: ToolDescriptor,
    argumentsJson: string,
  ): Promise<ToolExecutionResult>;
}

/**
 * Default {@link IToolCatalog} — in-memory + keyword-substring search. Mirrors
 * CircleAI.Hosting.Tools.InMemoryToolCatalog.
 */
export class InMemoryToolCatalog implements IToolCatalog {
  // Case-insensitive key (StringComparer.OrdinalIgnoreCase). Store the original
  // descriptor keyed by the lowercased name; a last-writer-wins upsert.
  private readonly byName = new Map<string, ToolDescriptor>();

  get count(): number {
    return this.byName.size;
  }

  async upsertAsync(descriptor: ToolDescriptor): Promise<void> {
    if (!descriptor) throw new Error("descriptor required");
    if (descriptor.name == null || descriptor.name.trim().length === 0)
      throw new Error("descriptor.name required");
    this.byName.set(descriptor.name.toLowerCase(), descriptor);
  }

  async removeAsync(name: string): Promise<boolean> {
    if (name == null || name.trim().length === 0) throw new Error("name required");
    return this.byName.delete(name.toLowerCase());
  }

  async getAsync(name: string): Promise<ToolDescriptor | null> {
    if (name == null || name.trim().length === 0) return null;
    return this.byName.get(name.toLowerCase()) ?? null;
  }

  list(): readonly ToolDescriptor[] {
    return [...this.byName.values()].sort((a, b) => ciCompare(a.name, b.name));
  }

  search(query: string, topK = 10): readonly ToolDescriptor[] {
    if (query == null || query.trim().length === 0 || topK <= 0) return [];
    const terms = query.split(" ").filter((t) => t.trim().length > 0);

    return [...this.byName.values()]
      .map((d) => ({ tool: d, score: scoreMatch(d, terms) }))
      .filter((x) => x.score > 0)
      .sort(
        (a, b) => b.score - a.score || ciCompare(a.tool.name, b.tool.name),
      )
      .slice(0, topK)
      .map((x) => x.tool);
  }

  listByProvider(provider: string): readonly ToolDescriptor[] {
    if (provider == null || provider.trim().length === 0)
      throw new Error("provider required");
    const p = provider.toLowerCase();
    return [...this.byName.values()]
      .filter((d) => (d.provider ?? "").toLowerCase() === p)
      .sort((a, b) => ciCompare(a.name, b.name));
  }
}

/**
 * Discover and import every tool from `provider` into `catalog`. Returns how
 * many were imported. Mirrors ToolCatalogExtensions.ImportFromAsync.
 */
export async function importFromAsync(
  catalog: IToolCatalog,
  provider: IToolProvider,
): Promise<number> {
  if (!catalog) throw new Error("catalog required");
  if (!provider) throw new Error("provider required");
  const tools = await provider.discoverAsync();
  let count = 0;
  for (const tool of tools) {
    await catalog.upsertAsync(tool);
    count++;
  }
  return count;
}

function scoreMatch(d: ToolDescriptor, terms: string[]): number {
  const name = (d.name ?? "").toLowerCase();
  const desc = (d.description ?? "").toLowerCase();
  const tagBlob = (d.tags ?? []).join(" ").toLowerCase();

  let score = 0;
  for (const t of terms) {
    const tl = t.toLowerCase();
    if (name.includes(tl)) score += 5;
    if (desc.includes(tl)) score += 2;
    if (tagBlob.includes(tl)) score += 3;
  }
  return score;
}

function ciCompare(a: string, b: string): number {
  const la = a.toLowerCase();
  const lb = b.toLowerCase();
  return la < lb ? -1 : la > lb ? 1 : 0;
}
