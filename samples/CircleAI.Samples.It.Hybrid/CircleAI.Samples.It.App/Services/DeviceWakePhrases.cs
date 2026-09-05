// DeviceWakePhrases.cs
//
// The wake phrases for a language, judged by the model that has to hear them.
//
// THE JUDGING IS THE POINT. Anyone can store a string; what makes this worth a
// class is that the phrase is put through the wake model's OWN tokenizer before
// it is accepted, so an owner is told at the moment they type that "Bee" is too
// short to survive a room, rather than three weeks later when the phone will not
// answer and they blame the app.

using CircleAI.Voice;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceWakePhrases : IWakePhrases
{
    private readonly SqliteAppStore _store;
    private readonly IResidentAssistant? _resident;

    /// <param name="resident">
    /// The listener that has to be told, or null for a head that has none. See
    /// <see cref="CatchUpAsync"/> — writing the file is not the same as changing
    /// what the microphone hears.
    /// </param>
    public DeviceWakePhrases(SqliteAppStore store, IResidentAssistant? resident = null)
    {
        _store = store;
        _resident = resident;
    }

    /// <summary>Makes the running listener match what was just stored.</summary>
    /// <remarks>
    /// THE THIRD OWNER. Choosing a phrase writes the settings table and writes
    /// through to the keywords file, and both were already right — but the
    /// microphone consults neither. It consults a graph compiled from that file
    /// when the listener was built, and nothing re-reads it.
    /// <para>
    /// Measured on a P30 on 2026-09-06: "Hey Circle AI" chosen, screen agreeing,
    /// file agreeing, log agreeing, and six minutes of saying it produced
    /// <c>closest="Hey B" 2/3 tokens</c>.
    /// </para>
    /// <para>
    /// Here rather than in the screen, so that every caller of ChooseAsync gets
    /// it and no future screen can forget. Cheap when nothing moved: the resident
    /// compares what it was built from against what is on disk and returns
    /// without touching the microphone.
    /// </para>
    /// </remarks>
    private Task CatchUpAsync(CancellationToken ct)
        => _resident?.RefreshAsync(ct) ?? Task.CompletedTask;

    /// <summary>The wake model, which is also what judges a typed phrase.</summary>
    /// <remarks>
    /// Same name and same folder as <see cref="DeviceWakeWord"/> uses. Kept as a
    /// constant in both rather than shared through a third class, because the two
    /// are already coupled by the file format they read and write - a shared
    /// constant would hide that rather than remove it.
    /// </remarks>
    private const string ModelName = "KWS-Zipformer-HeyB";

    /// <summary>Phrases the owner typed, newline separated, per language.</summary>
    private static string AddedKey(string language) => $"wake.phrases.{Root(language)}";

    /// <summary>Which phrase is listened for, per language.</summary>
    private static string ChosenKey(string language) => $"wake.chosen.{Root(language)}";

    /// <inheritdoc />
    public Task<IReadOnlyList<WakePhraseOption>> ForAsync(
        string language, CancellationToken ct = default)
    {
        var built = BuiltInWakePhrases.For(language);
        var added = Added(language);

        // BUILT-IN FIRST, then the owner's, each in the order they arrived. Not
        // sorted by quality: the list is a set of names for the same phone, and
        // reordering it under somebody as they add to it is disorienting.
        var all = built.Select(t => (Text: t, BuiltIn: true))
            .Concat(added.Select(t => (Text: t, BuiltIn: false)))
            .ToList();

        if (all.Count == 0)
            return Task.FromResult<IReadOnlyList<WakePhraseOption>>([]);

        var chosen = _store.Get(ChosenKey(language)) ?? all[0].Text;

        // ONE BOOK FOR THE WHOLE LIST, because the prefix rule is about phrases
        // in relation to each other: "Circle" and "Circle AI" registered together
        // means the longer one can never fire. Judging each phrase alone would
        // miss the one problem that only exists between them.
        var book = OpenBook();
        var judged = new List<WakePhraseOption>(all.Count);

        foreach (var (text, builtIn) in all)
        {
            var (quality, advice) = book is null
                ? (WakePhraseQuality.Good, "")
                : Judge(book, text);

            judged.Add(new WakePhraseOption(
                text,
                Chosen: string.Equals(text, chosen, StringComparison.Ordinal),
                BuiltIn: builtIn,
                quality,
                advice));
        }

        return Task.FromResult<IReadOnlyList<WakePhraseOption>>(judged);
    }

    /// <inheritdoc />
    public Task<WakePhraseResult> CheckAsync(
        string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(Consider(language, phrase, add: false));

    /// <inheritdoc />
    public async Task<WakePhraseResult> AddAsync(
        string language, string phrase, CancellationToken ct = default)
    {
        var result = Consider(language, phrase, add: true);
        await CatchUpAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task ChooseAsync(string language, string phrase, CancellationToken ct = default)
    {
        _store.Set(ChosenKey(language), phrase);

        // WRITTEN THROUGH TO THE FILE THE SPOTTER READS, not just remembered. A
        // chosen phrase that only exists in the settings table is a screen saying
        // one thing while the microphone waits for another - which is the exact
        // failure this whole screen was rebuilt to end.
        WriteKeywordFile(language);

        // AND THROUGH TO THE LISTENER, which is where that failure actually
        // lived. The file was already being written correctly; the graph built
        // from it never got rebuilt, so the write reached disk and stopped.
        await CatchUpAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string language, string phrase, CancellationToken ct = default)
    {
        // BUILT-IN PHRASES DO NOT GO. They are what the phone falls back to, and
        // a language whose only phrase was removed could not be woken at all.
        if (BuiltInWakePhrases.For(language).Contains(phrase, StringComparer.Ordinal))
            return;

        var kept = Added(language)
            .Where(t => !string.Equals(t, phrase, StringComparison.Ordinal))
            .ToList();
        _store.Set(AddedKey(language), kept.Count == 0 ? null : string.Join('\n', kept));

        // The removed one may have been the chosen one; fall back rather than
        // leave the phone listening for a phrase that is no longer offered.
        if (string.Equals(_store.Get(ChosenKey(language)), phrase, StringComparison.Ordinal))
            _store.Set(ChosenKey(language), null);

        WriteKeywordFile(language);
        await CatchUpAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Judge a phrase, and add it unless it cannot work.</summary>
    /// <remarks>
    /// CHECK AND ADD ARE THE SAME REASONING, which is why they are one method: a
    /// screen that warns you as you type and then applies different rules when you
    /// press the button is worse than one that never warned you.
    /// </remarks>
    private WakePhraseResult Consider(string language, string phrase, bool add)
    {
        var text = phrase?.Trim() ?? "";
        if (text.Length == 0)
            return new WakePhraseResult(false, WakePhraseQuality.Unusable, "Type a phrase first.");

        if (Existing(language).Contains(text, StringComparer.OrdinalIgnoreCase))
            return new WakePhraseResult(false, WakePhraseQuality.Unusable,
                "That phrase is already in the list.");

        var book = OpenBook();
        if (book is null)
        {
            // THE MODEL IS WHAT JUDGES, so without it there is no judgement to
            // give. Said plainly rather than accepting the phrase on trust and
            // discovering later that the model cannot represent it at all.
            return new WakePhraseResult(false, WakePhraseQuality.Unusable,
                "Turn Waking on first — the phrase is checked against the listener, "
                + "and it is not installed yet.");
        }

        var (quality, advice) = Judge(book, text);
        if (quality == WakePhraseQuality.Unusable)
            return new WakePhraseResult(false, quality, advice);

        if (!add) return new WakePhraseResult(false, quality, advice);

        var added = Added(language).ToList();
        added.Add(text);
        _store.Set(AddedKey(language), string.Join('\n', added));

        // A NEWLY ADDED PHRASE BECOMES THE ONE IN USE. Somebody who has just typed
        // their own name for the phone did not do it to leave it switched off.
        _store.Set(ChosenKey(language), text);
        WriteKeywordFile(language);

        return new WakePhraseResult(true, quality, advice);
    }

    /// <summary>What the engine thinks, in the owner's words.</summary>
    private static (WakePhraseQuality Quality, string Advice) Judge(WakePhraseBook book, string text)
    {
        var judged = book.Evaluate(text);
        var quality = judged.Verdict switch
        {
            WakePhraseVerdict.Good => WakePhraseQuality.Good,
            WakePhraseVerdict.Caution => WakePhraseQuality.Caution,
            _ => WakePhraseQuality.Unusable,
        };
        return (quality, judged.Advice ?? "");
    }

    /// <summary>Every phrase currently offered for a language.</summary>
    private IEnumerable<string> Existing(string language)
        => BuiltInWakePhrases.For(language).Concat(Added(language));

    /// <summary>The phrases the owner typed.</summary>
    private IReadOnlyList<string> Added(string language)
    {
        var raw = _store.Get(AddedKey(language));
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// The book, loaded against the installed model's own tokenizer.
    /// </summary>
    /// <remarks>
    /// Null when the wake model is not installed, which is a normal state rather
    /// than a failure - nothing has been downloaded on a fresh phone.
    /// </remarks>
    private static WakePhraseBook? OpenBook()
    {
        try
        {
            var dir = Path.Combine(ModelStore.Path, ModelName);
            if (!Directory.Exists(dir)) return null;

            var bpe = Directory
                .EnumerateFiles(dir, "bpe.model", SearchOption.AllDirectories)
                .FirstOrDefault();
            return bpe is null ? null : new WakePhraseBook(new SentencePieceTokenizer(bpe));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Where the spotter reads its keywords for a language.</summary>
    internal static string KeywordFile(string language)
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", $"wake-{Root(language)}.txt");

    /// <summary>
    /// Write the chosen phrase out in the format the spotter reads.
    /// </summary>
    /// <remarks>
    /// ONLY THE CHOSEN ONE. Registering every phrase for the language would put
    /// "Bee san" and "ビーさん" in the same file, and where one phrase starts with
    /// another the longer can never fire - across eighteen recordings of the longer
    /// phrase, every detection reported the shorter. One at a time is what the
    /// measurement says to do.
    /// </remarks>
    private void WriteKeywordFile(string language)
    {
        try
        {
            var book = OpenBook();
            if (book is null) return;

            var offered = Existing(language).ToList();
            if (offered.Count == 0) return;

            var chosen = _store.Get(ChosenKey(language)) ?? offered[0];
            if (!book.TryAdd(chosen, out var phrase))
            {
                VoiceTrace.Write($"kws: '{chosen}' cannot be written for '{language}'");
                return;
            }

            var path = KeywordFile(language);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            book.Save(path);
            // WHAT WAS WRITTEN, NOT WHAT IS HEARD. This used to read "'en'
            // listens for ...", which is a claim about the microphone made by
            // code that has only touched a file. On 2026-09-06 it reported
            // `'en' listens for "Hey Circle AI"` at the same moment the spotter
            // was reporting closest="Hey B", and it was the line that made the
            // fault look like a measurement problem for an hour. The listener
            // says what it listens for; this says what it wrote.
            VoiceTrace.Write($"kws: wrote \"{phrase.Text}\" for '{language}' "
                           + $"({phrase.Tokens.Count} tokens, {phrase.Verdict})");
        }
        catch (Exception ex)
        {
            VoiceTrace.Write($"kws: could not write keywords for '{language}': {ex.Message}");
        }
    }

    /// <summary>The language part of a tag, so "ja-JP" and "ja" share a list.</summary>
    private static string Root(string? tag)
    {
        var code = tag?.Trim();
        if (string.IsNullOrEmpty(code)) return "en";
        var cut = code.IndexOf('-');
        return cut > 0 ? code[..cut] : code;
    }
}
