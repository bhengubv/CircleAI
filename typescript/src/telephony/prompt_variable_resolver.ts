// telephony/prompt_variable_resolver.ts
//
// Substitute {{variables}} in a system prompt before sending to the LLM —
// faithful port of PromptVariableResolver.cs. Sources: a static dictionary or
// async providers (CRM look-ups, time-of-day, user identity, knowledge-base
// hits, etc.). Variables can come from anywhere the host wires up.
//
// C# `Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}")` → the same pattern with
// the `g` flag so every placeholder is matched/replaced. Provider resolution is
// async (`ValueTask<string?>` → `Promise<string | undefined>`).

/** Resolves the value for one prompt variable. Mirrors the `PromptVariableProvider` delegate. */
export type PromptVariableProvider = (
  variableName: string,
  signal?: AbortSignal,
) => Promise<string | undefined>;

const VARIABLE_PATTERN = /\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}/g;

/** Render a template with `{{var}}` placeholders against a set of providers. Mirrors `PromptVariableResolver`. */
export class PromptVariableResolver {
  private readonly providers = new Map<string, PromptVariableProvider>(); // key: lowercased name
  private readonly statics = new Map<string, string>(); // key: lowercased name
  private readonly defaultMissing: string;

  constructor(defaultMissing = "") {
    this.defaultMissing = defaultMissing ?? "";
  }

  /** Register a static value. Returns `this` for chaining. */
  set(name: string, value: string): this {
    if (!name || name.trim().length === 0) throw new Error("name required");
    this.statics.set(name.toLowerCase(), value ?? "");
    return this;
  }

  /** Register a dynamic value provider (e.g. CRM lookup). Returns `this` for chaining. */
  setProvider(name: string, provider: PromptVariableProvider): this {
    if (!name || name.trim().length === 0) throw new Error("name required");
    if (provider === null || provider === undefined) throw new Error("provider is required");
    this.providers.set(name.toLowerCase(), provider);
    return this;
  }

  /** Render `template` by substituting every `{{var}}`. */
  async renderAsync(template: string, signal?: AbortSignal): Promise<string> {
    if (!template) return "";

    const matches = [...template.matchAll(VARIABLE_PATTERN)];
    if (matches.length === 0) return template;

    const replacements = new Map<string, string>(); // key: lowercased name
    for (const m of matches) {
      const name = m[1]!;
      const key = name.toLowerCase();
      if (replacements.has(key)) continue;

      const staticVal = this.statics.get(key);
      if (staticVal !== undefined) {
        replacements.set(key, staticVal);
        continue;
      }
      const provider = this.providers.get(key);
      if (provider !== undefined) {
        const resolved = await provider(name, signal);
        replacements.set(key, resolved ?? this.defaultMissing);
        continue;
      }
      replacements.set(key, this.defaultMissing);
    }

    return template.replace(VARIABLE_PATTERN, (_full, name: string) => {
      return replacements.get(name.toLowerCase()) ?? this.defaultMissing;
    });
  }
}
