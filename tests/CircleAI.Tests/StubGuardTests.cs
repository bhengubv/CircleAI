// StubGuardTests.cs
//
// (3.3.0) STUB GUARD — fails the build if any CircleAI package in
// src/ ships as a stub. A "stub" here is one of:
//
//   1. A package whose only .cs files are `Contracts.cs` +
//      `NullImplementations.cs` and no real implementation.
//   2. A package containing fewer than the minimum threshold of
//      non-null-implementation lines of code.
//   3. A file whose body explicitly says it's a stub (comment
//      mentioning "is a stub", "is a placeholder", "TODO: implement",
//      "TODO: replace", "FIXME").
//   4. A NullImplementations.cs file that ships in a package without
//      any concrete (non-Null*, non-Default*, non-Contracts) sibling
//      file holding real code.
//
// The guard reports the FULL list of offenders so progress is visible
// every time you fix one. The test fails until the offender list is
// empty.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public class StubGuardTests
{
    private readonly ITestOutputHelper _out;
    public StubGuardTests(ITestOutputHelper output) { _out = output; }

    /// <summary>
    /// Minimum lines of real (non-NullImplementations.cs) code per
    /// package before the guard considers it a stub. Calibrated against
    /// the smallest legitimate impl after the 3.4.0 quality pass.
    /// </summary>
    private const int MinRealCodeLines = 60;

    /// <summary>Markers in source text that indicate a stub.</summary>
    private static readonly string[] StubMarkers =
    {
        "is a stub",
        "is a placeholder",
        "are placeholders",
        "TODO: implement",
        "TODO: Replace",
        "TODO: replace",
        "Swap this stub",
        "Swap the stub",
        "FIXME",
        "fixme",
    };

    /// <summary>
    /// File names whose presence is fine because they DON'T claim to
    /// implement the package's real capability — they're DI helpers.
    /// </summary>
    private static readonly HashSet<string> AdminFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AssemblyInfo.cs", "GlobalUsings.cs", "ServiceCollectionExtensions.cs",
        "Contracts.cs", "NullImplementations.cs",
    };

    [Fact]
    public void NoStubPackagesAllowed()
    {
        var srcRoot = FindSrcRoot();
        Assert.NotNull(srcRoot);

        var offenders = new List<string>();

        // Top-level CS files only per package: src/CircleAI.Foo/*.cs (no recursion into subdirs;
        // packages put their *.cs at the package root, which keeps the scan O(packages × top-files)
        // instead of O(packages × entire tree including obj/bin).
        foreach (var pkgDir in Directory.EnumerateDirectories(srcRoot!).OrderBy(p => p))
        {
            var pkgName = Path.GetFileName(pkgDir);
            if (!pkgName.StartsWith("CircleAI.", StringComparison.Ordinal)) continue;

            var csFiles = EnumerateSourceFiles(pkgDir).ToList();
            if (csFiles.Count == 0) continue;

            // Rule 1: Contracts.cs + NullImplementations.cs only.
            var nonAdminFiles = csFiles.Where(f => !AdminFileNames.Contains(Path.GetFileName(f))).ToList();
            if (nonAdminFiles.Count == 0)
            {
                offenders.Add($"{pkgName}: ships as Contracts.cs + NullImplementations.cs only (no real implementation file).");
                continue;
            }

            // Rules 2 + 3 share the file body — read once.
            var realLines = 0;
            foreach (var f in csFiles)
            {
                var fileName = Path.GetFileName(f);
                var content  = File.ReadAllText(f);
                if (!string.Equals(fileName, "NullImplementations.cs", StringComparison.OrdinalIgnoreCase))
                    realLines += CountRealLines(content);
                foreach (var marker in StubMarkers)
                {
                    if (content.Contains(marker, StringComparison.Ordinal))
                    {
                        var line = FindLine(content, marker);
                        offenders.Add($"{pkgName}/{fileName}:{line}: contains stub marker \"{marker}\".");
                    }
                }
            }
            if (realLines < MinRealCodeLines)
            {
                offenders.Add($"{pkgName}: only {realLines} lines of non-Null implementation (minimum {MinRealCodeLines}).");
            }
        }

        if (offenders.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"STUB GUARD: {offenders.Count} stub item(s) found across CircleAI/src/.");
            sb.AppendLine("Every item here must be fixed before this build can ship.");
            sb.AppendLine();
            foreach (var o in offenders) sb.AppendLine($"  • {o}");
            _out.WriteLine(sb.ToString());
            Assert.Fail($"{offenders.Count} stub item(s) detected. See test output for the full list.");
        }
    }

    [Fact]
    public void NoExplicitStubFilesAllowed()
    {
        var srcRoot = FindSrcRoot();
        Assert.NotNull(srcRoot);

        var offenders = new List<string>();
        foreach (var pkgDir in Directory.EnumerateDirectories(srcRoot!))
        foreach (var f in EnumerateSourceFiles(pkgDir))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            // Files explicitly named "*Stub", "*StubService", "*Placeholder".
            if (Regex.IsMatch(name, @"Stub(Service)?$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(name, @"Placeholder$",       RegexOptions.IgnoreCase))
            {
                offenders.Add(f.Substring(srcRoot!.Length).TrimStart(Path.DirectorySeparatorChar));
            }
        }

        if (offenders.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"STUB GUARD: {offenders.Count} file(s) named *Stub* or *Placeholder* found.");
            foreach (var o in offenders) sb.AppendLine($"  • {o}");
            _out.WriteLine(sb.ToString());
            Assert.Fail($"{offenders.Count} stub-named file(s) detected. See test output.");
        }
    }

    [Fact]
    public void EveryNullImplementationHasASiblingRealImplementation()
    {
        var srcRoot = FindSrcRoot();
        Assert.NotNull(srcRoot);

        var offenders = new List<string>();
        foreach (var nullImpl in Directory.EnumerateFiles(srcRoot!, "NullImplementations.cs", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(nullImpl)!;
            var pkg = Path.GetFileName(dir);
            if (!pkg.StartsWith("CircleAI.", StringComparison.Ordinal)) continue;

            var siblings = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => !AdminFileNames.Contains(n!))
                .ToList();

            if (siblings.Count == 0)
            {
                offenders.Add($"{pkg}: NullImplementations.cs has no concrete sibling implementation file.");
            }
        }

        if (offenders.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"STUB GUARD: {offenders.Count} package(s) ship NullImplementations.cs with no real sibling.");
            foreach (var o in offenders) sb.AppendLine($"  • {o}");
            _out.WriteLine(sb.ToString());
            Assert.Fail($"{offenders.Count} packages are null-only.");
        }
    }

    private static int CountRealLines(string content)
    {
        int count = 0;
        foreach (var l in content.Split('\n'))
        {
            var t = l.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("*/")) continue;
            if (t == "{" || t == "}") continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Enumerate top-level + one-level-deep .cs files per package, skipping bin/obj
    /// entirely. CircleAI packages put their sources at the root or in shallow subdirs
    /// (e.g. CircleAI.Memory/Sync/*.cs), so this is comprehensive without walking
    /// dozens of generated-output trees.
    /// </summary>
    private static IEnumerable<string> EnumerateSourceFiles(string pkgDir)
    {
        foreach (var f in Directory.EnumerateFiles(pkgDir, "*.cs", SearchOption.TopDirectoryOnly))
            yield return f;
        foreach (var sub in Directory.EnumerateDirectories(pkgDir))
        {
            var n = Path.GetFileName(sub);
            if (string.Equals(n, "bin", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n, "obj", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in Directory.EnumerateFiles(sub, "*.cs", SearchOption.AllDirectories))
                yield return f;
        }
    }

    private static int FindLine(string content, string marker)
    {
        var idx = content.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return 0;
        return content[..idx].Count(c => c == '\n') + 1;
    }

    private static string? FindSrcRoot()
    {
        // Walk up from the test assembly's working directory until we find a
        // sibling `src/` directory.
        var dir = AppContext.BaseDirectory;
        for (int depth = 0; depth < 10 && dir is not null; depth++)
        {
            var srcCandidate = Path.Combine(dir, "src");
            if (Directory.Exists(srcCandidate))
            {
                // Must contain CircleAI.Core to be the right src/.
                if (Directory.Exists(Path.Combine(srcCandidate, "CircleAI.Core")))
                {
                    return srcCandidate;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
