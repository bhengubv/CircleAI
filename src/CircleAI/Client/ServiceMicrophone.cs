// ServiceMicrophone.cs
//
// Circle AI's microphone, asked for from a client app.
//
// THE MICROPHONE THAT MATTERS IS THE SERVICE'S. The wake word listens in
// com.bhengubv.circleai.service, so that package must hold RECORD_AUDIO; this app
// holding it does nothing for "answer to its name". Every "Allow the microphone" in
// this app used to return false without asking anybody, because the service was
// meant to ask when a link was approved - and our own app is approved without a
// screen, so it never was.
//
// THE ANSWER IS READ FROM ANDROID, NOT TAKEN FROM THE SCREEN. The service's screen
// returns Ok or Canceled, which says how the dialog went; what decides the return
// value is the permission the service package actually holds afterwards, so a person
// who allowed it in Settings instead is not told no.
//
// AND THE PERMISSION IS NOT THE WHOLE JOB. From Android 11 a foreground service
// started from the BACKGROUND is given no microphone, and every start the service
// makes by itself is one. So this type has a second entry point - StartListeningAsync
// - which launches the same screen of the service's purely to make the start happen
// while something of ours is visible. Nothing is asked and nothing is shown.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>Asks for, and reports, the microphone of the Circle AI service.</summary>
public static class ServiceMicrophone
{
    private const string Tag = "CircleAI.Microphone";

    /// <summary>The request code the service's screen answers to.</summary>
    /// <remarks>Distinct from the consent sheet's, so one answer is never taken for the other.</remarks>
    public const int RequestCode = 0x31c;

    /// <summary>How long to wait for the answer before reading the permission anyway.</summary>
    /// <remarks>
    /// A person can leave the dialog up and walk away. Two minutes is long enough for
    /// anybody deciding, and short enough that a screen waiting on it comes back.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _answer;

    /// <summary>Whether a question raised by this process is still unanswered.</summary>
    /// <remarks>
    /// SO A SCREEN CAN SAY "STILL ASKING" RATHER THAN "NOT ALLOWED". The wait gives
    /// out after two minutes and reads the permission, which is right for a screen
    /// that has to come back - but it was then rendered as a refusal, under a dialog
    /// the person had not answered yet.
    /// </remarks>
    public static bool Pending
    {
        get { lock (Gate) return _answer is { Task.IsCompleted: false }; }
    }

