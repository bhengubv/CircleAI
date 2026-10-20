// RecordedQuestions.cs
//
// THE QUESTIONS THIS ASSISTANT GOT WRONG, WRITTEN DOWN SO IT CANNOT GET THEM WRONG
// QUIETLY AGAIN.
//
// Every one of these was asked on a real phone while testing with the owner, and every
// one was answered wrongly: an invented temperature, a guessed date, a sound answered
// as a question, a template marker read out, a refusal where a joke was asked for. They
// were prose in comments and docs; nothing replayed them. The quality run puts each to
// the service through the same door every app uses and checks the answer against what
// a right answer must and must not contain.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Requests;

namespace CircleAI.Assistant.Quality;

/// <summary>One recorded question and what a right answer looks like.</summary>
/// <param name="Id">A short stable id.</param>
/// <param name="Asked">What the person said (as transcribed).</param>
/// <param name="Recorded">What it answered when it got it wrong, where that was written down.</param>
/// <param name="MustContainAny">The answer must say at least one of these, as a word or a word's plural or ending ("seeds", "planting"), case-insensitive.</param>
/// <param name="MustNotContain">The answer must contain none of these.</param>
/// <param name="MaxSentences">The most sentences a right answer runs to (0 = no limit).</param>
/// <param name="NoInventedMeasurement">A number with degrees/celsius/fahrenheit in it is wrong (there is no weather source).</param>
/// <param name="Clock">The answer must carry today's day of the month or weekday (date questions).</param>
/// <param name="NotRefusal">A refusal is wrong.</param>
public sealed record RecordedQuestion(
    string Id,
    string Asked,
    string? Recorded = null,
    string[]? MustContainAny = null,
    string[]? MustNotContain = null,
    int MaxSentences = 0,
    bool NoInventedMeasurement = false,
    bool Clock = false,
    bool NotRefusal = false);

/// <summary>The recorded failing questions.</summary>
public static class RecordedQuestions
{
    private static readonly string[] Qwen = { "Qwen", "Alibaba", "通义" };

