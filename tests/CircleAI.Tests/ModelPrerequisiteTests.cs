// ModelPrerequisiteTests.cs
//
// A voice that cannot speak without a second download.
//
// THE BUG THESE EXIST FOR. JSUT-VITS speaks Open JTalk phoneme ids, so it needs
// the 104 MB naist-jdic dictionary, which is catalogued separately because every
// Japanese voice shares it. CircleAITtsProbe fetched it. CircleAISpeaker - the
// thing that actually speaks - mentioned it NOWHERE.
//
// So on a phone set to Japanese the speaker downloaded 144 MB of weights, asked
// OpenJTalkPhonemizer.Open for a phonemiser, and got null, because Open returns
// null when the dictionary directory is absent. The diagnostic screen could
// speak Japanese and the app could not, and the error named a missing phonemiser
// rather than a missing download.
//
// One fact with two owners, where the second did not know it was one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircleAI.Core;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class ModelPrerequisiteTests
{
    [Fact]
    public void An_open_jtalk_voice_needs_the_japanese_dictionary()
    {
        var jsut = Entry("JSUT-VITS", architecture: "vits-espnet");

        Assert.Contains(ModelPrerequisites.OpenJTalkDictionary, ModelPrerequisites.For(jsut));
    }

    [Fact]
    public void The_rule_is_the_architecture_not_the_name()
    {
        // WHAT THE PROBE'S OWN COMMENT ASKED FOR AND COULD NOT HAVE. A name
        // prefix is a convention nobody is obliged to follow; an architecture is
        // a fact about what the file is. A future ESPnet voice in any language
        // must pull the dictionary without anybody remembering to add it.
        var future = Entry("Kokoro-ja-espnet", architecture: "vits-espnet");

        Assert.Contains(ModelPrerequisites.OpenJTalkDictionary, ModelPrerequisites.For(future));
    }

    [Fact]
    public void The_name_is_still_honoured_when_the_architecture_is_blank()
    {
        // Losing a prerequisite silently costs somebody a voice that will not
        // speak; carrying a redundant check costs a string comparison.
        var noArch = Entry("JSUT-VITS", architecture: null);

        Assert.Contains(ModelPrerequisites.OpenJTalkDictionary, ModelPrerequisites.For(noArch));
    }

    [Fact]
    public void An_ordinary_voice_needs_nothing_extra()
    {
        Assert.Empty(ModelPrerequisites.For(Entry("MMS-zul", architecture: "vits")));
        Assert.Empty(ModelPrerequisites.For(Entry("Piper-en_US-lessac-medium", architecture: "vits")));
    }

    [Fact]
    public void The_dictionary_is_not_its_own_prerequisite()
    {
        var dictionary = Entry(ModelPrerequisites.OpenJTalkDictionary, architecture: "vits-espnet");

        Assert.Empty(ModelPrerequisites.For(dictionary));
    }

    [Fact]
    public void Resolving_finds_the_entry_in_the_catalogue()
    {
        var jsut = Entry("JSUT-VITS", architecture: "vits-espnet");
        var dict = Entry(ModelPrerequisites.OpenJTalkDictionary, architecture: "open-jtalk-naist-jdic");

        var resolved = ModelPrerequisites.Resolve(jsut, [jsut, dict]);

        Assert.Equal(ModelPrerequisites.OpenJTalkDictionary, Assert.Single(resolved).Name);
    }

    [Fact]
    public void A_prerequisite_missing_from_the_catalogue_is_skipped_not_thrown()
    {
        // A renamed entry must not stop a voice downloading. The voice may still
        // be usable, and a hard failure would take a working capability away
        // over a bookkeeping error.
        var jsut = Entry("JSUT-VITS", architecture: "vits-espnet");

        Assert.Empty(ModelPrerequisites.Resolve(jsut, [jsut]));
        Assert.Empty(ModelPrerequisites.Resolve(jsut, []));
        Assert.Empty(ModelPrerequisites.Resolve(jsut, null));
    }

    // ── Against the real catalogue ──────────────────────────────────────

    [Fact]
    public void Every_prerequisite_the_shipped_catalogue_asks_for_is_in_the_shipped_catalogue()
    {
        // THE ONE THAT WOULD HAVE CAUGHT THE ORIGINAL BUG'S COUSIN. A voice
        // asking for a download that is not catalogued cannot ever be satisfied,
        // and the symptom is a voice that does not speak.
        var catalogue = ShippedCatalogue();
        var names = catalogue.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unsatisfiable = catalogue
            .SelectMany(c => ModelPrerequisites.For(c).Select(p => (Voice: c.Name, Needs: p)))
            .Where(x => !names.Contains(x.Needs))
            .Select(x => $"{x.Voice} needs {x.Needs}, which is not catalogued")
            .ToList();

        Assert.True(unsatisfiable.Count == 0, string.Join("\n  ", unsatisfiable));
    }

    [Fact]
    public void The_japanese_voice_in_the_shipped_catalogue_really_does_ask_for_the_dictionary()
    {
        // Guards the wiring end to end: the registry's Architecture field must
        // survive deserialise (it used to be dropped) AND the rule must fire.
        var catalogue = ShippedCatalogue();

        var jsut = catalogue.SingleOrDefault(c => c.Name == "JSUT-VITS");
        Assert.NotNull(jsut);
        Assert.Equal("vits-espnet", jsut!.Architecture);

        Assert.Contains(ModelPrerequisites.OpenJTalkDictionary, ModelPrerequisites.For(jsut));
        Assert.Single(ModelPrerequisites.Resolve(jsut, catalogue));
    }

    [Fact]
    public void The_registrys_architecture_survives_deserialise()
    {
        // IT USED TO BE DROPPED. Every entry's Architecture was read from the
        // JSON and thrown away, which is why the prerequisite rule had to test
        // an entry NAME.
        var withArchitecture = ShippedCatalogue().Count(c => !string.IsNullOrWhiteSpace(c.Architecture));

        Assert.True(withArchitecture > 0,
            "no catalogue entry carries an Architecture; the registry field is being dropped again");
    }

    private static ModelEntry Entry(string name, string? architecture) =>
        new(name, "1", "ONNX") { Architecture = architecture, Modality = ModelModality.Tts };

    private static IReadOnlyList<ModelEntry> ShippedCatalogue()
    {
        using var registry = new ModelRegistryService();

        // The embedded resource, not a live catalogue: this asks what SHIPPED.
        return [.. registry.AllModels];
    }
}
