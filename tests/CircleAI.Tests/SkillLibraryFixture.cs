// SkillLibraryFixture.cs
//
// The shipping skill database, unpacked ONCE.
//
// WHY THIS EXISTS: xunit builds a fresh instance of a test class for EVERY
// test, so a constructor that unzipped the 20 MB library ran eleven times
// across the two classes that use it - 220 MB of temp writes and eleven FTS5
// connections, opened and torn down in parallel with three and a half thousand
// other tests.
//
// It did not fail. It ABORTED: two full runs ended "Test Run Aborted" after
// reporting 3,433 and 3,457 of 3,477 tests, both taking about fifteen minutes
// against five and a half for a clean one. An aborted run is worse than a
// failing one, because the number it prints looks like a pass.
//
// A class fixture is built once for the whole class and disposed once after it,
// which is what this work always wanted.

using System.IO.Compression;
using Xunit;

namespace CircleAI.Tests;

/// <summary>Unpacks the shipped skills.db once for a whole test class.</summary>
public sealed class SkillLibraryFixture : IDisposable
{
    /// <summary>Where the unpacked database is, or null when the asset is absent.</summary>
    public string? DatabasePath { get; }

    /// <summary>Whether the shipping asset was found at all.</summary>
    public bool Available => DatabasePath is not null;

    private readonly string _dir;

    public SkillLibraryFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "skills-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var zip = FindAsset();
        if (zip is null) return;

        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        var db = Path.Combine(_dir, "skills.db");
        entry.ExtractToFile(db, overwrite: true);
        DatabasePath = db;
    }

    /// <summary>Walks up from the test binary to the repo's asset.</summary>
    private static string? FindAsset()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CircleAI.Skills", "assets", "skills.db.zip");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir */ }
    }
}
