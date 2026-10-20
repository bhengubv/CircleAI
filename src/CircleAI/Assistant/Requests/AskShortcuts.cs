// AskShortcuts.cs
//
// ANSWERS THAT MUST NOT COME FROM THE MODEL.
//
// Recorded on the P30 while testing with the owner: asked the date, the brain guessed;
// asked the temperature in Tokushima, it invented "twenty five degrees"; handed Whisper's
// "(Bell)" it answered a question nobody asked; and "Hey B" put to it cost 38 seconds to
// say hello. Each of these has a right answer that is either already on the phone (the
// clock, the time zone) or is an honest "I can't see that" - and a 0.8B model is the
// wrong place to get either. The coordinator tries these, in order, before the brain.

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant.Requests;

/// <summary>The standard shortcut chain every head uses.</summary>
public static class AskShortcuts
{
    /// <summary>Greeting, non-speech, a question still to come, the clock, sums, then live data - in that order.</summary>
    /// <param name="wakePhrase">The phrase the phone listens for, so "Hey B" alone is recognised as a hello.</param>
    /// <param name="clock">The clock (tests pass a fixed one).</param>
    public static System.Collections.Generic.IReadOnlyList<IAskShortcut> Standard(
        Func<string?>? wakePhrase = null, TimeProvider? clock = null) => new IAskShortcut[]
    {
        new GreetingShortcut(wakePhrase),
        new NonSpeechShortcut(),
        new QuestionOpenerShortcut(wakePhrase),
        new DeviceClockShortcut(clock),
        new ArithmeticShortcut(),
        new CalendarFactsShortcut(clock),
        new CapitalsShortcut(),
        new JokeShortcut(wakePhrase),
        new NoLiveDataShortcut(),
    };
}

/// <summary>Sums are worked out, never generated.</summary>
/// <remarks>
/// On the P30 on 2026-10-17 the 0.8B answered "What is 347 times 89" with "20,543";
/// on an earlier day "12 times 8" with "6". A language model predicts digits; it does not
/// multiply. ArithmeticIntent does, and has for months - it was simply never in front
/// of the service's questions.
/// </remarks>
public sealed class ArithmeticShortcut : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "arithmetic";

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct) =>
        ValueTask.FromResult(ArithmeticIntent.TryAnswer(ask.Asked, out var answer) ? answer : null);
}

/// <summary>A hello is answered with a hello, never by the model.</summary>
/// <remarks>
/// On 2026-10-03 "Hey, B." went to the brain: three skills matched on "hey", the prompt
/// reached 573 tokens and the turn took 38.5 s - to say hello.
/// </remarks>
public sealed class GreetingShortcut(Func<string?>? wakePhrase = null) : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "greeting";

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        string? phrase = null;
        try { phrase = wakePhrase?.Invoke(); } catch { /* a listener that cannot say its phrase costs only the shortcut */ }
        return ValueTask.FromResult(Opener.IsNothingButHello(ask.Asked, phrase) ? AssistantPersona.Greeting : null);
    }
}

/// <summary>A sound the recogniser wrote down is not a question.</summary>
public sealed partial class NonSpeechShortcut : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "non-speech";

    // (Bell)  [Music]  (upbeat music)  *laughs*  [BLANK_AUDIO]  - and nothing else.
    [GeneratedRegex(@"^\s*(?:[\(\[\*♪][^\)\]\*♪]{0,40}[\)\]\*♪]\s*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex Annotations();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        var said = ask.Asked?.Trim() ?? string.Empty;
        var words = said.Trim('.', ',', '!', '?', ' ', '-', '…');
        if (words.Length == 0 || Annotations().IsMatch(said))
            return ValueTask.FromResult<string?>(AssistantPersona.DidNotCatchThat);
        return ValueTask.FromResult<string?>(null);
    }
}

