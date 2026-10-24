// LinkedWakeWord.cs
//
// The wake word runs in CircleAIService, and this screen follows it.
//
// DeviceWakeWord did it in this process: found the bundle, held the microphone,
// spotted the phrase. That is the always-on half, and it belongs to the app that has
// the foreground service and the notification disclosing the microphone.
//
// BUT "IT IS OVER THERE" IS NOT A SCREEN. This reported one fixed line — "Waking runs
// in CircleAI. Open CircleAI to turn it on" — about an app with no launcher icon,
// under a button that navigates to Settings. So the one screen in the product for
// saying the phrase and watching it land was a dead end that was wrong twice over: on
// a phone that WAS listening it still said to go and turn it on.
//
// Measured on a P30 on 2026-10-01: the microphone was open, the shade said so, the
// Settings row said so, and this screen told the person to open an app they cannot.
//
// POLLED, BECAUSE A BINDER CANNOT PUSH. The phrase fires in the service's process and
// nothing calls back, so the state is asked for on a cadence and the wake COUNT is
// what turns "it heard you" into something a client can observe.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <inheritdoc />
/// <param name="brain">The linked brain, whose connection this rides.</param>
/// <param name="currentActivity">
/// The activity in front, for the microphone dialog. Without it the screen still
/// follows the service; it just cannot ask.
/// </param>
public sealed class LinkedWakeWord(LinkedBrain brain, Func<Android.App.Activity?>? currentActivity = null) : IWakeWord
{
    /// <summary>How often the screen asks the service where it stands.</summary>
    /// <remarks>
    /// A SECOND IS WHAT A WAKE FEELS LIKE. Slower and the mark lights up noticeably
    /// after the person has finished speaking, which reads as the phone being slow
    /// rather than the screen being polled; faster buys nothing a person can see and
    /// spends binder calls on a screen that may be left open.
    /// </remarks>
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    /// <remarks>
    /// THE SERVICE'S MICROPHONE, ASKED FOR THROUGH THE SERVICE. This returned false
    /// without asking anybody, on the grounds that the service asks for its own when
    /// a client is approved for Voice - and our own app is approved without a screen,
    /// so nothing ever asked, and the button on this screen did nothing at all.
    /// </remarks>
    /// <remarks>
    /// AND NULL - "nothing was answered" - IS NOT A REFUSAL. ServiceMicrophone
    /// returns it when there was no activity to ask from or the wait gave out with
    /// the dialog still up; this screen only needs to know whether to start
    /// listening, so it reads null as "not yet" and asks again on the next visit.
    /// </remarks>
    public async Task<bool> RequestMicrophoneAsync(bool quietly = false)
        => await ServiceMicrophone.RequestAsync(
            currentActivity?.Invoke(), quietly: quietly).ConfigureAwait(false) == true;

