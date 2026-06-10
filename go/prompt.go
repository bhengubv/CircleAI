// prompt.go
//
// IPromptTemplateEngine + a fallback-only PromptTemplateEngine that
// renders the canonical Qwen/ChatML format.
//
// A full Jinja2-compatible renderer would pull in a non-stdlib dependency
// (pongo2 / gonja). For 1.5.0 parity we ship the fallback only; consumers
// that need catalog-driven custom chat_templates can wire their own
// IPromptTemplateEngine implementation.

package circleai

import (
	"strings"
)

// IPromptTemplateEngine renders chat messages into the prompt string the
// model expects.
type IPromptTemplateEngine interface {
	Render(modelDirectory string, messages []ChatMessage, addGenerationPrompt bool) string
}

// PromptTemplateEngine is the fallback-ChatML implementation.
// Returns the canonical Qwen/ChatML format. Catalog-driven custom
// chat_template strings (Jinja2 syntax) are out of scope for the Go
// port at this version.
type PromptTemplateEngine struct{}

// FallbackChatMLTemplate is the literal Jinja2 source the C# port
// falls back to; we don't render Jinja in Go, so this is exposed as a
// constant for documentation + cross-language fixture comparison.
const FallbackChatMLTemplate = `{%- for message in messages -%}
<|im_start|>{{ message.role }}
{{ message.content }}<|im_end|>
{% endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}`

// Render implements IPromptTemplateEngine.
func (PromptTemplateEngine) Render(modelDirectory string, messages []ChatMessage, addGenerationPrompt bool) string {
	_ = modelDirectory // ignored — fallback always renders ChatML
	var b strings.Builder
	for _, m := range messages {
		role := normaliseRole(m.Role)
		b.WriteString("<|im_start|>")
		b.WriteString(role)
		b.WriteString("\n")
		b.WriteString(m.Content)
		b.WriteString("<|im_end|>\n")
	}
	if addGenerationPrompt {
		b.WriteString("<|im_start|>assistant\n")
	}
	return b.String()
}

func normaliseRole(role string) string {
	role = strings.ToLower(strings.TrimSpace(role))
	if role == "" {
		return "user"
	}
	if role == "tool" || role == "function" {
		return "user"
	}
	return role
}
