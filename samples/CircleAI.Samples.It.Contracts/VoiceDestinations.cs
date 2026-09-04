using System.Globalization;
using System.Text;

namespace CircleAI.Samples.It;

/// <summary>Somewhere the app can take you when you ask out loud.</summary>
/// <param name="Route">Where it goes. The same route the bar and the links use.</param>
/// <param name="Title">What it is called, for the line it says on the way.</param>
/// <param name="Words">
/// The words that mean this place. Distinctive ones only - see the note on
/// <see cref="VoiceDestinations"/> about why "type" is not in here.
/// </param>
public sealed record VoiceDestination(string Route, string Title, IReadOnlyList<string> Words);

/// <summary>
/// Turning "I need translation" into a screen, and everything else into nothing.
/// </summary>
/// <remarks>
/// YOU COULD TALK TO THIS APP AND NOT TELL IT WHERE TO GO. The wake word fires,
/// the turn transcribes, and the transcript went straight to the answering model
/// - so "I need translation" was ANSWERED, with a sentence about translation,
/// rather than obeyed. The app could discuss translating and could not be told to
/// translate, which is the whole of why it felt unintuitive.
///
/// <para>
/// TWO RULES KEEP THIS FROM HIJACKING QUESTIONS, because a router that eats
/// "how do you say hello in isiZulu" is worse than no router at all.
/// </para>
/// <para>
/// First, a match needs a DISTINCTIVE word. "Type" and "you" are destinations in
/// the tab bar and are thrown out here: they turn up mid-sentence in things that
/// are plainly questions. A destination whose only word is common gets no voice
/// route, and that is a deliberate gap rather than an oversight.
/// </para>
/// <para>
/// Second, it has to SOUND like an instruction: either short enough to be a
/// command, or carrying an asking phrase - "open", "go to", "take me to", "I
/// need". A long sentence with no asking phrase is a question about the subject,
/// not a request to visit it.
/// </para>
/// <para>
/// AND IT ONLY EVER MOVES YOU WHEN YOU ASKED. MainLayout carries a hard-won note
/// that it must never navigate - it used to jump to Chat when a turn could not
/// run, so granting the microphone dropped people on a screen they had not
/// chosen, and curing silence with a teleport is worse than the silence. That
/// rule is about moving somebody on FAILURE. This moves them on request. The two
/// are opposites, and the rule still holds.
/// </para>
/// </remarks>
public static class VoiceDestinations
{
    /// <summary>Every place a voice can reach, and the words that mean it.</summary>
    /// <remarks>
    /// ONE OWNER. The routes here are the routes the pages declare; when a screen
    /// is renamed this table is the one other place that has to know, and a
    /// destination pointing at a route that no longer exists is a spoken promise
    /// that lands on Not Found.
    /// </remarks>
    /// <remarks>
    /// READ FROM <see cref="AppRoutes"/>, NOT DECLARED HERE. This used to be its
    /// own table of ten routes, and the app's menus were two others - fourteen
    /// declared by pages, four in the Services catalogue. All three disagreed,
    /// and the casualty was Translate: voice could reach it and no menu offered
    /// it, so the headline feature was invisible to anybody who did not speak.
    /// <para>
    /// A voice route that no menu shows is a secret, and an app with secret
    /// features is one people conclude is broken. So there is one table now, and
    /// this is a view over it: anything sayable is also somewhere findable,
    /// because both come from the same declaration.
    /// </para>
    /// <para>
    /// STILL NO ENTRY FOR "You". It cannot be matched without eating half of
    /// everything anybody says, so it carries no words and is reached by tapping.
    /// That is a deliberate gap and AppRoutes says so where the screen is
    /// declared, rather than by being quietly absent from a second list.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<VoiceDestination> All { get; } =
        AppRoutes.Spoken
            .Select(r => new VoiceDestination(r.Route, r.Title, r.Spoken))
            .ToList();

    /// <summary>Phrases that make an utterance a request rather than a subject.</summary>
    private static readonly string[] Asking =
    [
        "open", "go to", "take me to", "i need", "i want", "switch to", "show me",
        "let's", "lets", "start", "bring up",
    ];

    /// <summary>How many words still count as a command rather than a question.</summary>
    /// <remarks>
    /// Six, measured against the destinations above rather than picked: the
    /// longest bare instruction here is "what can you do", and a sentence twice
    /// that length with no asking phrase in it is somebody talking, not steering.
    /// </remarks>
    public const int CommandWords = 6;

    /// <summary>Where this sentence is asking to go, or null if it is not.</summary>
    /// <remarks>
    /// NULL IS THE COMMON ANSWER AND THAT IS CORRECT. Anything unmatched falls
    /// through to a normal turn, so the cost of a miss is the answer somebody
    /// would have got anyway, while the cost of a false match is being thrown off
    /// the screen they were using.
    /// </remarks>
    public static VoiceDestination? Match(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard)) return null;

        var text = Normalise(heard);
        if (text.Length == 0) return null;

        // Long, and not phrased as a request: a question that happens to mention
        // a screen. "How do you say hello in isiZulu" must not open Languages.
        if (!SoundsLikeAnInstruction(text)) return null;

        // Longest word first, so "language list" wins over "languages" and the
        // reported destination is the more specific one.
        return All
            .SelectMany(d => d.Words.Select(w => (Destination: d, Word: w)))
            .Where(x => HasWord(text, x.Word))
            .OrderByDescending(x => x.Word.Length)
            .Select(x => x.Destination)
            .FirstOrDefault();
    }

    /// <summary>Whether this sounds like a request rather than a subject.</summary>
    /// <remarks>
    /// Short enough to be a command, or carrying an asking phrase. A long
    /// sentence with neither is somebody talking ABOUT something, and acting on
    /// it throws them off what they were doing for the crime of mentioning it.
    /// </remarks>
    public static bool SoundsLikeAnInstruction(string normalised)
    {
        var words = normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var asking = Asking.Any(a => normalised.Contains(a, StringComparison.Ordinal));
        return words <= CommandWords || asking;
    }

    /// <summary>Whole words only, so "chat" does not match "chatter".</summary>
    /// <remarks>
    /// Public because the capability registry matches the same way. The RULES of
    /// listening belong in one place even though the things being listened for
    /// are spread across the features that own them.
    /// </remarks>
    public static bool HasWord(string text, string word)
    {
        var at = text.IndexOf(word, StringComparison.Ordinal);
        while (at >= 0)
        {
            var beforeOk = at == 0 || text[at - 1] == ' ';
            var end = at + word.Length;
            var afterOk = end == text.Length || text[end] == ' ';
            if (beforeOk && afterOk) return true;
            at = text.IndexOf(word, at + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>Lower case, no punctuation, single spaces.</summary>
    /// <remarks>
    /// A transcriber punctuates: "Translation." and "translation" are the same
    /// request, and a router that only knew one of them would work in testing and
    /// fail on the phone, where the words arrive through Whisper.
    /// </remarks>
    public static string Normalise(string heard)
    {
        var sb = new StringBuilder(heard.Length);
        var space = true;

        foreach (var raw in heard.Trim().ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(raw))
            {
                sb.Append(raw);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }

        return sb.ToString().Trim();
    }
}
