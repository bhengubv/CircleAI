// PocketBundleTests.cs
//
// Which voices a side-loaded Pocket-TTS bundle may be used for.
//
// RunPocketAsync had exactly one caller in the repo and it was in the sample
// that is being retired, so on the head that ships the five-second voice
// cloning this engine exists for was unreachable. The list that decided when to
// use it lived in an Activity, which is why only that head could read it.

using CircleAI.Assistant.Voice;
using Xunit;

namespace CircleAI.Tests;

public class PocketBundleTests : IDisposable
{
    // A directory that looks like a side-loaded bundle to the only check
    // PocketBundleFor makes: the folder is there.
    readonly string _root = Path.Combine(
        Path.GetTempPath(), "pocket-" + Guid.NewGuid().ToString("N"));

    public PocketBundleTests()
        => Directory.CreateDirectory(Path.Combine(_root, CircleAITtsProbe.PocketFolder));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    public void Every_tag_the_bundle_speaks_finds_it(string tag)
        => Assert.NotNull(CircleAITtsProbe.PocketBundleFor(_root, tag));

    [Fact]
    public void The_list_is_eight_tags_and_not_all_of_them_are_european()
    {
        // IT HAS BEEN WRITTEN DOWN AS "EIGHT EUROPEAN LANGUAGES" MORE THAN ONCE
        // and that is wrong twice: eight TAGS, six languages, and two of them -
        // Mexican Spanish and Brazilian Portuguese - are not European at all.
        // The test is here so the next person to summarise it has to be right.
        Assert.Equal(8, CircleAITtsProbe.PocketLanguages.Count);
        Assert.Contains("es-MX", CircleAITtsProbe.PocketLanguages);
        Assert.Contains("pt-BR", CircleAITtsProbe.PocketLanguages);
    }

    [Theory]
    [InlineData("zu")]
    [InlineData("xh")]
    [InlineData("ja")]
    [InlineData("af")]
    public void A_language_it_cannot_speak_falls_through_to_the_catalogue(string tag)
    {
        // THIS IS THE IMPORTANT HALF. Returning the bundle for a tag it cannot
        // speak would route isiZulu through an English cloning engine, which
        // does not fail - it produces confident nonsense.
        Assert.Null(CircleAITtsProbe.PocketBundleFor(_root, tag));
    }

    [Fact]
    public void Tags_match_whatever_case_they_arrive_in()
    {
        // A model config writes "ES-es", a settings file keeps "es-ES", and a
        // phone locale hands over "es_ES". Two of those have been seen.
        Assert.NotNull(CircleAITtsProbe.PocketBundleFor(_root, "ES-ES"));
        Assert.NotNull(CircleAITtsProbe.PocketBundleFor(_root, "EN"));
    }

    [Fact]
    public void Nothing_side_loaded_means_nothing_to_find()
    {
        // The ordinary case, on every phone where nobody has copied anything.
        var empty = Path.Combine(Path.GetTempPath(), "pocket-none-" + Guid.NewGuid().ToString("N"));
        Assert.Null(CircleAITtsProbe.PocketBundleFor(empty, "en"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_head_that_never_set_a_sideload_folder_asks_for_nothing(string? root)
    {
        // CircleAISpeaker.SideloadFolder is null until a head sets it, and one
        // head never did. Path.Combine on a null throws, which would take the
        // voice down on exactly the configuration that has no bundle anyway.
        Assert.Null(CircleAITtsProbe.PocketBundleFor(root, "en"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_tag_is_not_a_pocket_tag(string? tag)
        => Assert.Null(CircleAITtsProbe.PocketBundleFor(_root, tag));
}