    /// <summary>Every recorded question, in a stable order.</summary>
    public static IReadOnlyList<RecordedQuestion> Load() => new RecordedQuestion[]
    {
        // Weather: there is no weather source on the phone. Any number of degrees is invented.
        new("weather-tokushima", "What is the temperature in Tokushima Japan today?", "twenty five degrees",
            MustContainAny: new[] { "can't", "cannot", "can not", "don't have", "unable", "not able" },
            NoInventedMeasurement: true, MaxSentences: 3),
        new("weather-general", "Happy, how are you doing today? What is the general temperature today?",
            NoInventedMeasurement: true, MaxSentences: 4),
        new("weather-like", "what's the weather like", NoInventedMeasurement: true, MaxSentences: 3),
        new("weather-today", "What is the weather like today?", "NullReferenceException",
            MustNotContain: new[] { "Exception", "Object reference" }, NoInventedMeasurement: true, MaxSentences: 3),

        // Date and time come from the phone's clock, never the model.
        new("date-today", "Hey, B. What date is it today?", Clock: true, MaxSentences: 2),
        new("time-tokushima", "What time is it in Tokushima Japan?",
            MustContainAny: new[] { ":", "o'clock" }, MaxSentences: 2),

        // Sounds and garble: ask what they meant; never invent a topic.
        // "No." passed this on 2026-10-17: nothing forbidden was in it. A garble is answered
        // by asking what they meant, so that is now what is checked.
        // And "I am sorry to hear about your loss; please try again later" passed on the
        // word "again" (2026-10-20). Only asking what they meant counts.
        new("garbled", "Placeculeeai.", "IT! > I cannot place your cryptocurrency. It is illegal...",
            MustContainAny: new[] { "catch", "mean", "meant", "repeat", "say that again", "say it again", "clarify" },
            MustNotContain: new[] { "cryptocurrency", "illegal", "sorry to hear", "loss" }, MaxSentences: 3),
        new("sound-bell", "(Bell)", "I am ready for further questions when prompted.",
            MustContainAny: new[] { "catch", "again", "repeat", "say that" }, MaxSentences: 2),

        // Capability tiles: a short, on-topic reply - never a refusal.
        new("tile-health", "I have a health question", NotRefusal: true, MaxSentences: 4),
        // "Use it as base grain for breads, cassava..." and "...while ensuring safety" passed
        // on 2026-10-20: a meal plan naming none of the staples it was handed is not one.
        new("tile-meals", "Help me plan meals",
            MustContainAny: new[] { "pap", "maize", "samp", "rice", "potato", "bread", "bean", "lentil", "egg",
                                    "cabbage", "spinach", "morogo", "carrot", "butternut" },
            MustNotContain: new[] { "cassava", "ensuring safety" }, NotRefusal: true, MaxSentences: 6),
        new("tile-legal", "I have a legal question", NotRefusal: true, MaxSentences: 4),
        new("tile-government", "Help me deal with a government office", NotRefusal: true, MaxSentences: 6),
        new("tile-work-rights", "I have a question about my job or my rights at work", NotRefusal: true, MaxSentences: 4),
        new("tile-threat-model", "threat modeling for a mobile app", NotRefusal: true, MaxSentences: 8),
        // "September or October" and "late winter or early spring" passed on 2026-10-20. The
        // notes say October to December; a month outside that is a wrong answer. And an
        // answer must say WHEN: "Do not worry about planting maize now; it requires time and
        // care during the dry season" passed too, on "plant".
        new("maize", "How do I plant maize?", MustContainAny: new[] { "October", "November", "December", "rains" },
            MustNotContain: new[] { "winter", "autumn", "early fall", "late fall", "January", "February", "March",
                                    "April", "June", "July", "August", "September" },
            NotRefusal: true, MaxSentences: 8),

        // Earlier recorded failures.
        // "Cape Town" alone passed every run until 2026-10-20. South Africa has three, and
        // the one people mean by "the capital" is Pretoria.
        new("capital-za", "What is the capital of South Africa?", "160 tokens of Thinking Process",
            MustContainAny: new[] { "Pretoria" }, MaxSentences: 3),
        new("capital-fr", "What is the capital of France", "What did you mean?",
            MustContainAny: new[] { "Paris" }, MaxSentences: 2),
        new("joke", "Tell me a joke", "I cannot create jokes as requested by my instructions.",
            NotRefusal: true, MustNotContain: new[] { "I'm a machine", "my instructions" }, MaxSentences: 5),
        new("can-do", "What can you do?", "I'm Qwen3.5, the latest version of Alibaba's...",
            MustNotContain: Qwen, MaxSentences: 6),
        new("colours", "Name three colours", "Here is my name as an AI and the current weather",
            MustContainAny: new[] { "red", "blue", "green", "yellow", "black", "white", "orange", "purple" },
            MustNotContain: new[] { "weather" }, MaxSentences: 3),
        // "I can't remember specific appointments, but... it was a Friday" passed on
        // 2026-10-17. Remembering is now checked, and then asked back.
        new("remember-clinic", "Remember that my clinic appointment moved to Friday",
            "What was your original appointment time? </think> ...",
            MustContainAny: new[] { "remember", "noted", "saved" },
            MustNotContain: new[] { "can't remember", "cannot remember", "don't remember" }, MaxSentences: 3),
        new("recall-clinic", "When is my clinic appointment?", MustContainAny: new[] { "Friday" }, MaxSentences: 2),
        new("times-12-8", "What is 12 times 8", "6", MustContainAny: new[] { "96" }, MaxSentences: 2),
        new("times-347-89", "What is 347 times 89", "30621", MustContainAny: new[] { "30883", "30,883", "30 883" }, MaxSentences: 2),
        new("greeting", "Hey, B.", MaxSentences: 2),

        // The soak corpus.
        new("times-17-4", "What is 17 times 4?", MustContainAny: new[] { "68" }, MaxSentences: 2),
        new("capital-ke", "What is the capital of Kenya?", MustContainAny: new[] { "Nairobi" }, MaxSentences: 2),
        new("leap-year", "How many days are in a leap year?", MustContainAny: new[] { "366" },
            MustNotContain: new[] { "365 or 366" }, MaxSentences: 2),
    };
}

