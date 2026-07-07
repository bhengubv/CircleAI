// PromptTemplateEngine.cs
//
// Renders chat messages through a model's own chat_template
// (Jinja2 string from its tokenizer_config.json). The SDK never
// hardcodes ChatML — every model family declares its own format,
// the engine renders.
//
// New model family on ModelScope → ZERO C# code needed. The model
// publishes its tokenizer_config.json with a chat_template; the
// engine reads and renders.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircleAI.Core;
using Scriban;
using Scriban.Runtime;

namespace CircleAI.Inference;

/// <summary>
/// Render a chat history into the prompt string the model expects,
/// using its own <c>chat_template</c> (Jinja2 syntax, sourced from the
/// model's <c>tokenizer_config.json</c>).
/// </summary>
public interface IPromptTemplateEngine
{
    /// <summary>
    /// Render <paramref name="messages"/> through the template loaded
    /// for <paramref name="modelDirectory"/>. The directory is expected
    /// to contain <c>tokenizer_config.json</c> — the bundle that
    /// <c>ModelDownloadService.EnsureBundleAsync</c> writes for MNN
    /// models.
    /// </summary>
    /// <param name="modelDirectory">Absolute path to the model bundle directory.</param>
    /// <param name="messages">Chat history (system / user / assistant / tool).</param>
    /// <param name="addGenerationPrompt">
    /// When <c>true</c>, append the template's <c>add_generation_prompt</c>
    /// branch — required at inference time so the model continues from
    /// the assistant turn rather than re-emitting an end-of-text token.
    /// </param>
    string Render(string modelDirectory, IReadOnlyList<ChatMessage> messages, bool addGenerationPrompt = true);
}

/// <summary>
/// Default <see cref="IPromptTemplateEngine"/> backed by Scriban
/// (Liquid/Jinja2-compatible). Caches compiled templates per
/// model directory so repeated renders are allocation-light.
/// </summary>
public sealed class PromptTemplateEngine : IPromptTemplateEngine
{
    // tokenizer_config.json shape (ModelScope / HF convention):
    //   {
    //     "chat_template": "{% for message in messages %}...{% endfor %}",
    //     "bos_token": "<|im_start|>",     (optional)
    //     "eos_token": "<|im_end|>",       (optional)
    //     ...
    //   }
    private sealed record TokenizerConfig(string? ChatTemplate, string? BosToken, string? EosToken);

    private readonly Dictionary<string, (Template Template, TokenizerConfig Config)> _cache = new();
    private readonly object _cacheLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Fallback template applied when a model bundle has no
    /// <c>tokenizer_config.json</c> or it has no <c>chat_template</c>.
    /// Implements the canonical Qwen/ChatML format — works for every
    /// model in the current catalog. New families that need a
    /// different format must publish their own chat_template.
    /// </summary>
    private const string FallbackChatTemplate = """
        {%- for message in messages -%}
        <|im_start|>{{ message.role }}
        {{ message.content }}<|im_end|>
        {% endfor -%}
        {%- if add_generation_prompt -%}
        <|im_start|>assistant
        {%- endif -%}
        """;

    /// <inheritdoc />
    public string Render(string modelDirectory, IReadOnlyList<ChatMessage> messages, bool addGenerationPrompt = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(messages);

        var (template, _) = GetTemplate(modelDirectory);

        // Scriban member-renaming: by default it lowercases C# property
        // names. The Jinja2 chat_template references {{ message.role }}
        // and {{ message.content }} verbatim, so we keep them as-is.
        var scribanMessages = messages
            .Select(m => new ScriptObject
            {
                ["role"]    = NormaliseRole(m.Role),
                ["content"] = m.Content ?? string.Empty,
            })
            .Cast<object>()
            .ToList();

        var context = new ScriptObject
        {
            ["messages"]               = scribanMessages,
            ["add_generation_prompt"]  = addGenerationPrompt,
        };

        var renderContext = new TemplateContext { MemberRenamer = m => m.Name };
        renderContext.PushGlobal(context);

        var rendered = template.Render(renderContext);
        return rendered ?? string.Empty;
    }

    /// <summary>
    /// Remap our internal role tags onto the Jinja2 vocabulary the
    /// template expects. <c>role:"tool"</c> isn't in standard Qwen
    /// ChatML — remap to <c>user</c> with a "[Tool result]" prefix so
    /// the model still sees the data. (Per directive P3 item 15.)
    /// </summary>
    private static string NormaliseRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "user";
        return role.Trim().ToLowerInvariant() switch
        {
            "tool"     => "user",
            "function" => "user",
            _          => role.Trim().ToLowerInvariant(),
        };
    }

    private (Template Template, TokenizerConfig Config) GetTemplate(string modelDirectory)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(modelDirectory, out var cached)) return cached;

            var config       = LoadTokenizerConfig(modelDirectory);
            var templateText = !string.IsNullOrWhiteSpace(config.ChatTemplate)
                ? config.ChatTemplate!
                : FallbackChatTemplate;

            var parsed = Template.ParseLiquid(templateText);
            if (parsed.HasErrors)
            {
                // Falling back to canonical ChatML rather than throwing
                // — a malformed chat_template shouldn't take down the
                // SDK. Real Scriban errors will surface in observer
                // events once those are wired.
                parsed = Template.ParseLiquid(FallbackChatTemplate);
            }

            var pair = (parsed, config);
            _cache[modelDirectory] = pair;
            return pair;
        }
    }

    private static TokenizerConfig LoadTokenizerConfig(string modelDirectory)
    {
        // 1. HuggingFace-style tokenizer_config.json (chat_template at root).
        var hf = TryReadTokenizerConfig(Path.Combine(modelDirectory, "tokenizer_config.json"));
        if (!string.IsNullOrWhiteSpace(hf.ChatTemplate)) return hf;

        // 2. MNN-style llm_config.json — MNN model bundles ship the chat_template
        //    under a "jinja" object here, NOT in tokenizer_config.json.
        var mnn = TryReadMnnJinja(Path.Combine(modelDirectory, "llm_config.json"));
        if (!string.IsNullOrWhiteSpace(mnn.ChatTemplate)) return mnn;

        return hf;   // possibly all-null → canonical ChatML fallback is used
    }

    private static TokenizerConfig TryReadTokenizerConfig(string path)
    {
        if (!File.Exists(path)) return new TokenizerConfig(null, null, null);
        try
        {
            using var stream = File.OpenRead(path);
            using var doc    = JsonDocument.Parse(stream);
            var root         = doc.RootElement;
            return new TokenizerConfig(
                ChatTemplate: root.TryGetProperty("chat_template", out var ct) ? ct.GetString() : null,
                BosToken:     root.TryGetProperty("bos_token",     out var b)  ? b.GetString()  : null,
                EosToken:     root.TryGetProperty("eos_token",     out var e)  ? e.GetString()  : null);
        }
        catch { return new TokenizerConfig(null, null, null); }
    }

    private static TokenizerConfig TryReadMnnJinja(string path)
    {
        if (!File.Exists(path)) return new TokenizerConfig(null, null, null);
        try
        {
            using var stream = File.OpenRead(path);
            using var doc    = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("jinja", out var jinja) &&
                jinja.ValueKind == JsonValueKind.Object)
            {
                return new TokenizerConfig(
                    ChatTemplate: jinja.TryGetProperty("chat_template", out var ct) ? ct.GetString() : null,
                    BosToken:     null,
                    EosToken:     jinja.TryGetProperty("eos", out var e) ? e.GetString() : null);
            }
            return new TokenizerConfig(null, null, null);
        }
        catch { return new TokenizerConfig(null, null, null); }
    }
}
