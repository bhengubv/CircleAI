// KnownFacts.cs
//
// FACTS WITH ONE RIGHT ANSWER ARE LOOKED UP OR WORKED OUT, NOT GENERATED.
//
// Measured on the P30 on 2026-10-20, AFTER the input to the model had been dumped and
// shown to be exactly right: Qwen3.5-0.8B still answered "How many days are in a leap
// year?" with "There are 365 or 366 days in a leap year depending on whether it is a
// common year or a leap year", and "What is the capital of South Africa?" with "Cape
// Town" - every run. A calendar fact is arithmetic and a capital is a lookup; the clock
// and the sums already work this way, and these join them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant.Requests;

/// <summary>Days, weeks, months and leap years: worked out, never generated.</summary>
public sealed partial class CalendarFactsShortcut(TimeProvider? clock = null) : IAskShortcut
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    /// <inheritdoc />
    public string Name => "calendar-facts";

    // "How many days are in a leap year?", "How many days does a year have?", "How many
    // days in February?", "How many weeks are there in a year?"
    [GeneratedRegex(@"\bhow\s+many\s+(?<unit>days|weeks|months|hours|minutes|seconds)\s+(?:are\s+)?(?:there\s+)?(?:in|does)\s+(?:a|an|one)?\s*(?<span>leap\s+year|normal\s+year|common\s+year|year|week|day|hour|minute|january|february|march|april|may|june|july|august|september|october|november|december)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HowMany();

    [GeneratedRegex(@"\bis\s+(?<year>\d{4})\s+a\s+leap\s+year\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsLeap();

    [GeneratedRegex(@"\b(?:when\s+is\s+the\s+next|next)\s+leap\s+year\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NextLeap();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct) =>
        ValueTask.FromResult(Answer(ask.Asked ?? string.Empty, _clock.GetUtcNow().Year));

    /// <summary>The answer to a calendar question, or null when it is not one.</summary>
    public static string? Answer(string said, int thisYear)
    {
        var leap = IsLeap().Match(said);
        if (leap.Success)
        {
            var year = int.Parse(leap.Groups["year"].Value, CultureInfo.InvariantCulture);
            return DateTime.IsLeapYear(year)
                ? $"Yes, {year} is a leap year: it has 366 days."
                : $"No, {year} is not a leap year: it has 365 days.";
        }

        if (NextLeap().IsMatch(said))
        {
            var next = thisYear + 1;
            while (!DateTime.IsLeapYear(next)) next++;
            return $"The next leap year is {next}.";
        }

        var m = HowMany().Match(said);
        if (!m.Success) return null;

        var unit = m.Groups["unit"].Value.ToLowerInvariant();
        var span = Regex.Replace(m.Groups["span"].Value.ToLowerInvariant(), @"\s+", " ");
        return (unit, span) switch
        {
            ("days", "leap year") => "A leap year has 366 days.",
            ("days", "normal year" or "common year") => "A normal year has 365 days.",
            ("days", "year") => "A year has 365 days, or 366 in a leap year.",
            ("days", "week") => "A week has 7 days.",
            ("days", "february") => "February has 28 days, or 29 in a leap year.",
            ("days", var month) when Array.IndexOf(Months, month) >= 0 =>
                $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(month)} has {DateTime.DaysInMonth(2001, Array.IndexOf(Months, month) + 1)} days.",
            ("weeks", "year" or "normal year" or "common year") => "A year has 52 weeks and one day, or 52 weeks and two days in a leap year.",
            ("weeks", "leap year") => "A leap year has 52 weeks and two days.",
            ("months", "year" or "leap year" or "normal year" or "common year") => "A year has 12 months.",
            ("hours", "day") => "A day has 24 hours.",
            ("hours", "week") => "A week has 168 hours.",
            ("minutes", "hour") => "An hour has 60 minutes.",
            ("minutes", "day") => "A day has 1,440 minutes.",
            ("seconds", "minute") => "A minute has 60 seconds.",
            ("seconds", "hour") => "An hour has 3,600 seconds.",
            _ => null,
        };
    }
}

/// <summary>"Tell me a joke": a short, clean one from data, never generated.</summary>
/// <remarks>
/// A 0.8B CANNOT LAND A PUNCHLINE. Five runs on the P30 on 2026-10-20 gave "Why did the
/// scarecrow win an award? He was very useful!" and "Why did the scarecrow feel unloved?
/// Because he had his own barn!" - the shape of a joke with the joke missing. Every
/// assistant people use tells curated ones; this rotates through a shipped list, so the
/// same one does not come twice running.
/// </remarks>
public sealed partial class JokeShortcut(Func<string?>? wakePhrase = null) : IAskShortcut
{
    private static readonly Lazy<IReadOnlyList<string>> Jokes = new(Load);
    private static int _next = Random.Shared.Next(1000);

    /// <inheritdoc />
    public string Name => "joke";

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:tell|give|say)\s+(?:me|us)\s+(?:a|another|one\s+more|a\s+(?:good|funny|short|new))\s+joke|(?:do\s+you\s+)?know\s+(?:a|any)\s+(?:good\s+)?jokes?|make\s+me\s+laugh)[\s.!?]*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AskForOne();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        string? phrase = null;
        try { phrase = wakePhrase?.Invoke(); } catch { /* the shortcut still works without it */ }
        return ValueTask.FromResult(Answer(Said.WithoutWakePhrase(ask.Asked, phrase)));
    }

    /// <summary>The next joke when <paramref name="said"/> asks for one, or null.</summary>
    public static string? Answer(string? said)
    {
        if (!AskForOne().IsMatch((said ?? string.Empty).Trim())) return null;
        var jokes = Jokes.Value;
        if (jokes.Count == 0) return null;
        var i = (uint)Interlocked.Increment(ref _next);
        return "Here's one: " + jokes[(int)(i % (uint)jokes.Count)];
    }

    /// <summary>How many jokes the shipped data holds (0 means it did not load).</summary>
    public static int Known => Jokes.Value.Count;

    private static IReadOnlyList<string> Load()
    {
        var list = new List<string>();
        using var stream = typeof(JokeShortcut).Assembly.GetManifestResourceStream("CircleAI.Assistant.Requests.jokes.json");
        if (stream is null) return list;
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.TryGetProperty("Jokes", out var jokes) && jokes.ValueKind == JsonValueKind.Array)
            foreach (var j in jokes.EnumerateArray())
                if (j.GetString() is { Length: > 0 } joke) list.Add(joke);
        return list;
    }
}

