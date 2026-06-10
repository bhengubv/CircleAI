// Prompt.kt
//
// Fallback-ChatML PromptTemplateEngine. Catalog-driven Jinja2 templates
// need a non-stdlib renderer (jinjava); out of scope for this version.

package com.bhengubv.circleai.prompt

import com.bhengubv.circleai.models.ChatMessage

interface IPromptTemplateEngine {
    fun render(modelDirectory: String, messages: List<ChatMessage>, addGenerationPrompt: Boolean = true): String
}

const val FALLBACK_CHAT_TEMPLATE = """{%- for message in messages -%}
<|im_start|>{{ message.role }}
{{ message.content }}<|im_end|>
{% endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}"""

class PromptTemplateEngine : IPromptTemplateEngine {
    override fun render(modelDirectory: String, messages: List<ChatMessage>, addGenerationPrompt: Boolean): String {
        val sb = StringBuilder()
        for (m in messages) {
            sb.append("<|im_start|>")
              .append(normaliseRole(m.role))
              .append('\n')
              .append(m.content)
              .append("<|im_end|>\n")
        }
        if (addGenerationPrompt) {
            sb.append("<|im_start|>assistant\n")
        }
        return sb.toString()
    }

    private fun normaliseRole(role: String?): String {
        val r = role?.trim()?.lowercase().orEmpty()
        if (r.isEmpty()) return "user"
        if (r == "tool" || r == "function") return "user"
        return r
    }
}
