// PlayMediaCapability.cs
//
// "Play Coldplay" — the first thing the circle can be asked that acts on the
// phone rather than on this app.
//
// WHY Claims() AND ALMOST NO PHRASES. ICapability's own warning is that a phrase
// common enough to turn up mid-question will hijack it, and "play" is exactly
// that word: "what does play mean", "how do you play chess", "the play was
// good". A word list cannot tell those from an instruction. The SHAPE can — a
// command to play something starts with the asking, and a question does not — so
// this recognises itself by shape and offers only the phrases that are already
// unambiguous.
//
// AND IT ASKS RATHER THAN GUESSES. "Play music" names no music. Picking
// something would be the assistant deciding what somebody wants to hear, which
// is the confident-and-wrong behaviour this codebase keeps having to remove; the
// honest answer is a question, and it costs one exchange.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>Plays what somebody asked for, through whatever this phone has.</summary>
public sealed class PlayMediaCapability : ICapability
{
    private readonly IPlaysMedia _player;

    public PlayMediaCapability(IPlaysMedia player) => _player = player;

    /// <inheritdoc />
    public string Id => "play:media";

    /// <inheritdoc />
    public string Title => "Play music";

    /// <summary>
    /// Only the openings that cannot be anything but an instruction.
    /// </summary>
    /// <remarks>
    /// "play" alone is deliberately absent — see the header. These three are
    /// safe because nobody asks a question that begins with them.
    /// </remarks>
    public IReadOnlyList<string> Phrases { get; } = ["put on", "play me", "play some"];

    /// <summary>
    /// Opening a player is undone by pausing it.
    /// </summary>
    /// <remarks>
    /// Not Costly: this spends nothing and sends nothing. What the player does
    /// afterwards — streaming, and the data that costs — is the player's own
    /// business and its own app's disclosure, not something this can honestly
    /// claim to control either way.
    /// </remarks>
    public Cost Cost => Cost.Free;

    /// <summary>The openings a sentence must start with to be this.</summary>
    private static readonly string[] Openings =
        ["play me", "play some", "play the", "play", "put on", "listen to"];

    /// <inheritdoc />
    /// <remarks>
    /// STARTS WITH, NOT CONTAINS. "Play Coldplay" is an instruction; "what does
    /// play mean" contains the same word and is a question. Position is the whole
    /// difference, and it is why this is a shape test rather than a word list.
    /// </remarks>
    public bool Claims(string normalised)
        => Subject(normalised) is not null;

    /// <summary>
    /// What was asked for, or null when this sentence is not a play instruction.
    /// </summary>
    /// <remarks>
    /// Returns an EMPTY string for "play music" — the sentence IS an instruction
    /// and names nothing, which is a different answer from "this is not an
    /// instruction" and gets a different reply. Null means not ours.
    /// </remarks>
    public static string? Subject(string normalised)
    {
        if (string.IsNullOrWhiteSpace(normalised)) return null;

        var text = normalised.Trim();

        // Longest first, so "play some jazz" is not read as "play" + "some jazz"
        // with "some" left in the query.
        foreach (var opening in Openings.OrderByDescending(o => o.Length))
        {
            if (!text.StartsWith(opening + " ", StringComparison.OrdinalIgnoreCase)
                && !text.Equals(opening, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = text.Length == opening.Length
                ? string.Empty
                : text[(opening.Length + 1)..].Trim();

            // "play music" / "play some music" name nothing. Treated as an
            // instruction with no subject rather than as a request for a track
            // called "music".
            foreach (var empty in new[] { "music", "something", "a song", "songs", "a tune" })
                if (rest.Equals(empty, StringComparison.OrdinalIgnoreCase)) return string.Empty;

            return rest;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<(bool Ready, string Why)> ReadyAsync(CancellationToken ct = default)
    {
        // ASKED OF THE DEVICE, NOT ASSUMED. A head with no player must say so
        // here rather than accept the command and fail afterwards - offering
        // something that cannot run is the broken promise ReadyAsync exists for.
        var probe = await _player.PlayAsync(string.Empty, ct).ConfigureAwait(false);

        return probe switch
        {
            PlayResult.NotHere => (false, "This can only play music on a phone."),
            PlayResult.NoPlayer => (false, "There is no music app on this phone to play it."),
            _ => (true, string.Empty),
        };
    }

    /// <inheritdoc />
    public async Task<Did> DoAsync(Ask ask, CancellationToken ct = default)
    {
        var what = Subject(VoiceDestinations.Normalise(ask.Heard));

        if (what is null)
            return new Did(false, "I did not catch what to play.");

        // NAMED NOTHING, SO ASK. Choosing something here would be the assistant
        // deciding what somebody wants to hear.
        if (what.Length == 0)
            return new Did(false, "Play what?");

        // SAID BEFORE IT HAPPENS, NOT AFTER. One line below, a music app takes
        // the screen and probably the audio focus, and this app is behind it. An
        // announcement made afterwards is explaining something that has already
        // happened to somebody who has been wondering for two seconds why their
        // phone opened Spotify.
        await ask.Announcer.SayingAsync($"Playing {what}", ct).ConfigureAwait(false);

        var result = await _player.PlayAsync(what, ct).ConfigureAwait(false);

        if (result != PlayResult.Playing) ask.Announcer.Done();

        return result switch
        {
            // Announced: true - the sentence has already been spoken, and saying
            // it again over the top of the player would be a stutter.
            PlayResult.Playing => new Did(true, $"Playing {what}", Announced: true),
            PlayResult.NoPlayer => new Did(false, "There is no music app on this phone to play it."),
            _ => new Did(false, "This can only play music on a phone."),
        };
    }
}
