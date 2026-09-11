// VoiceCoverageTests.cs
//
// Every language the app offers must have a voice it can speak with.
//
// A LANGUAGE IN THE PICKER IS A PROMISE. Somebody chooses isiZulu because they
// want to be spoken to in isiZulu, and a picker that lists a language with no
// voice behind it has told them something untrue before they have typed a word.
// The failure is silent from the inside: selection succeeds, the screen renders,
// and nothing speaks.
//
// Measured 2026-09-11, when this file was written: 69 languages offered, 69
// voice entries covering 78 tags, and ZERO offered languages without a voice.
// This exists so that stays true - the two lists are edited by different people
// for different reasons, and nothing else connects them.
//
// CATALOGUED, NOT INSTALLED, and the difference matters. This asserts that a
// voice EXISTS to be fetched for every offered language. What a particular
// handset has actually downloaded is a device question, and FirstRun already
// carries the warning about conflating the two - Home once claimed 78 languages
// while the two installed voices covered about twelve.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class VoiceCoverageTests
{
    [Fact]
    public void Every_language_the_app_offers_has_a_voice_catalogued_for_it()
    {
        var covered = VoiceLanguages();

        var silent = SampleLanguages.All.Values
            .Where(l => !covered.ContainsKey(Root(l.Tag)))
            .Select(l => $"{l.Tag} ({l.Name})")
            .ToList();

        Assert.True(silent.Count == 0,
            "These languages are offered in the picker with no voice catalogued, so " +
            "choosing them promises speech the app cannot produce:\n  " +
            string.Join("\n  ", silent));
    }

    [Fact]
    public void Every_catalogued_voice_declares_which_languages_it_speaks()
    {
        // A voice with no Language is a voice nothing can select for anybody.
        // It is downloadable, it costs storage, and it can never be chosen.
        var mute = Voices()
            .Where(v => string.IsNullOrWhiteSpace(Language(v)))
            .Select(Name)
            .ToList();

        Assert.True(mute.Count == 0,
            "These voices declare no language and so can never be selected:\n  " +
            string.Join("\n  ", mute));
    }

    [Fact]
    public void No_voice_is_shipped_for_a_language_the_app_never_offers()
    {
        // THE OTHER DIRECTION, AND IT IS NOT SYMMETRICAL. A missing voice is a
        // broken promise; a voice for a language nobody can choose is bytes in
        // the catalogue that no screen can reach.
        //
        // KNOWN AND ACCEPTED TODAY: Spanish, Dutch and Portuguese. Six Piper
        // voices are catalogued for them (es_ES, es_MX, nl_BE, nl_NL, pt_BR,
        // pt_PT) and SampleLanguages lists none of the three - so the voices are
        // unreachable from the picker. Portuguese is the one that stings on a
        // product aimed at this continent: it is Angola and Mozambique.
        //
        // Listed rather than ignored so that adding a voice for a fourth
        // unreachable language fails here and has to be a decision.
        string[] acceptedUnreachable = ["es", "nl", "pt"];

        var offered = SampleLanguages.All.Values
            .Select(l => Root(l.Tag))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unreachable = VoiceLanguages().Keys
            .Select(Root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !offered.Contains(t))
            .Where(t => !acceptedUnreachable.Contains(t, StringComparer.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(unreachable.Count == 0,
            "Voices are catalogued for these languages and the app offers none of them, " +
            "so nothing can select them:\n  " + string.Join("\n  ", unreachable));
    }

    [Fact]
    public void The_accepted_unreachable_languages_really_do_still_have_voices()
    {
        // An exemption for something that no longer exists is an exemption that
        // silently covers nothing - the same trap as a stale PlatformOnly entry.
        var covered = VoiceLanguages();

        foreach (var tag in new[] { "es", "nl", "pt" })
            Assert.True(covered.ContainsKey(tag),
                $"'{tag}' is listed as an accepted unreachable language but has no voice; " +
                "remove it from the exemption list.");
    }

    // ── Reading the catalogue ───────────────────────────────────────────

    /// <summary>Language tag → the voices that speak it.</summary>
    private static Dictionary<string, List<string>> VoiceLanguages()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var voice in Voices())
        {
            foreach (var tag in (Language(voice) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = tag.Trim();
                if (key.Length == 0) continue;
                if (!map.TryGetValue(key, out var names)) map[key] = names = [];
                names.Add(Name(voice));
            }
        }

        return map;
    }

    private static IEnumerable<JsonElement> Voices()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RegistryPath()));
        foreach (var m in doc.RootElement.GetProperty("Models").EnumerateArray().ToList())
            if (m.TryGetProperty("Modality", out var mod) &&
                string.Equals(mod.GetString(), "Tts", StringComparison.Ordinal))
                yield return m.Clone();
    }

    private static string? Language(JsonElement v)
        => v.TryGetProperty("Language", out var l) ? l.GetString() : null;

    private static string Name(JsonElement v)
        => v.TryGetProperty("Name", out var n) ? n.GetString() ?? "(unnamed)" : "(unnamed)";

    /// <summary>"pt-BR" and "pt" are the same language for this question.</summary>
    private static string Root(string tag)
    {
        var dash = tag.IndexOf('-');
        return dash > 0 ? tag[..dash] : tag;
    }

    /// <summary>
    /// The registry on disk rather than the embedded resource.
    /// </summary>
    /// <remarks>
    /// Read as a file so a failure names the thing an author has to edit. The
    /// embedded copy is generated from it by tools/sync-registry, and pointing a
    /// coverage failure at a resource stream would send somebody to the wrong
    /// place.
    /// </remarks>
    private static string RegistryPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "src", "CircleAI.Core", "Models", "embedded_registry.json");
        Assert.True(File.Exists(path), $"registry not found at {path}");
        return path;
    }
}
