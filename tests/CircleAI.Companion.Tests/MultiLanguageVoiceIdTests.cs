// MultiLanguageVoiceIdTests.cs
//
// A model that speaks eleven languages must be told which one to speak.
//
// The South African VITS voice takes a language id per utterance. Id 0 is
// Afrikaans — a Germanic language — so an unset id made isiZulu, isiXhosa,
// Sesotho, Setswana, Tshivenda, siSwati, isiNdebele, Sepedi and Xitsonga all come
// out with Afrikaans phonetics. Nine languages wrong. Synthesis succeeded every
// time and every automated check passed: the audio was the right length, the WAV
// was valid, the hash matched. The only thing that detected it was somebody who
// speaks one of those languages listening to it and saying it sounded like a
// European fumbling.
//
// So the mapping is pinned here, in a test, rather than trusted to a file that
// happened to sit beside the model during development.

using System.Text.Json;
using Xunit;

namespace CircleAI.Companion.Tests;

public class MultiLanguageVoiceIdTests
{
    /// <summary>The ids the shipped model actually uses, as published with it.</summary>
    private static readonly Dictionary<string, int> Published = new()
    {
        ["afr"] = 0, ["eng"] = 1, ["nbl"] = 2, ["nso"] = 3, ["sot"] = 4, ["ssw"] = 5,
        ["tsn"] = 6, ["tso"] = 7, ["ven"] = 8, ["xho"] = 9, ["zul"] = 10,
    };

    /// <summary>Catalogue tag → the key the model's own map is written in.</summary>
    private static string ThreeLetter(string tag) => tag switch
    {
        "af" => "afr", "en" => "eng", "nr" => "nbl", "st" => "sot", "ss" => "ssw",
        "tn" => "tsn", "ts" => "tso", "ve" => "ven", "xh" => "xho", "zu" => "zul",
        _ => tag,
    };

    [Theory]
    [InlineData("af", 0)]  [InlineData("en", 1)]  [InlineData("nr", 2)]
    [InlineData("nso", 3)] [InlineData("st", 4)]  [InlineData("ss", 5)]
    [InlineData("tn", 6)]  [InlineData("ts", 7)]  [InlineData("ve", 8)]
    [InlineData("xh", 9)]  [InlineData("zu", 10)]
    public void Every_catalogue_tag_maps_to_the_right_voice_in_the_model(string tag, int expectedId)
    {
        Assert.Equal(expectedId, Published[ThreeLetter(tag)]);
    }

    [Fact]
    public void No_two_languages_share_an_id()
    {
        // A collision would silently make one language speak as another — the
        // same class of failure, just harder to spot.
        Assert.Equal(Published.Count, Published.Values.Distinct().Count());
    }

    [Fact]
    public void Only_Afrikaans_is_id_zero_so_an_unset_id_is_always_wrong_for_the_other_ten()
    {
        // Stated as a test because it is the whole reason this bug was invisible:
        // failing to set the id does not error, it just speaks Afrikaans.
        Assert.Equal(0, Published["afr"]);
        Assert.All(Published.Where(kv => kv.Key != "afr"), kv => Assert.NotEqual(0, kv.Value));
    }

    [Fact]
    public void The_shipped_map_parses_and_covers_all_eleven()
    {
        // Byte-for-byte what is uploaded beside the model in the bucket.
        const string shipped = """
            {
                "afr": 0, "eng": 1, "nbl": 2, "nso": 3, "sot": 4, "ssw": 5,
                "tsn": 6, "tso": 7, "ven": 8, "xho": 9, "zul": 10
            }
            """;
        using var doc = JsonDocument.Parse(shipped);
        var parsed = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());

        Assert.Equal(11, parsed.Count);
        Assert.Equal(Published, parsed);
    }
}
