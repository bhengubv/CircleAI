// RuleCorpus.cs
//
// Somebody's rules, from wherever they keep them.
//
// THE RULES ARE NOT THE SYSTEM. Every memory test written so far used one
// person's rules as literals - "never restart a device", "-t:InstallKeepingData",
// "I prefer brief feedback" - which proves the memory works for that person and
// says nothing about anybody else. A nurse, a farmer and a teacher have rules
// too, in their own words and their own languages, and none of them are in this
// repository.
//
// So the fixtures come from a corpus and the assertions are about SHAPE. Swap
// the corpus and every property still has to hold; that is what makes them
// tests of the memory rather than tests of one memory's contents.
//
// THE MARKDOWN IS A LIVE CORPUS, not a copy. Rules are read out of the .md
// files as they are on disk, so a rule added to MEMORY.md tomorrow is under
// test tomorrow - and a different person's directory of rules is a different
// corpus with no code change at all.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Memory;

namespace CircleAI.Tests;

/// <summary>One rule, however somebody wrote it down.</summary>
/// <param name="Name">What it is called where it came from.</param>
/// <param name="Statement">The rule itself, in the words it was written in.</param>
/// <param name="Subject">The situation key it belongs to, if it has one.</param>
/// <param name="Kind">What sort of thing it is.</param>
public sealed record Rule(string Name, string Statement, string? Subject, AtomKind Kind)
{
    /// <summary>The rule as something the memory can hold.</summary>
    public MemoryAtom ToAtom() => new()
    {
        Kind = Kind,
        Text = Statement,
        Subject = Subject,
        Outcome = Kind == AtomKind.Decision ? DecisionOutcome.Resolved : null,
    };
}

/// <summary>A person's rules, and where they came from.</summary>
public sealed record RuleCorpus(string Name, IReadOnlyList<Rule> Rules)
{
    public override string ToString() => $"{Name} ({Rules.Count} rules)";
}

/// <summary>Where corpora come from.</summary>
public static class RuleCorpora
{
    // ------------------------------------------------------------------
    // Reading somebody's markdown
    // ------------------------------------------------------------------

    /// <summary>
    /// Rules out of a directory of markdown files.
    /// </summary>
    /// <remarks>
    /// The shape is the one these files already use: YAML frontmatter carrying
    /// a name, a one-line description that IS the rule, and a type. Nothing
    /// here is specific to whose files they are.
    /// </remarks>
    public static RuleCorpus FromMarkdown(string name, string directory, int limit = 60)
    {
        if (!Directory.Exists(directory)) return new RuleCorpus(name, Array.Empty<Rule>());

        var rules = Directory
            .EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(Parse)
            .Where(r => r is not null)
            .Select(r => r!)
            .Take(limit)
            .ToList();

        return new RuleCorpus(name, rules);
    }

    private static Rule? Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;

        string? ruleName = null, description = null, type = null;

        for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            var value = Unquote(line[(colon + 1)..].Trim());
            if (value.Length == 0) continue;

            // Indented keys belong to a nested block - metadata.type is still
            // the type, and nothing else nested is wanted.
            var nested = char.IsWhiteSpace(line[0]);

