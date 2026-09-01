/**
 * hosting/generative_ui
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/generative_ui.ts
//
// Port of the CircleAI.Hosting.GenerativeUI surface:
//   • IGenerativeUIRenderer.cs — UiComponent, UiCatalogEntry, UiCatalogs.Default,
//     IGenerativeUIRenderer, RecordingGenerativeUIRenderer
//   • JsonRenderParser.cs — strict JSON → UiComponent tree parser + catalog
//     prompt description
//
// The parser rejects any kind not in the catalog and any property not declared
// on its kind (strict mode); non-strict downgrades unknown kinds to a textBlock.

/**
 * One UI element produced by a generative-UI model. Mirrors
 * CircleAI.Hosting.GenerativeUI.UiComponent.
 */
export interface UiComponent {
  /** Catalog identifier, e.g. "card", "button", "list". */
  readonly kind: string;
  /** Bag of property values keyed by JSON property name. */
  readonly properties: Readonly<Record<string, unknown>>;
  /** Optional nested components. */
  readonly children?: readonly UiComponent[] | null;
}

/**
 * Catalog entry declaring an allowed kind + its properties. Mirrors
 * CircleAI.Hosting.GenerativeUI.UiCatalogEntry.
 */
export interface UiCatalogEntry {
  readonly kind: string;
  readonly description: string;
  /** Property names → JSON Schema type strings. */
  readonly allowedProperties: Readonly<Record<string, string>>;
  readonly allowsChildren?: boolean;
}

/** Pre-canned component catalogs. Mirrors CircleAI.Hosting.GenerativeUI.UiCatalogs. */
export const UiCatalogs = {
  /** Minimal "chat assistant tool output" catalog. */
  Default: [
    {
      kind: "card",
      description:
        "A bordered container with a title and body. May contain children.",
      allowedProperties: { title: "string", caption: "string?" },
      allowsChildren: true,
    },
    {
      kind: "list",
      description:
        "An ordered or unordered list. Children are the list items.",
      allowedProperties: { ordered: "boolean" },
      allowsChildren: true,
    },
    {
      kind: "button",
      description: "A tappable button. Emit an action identifier when clicked.",
      allowedProperties: { label: "string", action: "string", style: "string?" },
      allowsChildren: false,
    },
    {
      kind: "textBlock",
      description: "Inline text content, optionally markdown.",
      allowedProperties: { text: "string", markdown: "boolean?" },
      allowsChildren: false,
    },
    {
      kind: "image",
      description: "An image displayed from a URL or data-URI.",
      allowedProperties: { src: "string", alt: "string?" },
      allowsChildren: false,
    },
  ] as readonly UiCatalogEntry[],
} as const;

/**
 * Renderer contract. Consumers implement this to materialise UiComponent
 * records into a native UI. Mirrors
 * CircleAI.Hosting.GenerativeUI.IGenerativeUIRenderer.
 */
export interface IGenerativeUIRenderer {
  /** Render a single root component. */
  renderAsync(root: UiComponent): Promise<void>;
}

/**
 * No-op renderer for tests / headless server scenarios; retains the last
 * rendered component. Mirrors RecordingGenerativeUIRenderer.
 */
export class RecordingGenerativeUIRenderer implements IGenerativeUIRenderer {
  lastRendered: UiComponent | null = null;
  renderCount = 0;

  async renderAsync(root: UiComponent): Promise<void> {
    this.lastRendered = root;
    this.renderCount++;
  }
}

/** Error raised by strict {@link parseRender}. */
export class JsonRenderError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "JsonRenderError";
  }
}

/**
 * Parse one JSON document into a {@link UiComponent} tree, validating against a
 * catalog. Mirrors CircleAI.Hosting.GenerativeUI.JsonRenderParser.Parse.
 *
 * @param strict When true, unknown kinds and undeclared properties throw. When
 *   false, an unknown kind becomes a textBlock with a debug message.
 */
export function parseRender(
  json: string,
  catalog: readonly UiCatalogEntry[],
  strict = true,
): UiComponent {
  if (json == null || json.length === 0) throw new JsonRenderError("json required");
  if (!catalog) throw new JsonRenderError("catalog required");

  let root: unknown;
  try {
    root = JSON.parse(json);
  } catch (e) {
    throw new JsonRenderError(
      `Invalid JSON: ${e instanceof Error ? e.message : String(e)}`,
    );
  }

  const index = new Map<string, UiCatalogEntry>();
  for (const c of catalog) index.set(c.kind.toLowerCase(), c);

  return parseElement(root, index, strict);
}

function parseElement(
  el: unknown,
  catalog: Map<string, UiCatalogEntry>,
  strict: boolean,
): UiComponent {
  if (!isPlainObject(el))
    throw new JsonRenderError(`Expected JSON object, got ${describeKind(el)}.`);

  const kindVal = el["kind"];
  const kind = typeof kindVal === "string" ? kindVal : null;
  if (kind == null || kind.length === 0)
    throw new JsonRenderError("Component missing required 'kind' field.");

  const entry = catalog.get(kind.toLowerCase());
  if (entry === undefined) {
    if (strict) throw new JsonRenderError(`Unknown component kind '${kind}'.`);
    return {
      kind: "textBlock",
      properties: { text: `[unknown kind '${kind}']`, markdown: false },
    };
  }

  const props: Record<string, unknown> = {};
  const propsEl = el["properties"];
  if (isPlainObject(propsEl)) {
    for (const [name, value] of Object.entries(propsEl)) {
      if (strict && !Object.prototype.hasOwnProperty.call(entry.allowedProperties, name))
        throw new JsonRenderError(
          `Component '${kind}' does not allow property '${name}'.`,
        );
      props[name] = value;
    }
  }

  let children: UiComponent[] | undefined;
  const childEl = el["children"];
  if (Array.isArray(childEl)) {
    if (!entry.allowsChildren) {
      if (strict)
        throw new JsonRenderError(`Component '${kind}' does not allow children.`);
    } else {
      children = childEl.map((c) => parseElement(c, catalog, strict));
    }
  }

  return { kind, properties: props, children: children ?? null };
}

/**
 * Build a system-prompt snippet describing the catalog to the model. Mirrors
 * JsonRenderParser.DescribeCatalogForPrompt.
 */
export function describeCatalogForPrompt(
  catalog: readonly UiCatalogEntry[],
): string {
  if (!catalog) throw new JsonRenderError("catalog required");
  const lines: string[] = [];
  lines.push(
    "You may respond with a single JSON object describing one UI component.",
  );
  lines.push(
    'Allowed shape: { "kind": string, "properties": { ... }, "children"?: [ ... ] }',
  );
  lines.push("");
  lines.push("Allowed kinds:");
  for (const e of catalog) {
    lines.push(`- ${e.kind} — ${e.description}`);
    for (const [name, type] of Object.entries(e.allowedProperties))
      lines.push(`    - ${name}: ${type}`);
    if (e.allowsChildren) lines.push("    - children: array of components");
  }
  // AppendLine appends "\n" per line including the last.
  return lines.join("\n") + "\n";
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function describeKind(v: unknown): string {
  if (v === null) return "Null";
  if (Array.isArray(v)) return "Array";
  switch (typeof v) {
    case "string":
      return "String";
    case "number":
      return "Number";
    case "boolean":
      return v ? "True" : "False";
    default:
      return typeof v;
  }
}