/// <summary>Capital cities, from data shipped with the app.</summary>
/// <remarks>
/// A COUNTRY WITH MORE THAN ONE CAPITAL GETS ALL OF THEM: South Africa's answer names
/// Pretoria, Cape Town and Bloemfontein. A name two places share ("Georgia", "Congo") gets
/// both. A place the data does not name - a contested one is left out on purpose - goes to
/// the model as before.
/// </remarks>
public sealed partial class CapitalsShortcut : IAskShortcut
{
    private static readonly Lazy<IReadOnlyList<(string Key, Country Country)>> Index = new(Load);

    /// <inheritdoc />
    public string Name => "capitals";

    // "What is the capital of South Africa?", "capital city of the Netherlands", "Which
    // city is the capital of Kenya". Anchored at the end: "the capital of France's
    // economy" is not this.
    [GeneratedRegex(@"\bcapital(?:\s+city)?\s+of\s+(?<place>[\p{L}\s\.'’\-]+?)\s*[\?\.!]*\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CapitalOf();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct) =>
        ValueTask.FromResult(Answer(ask.Asked ?? string.Empty));

    /// <summary>The capital of the place named in <paramref name="said"/>, or null.</summary>
    public static string? Answer(string said)
    {
        var m = CapitalOf().Match(said);
        if (!m.Success) return null;

        var key = Key(m.Groups["place"].Value);
        if (key.StartsWith("the ", StringComparison.Ordinal)) key = key[4..];
        foreach (var (name, country) in Index.Value)
        {
            if (!string.Equals(name, key, StringComparison.Ordinal)) continue;
            return !string.IsNullOrWhiteSpace(country.Answer)
                ? country.Answer
                : $"The capital of {country.Display ?? country.Name} is {country.Capital}.";
        }
        return null;
    }

    /// <summary>A place name as it is compared: lower case, no accents, single spaces.</summary>
    private static string Key(string place)
    {
        var decomposed = place.Trim().Replace('’', '\'').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return Regex.Replace(sb.ToString().Trim(' ', '.', '-'), @"\s+", " ");
    }

    // READ WITH JsonDocument, NOT A REFLECTION DESERIALISER: the service APK is trimmed, and
    // a trimmed-away property would leave this table silently empty on the phone.
    private static IReadOnlyList<(string Key, Country Country)> Load()
    {
        var index = new List<(string, Country)>();
        var asm = typeof(CapitalsShortcut).Assembly;
        using var stream = asm.GetManifestResourceStream("CircleAI.Assistant.Requests.capitals.json");
        if (stream is null) return index;

        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("Countries", out var countries)) return index;
        foreach (var e in countries.EnumerateArray())
        {
            var c = new Country(Text(e, "Name") ?? string.Empty, Text(e, "Display"), Text(e, "Capital") ?? string.Empty, Text(e, "Answer"));
            if (c.Name.Length == 0 || c.Capital.Length == 0) continue;
            index.Add((Key(c.Name), c));
            if (e.TryGetProperty("Aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                foreach (var a in aliases.EnumerateArray())
                    if (a.GetString() is { Length: > 0 } alias) index.Add((Key(alias), c));
        }
        return index;

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>How many places the shipped data names, aliases included (0 means it did not load).</summary>
    public static int KnownPlaces => Index.Value.Count;

    private sealed record Country(string Name, string? Display, string Capital, string? Answer);
}