            if (!nested && key == "name") ruleName = value;
            else if (!nested && key == "description") description = value;
            else if (key == "type") type ??= value;
        }

        if (ruleName is null || description is null) return null;

        return new Rule(
            ruleName,
            description,
            // The name doubles as a subject: it is what the person filed the
            // rule under, which is exactly what a situation key is.
            Subject: ruleName.ToLowerInvariant(),
            Kind: KindOf(type));
    }

    /// <summary>What a person's own filing type means to the memory.</summary>
    private static AtomKind KindOf(string? type) => type?.ToLowerInvariant() switch
    {
        "feedback"  => AtomKind.Ruling,        // guidance on how to work: a rule
        "project"   => AtomKind.Decision,      // what is being done and why
        "reference" => AtomKind.Fact,          // a pointer to something that can go stale
        "user"      => AtomKind.Relationship,  // who they are
        _           => AtomKind.Ruling,
    };

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        return value.Trim();
    }

    // ------------------------------------------------------------------
    // People who are not us
    // ------------------------------------------------------------------
    //
    // A CORPUS OF ONE PERSON PROVES NOTHING ABOUT THE NEXT ONE. These are rules
    // from work that has nothing to do with software, in languages the
    // catalogue is built for, phrased the way people in those jobs phrase
    // things. If a property only holds for English rules about deployment, it
    // is not a property of the memory.

    /// <summary>A ward nurse.</summary>
    public static RuleCorpus Nurse { get; } = new("nurse", new[]
    {
        new Rule("handover", "Never hand over a patient without saying what changed on your shift",
                 "ward:handover", AtomKind.Ruling),
        new Rule("allergies", "Always read the allergy band aloud before giving anything",
                 "ward:medication", AtomKind.Ruling),
        new Rule("falls", "Bed rails go up for anyone who scored over eight on the falls chart",
                 "ward:falls", AtomKind.Decision),
        new Rule("nights", "She would rather be told at three in the morning than find out at seven",
                 "ward:escalation", AtomKind.Preference),
        new Rule("sharps", "The sharps bin is replaced at three quarters, not when it is full",
                 "ward:sharps", AtomKind.Fact),
    });

    /// <summary>A smallholder farmer, in isiZulu.</summary>
    public static RuleCorpus Farmer { get; } = new("umlimi", new[]
    {
        new Rule("ukutshala", "Ungatshali ummbila ngaphambi kokuba imvula ifike kabili",
                 "insimu:ukutshala", AtomKind.Ruling),
        new Rule("izinkomo", "Izinkomo zidla emini, hhayi ekuseni kakhulu",
                 "izilwane:ukudla", AtomKind.Decision),
        new Rule("umanyolo", "Umanyolo omningi ushisa izithombo",
                 "insimu:umanyolo", AtomKind.Fact),
        new Rule("intengo", "Ungathengisi ngaphansi kwentengo yasemakethe yangeSonto",
                 "imakethe:intengo", AtomKind.Ruling),
    });

    /// <summary>A primary school teacher, in Sesotho.</summary>
    public static RuleCorpus Teacher { get; } = new("mosuwe", new[]
    {
        new Rule("thuto", "O se ke wa qala thuto pele bana bohle ba dutse",
                 "sekolo:thuto", AtomKind.Ruling),
        new Rule("batswadi", "Batswadi ba tsebiswa pele ho beke e latelang",
                 "sekolo:batswadi", AtomKind.Decision),
        new Rule("dipalo", "Bana ba ithuta dipalo hantle hoseng",
                 "sekolo:dipalo", AtomKind.Fact),
    });

    /// <summary>A shopkeeper, in Afrikaans.</summary>
    public static RuleCorpus Shopkeeper { get; } = new("winkelier", new[]
    {
        new Rule("krediet", "Moet nooit krediet gee sonder 'n naam en 'n nommer nie",
                 "winkel:krediet", AtomKind.Ruling),
        new Rule("voorraad", "Tel die voorraad Vrydagaand, nie Maandagoggend nie",
                 "winkel:voorraad", AtomKind.Decision),
        new Rule("brood", "Brood kom voor sesuur, so die deur is oop kwart voor",
                 "winkel:aflewering", AtomKind.Fact),
    });

    /// <summary>Someone working in Japanese, where nothing is space-separated.</summary>
    public static RuleCorpus Tokyo { get; } = new("tokyo", new[]
    {
        new Rule("kaigi", "会議の前に必ず資料を配ること", "shigoto:kaigi", AtomKind.Ruling),
        new Rule("nouki", "納期は金曜日の正午、それ以降は来週扱い", "shigoto:nouki", AtomKind.Decision),
        new Rule("renraku", "急ぎの件は電話、それ以外はメール", "shigoto:renraku", AtomKind.Preference),
    });

    /// <summary>The rules this repository keeps in markdown, as they are today.</summary>
    public static RuleCorpus Repository { get; } = FromMarkdown(
        "repository", RepositoryRoot() is { } root ? Path.Combine(root, "docs", "rules") : "");

    /// <summary>
    /// Whoever is running this, if they keep rules the same way.
    /// </summary>
    /// <remarks>
    /// Empty on any machine that does not - which is the point. A corpus that
    /// only exists on one laptop must not be what a property depends on.
    /// </remarks>
    public static RuleCorpus Local { get; } = FromMarkdown(
        "local",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".circleai", "rules"));

    // ------------------------------------------------------------------

    /// <summary>Every corpus with anything in it.</summary>
    public static IEnumerable<RuleCorpus> All =>
        new[] { Nurse, Farmer, Teacher, Shopkeeper, Tokyo, Repository, Local }
            .Where(c => c.Rules.Count > 0);

    /// <summary>For a Theory: every corpus, one per row.</summary>
    public static IEnumerable<object[]> Each() => All.Select(c => new object[] { c });

    /// <summary>Every rule in every corpus, one per row.</summary>
    public static IEnumerable<object[]> EachRule() =>
        All.SelectMany(c => c.Rules.Select(r => new object[] { c.Name, r }));

    private static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CircleAI.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
