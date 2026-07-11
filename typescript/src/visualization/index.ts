// visualization/index.ts
//
// Full-parity port of CircleAI.Visualization (C#). C# is the exact spec.
//
// Dashboard-definition store, API-doc builder, static-site builder:
// IDashboardDefinitionStore / IApiDocBuilder / ISiteBuilder contracts,
// DashboardDefinition / ApiDoc / GeneratedSite records, deterministic in-memory
// implementations, and the Null* defaults.
//
// Type mappings (C# → TS):
//   record                                            → readonly interface (+ factory)
//   IReadOnlyDictionary<string, ReadOnlyMemory<byte>> → ReadonlyMap<string, Uint8Array>
//   JsonDocument.Parse / JsonSerializer.Serialize     → JSON.parse / JSON.stringify
//   ValueTask<T>                                       → Promise<T>

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A dashboard definition. Mirrors C# `DashboardDefinition`. */
export interface DashboardDefinition {
  readonly dashboardId: string;
  readonly title: string;
  readonly jsonSpec: string;
}

/** Constructs a {@link DashboardDefinition}. */
export function dashboardDefinition(dashboardId: string, title: string, jsonSpec: string): DashboardDefinition {
  return { dashboardId, title, jsonSpec };
}

/** An API doc. Mirrors C# `ApiDoc`. */
export interface ApiDoc {
  readonly docId: string;
  readonly title: string;
  readonly openApiJson: string;
}

/** Constructs an {@link ApiDoc}. */
export function apiDoc(docId: string, title: string, openApiJson: string): ApiDoc {
  return { docId, title, openApiJson };
}

/** A generated static site. Mirrors C# `GeneratedSite`. */
export interface GeneratedSite {
  readonly siteId: string;
  readonly files: ReadonlyMap<string, Uint8Array>;
}

/** Constructs a {@link GeneratedSite}. */
export function generatedSite(siteId: string, files: ReadonlyMap<string, Uint8Array>): GeneratedSite {
  return { siteId, files };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Persistent dashboard-definition store. Mirrors C# `IDashboardDefinitionStore`. */
export interface IDashboardDefinitionStore {
  readonly backendId: string;
  upsertAsync(d: DashboardDefinition): Promise<void>;
  getAsync(id: string): Promise<DashboardDefinition | null>;
  listAsync(): Promise<readonly DashboardDefinition[]>;
}

/** OpenAPI doc builder. Mirrors C# `IApiDocBuilder`. */
export interface IApiDocBuilder {
  readonly backendId: string;
  buildAsync(openApiSpec: string): Promise<ApiDoc>;
}

/** Static-site builder. Mirrors C# `ISiteBuilder`. */
export interface ISiteBuilder {
  readonly backendId: string;
  buildAsync(siteSpec: string): Promise<GeneratedSite>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/** Thread-safe in-memory dashboard store. Mirrors C# `InMemoryDashboardStore`. */
export class InMemoryDashboardStore implements IDashboardDefinitionStore {
  private readonly items = new Map<string, DashboardDefinition>();

  get backendId(): string {
    return "in-memory";
  }

  async upsertAsync(d: DashboardDefinition): Promise<void> {
    if (d == null) throw new Error("d required");
    if (d.dashboardId == null || d.dashboardId.trim().length === 0) throw new Error("DashboardId required");
    this.items.set(d.dashboardId, d);
  }

  async getAsync(id: string): Promise<DashboardDefinition | null> {
    if (id == null || id.trim().length === 0) throw new Error("id required");
    return this.items.get(id) ?? null;
  }

  async listAsync(): Promise<readonly DashboardDefinition[]> {
    return [...this.items.values()];
  }
}

/** Normalising OpenAPI-doc builder. Mirrors C# `JsonApiDocBuilder`. */
export class JsonApiDocBuilder implements IApiDocBuilder {
  get backendId(): string {
    return "json-normaliser";
  }

  async buildAsync(openApiSpec: string): Promise<ApiDoc> {
    if (openApiSpec == null || openApiSpec.trim().length === 0) throw new Error("openApiSpec required");
    const root = JSON.parse(openApiSpec) as unknown;
    let title = "API";
    if (isObject(root) && isObject(root.info) && typeof root.info.title === "string") {
      title = root.info.title;
    }
    const docId = title.replace(/ /g, "-").toLowerCase();
    const canonical = JSON.stringify(root);
    return apiDoc(docId, title, canonical);
  }
}

/** Static-site builder from a `{"pages":[{path,html}]}` spec. Mirrors C# `StaticSiteBuilder`. */
export class StaticSiteBuilder implements ISiteBuilder {
  get backendId(): string {
    return "static";
  }

  async buildAsync(siteSpec: string): Promise<GeneratedSite> {
    if (siteSpec == null || siteSpec.trim().length === 0) throw new Error("siteSpec required");
    const root = JSON.parse(siteSpec) as unknown;
    const files = new Map<string, Uint8Array>();

    if (!isObject(root) || !Array.isArray(root.pages)) {
      throw new Error("siteSpec must contain a pages[] array.");
    }

    for (const page of root.pages) {
      if (!isObject(page)) continue;
      const path = typeof page.path === "string" ? page.path : null;
      const html = typeof page.html === "string" ? page.html : null;
      if (path == null || path.trim().length === 0 || html == null) continue;
      files.set(path, utf8(html));
    }

    return generatedSite(`site-${newGuidN()}`, files);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link IDashboardDefinitionStore}. */
export class NullDashboardDefinitionStore implements IDashboardDefinitionStore {
  static readonly instance = new NullDashboardDefinitionStore();
  get backendId(): string {
    return "null";
  }
  async upsertAsync(): Promise<void> {
    /* no-op */
  }
  async getAsync(): Promise<DashboardDefinition | null> {
    return null;
  }
  async listAsync(): Promise<readonly DashboardDefinition[]> {
    return [];
  }
}

/** Fail-safe {@link IApiDocBuilder}. */
export class NullApiDocBuilder implements IApiDocBuilder {
  static readonly instance = new NullApiDocBuilder();
  get backendId(): string {
    return "null";
  }
  async buildAsync(): Promise<ApiDoc> {
    return apiDoc(EMPTY_GUID, "", "{}");
  }
}

/** Fail-safe {@link ISiteBuilder}. */
export class NullSiteBuilder implements ISiteBuilder {
  static readonly instance = new NullSiteBuilder();
  get backendId(): string {
    return "null";
  }
  async buildAsync(): Promise<GeneratedSite> {
    return generatedSite(EMPTY_GUID, new Map<string, Uint8Array>());
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

/** 32-char lowercase hex id (mirrors C# `Guid.NewGuid().ToString("n")`). */
function newGuidN(): string {
  let s = "";
  for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
  return s;
}
