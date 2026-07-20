// ModelStorageDirectoryTests.cs
//
// AIOptions carried TWO public properties for one concept —
// ModelStorageDirectory (nullable, read by CheckForUpgradesAsync) and
// ModelStorageDir (defaulted, what the model loader used). Different code read
// different ones, so a host that set only one got models downloaded to a
// directory that upgrade detection never scanned. Nothing failed loudly; the
// upgrade check just returned "nothing to do" forever.
//
// ResolvedModelStorageDirectory is now the single source of truth. These tests
// pin the precedence so the split brain cannot come back.

using System;
using System.IO;
using CircleAI.Hosting;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelStorageDirectoryTests
{
    [Fact]
    public void ModelStorageDirectory_Wins_WhenBothAreSet()
    {
#pragma warning disable CS0618 // exercising the legacy property is the point
        var opts = new AIOptions
        {
            ModelStorageDirectory = "/canonical/models",
            ModelStorageDir       = "/legacy/models",
        };
#pragma warning restore CS0618

        Assert.Equal("/canonical/models", opts.ResolvedModelStorageDirectory);
    }

    [Fact]
    public void LegacyProperty_IsHonoured_WhenItIsTheOnlyOneSet()
    {
        // A host written against the old API must keep working.
#pragma warning disable CS0618
        var opts = new AIOptions { ModelStorageDir = "/legacy/models" };
#pragma warning restore CS0618

        Assert.Equal("/legacy/models", opts.ResolvedModelStorageDirectory);
    }

    [Fact]
    public void CanonicalProperty_IsHonoured_WhenItIsTheOnlyOneSet()
    {
        var opts = new AIOptions { ModelStorageDirectory = "/canonical/models" };

        Assert.Equal("/canonical/models", opts.ResolvedModelStorageDirectory);
    }

    [Fact]
    public void NeitherSet_FallsBackToADefaultUnderBaseDirectory()
    {
        var opts = new AIOptions();

        var resolved = opts.ResolvedModelStorageDirectory;

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models"), resolved);
    }

    [Fact]
    public void Whitespace_IsTreatedAsUnset()
    {
        // "" or "   " must not become the storage root — that would resolve to
        // the process working directory and scatter 400 MB bundles into it.
#pragma warning disable CS0618
        var opts = new AIOptions
        {
            ModelStorageDirectory = "   ",
            ModelStorageDir       = "",
        };
#pragma warning restore CS0618

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models"),
                     opts.ResolvedModelStorageDirectory);
    }
}
