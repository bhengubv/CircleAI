// LinkedResidentAssistant.cs
//
// The "answer to its name" switch, made to actually switch something.
//
// IT USED TO SAY "OPEN CIRCLEAI TO TURN IT ON OR OFF" - about an app with no
// launcher icon. That was the honest half of the answer (the microphone and the
// foreground service really are over there) attached to an action nobody can take.
// A toggle whose only instruction is unreachable is a toggle that does not exist.
//
// THE STATE IS ASKED, NOT REMEMBERED. Whether the phone is listening is the
// SERVICE's fact, and it can change without this app: the OS kills the process, the
// person revokes the microphone, a battery optimiser steps in. So every call asks,
// and nothing here is cached - a stale "Listening" on a screen is worse than a slow
// one.
//
// AND TWO THINGS ONLY A CLIENT IN FRONT CAN DO. A runtime permission needs a
// visible activity to ask from, and from Android 11 so does the START of a
// microphone-holding foreground service - so when the service answers
// NeedsPermission or NeedsForeground, this is the half of the product that can act
// on it. Both go through ServiceMicrophone, which owns the service's one screen.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <inheritdoc />
/// <param name="brain">The linked brain, whose connection this rides.</param>
/// <param name="currentActivity">
/// The activity in front, for the microphone dialog when switching it on needs one.
/// Without it the switch reports why it cannot listen; it just cannot ask.
/// </param>
public sealed class LinkedResidentAssistant(LinkedBrain brain, Func<Android.App.Activity?>? currentActivity = null)
    : IResidentAssistant
{
    /// <inheritdoc />
    /// <remarks>
    /// THE LAST ANSWER THE SERVICE GAVE, not a guess and not a constant. This is
    /// synchronous and the truth is across a link, so it cannot ask — it was
    /// hardcoded false for exactly that reason, which meant the settings checkbox
    /// could never tick no matter what the service said. Settings reads this to
    /// decide whether the switch is on.
    /// <para>
    /// It is only ever written by a call that DID ask, so before the first call it
    /// reports false — not listening — which is the safe direction: a dark switch
    /// over a listening phone would be a lie about the microphone, and the screen
    /// refreshes within a second of opening.
    /// </para>
    /// </remarks>
    public bool IsListening => _listening;

    private volatile bool _listening;

    /// <inheritdoc />
    /// <remarks>
    /// Declared to satisfy the interface, never raised: the wake word fires in the
    /// service's process and there is no verb that pushes an event back.
    /// </remarks>
    public event EventHandler<string>? Woke { add { } remove { } }

    /// <inheritdoc />
    /// <remarks>
    /// SWITCHING IT ON IS ASKING FOR THE MICROPHONE. The service refused with
    /// NeedsPermission and a hint, and that was the end of it: the switch went back
    /// off on every phone where the microphone had never been allowed, which after a
    /// fresh install was every phone. Somebody turning on "answer to its name" has
    /// said what they want, so the dialog comes up here and the start is tried again.
    /// <para>
    /// ONLY WHEN THE SERVICE SAID SO. "Not linked yet" is also NeedsPermission, and
    /// it is not about a microphone.
    /// </para>
    /// </remarks>
    public async Task<ResidentStatus> StartAsync(CancellationToken ct = default)
    {
        var (status, fromService) = await AskAsync(c => c.ResidentStartAsync(ct), ct).ConfigureAwait(false);
        if (!fromService) return status;

        if (status.State == ResidentState.NeedsPermission
            && !ServiceMicrophone.Granted(Android.App.Application.Context))
        {
            // QUIETLY, BECAUSE THIS IS A SWITCH.
            //
            // A SWITCH SWITCHES: on, off, nothing else. This is what "Answer to its
            // name" and "Listen for the wake phrase" are wired to, and asking loudly
            // meant that flipping one on, on a phone where the microphone had been
            // refused for good, took the person OUT of the app and onto Circle AI
            // Service's page in Android's Settings. Nobody asked to go there; they
            // asked for the thing to be on.
            //
            // The dialog still comes up - it is the only way a switch can do what its
            // label says - and a no is taken as a no. The switch then shows off and
            // its own subtitle says what it needs, which is a control reporting its
            // state rather than a control that navigates.
            //
            // The trip to Settings belongs to a BUTTON whose label is that verb:
            // "Allow the microphone", on the wake screen and on the first-run card.
            // Those are the only two asks that pass quietly: false.
            if (await ServiceMicrophone.RequestAsync(currentActivity?.Invoke(), ct, quietly: true)
                    .ConfigureAwait(false) != true)
                return status;

            status = (await AskAsync(c => c.ResidentStartAsync(ct), ct).ConfigureAwait(false)).Status;
        }

        return await ClearNeedsForegroundAsync(status, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ResidentStatus> StopAsync(CancellationToken ct = default)
    {
        var status = (await AskAsync(c => c.ResidentStopAsync(ct), ct).ConfigureAwait(false)).Status;

        // SWITCHED OFF BY HAND IS A FRESH START NEXT TIME. The visible-screen start
        // is latched to once per process so a service that keeps asking for one
        // cannot have an invisible activity thrown at it on every status read; an
        // explicit off, followed by an explicit on, is a different intention and
        // deserves the full path again.
        ServiceMicrophone.AllowStartingAgain();
        return status;
    }

    /// <inheritdoc />
    /// <remarks>
    /// RESUMING IS NOT STARTING, AND IT USED TO SEND THE SAME VERB. There was no
    /// resume verb, so this sent ResidentStart - which reads no preference and
    /// records the owner as wanting the microphone on its way out. This method runs
    /// on MainLayout's first render, which is every cold launch of the app, so an
    /// explicit "off" survived until the next time somebody opened it.
    /// <para>
    /// It never raises the microphone dialog: nobody pressed anything. It WILL start
    /// the listener from this app's visible window when the service says that is what
    /// it is waiting for - which is not a new consent, it is the one the owner
    /// already gave, finally being honoured on a platform that needs a visible
    /// moment to honour it in.
    /// </para>
    /// <para>
    /// AN OLDER SERVICE DOES NOT KNOW THE VERB and answers "unknown verb". The
    /// fallback is the STATUS verb, deliberately - never ResidentStart, which is the
    /// bug this method exists to stop.
    /// </para>
    /// </remarks>
    public async Task<ResidentStatus> ResumeAsync(CancellationToken ct = default)
    {
        var (status, fromService) = await AskAsync(c => c.ResidentResumeAsync(ct), ct).ConfigureAwait(false);

        if (!fromService)
        {
            var (fallback, answered) = await AskAsync(c => c.ResidentStatusAsync(ct), ct).ConfigureAwait(false);
            if (!answered) return status;
            status = fallback;
        }

        return await ClearNeedsForegroundAsync(status, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ResidentStatus> RefreshAsync(CancellationToken ct = default)
        => (await AskAsync(c => c.ResidentStatusAsync(ct), ct).ConfigureAwait(false)).Status;

    /// <summary>
    /// If the service is only waiting for a visible screen, give it this one.
    /// </summary>
    /// <remarks>
    /// THE ONE STATE A CLIENT CAN FIX AND THE SERVICE CANNOT. NeedsForeground means
    /// wanted, allowed, downloaded, and refused a microphone because the start came
    /// from the background - so the fix is a start that comes from here, through the
    /// service's own screen. One attempt, then the status is read again so the caller
    /// is told what actually happened rather than what was attempted.
    /// </remarks>
    private async Task<ResidentStatus> ClearNeedsForegroundAsync(ResidentStatus status, CancellationToken ct)
    {
        if (status.State != ResidentState.NeedsForeground) return status;

        var activity = currentActivity?.Invoke();
        if (activity is null) return status;

        ServiceMicrophone.StartListening(activity);

        // THE SCREEN FINISHES ON ITS OWN AND THE START IS SYNCHRONOUS INSIDE IT, so a
        // short wait is enough to report the truth rather than the attempt. Not
        // awaited on the activity result: this is a status read, not a dialog, and a
        // caller on first render must not be held up by it.
        await Task.Delay(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
        return (await AskAsync(c => c.ResidentStatusAsync(ct), ct).ConfigureAwait(false)).Status;
    }

    /// <summary>One verb over the link, and whether the answer came from the service.</summary>
    private async Task<(ResidentStatus Status, bool FromService)> AskAsync(
        Func<CircleAiLinkClient, Task<LinkRowsReply>> call, CancellationToken ct)
    {
        if (!brain.ServiceInstalled)
            return (new ResidentStatus(ResidentState.NotInstalled,
                ServicePairing.Name + " is not installed",
                "This app asks Circle AI to listen and to think, and the part that does "
                + "both is a separate download. Add it to use them."), false);

        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null)
            return (new ResidentStatus(ResidentState.NeedsPermission,
                "Not linked yet",
                "Approve CircleAI in Settings, then it can listen for you."), false);

        var reply = await call(client).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count == 0)
            return (new ResidentStatus(ResidentState.Failed,
                "CircleAI could not say",
                reply.Error ?? "It did not answer."), false);

        var row = reply.Rows[0];
        var state = Enum.TryParse<ResidentState>(LinkSetupRows.Text(row, 0), out var st)
            ? st
            : ResidentState.Failed;

        _listening = state == ResidentState.Listening;

        // A WORKING LISTENER EARNS THE NEXT VISIBLE START. The latch stops an
        // invisible activity being launched over and over at a service that will not
        // listen; it must not stop the one that WILL, the next time the platform
        // takes the microphone away - an update, a kill, a reboot.
        if (_listening) ServiceMicrophone.AllowStartingAgain();

        return (new ResidentStatus(state, LinkSetupRows.Text(row, 1), LinkSetupRows.Text(row, 2)), true);
    }
}
