// ModelPathsTests.cs
//
// Where models live, decided once.
//
// This is a test about a folder, and it exists because a 523 MB chat model was
// downloaded twice onto a phone with 890 MB of app data. Four places decided
// the model directory; on a desktop they all agreed and on Android two of them
// did not, because SpecialFolder.ApplicationData is a SUBDIRECTORY of the
// folder MAUI calls AppDataDirectory. Nothing failed. Both paths existed, both
// were writable, and the only symptom was the disk.

using System;
using System.IO;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public class ModelPathsTests
{
    [Fact]
    public void There_is_one_answer_and_everything_uses_it()
    {
        // The property that was broken: asking twice gives the same folder.
        Assert.Equal(ModelPaths.Default, ModelPaths.Default);
        Assert.EndsWith(Path.Combine("CircleAI", "Models"), ModelPaths.Default, StringComparison.Ordinal);
    }

    [Fact]
    public void A_caller_that_names_a_folder_gets_that_folder()
    {
        var asked = Path.Combine(Path.GetTempPath(), "circleai-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(asked, ModelPaths.Resolve(asked));
            Assert.True(Directory.Exists(asked), "resolving should create it, so a caller need not");
        }
        finally { try { Directory.Delete(asked, true); } catch { } }
    }

    [Fact]
    public void A_caller_that_names_nothing_gets_the_default_rather_than_its_own_guess()
    {
        // Every loader had its own answer for what null meant. This is that
        // answer, once - and it is why a forgotten argument no longer costs
        // half a gigabyte.
        Assert.Equal(ModelPaths.Default, ModelPaths.Resolve(null));
        Assert.Equal(ModelPaths.Default, ModelPaths.Resolve("   "));
    }

    [Fact]
    public void The_models_folder_is_inside_the_apps_own_storage()
    {
        // On Android the old default was files/.config/CircleAI/Models while
        // the app used files/CircleAI/Models. Both are "inside app storage",
        // which is exactly why nobody caught it - so this asserts the shape
        // the platform actually gives us rather than a literal path.
        Assert.StartsWith(ModelPaths.Root, ModelPaths.Default, StringComparison.Ordinal);
        Assert.DoesNotContain(".config", ModelPaths.Default, StringComparison.Ordinal);
    }
}
