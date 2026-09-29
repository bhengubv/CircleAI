// ServiceWakePhrases.cs
//
// What you say to wake the phone is set where the listening happens.
//
// DeviceWakePhrases READ A MODEL TO ANSWER THIS. It opens the KWS bundle's BPE
// tokeniser out of the model store and judges a typed phrase against it - fewer
// than four tokens does not survive a room, a phrase another phrase starts with
// can never fire. That judgement needs the model, the model is in CircleAI, and
// so is the microphone that would act on the answer.
//
// SO THIS SENDS PEOPLE THERE RATHER THAN ANSWERING BADLY. The alternative was a
// screen in this app that accepts a phrase, stores it, and changes nothing about
// what the phone listens for - which is worse than no screen at all.
//
// CLOSING IT PROPERLY IS A WIRE CHANGE: a verb that asks the service for its
// phrases and offers one back, with its own scope, because a wake phrase is
// something the person hears their phone answer to and not a preference this app
// gets to set on its own.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceWakePhrases(LinkedBrain brain) : IWakePhrases
{
    private string Where => brain.ServiceInstalled
        ? "Your wake phrase is set in CircleAI, which is the app that listens for it. "
        + "Open CircleAI to choose or add one."
        : "CircleAI is not installed. It is the app that listens for your wake phrase.";

    /// <inheritdoc />
    /// <remarks>
    /// EMPTY, WHICH THE CONTRACT ALREADY TREATS AS A REAL ANSWER - forty of the
    /// seventy-eight languages have no phrase - so the screen shows its "add one"
    /// path, and <see cref="CheckAsync"/> is where the reason arrives. Listing a
    /// phrase here would claim this app knows what the service is listening for,
    /// and it does not: there is no verb to ask.
    /// </remarks>
    public Task<IReadOnlyList<WakePhraseOption>> ForAsync(string language, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WakePhraseOption>>([]);

    /// <inheritdoc />
    public Task<WakePhraseResult> CheckAsync(string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(new WakePhraseResult(false, WakePhraseQuality.Unusable, Where));

    /// <inheritdoc />
    /// <remarks>
    /// REFUSES, AND SAYS WHERE. Adding would write a phrase into this app's store
    /// that nothing listens for - the exact "built but unreachable" shape this repo
    /// keeps paying for.
    /// </remarks>
    public Task<WakePhraseResult> AddAsync(string language, string phrase, CancellationToken ct = default)
        => Task.FromResult(new WakePhraseResult(false, WakePhraseQuality.Unusable, Where));

    /// <inheritdoc />
    public Task ChooseAsync(string language, string phrase, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveAsync(string language, string phrase, CancellationToken ct = default)
        => Task.CompletedTask;
}