/// <summary>"I have a health question" is somebody about to ask one.</summary>
/// <remarks>
/// THE CAPABILITY TILES SAY THIS, AND NOTHING HAS BEEN ASKED YET. On the P30 on 2026-10-17
/// the health tile's opener went to the 0.8B with a skill in front of it, and ten seconds
/// later came back as "I can summarize the background as 'I can provide basic animal
/// care'". Somebody who says they have a question wants to be asked for it - at once.
/// A question that comes WITH it ("I have a question: what is...") is not this.
/// </remarks>
public sealed partial class QuestionOpenerShortcut(Func<string?>? wakePhrase = null) : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "question-opener";

    // "I have a health question", "I've got a quick legal question",
    // "I have a question about my job or my rights at work".
    [GeneratedRegex(@"^(?:i\s+have(?:\s+got)?|i['’]ve\s+got|i\s+got)\s+(?:a|an|one|another|some)\s+(?:(?<kind>[\p{L}'’\-]+(?:\s+[\p{L}'’\-]+){0,2})\s+)?questions?(?:\s+(?:about|on|regarding)\s+(?<about>[^?.!:]{2,120}?))?\s*[.!?]*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Opening();

    // "Help me deal with a government office", "Help me with my CV". THE GOVERNMENT TILE:
    // on 2026-10-17 at 10:30 the 0.8B answered it "I can't help you deal with a government
    // office directly" - a refusal, twelve seconds in, to somebody who had not yet said
    // what they needed. "Help me plan meals" says what it needs, and is not this.
    [GeneratedRegex(@"^help\s+me\s+(?:deal\s+with|sort\s+out|with)\s+(?<what>[^?.!:]{2,60}?)[\s.!]*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HelpWith();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        string? phrase = null;
        try { phrase = wakePhrase?.Invoke(); } catch { /* the shortcut still works without it */ }
        var said = Said.WithoutWakePhrase(ask.Asked, phrase);

        var m = Opening().Match(said);
        if (m.Success)
        {
            var kind = m.Groups["kind"].Success ? m.Groups["kind"].Value.Trim() : null;
            var about = m.Groups["about"].Success ? Said.Lead(Said.ToSecondPerson(m.Groups["about"].Value)) : null;
            return ValueTask.FromResult<string?>(AssistantPersona.AskForIt(kind, about));
        }

        var help = HelpWith().Match(said);
        if (help.Success)
        {
            // "a government office" is said back as "the government office".
            var what = Said.Lead(Said.ToSecondPerson(help.Groups["what"].Value.Trim()));
            what = Regex.Replace(what, @"^(?:a|an)\s+", "the ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return ValueTask.FromResult<string?>(AssistantPersona.AskWhatTheyNeed(what));
        }

        return ValueTask.FromResult<string?>(null);
    }
}

/// <summary>The date and the time come from the phone's clock.</summary>
public sealed partial class DeviceClockShortcut(TimeProvider? clock = null, Func<TimeZoneInfo>? localZone = null) : IAskShortcut
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<TimeZoneInfo> _zone = localZone ?? (() => TimeZoneInfo.Local);

    /// <inheritdoc />
    public string Name => "clock";

    [GeneratedRegex(@"\b(what(?:'s| is)?\s+(?:the\s+)?(?:date|day)(?:\s+(?:is it|today|it is))?|what\s+day\s+is\s+(?:it|today)|today'?s\s+date|which\s+day\s+is\s+(?:it|today))\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateQuestion();

    [GeneratedRegex(@"\bwhat(?:'s| is)?\s+(?:the\s+)?time(?:\s+is\s+it)?(?:\s+(?:right\s+)?now)?(?:\s+in\s+(?<place>[\p{L}\s\-'\.,]+?))?\s*[\?\.!]*\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeQuestion();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        var said = ask.Asked ?? string.Empty;
        // "What date is the meeting?" is not this; only the bare question is.
        var time = TimeQuestion().Match(said);
        if (time.Success)
        {
            var place = time.Groups["place"].Success ? time.Groups["place"].Value.Trim(' ', ',', '.') : null;
            if (string.IsNullOrWhiteSpace(place))
                return ValueTask.FromResult<string?>(SayTime(_clock.GetUtcNow(), _zone(), null));

            var zone = ZoneFor(place!);
            // A PLACE IT CANNOT PLACE IS NOT GUESSED AT. Null hands the question on.
            return ValueTask.FromResult(zone is null ? null : SayTime(_clock.GetUtcNow(), zone.Value.Zone, zone.Value.Name));
        }

        if (DateQuestion().IsMatch(said) && !said.Contains(" of the ", StringComparison.OrdinalIgnoreCase))
        {
            var now = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone());
            return ValueTask.FromResult<string?>(
                "It's " + now.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")) + ".");
        }

        return ValueTask.FromResult<string?>(null);
    }

    private static string SayTime(DateTimeOffset utc, TimeZoneInfo zone, string? place)
    {
        var at = TimeZoneInfo.ConvertTime(utc, zone);
        var clock = at.ToString("h:mm", CultureInfo.InvariantCulture);
        var part = at.Hour switch { < 12 => "in the morning", < 17 => "in the afternoon", < 21 => "in the evening", _ => "at night" };
        return place is null ? $"It's {clock} {part}." : $"It's {clock} {part} in {place}.";
    }

    /// <summary>The zone for a place named in a question, when it can be placed for certain.</summary>
    internal static (TimeZoneInfo Zone, string Name)? ZoneFor(string place)
    {
        var words = place.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return null;

        // A CITY THAT IS A ZONE'S OWN NAME FIRST ("New York" -> America/New_York), then a
        // country that has exactly one zone ("Tokushima, Japan" -> Asia/Tokyo). A country
        // with several zones and no city we know is a question, not an answer.
        foreach (var (zoneId, _) in TimeZoneCountries.All)
        {
            var city = zoneId[(zoneId.LastIndexOf('/') + 1)..].Replace('_', ' ');
            if (city.Length >= 4 && place.Contains(city, StringComparison.OrdinalIgnoreCase) && Find(zoneId) is { } z)
                return (z, city);
        }

        // NAMED BY WHAT IT MATCHED, NOT BY WHAT WAS HEARD. "Turkish human Japan" was a
        // mis-hearing of "Tokushima, Japan": the answer says Japan, not the garble.
        // THE TABLE HOLDS COUNTRY CODES ("JP"), so the name a person says is the
        // region's English name, asked of the platform.
        foreach (var country in TimeZoneCountries.All.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            var name = CountryName(country.Key);
            if (name is null || !place.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (country.Count() == 1 && Find(country.First().Key) is { } only) return (only, name);
            return null;   // several zones and no city we know: a question, not an answer
        }

        return null;
    }

    private static string? CountryName(string code)
    {
        try { return new RegionInfo(code).EnglishName; }
        catch { return null; }   // no region data on this platform
    }

    private static TimeZoneInfo? Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }
}

/// <summary>Live facts the phone cannot see are said to be unseen, never invented.</summary>
public sealed partial class NoLiveDataShortcut : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "no-live-data";

    // Weather, the temperature outside, a forecast, rain, today's news, a share price.
    // "The boiling temperature of water" is general knowledge and must NOT match.
    [GeneratedRegex(@"\b(weather|forecast|(?:the|general)\s+temperature\s+(?:today|outside|now|in\s+\p{L}+)|temperature\s+(?:today|outside|right now)|how\s+(?:hot|cold|warm)\s+is\s+it|is\s+it\s+(?:going\s+to\s+)?rain|will\s+it\s+rain|news\s+(?:today|headlines)|today'?s\s+news|latest\s+news|headlines|stock\s+price|share\s+price)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveData();

    /// <inheritdoc />
    public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct) =>
        ValueTask.FromResult(LiveData().IsMatch(ask.Asked ?? string.Empty) ? AssistantPersona.NoLiveData : null);
}
