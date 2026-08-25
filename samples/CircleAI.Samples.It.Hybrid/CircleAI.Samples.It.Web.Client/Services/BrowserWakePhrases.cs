// BrowserWakePhrases.cs
//
// A browser can show which phrases exist. It cannot judge a new one.
//
// THE JUDGING IS THE MODEL'S, and the model is on the phone. Evaluating a typed
// phrase means running it through the wake listener's own tokenizer to count
// tokens and check nothing shadows it - measurements, not opinions - so a
// browser that accepted phrases would be guessing on behalf of a device it
// cannot see. It says so instead.
//
// What it can do honestly is list the phrases that ship with the app, because
// that table is plain data, and let somebody read them before they install.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserWakePhrases : IWakePhrases
{
    /// <inheritdoc />
    /// <remarks>
    /// The built-in phrases only, and never a chosen one: choosing is a fact
    /// about a particular phone, and this is not one.
    /// </remarks>
    public Task<IReadOnlyList<WakePhraseOption>> ForAsync(
        string language, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WakePhraseOption>>(
            BuiltInWakePhrases.For(language)
                .Select(t => new WakePhraseOption(
                    t, Chosen: false, BuiltIn: true, WakePhraseQuality.Good, ""))
                .ToList());

    /// <inheritdoc />
    public Task<WakePhraseResult> CheckAsync(
        string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(NotHere);

    /// <inheritdoc />
    public Task<WakePhraseResult> AddAsync(
        string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(NotHere);

    /// <inheritdoc />
    public Task ChooseAsync(string language, string phrase, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveAsync(string language, string phrase, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Said once, so both doors give the same answer.</summary>
    private static WakePhraseResult NotHere => new(
        Added: false,
        WakePhraseQuality.Unusable,
        "Wake phrases are checked by the listener on the phone. Install the app to add one.");
}
