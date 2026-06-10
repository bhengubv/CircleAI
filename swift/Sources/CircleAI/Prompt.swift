// Prompt.swift
//
// Fallback-ChatML PromptTemplateEngine. Custom Jinja2 templates need a
// non-stdlib renderer; out of scope.

import Foundation

public protocol IPromptTemplateEngine: Sendable {
    func render(modelDirectory: String, messages: [ChatMessage], addGenerationPrompt: Bool) -> String
}

public let FALLBACK_CHAT_TEMPLATE = """
{%- for message in messages -%}
<|im_start|>{{ message.role }}
{{ message.content }}<|im_end|>
{% endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}
"""

public struct PromptTemplateEngine: IPromptTemplateEngine {
    public init() {}

    public func render(modelDirectory: String, messages: [ChatMessage], addGenerationPrompt: Bool = true) -> String {
        var out = ""
        for m in messages {
            out += "<|im_start|>" + normaliseRole(m.role) + "\n"
            out += m.content + "<|im_end|>\n"
        }
        if addGenerationPrompt { out += "<|im_start|>assistant\n" }
        return out
    }

    private func normaliseRole(_ role: String?) -> String {
        let r = (role ?? "").trimmingCharacters(in: .whitespaces).lowercased()
        if r.isEmpty { return "user" }
        if r == "tool" || r == "function" { return "user" }
        return r
    }
}