/// <summary>Checks an answer against a recorded question's expectations.</summary>
public static partial class AnswerExpectation
{
    // WHAT MUST NEVER BE SAID, WHATEVER THE QUESTION: template markers, a thinking
    // trace, markdown read aloud, the old sample's prompt prefix, a memory heading.
    private static readonly string[] Always =
    {
        "<think>", "</think>", "<|im_", "Thinking Process", "**", "IT! >", "Things you already know about them",
        "Background for you", "Facts for you", "What may help", "Available Skills",
        // The notes quoted back as the person's own (2026-10-17, the meal and health tiles).
        "the background", "your background", "facts provided", "summary of the facts",
        "provided facts", "by the facts", "by your facts", "the notes", "your notes", "Notes -",
        // ...and in the shapes a review worked out from those on 2026-10-21.
        "you provided", "you gave me", "information provided", "notes provided", "use them if they help",
        "your guidelines", "provided guidelines", "guidelines provided", "based on the guidelines", "provided text",
    };

    private static readonly string[] Refusals =
    {
        "I cannot help", "I can't help", "I'm sorry, but I can", "I am unable to", "I'm not able to help",
        "I cannot provide", "I can't provide",
    };

    // "As an AI..." is a refusal; "use mulch as an aid" is not.
    [GeneratedRegex(@"\bas\s+an\s+AI\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsAnAI();

    [GeneratedRegex(@"\d+\s*(?:°|degrees?|celsius|fahrenheit|ºC|°C|°F)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Measurement();

    [GeneratedRegex(@"(?:twenty|thirty|fifteen|ten|eleven|twelve|thirteen|fourteen|sixteen|seventeen|eighteen|nineteen|forty)[-\s]*(?:\w+\s+)?degrees", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpokenMeasurement();

    /// <summary>Whether <paramref name="answer"/> is right for <paramref name="q"/>, and why not.</summary>
    public static (bool Passed, string Reason) Check(RecordedQuestion q, string? answer, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(q);
        var a = answer?.Trim() ?? string.Empty;
        if (a.Length == 0) return (false, "no answer");

        foreach (var bad in Always)
            if (a.Contains(bad, StringComparison.OrdinalIgnoreCase)) return (false, "contains '" + bad + "'");
        foreach (var bad in q.MustNotContain ?? Array.Empty<string>())
            if (a.Contains(bad, StringComparison.OrdinalIgnoreCase)) return (false, "contains '" + bad + "'");

        if (q.MustContainAny is { Length: > 0 } any && !any.Any(x => Says(a, x)))
            return (false, "missing any of: " + string.Join(", ", any));

        if (q.NoInventedMeasurement && (Measurement().IsMatch(a) || SpokenMeasurement().IsMatch(a)))
            return (false, "invented a measurement");

        if (q.NotRefusal && (Refusals.Any(r => a.Contains(r, StringComparison.OrdinalIgnoreCase)) || AsAnAI().IsMatch(a)))
            return (false, "refused");

        if (q.Clock)
        {
            var at = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, zone ?? TimeZoneInfo.Local);
            var day = at.Day.ToString(CultureInfo.InvariantCulture);
            var weekday = at.ToString("dddd", CultureInfo.GetCultureInfo("en-GB"));
            if (!a.Contains(weekday, StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(a, @"\b" + day + @"(st|nd|rd|th)?\b"))
                return (false, "not today's date");
        }

        if (q.MaxSentences > 0)
        {
            var sentences = Sentences(a);
            if (sentences > q.MaxSentences) return (false, $"{sentences} sentences, at most {q.MaxSentences}");
        }

        return (true, "ok");
    }

    /// <summary>Whether <paramref name="answer"/> says <paramref name="word"/>: as a word, or with an ending.</summary>
    /// <remarks>
    /// A WORD, NOT A STRING. As a string "samp" is in "sample", "rice" in "prices", "egg" in
    /// "veggies" and "pap" in "paper", so a meal plan naming no staple passed as one that did
    /// (review, 2026-10-21). An edge that is not a letter or digit (":") has no word boundary
    /// to keep.
    /// </remarks>
    public static bool Says(string answer, string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        var pattern = (char.IsLetterOrDigit(word[0]) ? @"\b" : string.Empty) + Regex.Escape(word)
                    + (char.IsLetterOrDigit(word[^1]) ? @"(?:s|es|ed|ing)?\b" : string.Empty);
        return Regex.IsMatch(answer ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>How many sentences <paramref name="answer"/> runs to.</summary>
    /// <remarks>
    /// A LIST NUMBER IS NOT A FULL STOP. "Here are three common colors: 1. Red 2. Blue
    /// 3. Green" is one sentence, and failed on 2026-10-17 as four; "3.5" is one number.
    /// But a number ending a sentence is a full stop: "12 times 8 is 96. That is eight
    /// twelves." is two. So a number counts as a list's only where it starts the answer,
    /// follows a colon, or runs on from or into the next one.
    /// </remarks>
    public static int Sentences(string answer)
    {
        var a = Decimal().Replace(answer ?? string.Empty, "$1$2");
        var marks = ListMarker().Matches(a);
        var numbers = marks.Select(m => int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)).ToArray();
        var text = new System.Text.StringBuilder(a);
        for (var i = marks.Count - 1; i >= 0; i--)
        {
            var before = a[..marks[i].Index].TrimEnd();
            var listed = before.Length == 0 || before.EndsWith(':')
                      || (i > 0 && numbers[i - 1] == numbers[i] - 1)
                      || (i + 1 < numbers.Length && numbers[i + 1] == numbers[i] + 1);
            if (listed) text.Remove(marks[i].Index, marks[i].Length).Insert(marks[i].Index, " ");
        }
        return Regex.Matches(text.ToString(), @"[^.!?]+[.!?]+|[^.!?]+$").Count(m => m.Value.Any(char.IsLetterOrDigit));
    }

    // "1. ", "2) " at the start or after a space, followed by a space.
    [GeneratedRegex(@"(?:^|(?<=\s))(?<n>\d{1,2})[.)](?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();

    // The point in "3.5".
    [GeneratedRegex(@"(\d)\.(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Decimal();
}

/// <summary>One verdict in a quality run.</summary>
public sealed record QualityItem(string Id, bool Passed, string Reason, string Answer, TurnTiming? Timing);

/// <summary>A whole quality run.</summary>
public sealed record QualityReport(IReadOnlyList<QualityItem> Items, int Passed, int Total, string? ModelId);

/// <summary>Puts the recorded questions through the same door every app uses.</summary>
public sealed class QualityRunner(IAskFront front, string callerKey)
{
    /// <summary>Ask each question in turn and check each answer.</summary>
    public async Task<QualityReport> RunAsync(IReadOnlyList<RecordedQuestion>? items, TimeSpan perItem, CancellationToken ct)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var results = new List<QualityItem>();
        string? model = null;
        foreach (var q in items ?? RecordedQuestions.Load())
        {
            ct.ThrowIfCancellationRequested();
            // SPOKEN: every one of these was said to the phone, so they are answered as
            // they were - short, for the ear.
            var admission = front.Submit(new AskSubmission(callerKey, $"quality-{run}-{q.Id}", string.Empty,
                                                           AskLane.Foreground, q.Asked, q.Asked, Spoken: true));
            if (!admission.Accepted || admission.Record is null)
            {
                results.Add(new QualityItem(q.Id, false, "refused: " + admission.Refusal, string.Empty, null));
                continue;
            }

            var done = await front.WaitAsync(callerKey, admission.Record.Id, perItem, ct).ConfigureAwait(false);
            var answer = done?.State == AskState.Succeeded ? done.Text : done?.Sentence ?? string.Empty;
            var (passed, reason) = done?.State == AskState.Succeeded
                ? AnswerExpectation.Check(q, answer)
                : (false, "not answered: " + (done?.State.ToString() ?? "missing"));
            model ??= done?.Timing?.ModelId ?? done?.ModelId;
            results.Add(new QualityItem(q.Id, passed, reason, answer, done?.Timing));
        }
        return new QualityReport(results, results.Count(r => r.Passed), results.Count, model);
    }
}
