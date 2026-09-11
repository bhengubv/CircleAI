// TargetFrameworkTests.cs
//
// Every shipped library must still be usable from net9.0.
//
// THE DRIFT WAS SILENT AND THE DOCUMENTATION COVERED FOR IT. CLAUDE.md said
// "everything multi-targets net9.0 and net10.0" and fourteen libraries did not -
// including all three CircleAI.Assistant projects, which ARE the product. A
// developer on net9.0 could reference a hundred and fifty-four libraries and not
// the assistant, and nothing anywhere said so: the net9 leg of the test suite
// stayed green because the test project could not reference those libraries in
// the first place.
//
// Nothing catches this but a check on the project files themselves. A build
// cannot: a net10-only library builds perfectly well, and every consumer that
// can see it is also net10.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircleAI.Tests;

public class TargetFrameworkTests
{
    /// <summary>
    /// Projects that legitimately target one platform rather than one runtime.
    /// </summary>
    /// <remarks>
    /// An Android head is Android. Multi-targeting it to net9.0 would produce an
    /// assembly nothing could load, which is worse than not shipping one.
    /// </remarks>
    private static readonly string[] PlatformOnly =
    [
        "CircleAI.Assistant.Device",
        "CircleAI.Device",
    ];

    [Fact]
    public void Every_shipped_library_is_usable_from_net9()
    {
        var offenders = new List<string>();

        foreach (var project in Projects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (PlatformOnly.Contains(name, StringComparer.Ordinal)) continue;

            var xml = File.ReadAllText(project);

            var single = Regex.Match(xml, @"<TargetFramework>\s*([^<]+?)\s*</TargetFramework>");
            var multi = Regex.Match(xml, @"<TargetFrameworks>\s*([^<]+?)\s*</TargetFrameworks>");

            // A platform TFM in the singular form is a head, not a library.
            if (single.Success && IsPlatform(single.Groups[1].Value)) continue;

            var frameworks = multi.Success ? multi.Groups[1].Value
                           : single.Success ? single.Groups[1].Value
                           : null;

            if (frameworks is null) continue;   // SDK default; nothing declared here

            if (!frameworks.Split(';').Select(f => f.Trim())
                           .Any(f => f.StartsWith("net9.0", StringComparison.Ordinal)))
                offenders.Add($"{name} targets {frameworks}");
        }

        Assert.True(offenders.Count == 0,
            "These libraries cannot be used from net9.0. Either multi-target them " +
            "or add them to PlatformOnly with a reason:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_assistant_trio_in_particular_is_reachable_from_net9()
    {
        // Called out by name because these three are THE PRODUCT. The sample app
        // is a thin client; src/CircleAI.Assistant* is what a developer adopts,
        // and all three were net10-only while the documentation said otherwise.
        foreach (var name in new[]
        {
            "CircleAI.Assistant",
            "CircleAI.Assistant.Runtime",
            "CircleAI.Assistant.Sweep",
        })
        {
            var project = Path.Combine(Root(), "src", name, name + ".csproj");
            Assert.True(File.Exists(project), $"{name} not found at {project}");

            var xml = File.ReadAllText(project);
            Assert.Contains("net9.0", xml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_platform_only_list_names_projects_that_exist()
    {
        // An exemption for a project that has been renamed or deleted is an
        // exemption that silently covers nothing, and the next net10-only
        // library slips through beside it.
        foreach (var name in PlatformOnly)
        {
            var project = Path.Combine(Root(), "src", name, name + ".csproj");
            Assert.True(File.Exists(project),
                $"PlatformOnly lists '{name}', which no longer exists. Remove it.");
        }
    }

    private static IEnumerable<string> Projects()
        => Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.csproj",
                                    SearchOption.AllDirectories);

    private static bool IsPlatform(string tfm)
        => tfm.Contains("android", StringComparison.OrdinalIgnoreCase)
        || tfm.Contains("ios", StringComparison.OrdinalIgnoreCase)
        || tfm.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase)
        || tfm.Contains("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>The repo root, found by walking up from the test assembly.</summary>
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
