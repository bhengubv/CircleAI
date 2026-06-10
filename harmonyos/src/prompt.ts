// prompt.ts — Fallback-ChatML PromptTemplateEngine.
// Custom Jinja2 templates require a renderer; out of scope for the portable
// surface. Host apps wire in their own engine.

import { ChatMessage } from './models';

export interface IPromptTemplateEngine {
  render(modelDirectory: string, messages: ReadonlyArray<ChatMessage>, addGenerationPrompt: boolean): string;
}

export const FALLBACK_CHAT_TEMPLATE = `{%- for message in messages -%}
<|im_start|>{{ message.role }}
{{ message.content }}<|im_end|>
{% endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}`;

function normaliseRole(role: string | null | undefined): string {
  const r = (role ?? '').trim().toLowerCase();
  if (!r) return 'user';
  if (r === 'tool' || r === 'function') return 'user';
  return r;
}

export class PromptTemplateEngine implements IPromptTemplateEngine {
  render(_modelDirectory: string, messages: ReadonlyArray<ChatMessage>, addGenerationPrompt: boolean = true): string {
    let out = '';
    for (const m of messages) {
      out += `<|im_start|>${normaliseRole(m.role)}\n${m.content}<|im_end|>\n`;
    }
    if (addGenerationPrompt) out += '<|im_start|>assistant\n';
    return out;
  }
}
