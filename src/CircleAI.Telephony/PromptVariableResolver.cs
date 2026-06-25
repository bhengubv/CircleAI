// PromptVariableResolver.cs
//
// (3.3.0) Substitute {{variables}} in a system prompt before sending
// to the LLM. Sources: static dictionary, IServiceProvider-resolved
// providers, or per-call context. Variables can come from CRM
// look-ups, time-of-day, user identity, knowledge-base hits, etc.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Resolves the value for one prompt variable.</summary>
public delegate ValueTask<string?> PromptVariableProvider(string variableName, CancellationToken ct);

/// <summary>(3.3.0) Render a template with <c>{{var}}</c> placeholders against a set of providers.</summary>
public sealed class PromptVariableResolver
{
    private static readonly Regex VariablePattern = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, PromptVariableProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _statics = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultMissing;

    public PromptVariableResolver(string defaultMissing = "")
    {
        _defaultMissing = defaultMissing ?? "";
    }

    /// <summary>Register a static value.</summary>
    public PromptVariableResolver Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));
        _statics[name] = value ?? "";
        return this;
    }

    /// <summary>Register a dynamic value provider (e.g. CRM lookup).</summary>
    public PromptVariableResolver SetProvider(string name, PromptVariableProvider provider)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));
        _providers[name] = provider ?? throw new ArgumentNullException(nameof(provider));
        return this;
    }

    /// <summary>Render <paramref name="template"/> by substituting every <c>{{var}}</c>.</summary>
    public async ValueTask<string> RenderAsync(string template, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var matches = VariablePattern.Matches(template);
        if (matches.Count == 0) return template;

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var name = m.Groups[1].Value;
            if (replacements.ContainsKey(name)) continue;

            if (_statics.TryGetValue(name, out var v))
            {
                replacements[name] = v;
                continue;
            }
            if (_providers.TryGetValue(name, out var provider))
            {
                var resolved = await provider(name, ct).ConfigureAwait(false);
                replacements[name] = resolved ?? _defaultMissing;
                continue;
            }
            replacements[name] = _defaultMissing;
        }

        return VariablePattern.Replace(template, m => replacements[m.Groups[1].Value]);
    }
}
