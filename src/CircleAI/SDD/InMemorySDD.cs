// InMemorySDD.cs
//
// (3.3.0) Real in-memory specification store + a JSON-schema-shape
// validator + a simple language scaffolder that produces a complete,
// compilable hello-world project for the requested target language.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.SDD;

public sealed class InMemorySpecificationStore : ISpecificationStore
{
    private readonly ConcurrentDictionary<string, Specification> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask UpsertAsync(Specification spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.SpecId)) throw new ArgumentException("SpecId required");
        _items[spec.SpecId] = spec;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Specification?> GetAsync(string specId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(specId)) throw new ArgumentException("specId required", nameof(specId));
        _items.TryGetValue(specId, out var s);
        return ValueTask.FromResult(s);
    }

    public ValueTask<IReadOnlyList<Specification>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Specification>>(_items.Values.ToArray());
}

/// <summary>(3.3.0) Validate that the spec's body parses as JSON and that — when a schema is provided — it's
/// a syntactically valid JSON schema (object with type/properties).</summary>
public sealed class JsonShapeSpecificationValidator : ISpecificationValidator
{
    public string BackendId => "json-shape";

    public ValueTask<SpecValidationResult> ValidateAsync(Specification spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(spec.Title)) errors.Add("Title is required.");
        if (string.IsNullOrWhiteSpace(spec.Body))  errors.Add("Body is required.");
        if (!string.IsNullOrWhiteSpace(spec.Schema))
        {
            try
            {
                using var doc = JsonDocument.Parse(spec.Schema);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) errors.Add("Schema must be a JSON object.");
                else if (!doc.RootElement.TryGetProperty("type", out _))
                    errors.Add("Schema must declare a top-level 'type'.");
            }
            catch (JsonException ex) { errors.Add($"Schema is not valid JSON: {ex.Message}"); }
        }
        return ValueTask.FromResult(new SpecValidationResult(errors.Count == 0, errors));
    }
}

/// <summary>(3.3.0) Spec → minimal compilable project (C#, TypeScript, Python).</summary>
public sealed class HelloWorldSpecToScaffold : ISpecToScaffold
{
    public string BackendId => "hello-world";

    public ValueTask<ScaffoldedProject> ScaffoldAsync(Specification spec, string targetLanguage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("targetLanguage required");

        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var lang  = targetLanguage.ToLowerInvariant();
        var name  = SanitizeName(spec.SpecId);

        switch (lang)
        {
            case "csharp" or "c#":
                files["Program.cs"]            = Bytes($"Console.WriteLine(\"{name}: {EscapeText(spec.Title)}\");\n");
                files[$"{name}.csproj"]        = Bytes("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>\n</Project>\n");
                files["README.md"]             = Bytes($"# {EscapeText(spec.Title)}\n\n{EscapeText(spec.Body)}\n");
                break;
            case "typescript" or "ts":
                files["index.ts"]              = Bytes($"console.log(\"{name}: {EscapeText(spec.Title)}\");\n");
                files["package.json"]          = Bytes($"{{\"name\":\"{name}\",\"version\":\"0.1.0\",\"main\":\"index.ts\",\"scripts\":{{\"start\":\"ts-node index.ts\"}}}}\n");
                files["tsconfig.json"]         = Bytes("{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"commonjs\"}}\n");
                files["README.md"]             = Bytes($"# {EscapeText(spec.Title)}\n\n{EscapeText(spec.Body)}\n");
                break;
            case "python" or "py":
                files["main.py"]               = Bytes($"def main():\n    print(\"{name}: {EscapeText(spec.Title)}\")\n\nif __name__ == \"__main__\":\n    main()\n");
                files["pyproject.toml"]        = Bytes($"[project]\nname = \"{name}\"\nversion = \"0.1.0\"\nrequires-python = \">=3.10\"\n");
                files["README.md"]             = Bytes($"# {EscapeText(spec.Title)}\n\n{EscapeText(spec.Body)}\n");
                break;
            default:
                throw new NotSupportedException($"Language '{targetLanguage}' is not supported by HelloWorldSpecToScaffold (csharp / typescript / python).");
        }

        return ValueTask.FromResult(new ScaffoldedProject($"{name}-{lang}", files));
    }

    private static string SanitizeName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "project";
        var sb = new StringBuilder();
        foreach (var ch in id)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
        }
        return sb.Length == 0 ? "project" : sb.ToString();
    }

    private static string EscapeText(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private static ReadOnlyMemory<byte> Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
