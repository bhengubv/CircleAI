// PackageTargetsTests.cs
//
// buildTransitive/CircleAI.targets (src/CircleAI/Voice/CircleAI.Package.targets) brings
// Whisper.net.Runtime's own targets into every app that references the CircleAI
// package, because the dependency is packed with exclude="Build" and its natives never
// arrived. The targets file names the runtime's version in a path; CircleAI.csproj
// names it in a PackageReference. If the two ever part, the import's Exists() guard
// makes the miss SILENT - the app builds, and speech recognition fails on first use -
// so they are held equal here.

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircleAI.Tests;

public sealed class PackageTargetsTests
{
    [Fact]
    public void The_package_targets_import_the_Whisper_runtime_the_library_references()
    {
        var root = Root();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "CircleAI", "CircleAI.csproj"));
        var targets = File.ReadAllText(Path.Combine(root, "src", "CircleAI", "Voice", "CircleAI.Package.targets"));

        var referenced = Regex.Match(csproj, "Include=\"Whisper\\.net\\.Runtime\" Version=\"([^\"]+)\"").Groups[1].Value;
        var imported = Regex.Match(targets, "whisper\\.net\\.runtime/([^/]+)/build/").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(referenced), "CircleAI.csproj no longer references Whisper.net.Runtime by version.");
        Assert.Equal(referenced, imported);
    }

    /// <summary>The repo root, found by walking up from the test assembly.</summary>
    private static string Root()
    {
        var dir = System.AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
