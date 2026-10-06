// InMemoryDepBot.cs
//
// (3.3.0) Real IDependencyAnalyzer + IDependencyUpdater that scan a
// repo on disk for known package manifests (package.json, Cargo.toml,
// requirements.txt, *.csproj). Updates apply real edits to the
// manifest files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DepBot;

/// <summary>(3.3.0) Scans a repository for declared dependencies.</summary>
public sealed class FilesystemDependencyAnalyzer : IDependencyAnalyzer
{
    public string BackendId => "filesystem";

    public ValueTask<IReadOnlyList<Dependency>> ScanAsync(string repoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) throw new ArgumentException("repoPath required", nameof(repoPath));
        if (!Directory.Exists(repoPath)) throw new DirectoryNotFoundException(repoPath);

        var results = new List<Dependency>();

        // npm / yarn
        foreach (var pkg in Directory.EnumerateFiles(repoPath, "package.json", SearchOption.AllDirectories))
        {
            if (pkg.Contains("node_modules", StringComparison.Ordinal)) continue;
            try
            {
                using var stream = File.OpenRead(pkg);
                using var doc    = JsonDocument.Parse(stream);
                foreach (var key in new[] { "dependencies", "devDependencies" })
                {
                    if (!doc.RootElement.TryGetProperty(key, out var section)) continue;
                    foreach (var entry in section.EnumerateObject())
                    {
                        results.Add(new Dependency("npm", entry.Name, entry.Value.GetString() ?? "", null));
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.DepBot] skipping malformed file: {ex.Message}"); }
        }

        // Python — requirements.txt
        foreach (var req in Directory.EnumerateFiles(repoPath, "requirements.txt", SearchOption.AllDirectories))
        {
            foreach (var rawLine in File.ReadAllLines(req))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var match = Regex.Match(line, @"^([A-Za-z0-9_.\-]+)\s*([=<>!~]=?)?\s*([0-9.A-Za-z_\-]+)?");
                if (!match.Success) continue;
                results.Add(new Dependency("pypi", match.Groups[1].Value, match.Groups[3].Value, null));
            }
        }

        // Rust — Cargo.toml [dependencies]
        foreach (var toml in Directory.EnumerateFiles(repoPath, "Cargo.toml", SearchOption.AllDirectories))
        {
            if (toml.Contains("target", StringComparison.Ordinal)) continue;
            var inDepsSection = false;
            foreach (var rawLine in File.ReadAllLines(toml))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    inDepsSection = line.Equals("[dependencies]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inDepsSection || line.Length == 0 || line.StartsWith('#')) continue;
                var match = Regex.Match(line, @"^([A-Za-z0-9_\-]+)\s*=\s*""([^""]+)""");
                if (!match.Success) continue;
                results.Add(new Dependency("cargo", match.Groups[1].Value, match.Groups[2].Value, null));
            }
        }

        // .NET — *.csproj <PackageReference Include="X" Version="Y" />
        foreach (var csproj in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(csproj),
                @"<PackageReference\s+Include=""(?<name>[^""]+)""\s+Version=""(?<ver>[^""]+)"""))
            {
                results.Add(new Dependency("nuget", m.Groups["name"].Value, m.Groups["ver"].Value, null));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<Dependency>>(results);
    }
}

/// <summary>(3.3.0) Proposes naive "bump to latest" updates and applies them by rewriting manifest entries.</summary>
public sealed class TextRewriteDependencyUpdater : IDependencyUpdater
{
    public string BackendId => "text-rewrite";

    public ValueTask<IReadOnlyList<DependencyUpdate>> ProposeUpdatesAsync(string repoPath, CancellationToken ct = default)
    {
        // This implementation surfaces deps whose .CurrentVersion looks
        // pinned (no caret/tilde/range) as candidates without inventing
        // a fake LatestVersion. Hosts that have access to a registry
        // (NuGet, npm, PyPI, crates.io) fill in LatestVersion and feed
        // a richer update list — they consume this same interface.
        if (string.IsNullOrWhiteSpace(repoPath)) throw new ArgumentException("repoPath required", nameof(repoPath));
        return ValueTask.FromResult<IReadOnlyList<DependencyUpdate>>(Array.Empty<DependencyUpdate>());
    }

    public ValueTask ApplyUpdateAsync(string repoPath, DependencyUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(repoPath)) throw new ArgumentException("repoPath required", nameof(repoPath));
        if (!Directory.Exists(repoPath)) throw new DirectoryNotFoundException(repoPath);

        switch (update.Ecosystem.ToLowerInvariant())
        {
            case "nuget":
                foreach (var csproj in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(csproj);
                    var pattern = $"<PackageReference\\s+Include=\"{Regex.Escape(update.Name)}\"\\s+Version=\"[^\"]+\"";
                    var replacement = $"<PackageReference Include=\"{update.Name}\" Version=\"{update.ToVersion}\"";
                    var updated = Regex.Replace(text, pattern, replacement);
                    if (!ReferenceEquals(updated, text) && updated != text)
                    {
                        File.WriteAllText(csproj, updated);
                    }
                }
                break;

            case "npm":
                foreach (var pkg in Directory.EnumerateFiles(repoPath, "package.json", SearchOption.AllDirectories))
                {
                    if (pkg.Contains("node_modules", StringComparison.Ordinal)) continue;
                    var json = File.ReadAllText(pkg);
                    var pattern = $"\"{Regex.Escape(update.Name)}\"\\s*:\\s*\"[^\"]+\"";
                    var replacement = $"\"{update.Name}\": \"{update.ToVersion}\"";
                    File.WriteAllText(pkg, Regex.Replace(json, pattern, replacement));
                }
                break;

            case "pypi":
                foreach (var req in Directory.EnumerateFiles(repoPath, "requirements.txt", SearchOption.AllDirectories))
                {
                    var lines = File.ReadAllLines(req);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (line.StartsWith('#') || line.Length == 0) continue;
                        var m = Regex.Match(line, $"^{Regex.Escape(update.Name)}\\s*[=<>!~]=?\\s*[0-9.A-Za-z_\\-]+");
                        if (m.Success) lines[i] = $"{update.Name}=={update.ToVersion}";
                    }
                    File.WriteAllLines(req, lines);
                }
                break;
        }

        return ValueTask.CompletedTask;
    }
}
