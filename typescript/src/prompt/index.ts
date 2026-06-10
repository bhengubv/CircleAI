// prompt/index.ts
//
// Prompt template engine — port of CircleAI.Inference.PromptTemplateEngine.
//
// Reads each model's chat_template (a Jinja2 string published in
// tokenizer_config.json). Lazy-loads `nunjucks` when available for
// catalog-published templates; falls back to a hand-coded ChatML
// renderer when nunjucks is absent or when no chat_template is published.

import { promises as fs } from "node:fs";
import * as path from "node:path";
import type { ChatMessage } from "../models/index.js";

export const FALLBACK_CHAT_TEMPLATE = `\
{%- for message in messages -%}
<|im_start|>{{ message.role }}
{{ message.content }}<|im_end|>
{% endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}
`;

export interface IPromptTemplateEngine {
  render(
    modelDirectory: string,
    messages: readonly ChatMessage[],
    addGenerationPrompt?: boolean,
  ): Promise<string>;
}

interface NunjucksLike {
  configure(opts: { autoescape: boolean }): unknown;
  renderString(src: string, ctx: Record<string, unknown>): string;
}

let _nunjucks: NunjucksLike | null | undefined = undefined;
async function loadNunjucks(): Promise<NunjucksLike | null> {
  if (_nunjucks !== undefined) return _nunjucks;
  try {
    // @ts-expect-error optional dep — typed at runtime via the structural cast below
    const mod = (await import("nunjucks")) as unknown;
    const env = (
      (mod as { default?: NunjucksLike }).default ?? (mod as NunjucksLike)
    );
    env.configure({ autoescape: false });
    _nunjucks = env;
  } catch {
    _nunjucks = null;
  }
  return _nunjucks;
}

export class PromptTemplateEngine implements IPromptTemplateEngine {
  // Cache compiled-template-source per model dir.
  private readonly cache = new Map<string, string>();

  async render(
    modelDirectory: string,
    messages: readonly ChatMessage[],
    addGenerationPrompt = true,
  ): Promise<string> {
    if (!modelDirectory) throw new Error("modelDirectory is required");

    let tmpl = this.cache.get(modelDirectory);
    if (tmpl === undefined) {
      tmpl = await loadChatTemplate(modelDirectory);
      this.cache.set(modelDirectory, tmpl);
    }

    const ctx = {
      messages: messages.map((m) => ({
        role: normaliseRole(m.role),
        content: m.content ?? "",
      })),
      add_generation_prompt: addGenerationPrompt,
    };

    // Always-available fallback path.
    if (tmpl === FALLBACK_CHAT_TEMPLATE) {
      return renderFallbackChatML(
        ctx.messages,
        ctx.add_generation_prompt,
      );
    }

    // Custom template — try nunjucks, fall back if missing or parse error.
    const nj = await loadNunjucks();
    if (nj === null) {
      return renderFallbackChatML(
        ctx.messages,
        ctx.add_generation_prompt,
      );
    }
    try {
      return nj.renderString(tmpl, ctx);
    } catch {
      return renderFallbackChatML(
        ctx.messages,
        ctx.add_generation_prompt,
      );
    }
  }
}

// ── Helpers ──────────────────────────────────────────────────────────────

function normaliseRole(role: string | undefined): string {
  if (!role || !role.trim()) return "user";
  const norm = role.trim().toLowerCase();
  if (norm === "tool" || norm === "function") return "user";
  return norm;
}

async function loadChatTemplate(modelDirectory: string): Promise<string> {
  const cfgPath = path.join(modelDirectory, "tokenizer_config.json");
  try {
    const raw = await fs.readFile(cfgPath, "utf-8");
    const cfg = JSON.parse(raw) as { chat_template?: string };
    if (cfg.chat_template && cfg.chat_template.trim().length > 0) {
      return cfg.chat_template;
    }
  } catch {
    /* missing or malformed — fall through */
  }
  return FALLBACK_CHAT_TEMPLATE;
}

function renderFallbackChatML(
  messages: readonly { role: string; content: string }[],
  addGenerationPrompt: boolean,
): string {
  const out: string[] = [];
  for (const m of messages) {
    out.push(`<|im_start|>${m.role}\n${m.content}<|im_end|>\n`);
  }
  if (addGenerationPrompt) {
    out.push("<|im_start|>assistant\n");
  }
  return out.join("");
}
