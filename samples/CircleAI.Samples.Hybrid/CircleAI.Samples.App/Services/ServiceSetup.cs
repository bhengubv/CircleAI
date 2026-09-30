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
    public bool IsRunning => false;

    /// <inheritdoc />
    public async Task<Readiness> ReadinessAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled)
            return new Readiness(ReadyStage.NeedsSetup, "Install CircleAI",
                "This app asks CircleAI to think, speak and listen.", CanTalk: false);

        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return state.Ready
            ? new Readiness(ReadyStage.Ready, "Ready", state.Detail, CanTalk: true)
            : new Readiness(ReadyStage.NeedsSetup, "Approve the link", state.Detail, CanTalk: false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// EMPTY, because there is nothing to download. A plan with rows in it would
    /// offer somebody work this app cannot do and does not need done.
    /// </remarks>
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SetupItem>>(Array.Empty<SetupItem>());

    /// <inheritdoc />
    /// <remarks>
    /// ONE ROW, AND IT IS THE TRUE ONE. A census counts what is present on the
    /// device; for this app that is exactly one thing — CircleAI itself.
    /// </remarks>
    public Task<Census> CensusAsync(CancellationToken ct = default)
    {
        var there = brain.ServiceInstalled;
        var row = new CensusRow("CircleAI", there, 0,
            there ? "Installed — it holds the models." : "Not installed.");
        return Task.FromResult(new Census(
            new[] { row }, there ? 1 : 0, 1,
            there ? "CircleAI is installed." : "CircleAI is not installed."));
    }

    /// <inheritdoc />
    /// <remarks>Nothing to run: the service downloads its own models.</remarks>
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        => Task.CompletedTask;

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
