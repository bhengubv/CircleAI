// LinkedWakePhrases.cs
//
// What the phone answers to, set from an app somebody can actually open.
//
// THE ROW USED TO SAY "OPEN CIRCLEAI TO CHOOSE OR ADD ONE". That sentence was
// honest about where the setting lives and useless as advice: CircleAI is a service,
// it has no launcher icon, and there is nothing for a person to open. A setting that
// can only be changed somewhere unreachable is not a setting.
//
// THE JUDGEMENT TRAVELS, NOT THE MODEL, and that is why this is a proxy rather than
// a reimplementation. Deciding whether a typed phrase will survive a room is not
// taste - fewer than four tokens is measurably worse through air, and a phrase
// another phrase starts with can never fire at all. Both of those are read off the
// keyword spotter's own tokeniser, which is hundreds of megabytes and belongs with
// the model. So the client sends the words and gets the verdict back.
//
// A REFUSAL IS AN ANSWER HERE TOO. Where the link cannot reach the service, this
// returns nothing rather than a guess, and Unusable with the reason rather than a
// cheerful "added" for a phrase nothing will ever listen for.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <inheritdoc />
/// <param name="brain">The linked brain, whose connection this rides.</param>
public sealed class LinkedWakePhrases(LinkedBrain brain) : IWakePhrases
{
    /// <inheritdoc />
    /// <remarks>
    /// EMPTY WHEN THE LINK IS DOWN, which the contract already treats as a real
    /// answer - forty of seventy-eight languages have no phrase - so the screen shows
    /// its "add one" path rather than a list this app invented.
    /// </remarks>
    public async Task<IReadOnlyList<WakePhraseOption>> ForAsync(
        string language, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return [];

        var reply = await client.WakePhrasesForAsync(language, ct).ConfigureAwait(false);
        if (!reply.Ok) return [];

        return [.. reply.Rows.Select(r => new WakePhraseOption(
            LinkSetupRows.Text(r, 0),
            LinkSetupRows.Flag(r, 1),
            LinkSetupRows.Flag(r, 2),
            Enum.TryParse<WakePhraseQuality>(LinkSetupRows.Text(r, 3), out var q) ? q : WakePhraseQuality.Caution,
            LinkSetupRows.Text(r, 4)))];
    }

    /// <inheritdoc />
    public Task<WakePhraseResult> CheckAsync(string language, string phrase, CancellationToken ct = default)
        => VerdictAsync(c => c.WakePhraseCheckAsync(language, phrase, ct), ct);

    /// <inheritdoc />
    public Task<WakePhraseResult> AddAsync(string language, string phrase, CancellationToken ct = default)
        => VerdictAsync(c => c.WakePhraseAddAsync(language, phrase, ct), ct);

    /// <inheritdoc />
    public async Task ChooseAsync(string language, string phrase, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return;
        await client.WakePhraseChooseAsync(language, phrase, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string language, string phrase, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return;
        await client.WakePhraseRemoveAsync(language, phrase, ct).ConfigureAwait(false);
    }

    /// <summary>Check and Add differ only in which verb they send; the reply is one shape.</summary>
    private async Task<WakePhraseResult> VerdictAsync(
        Func<CircleAiLinkClient, Task<LinkRowsReply>> ask, CancellationToken ct)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null)
            return new WakePhraseResult(false, WakePhraseQuality.Unusable, NotLinked);

        var reply = await ask(client).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count == 0)
            return new WakePhraseResult(false, WakePhraseQuality.Unusable, reply.Error ?? NotLinked);

        var row = reply.Rows[0];
        return new WakePhraseResult(
            LinkSetupRows.Flag(row, 0),
            Enum.TryParse<WakePhraseQuality>(LinkSetupRows.Text(row, 1), out var q) ? q : WakePhraseQuality.Caution,
            LinkSetupRows.Text(row, 2));
    }

    private const string NotLinked =
        "CircleAI is not linked to this app yet. Approve the link in Settings, then you can set the wake phrase here.";
}
