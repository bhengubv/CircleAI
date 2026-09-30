// ServiceSetup.cs
//
// Setting up means installing CircleAI, not downloading models here.
//
// DeviceSetup planned and ran model downloads into THIS app's storage. A thin app
// downloads nothing: the service holds the models, so "is it ready" collapses to "is
// CircleAI installed and linked", and the work a person has to do is one install
// rather than a queue of gigabytes.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceSetup(LinkedBrain brain) : ISetup
{
    /// <inheritdoc />
    /// <remarks>Nothing downloads here, so nothing is ever running.</remarks>
    /// <inheritdoc />
    /// <remarks>True while this screen is following a run; the run itself is the service's.</remarks>
    public bool IsRunning => _running;

    private bool _running;

    /// <summary>The service's setup, over the link. The mapping onto the wire is in the product.</summary>
    private readonly LinkedSetup _link = new(brain);

    /// <inheritdoc />
    /// <remarks>
    /// CIRCLEAI'S OWN ANSWER. Whether this phone can hold a conversation is a fact
    /// about the models, and the models are over there.
    /// </remarks>
    public async Task<Readiness> ReadinessAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled)
            return new Readiness(ReadyStage.NeedsSetup, "CircleAI is not installed",
                                 "It holds the models and does the thinking. Install it to begin.",
                                 false);

        return await _link.ReadinessAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE SERVICE'S PLAN, NOT THIS APP'S. It used to return empty with a comment
    /// saying there was nothing to download, and that was true of this app and
    /// useless to the person: CircleAI is the thing with models to fetch, and it has
    /// no screen to offer them on. The rows come back over the link.
    /// </remarks>
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => _link.PlanAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// WHAT CIRCLEAI HOLDS, asked over the link. This used to answer with one row
    /// saying whether CircleAI was installed, which is a true sentence and not a
    /// census: the question a person is asking on that screen is what their phone can
    /// actually DO - can it see, can it hear, can it speak their language - and every
    /// one of those answers is on the other side of the link.
    /// <para>
    /// NOT INSTALLED IS STILL THE FIRST ANSWER. Nothing else is worth saying until
    /// the service is there at all, and the link cannot tell you so.
    /// </para>
    /// </remarks>
    public Task<Census> CensusAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled)
        {
            var missing = new CensusRow("CircleAI", false, 0, "Not installed.");
            return Task.FromResult(new Census(
                new[] { missing }, 0, 1, "CircleAI is not installed."));
        }

        return _link.CensusAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE APP ASKS, THE SERVICE FETCHES. This used to be a no-op whose comment said
    /// "the service downloads its own models" - which was never true of anything: the
    /// service has no screen on which somebody could ask it to, so nothing was ever
    /// downloaded by anybody. The tap happens here, the gigabytes land there.
    /// <para>
    /// The run outlives this call and this screen. Closing the app mid-download and
    /// coming back rejoins the same run rather than starting a second copy of it.
    /// </para>
    /// </remarks>
    public async Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
    {
        _running = true;
        try { await _link.RunAsync(progress, ct).ConfigureAwait(false); }
        finally { _running = false; }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TourStep>>(new[]
        {
            new TourStep("Install CircleAI",
                "It holds the models and does the thinking, so this app stays small.",
                "Open CircleAI", null),
        });

    /// <inheritdoc />
    /// <remarks>
    /// FALSE: this app does not hold the microphone for listening, so asking for it
    /// would take a permission nothing uses. Recording for a single spoken question
    /// is asked for at the point it happens.
    /// </remarks>
    public Task<bool> AllowMicrophoneAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>Background work is the service's; this app has none to exempt.</remarks>
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>
    /// ASKED OVER THE LINK, NOT GUESSED. Capabilities is the cheapest verb that goes
    /// through the same authorisation - its scope is Chat and the manifest is not
    /// private - so a granted link answers it and an ungranted one refuses.
    /// </remarks>
    public async Task<bool> LinkApprovedAsync(CancellationToken ct = default)
    {
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return state.Ready;
    }

    /// <inheritdoc />
    /// <remarks>
    /// STARTED FOR RESULT, AND THAT IS LOAD-BEARING. LinkConsentActivity reads
    /// CallingPackage to know who is asking, and the OS only fills that in for an
    /// activity started for result - StartActivity would show the same sheet with no
    /// idea who it was about, and it would grant nothing.
    /// <para>
    /// It needs the ACTIVITY, which is why this cannot live in the shared UI or in the
    /// product library: neither has one. The whole reason ISetup carries this at all
    /// is that the head is the only thing that can do it.
    /// </para>
    /// <para>
    /// THE RESULT IS NOT READ FROM onActivityResult. The consent screen writes the
    /// grant on the SERVICE side, so the honest way to learn the answer is to ask the
    /// service again rather than trust a result code. Polling, because the sheet is a
    /// separate app: this returns when the grant appears, when the person backs out,
    /// or after a minute - whichever comes first.
    /// </para>
    /// </remarks>
    public async Task<bool> ApproveLinkAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled) return false;
        if (await LinkApprovedAsync(ct).ConfigureAwait(false)) return true;

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null) return false;

        // EVERYTHING THIS APP ACTUALLY USES, asked for once. Chat to think, Memory to
        // remember, Skills to look things up, Voice to hear and speak. Asking for them
        // one at a time would mean four sheets for one decision.
        var scope = CircleAI.Linking.LinkScope.Chat
                  | CircleAI.Linking.LinkScope.Memory
                  | CircleAI.Linking.LinkScope.Skills
                  | CircleAI.Linking.LinkScope.Voice;

        try { activity.StartActivityForResult(brain.ConsentIntent(scope), ConsentRequest); }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Link", "could not open the approval screen: " + ex.Message);
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await LinkApprovedAsync(ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    /// <summary>Request code for the consent sheet. Nothing reads the result; see above.</summary>
    private const int ConsentRequest = 0x11c;
}