    /// <summary>Whether the Circle AI service may use the microphone now.</summary>
    /// <remarks>
    /// Asked about the SERVICE's package. A client may ask this of another package:
    /// it needs the &lt;queries&gt; entry for it that binding already requires.
    /// </remarks>
    public static bool Granted(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return context.PackageManager?.CheckPermission(
                       Android.Manifest.Permission.RecordAudio, LinkIpc.HostPackage)
                   == Permission.Granted;
        }
        catch
        {
            // A phone that will not answer has not granted it.
            return false;
        }
    }

    /// <summary>
    /// Ask the person to let Circle AI use the microphone, through the service's own
    /// screen, and return whether it now may.
    /// </summary>
    /// <param name="activity">The activity in front; the dialog needs one. Null asks nothing.</param>
    /// <param name="ct">Stops waiting, not the dialog.</param>
    /// <param name="quietly">
    /// Asked on the person's behalf rather than at a press - first run, for a wake
    /// word that is on by default. A phone that will not ask again is then left
    /// alone instead of taking the person to Settings.
    /// </param>
    /// <returns>
    /// True when the service may now use the microphone, false when it may not, and
    /// NULL when nothing was answered: there was no activity to ask from, the screen
    /// could not be opened, or the wait gave out with the question still on screen.
    /// </returns>
    public static async Task<bool?> RequestAsync(
        Activity? activity, CancellationToken ct = default, bool quietly = false)
    {
        if (activity is null)
        {
            Android.Util.Log.Warn(Tag, "cannot ask: no activity in front");
            return null;
        }

        if (Granted(activity)) return true;

        // ONE DIALOG, HOWEVER MANY BUTTONS. The tour, the wake screen and the
        // settings switch can all ask; a second press while the dialog is up waits
        // for the same answer instead of stacking another screen on it.
        TaskCompletionSource<bool> answer;
        bool launch;
        lock (Gate)
        {
            launch = _answer is null || _answer.Task.IsCompleted;
            if (launch) _answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = _answer!;
        }

        if (launch)
        {
            // ON THE MAIN THREAD, because the callers are not: the wake screen's loop
            // and the settings switch arrive here from behind ConfigureAwait(false).
            activity.RunOnUiThread(() =>
            {
                try
                {
                    activity.StartActivityForResult(CircleAiLinkClient.MicrophoneIntent(quietly), RequestCode);
                    Android.Util.Log.Info(Tag, quietly
                        ? "asked Circle AI for the microphone, on the person's behalf"
                        : "asked Circle AI for the microphone");
                }
                catch (Exception ex)
                {
                    // Usually Circle AI is older than this app and has no such screen.
                    Android.Util.Log.Warn(Tag, "could not open Circle AI's microphone screen: " + ex.Message);
                    answer.TrySetResult(false);
                    Forget(answer);
                }
            });
        }

        try
        {
            await answer.Task.WaitAsync(Patience, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A TIMED-OUT QUESTION USED TO BLOCK EVERY LATER ONE. The waiter gave up
            // and the shared TaskCompletionSource stayed, uncompleted, for the life of
            // the process - and the branch above only launches when the current one is
            // COMPLETE. So after one request whose answer never arrived (the activity
            // went away, the result landed on a different one), "Allow the microphone"
            // on Home, Loading and Setup, the wake screen and the settings switch each
            // waited two minutes behind a dialog that was never raised, and then said
            // "Not allowed". Forgetting it means the next press asks again.
            Android.Util.Log.Info(Tag, "no answer in " + Patience.TotalMinutes + " min; reading the permission anyway");
            Forget(answer);

            // AND "NO ANSWER" IS NOT "NO". The question may still be on screen; the
            // permission is the only thing worth trusting, and if it is not granted
            // yet the honest report is that nothing has been decided.
            return Granted(activity) ? true : null;
        }

        var granted = Granted(activity);
        Android.Util.Log.Info(Tag, granted ? "Circle AI may use the microphone" : "Circle AI may not use the microphone");
        return granted;
    }

    /// <summary>
    /// Start the wake word from a visible screen, because the service may not start
    /// it from the background.
    /// </summary>
    /// <param name="activity">The activity in front. Null does nothing.</param>
    /// <remarks>
    /// THE FIX FOR THE STATE THE SERVICE CANNOT GET OUT OF BY ITSELF. From Android 11
    /// a foreground service started from the background is given no microphone, and
    /// every start the service makes alone is one: the resume when its process comes
    /// up, the ResidentStart verb, and the fetch's "your wake bundle just landed". The
    /// service detects it (ResidentListeningControl.CanBeHeard), stops, and reports
    /// NeedsForeground - and this is how a client that IS in front clears it.
    /// <para>
    /// It launches the service's microphone screen with
    /// <see cref="LinkIpc.StartExtra"/>. Nothing is asked - the permission is already
    /// held, or there would be nothing to start - and the screen is translucent and
    /// empty, so a person sees their own app throughout.
    /// </para>
    /// <para>
    /// ONCE PER PROCESS UNLESS IT WORKED. A service that keeps answering
    /// NeedsForeground - an old build, a phone we have not understood yet - must not
    /// have an invisible activity thrown at it on every screen that reads the status.
    /// </para>
    /// </remarks>
    public static void StartListening(Activity? activity)
    {
        if (activity is null)
        {
            Android.Util.Log.Info(Tag, "cannot start listening from a visible screen: no activity in front");
            return;
        }

        lock (Gate)
        {
            if (_startTried) return;
            _startTried = true;
        }

        activity.RunOnUiThread(() =>
        {
            try
            {
                activity.StartActivityForResult(
                    CircleAiLinkClient.MicrophoneIntent(quietly: true, startListening: true), RequestCode);
                Android.Util.Log.Info(Tag, "asked Circle AI to start listening while this app is up");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "could not ask Circle AI to start listening: " + ex.Message);
            }
        });
    }

    private static bool _startTried;

    /// <summary>Lets the wake word be started from a visible screen again.</summary>
    /// <remarks>
    /// Called when listening is switched off by hand, so the next switch-on may use
    /// the visible-screen path again rather than being refused by a latch set an hour
    /// ago.
    /// </remarks>
    public static void AllowStartingAgain()
    {
        lock (Gate) _startTried = false;
    }

    /// <summary>
    /// Hand an activity result to the request waiting for it. Wire this to the
    /// head's activity results; it ignores every request code but its own.
    /// </summary>
    /// <returns>True when the result was the microphone screen's.</returns>
    public static bool Answered(int requestCode, Result resultCode)
    {
        if (requestCode != RequestCode) return false;

        TaskCompletionSource<bool>? answer;
        lock (Gate) answer = _answer;
        answer?.TrySetResult(resultCode == Result.Ok);
        return true;
    }

    /// <summary>Drop a question nobody is waiting for any more.</summary>
    private static void Forget(TaskCompletionSource<bool> answer)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_answer, answer)) _answer = null;
        }
    }
}