    /// <inheritdoc />
    public async Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct)
    {
        if (!brain.ServiceInstalled)
        {
            updates.Report(new WakeStatus(WakeState.NotInstalled,
                ServicePairing.Name + " is not installed",
                "This app asks Circle AI to listen and to think, and the part that does "
                + "both is a separate download. Add it to use them."));
            return;
        }

        // START IT, THEN FOLLOW IT. Somebody who opened this screen wants it
        // listening; the service treats a start as a no-op when it already is, so
        // this is the one call that both switches it on and reports where it stands.
        var start = true;
        var seen = -1;
        var asked = false;
        var startedHere = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await brain.LinkAsync(ct).ConfigureAwait(false);
                if (client is null)
                {
                    updates.Report(new WakeStatus(WakeState.NeedsPermission,
                        "Not linked yet",
                        "Approve CircleAI in Settings, then it can listen for you."));
                    await Task.Delay(Cadence, ct).ConfigureAwait(false);
                    continue;
                }

                var reply = start
                    ? await client.ResidentStartAsync(ct).ConfigureAwait(false)
                    : await client.ResidentStatusAsync(ct).ConfigureAwait(false);
                start = false;

                if (!reply.Ok || reply.Rows.Count == 0)
                {
                    updates.Report(new WakeStatus(WakeState.Failed,
                        "CircleAI could not say",
                        reply.Error ?? "It did not answer."));
                    await Task.Delay(Cadence, ct).ConfigureAwait(false);
                    continue;
                }

                var row    = reply.Rows[0];
                var state  = Enum.TryParse<ResidentState>(LinkSetupRows.Text(row, 0), out var st)
                    ? st
                    : ResidentState.Failed;
                var status = LinkSetupRows.Text(row, 1);
                var hint   = LinkSetupRows.Text(row, 2);
                var heard  = (int)LinkSetupRows.Number(row, 3);

                // THE COUNT GOING UP IS THE DETECTION. The first reply establishes
                // where the counter already was — a service that has been up all day
                // must not make the mark flash the moment this screen opens.
                var woke = seen >= 0 && heard > seen;
                seen = heard;

                updates.Report(woke
                    ? new WakeStatus(WakeState.Heard, "Heard you", hint, heard)
                    : new WakeStatus(Map(state), status, hint, heard));

                // NO MICROPHONE: ASK, ONCE PER VISIT, AND QUIETLY. Opening this screen
                // is asking for it to listen, the same reading the start above makes,
                // and the in-process head asked on arrival too. Once, so a "no" is not
                // met with the dialog again every second; "Allow the microphone" starts
                // a fresh visit, which asks again. Allowed, the next pass starts it.
                //
                // QUIETLY, BECAUSE NOBODY PRESSED ANYTHING. This asked exactly as the
                // button does - so on a phone where the microphone had been refused
                // permanently, Android showed no dialog, the service's screen concluded
                // "the phone will not ask again", and Circle AI Service's page in
                // Settings opened over the app EVERY TIME somebody opened this screen.
                // The rule the whole mechanism is built on - only a press earns
                // Settings - was being broken by the screen that asks most often.
                if (MicrophoneAsk.ShouldAsk(state, fromService: true, granted:
                        ServiceMicrophone.Granted(Android.App.Application.Context), askedThisVisit: asked))
                {
                    asked = true;
                    if (await RequestMicrophoneAsync(quietly: true).ConfigureAwait(false))
                    {
                        start = true;
                        continue;
                    }
                }

                // ALLOWED, WANTED, DOWNLOADED - AND REFUSED THE MICROPHONE BECAUSE THE
                // SERVICE HAD TO START IT FROM THE BACKGROUND. This screen is visible,
                // which is the one thing the platform wants, so it is also the cheapest
                // place to fix it: the service's own screen makes the start and finishes.
                //
                // ONCE PER VISIT, like the ask. Left ungated this polls at 1 Hz, and
                // every pass would have the service open the microphone, find it
                // silenced and close it again - a second-by-second cycle on the one
                // resource this feature is about.
                if (state == ResidentState.NeedsForeground && !startedHere)
                {
                    startedHere = true;
                    ServiceMicrophone.StartListening(currentActivity?.Invoke());
                    start = true;
                }

                await Task.Delay(Cadence, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving the screen. Listening carries on in the service, which is the
            // point of it being there.
        }
    }

    /// <summary>What the service's state means to this screen.</summary>
    /// <remarks>
    /// OFF IS NOT "NOT INSTALLED", and the difference decides which button appears.
    /// NotInstalled offers "Turn it on", which is exactly right for a listener that
    /// is simply switched off — it navigates to the switch. Unsupported and a missing
    /// bundle land there too, and the service's own hint says which it is rather than
    /// this mapping pretending to know.
    /// <para>
    /// ScreenOnly IS listening, so it reads as listening; the narrower promise it
    /// carries is in the hint the service wrote, not in a state this screen invents.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// AND NeedsForeground IS "STARTING", NOT "NOT INSTALLED". It means wanted,
    /// allowed and downloaded, with only a visible screen missing - and this screen
    /// IS one, so the block above has already asked the service to start on its way
    /// through. Falling through to NotInstalled would have offered "Turn it on",
    /// which navigates away from the screen that was about to fix it.
    /// </remarks>
    private static WakeState Map(ResidentState state) => state switch
    {
        ResidentState.Listening       => WakeState.Listening,
        ResidentState.ScreenOnly      => WakeState.Listening,
        ResidentState.NeedsPermission => WakeState.NeedsPermission,
        ResidentState.NeedsForeground => WakeState.Preparing,
        ResidentState.Failed          => WakeState.Failed,
        _                             => WakeState.NotInstalled,
    };
}
