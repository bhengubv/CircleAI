//! prompt.rs
//!
//! Fallback ChatML prompt template engine. A real Jinja2-style renderer would
//! pull a runtime dep (minijinja / askama) which is out of scope for the
//! portable surface — host apps that need custom templates wire one in.

use crate::models::ChatMessage;

pub trait IPromptTemplateEngine: Send + Sync {
    fn render(
        &self,
        model_directory: &str,
        messages: &[ChatMessage],
        add_generation_prompt: bool,
    ) -> String;
}

pub const FALLBACK_CHAT_TEMPLATE: &str = r#"{%- for message in messages -%}
<|im_start|>{{ message.role }}
{{ message.content }}<|im_end|>
{% endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}"#;

pub struct PromptTemplateEngine;

impl PromptTemplateEngine {
    pub fn new() -> Self {
        Self
    }
}

impl Default for PromptTemplateEngine {
    fn default() -> Self {
        Self::new()
    }
}

impl IPromptTemplateEngine for PromptTemplateEngine {
    fn render(
        &self,
        _model_directory: &str,
        messages: &[ChatMessage],
        add_generation_prompt: bool,
    ) -> String {
        let mut out = String::new();
        for m in messages {
            out.push_str("<|im_start|>");
            out.push_str(&normalise_role(&m.role));
            out.push('\n');
            out.push_str(&m.content);
            out.push_str("<|im_end|>\n");
        }
        if add_generation_prompt {
            out.push_str("<|im_start|>assistant\n");
        }
        out
    }
}

fn normalise_role(role: &str) -> String {
    let r = role.trim().to_ascii_lowercase();
    if r.is_empty() {
        return "user".to_string();
    }
    if r == "tool" || r == "function" {
        return "user".to_string();
    }
    r
}
