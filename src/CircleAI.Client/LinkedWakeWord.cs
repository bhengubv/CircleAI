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
public sealed class LinkedWakeWord(LinkedBrain brain) : IWakeWord
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
    /// FALSE, and not a refusal to try: this app does not hold the microphone, so
    /// asking a person for it here would take a permission nothing then uses. The
    /// service asks for its own, when a client is approved for Voice.
    /// </remarks>
    public Task<bool> RequestMicrophoneAsync() => Task.FromResult(false);

    /// <inheritdoc />
    public async Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct)
    {
        if (!brain.ServiceInstalled)
        {
            updates.Report(new WakeStatus(WakeState.NotInstalled,
                "CircleAI is not installed",
                "This app asks CircleAI to listen and to think. Install it to use them."));
            return;
        }

        // START IT, THEN FOLLOW IT. Somebody who opened this screen wants it
        // listening; the service treats a start as a no-op when it already is, so
        // this is the one call that both switches it on and reports where it stands.
        var start = true;
        var seen = -1;

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
    private static WakeState Map(ResidentState state) => state switch
    {
        ResidentState.Listening       => WakeState.Listening,
        ResidentState.ScreenOnly      => WakeState.Listening,
        ResidentState.NeedsPermission => WakeState.NeedsPermission,
        ResidentState.Failed          => WakeState.Failed,
        _                             => WakeState.NotInstalled,
    };
}
