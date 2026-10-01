// ServiceDeviceFacts.cs
//
// What this phone can do is the service's answer, not this app's.
//
// DeviceFacts read the model catalogue and the device probe in THIS process to work
// out which abilities were on. A thin app has neither: it holds no models and makes
// no fit decisions. So it asks the one process that does, and where the link cannot
// carry the question it says so rather than composing a plausible list.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceDeviceFacts(LinkedBrain brain) : IDeviceFacts
{
    /// <summary>The service's own view of this device, over the link.</summary>
    private readonly LinkedDeviceFacts _link = new(brain);

    /// <summary>The service's setup, for the "Turn on" buttons on the abilities screen.</summary>
    /// <remarks>
    /// The SAME object the setup screen drives, so a download started from either
    /// place is one run rather than two racing for the same file.
    /// </remarks>
    private readonly LinkedSetup _setup = new(brain);

    /// <inheritdoc />
    /// <remarks>
    /// ASKED, NOT INFERRED. This used to derive all four rows from one fact - whether
    /// the link was up - because there was no verb to ask for more, so a device with
    /// the ears but not the eyes read as if everything worked. The service knows
    /// per-ability and now says so.
    /// <para>
    /// The fallback is still the old shape, for the case that is genuinely about this
    /// app rather than the device: no link, nothing to ask.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
    {
        var rows = await _link.AbilitiesAsync(ct).ConfigureAwait(false);
        if (rows.Count > 0) return rows;

        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return new[]
        {
            new AbilityRow("Answering", state.Detail, AbilityState.NotCatalogued),
            new AbilityRow("Talking",   state.Detail, AbilityState.NotCatalogued),
            new AbilityRow("Listening", state.Detail, AbilityState.NotCatalogued),
            new AbilityRow("Seeing",    state.Detail, AbilityState.NotCatalogued),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE FOOTPRINT IS THE SERVICE'S. "What Circle AI uses" is the models, the voices
    /// and the skill database - none of which are in this process, which is why this
    /// screen showed nothing here at all.
    /// </remarks>
    public Task<StorageReport> StorageAsync(CancellationToken ct = default)
        => _link.StorageAsync(ct);

    /// <inheritdoc />
    public async Task<PhoneFacts> PhoneAsync(CancellationToken ct = default)
    {
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return new PhoneFacts(
            new[]
            {
                new PhoneFact("Where it runs",
                    "In CircleAI, on this phone. This app holds no models of its own."),
                new PhoneFact("Right now", state.Detail),
            },
            Array.Empty<string>());
    }

    /// <inheritdoc />
    /// <remarks>
    /// IT TURNS THINGS ON NOW. This returned "Open CircleAI and approve the link to
    /// use this" for every ability, on every device, whatever was tapped and whether
    /// or not the link was already approved - so every "Turn on" button on the
    /// abilities screen was dead, and the sentence it printed sent people to an app
    /// with no launcher icon. It was written before the setup verbs existed, when
    /// fetching a model really was something only the other side could begin.
    /// <para>
    /// ONE RUN, NOT ONE ABILITY, and the wording says so rather than implying
    /// otherwise. The service plans for the DEVICE - what it needs to see, hear,
    /// speak and think - and turning on any one of those starts that plan. A
    /// per-ability fetch would need the service to split its plan, which is a change
    /// over there, not a sentence over here.
    /// </para>
    /// </remarks>
    public async Task<string> TurnOnAsync(string title, IProgress<string>? progress = null,
                                          CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled) return "Install CircleAI to use this.";

        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        if (!state.Ready)
            return "Approve CircleAI in Settings first, then this can be turned on.";

        try
        {
            // The progress the screen already knows how to show: one line per step.
            var relay = progress is null
                ? null
                : new Progress<SetupProgressReport>(p =>
                    progress.Report($"{p.Title} — {p.Fraction:P0}"));

            await _setup.RunAsync(relay, ct).ConfigureAwait(false);

            // "On" IS THE CONTRACT'S WORD FOR SUCCESS, NOT A SENTENCE ABOUT IT.
            // The screen compares this to "On" and treats anything else as the
            // reason it failed - so the friendly "Waking is ready." was printed in
            // the warning slot under the heading, on a successful run, every time.
            return "On";
        }
        catch (Exception ex)
        {
            return Trouble.Say(ex);
        }
    }
}
