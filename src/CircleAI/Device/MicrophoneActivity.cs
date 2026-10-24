// MicrophoneActivity.cs
//
// The one screen that gives Circle AI the microphone, and the one that starts it
// listening.
//
// A RUNTIME PERMISSION NEEDS AN ACTIVITY TO ASK FOR IT, and this app is a service
// with no screens. The link-approval sheet asked, after a person approved an app for
// Voice - and our own app never sees that sheet, because an app signed with our key is
// approved without one. So the wake word, on by default, could not listen after any
// fresh install, and nothing on the phone could change it. Measured on the P30 on
// 2026-10-23 with both 1.1 pairs: "resuming: the owner had it on", then silence, and
// RECORD_AUDIO granted=false on the service.
//
// AND THE PERMISSION TURNED OUT TO BE HALF THE JOB. From Android 11 a foreground
// service STARTED FROM THE BACKGROUND is given no microphone - refused on 11,
// silenced on 12 and 13, and from 14 the microphone-typed start is rejected outright.
// Every start this service makes by itself is a background start: the resume when its
// process comes up, the ResidentStart verb, and the fetch's "your wake bundle just
// landed", which on a fresh install is the one that matters. A start made while an
// activity of THIS package is visible is allowed, and latches that allowance for the
// life of the service record - so this screen is also where listening begins, with
// LinkIpc.StartExtra, with nothing asked and nothing shown.
//
// This is that screen and nothing else. It runs transparent, so only the system
// dialog is seen. A client that is in front starts it FOR RESULT, which lets the
// dialog show and makes getCallingPackage name the caller. It asks for RECORD_AUDIO
// only, and only for callers the link already trusts with Voice.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Assistant;
using CircleAI.Assistant.Device;
using CircleAI.Linking;

namespace CircleAI.Device;

/// <summary>Asks for Circle AI's microphone on behalf of an approved client.</summary>
/// <remarks>
/// NOT RECREATED UNDER ITS OWN DIALOG. A configuration change the manifest does not
/// list would rebuild this screen while its dialog is up, and the first version then
/// asked a SECOND time - which Android answers immediately and emptily, so the client
/// was told "not allowed" while the real dialog was still on screen waiting to be
/// answered. The list below covers the ones that happen on their own: scheduled dark
/// mode (UiMode), a font or display-size change, a locale change.
/// </remarks>
[Activity(Name = "ai.circle.MicrophoneActivity", Exported = true,
          Theme = "@android:style/Theme.Translucent.NoTitleBar", ExcludeFromRecents = true,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                               | ConfigChanges.ScreenLayout | ConfigChanges.Keyboard
                               | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode
                               | ConfigChanges.Density | ConfigChanges.SmallestScreenSize
                               | ConfigChanges.FontScale | ConfigChanges.Locale
                               | ConfigChanges.LayoutDirection)]
[IntentFilter(new[] { LinkIpc.MicrophoneAction }, Categories = new[] { Intent.CategoryDefault })]
public sealed class MicrophoneActivity : Activity
{
    private const string Tag = "CircleAI.Microphone";

    /// <summary>How long the listener may take to start before this screen gives up on it.</summary>
    /// <remarks>
    /// AN INVISIBLE SCREEN THAT SWALLOWS TOUCHES MUST NOT OUTLIVE A STUCK START. The
    /// start loads a keyword model and opens a recorder; on a P30 that is a second or
    /// two. Past this the screen goes anyway - the start carries on in the service,
    /// where it belongs, and the client reads the result from the status.
    /// </remarks>
    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(8);

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetResult(Result.Canceled);   // the default until the microphone is allowed

        try
        {
            // REBUILT WITH A QUESTION ALREADY ON SCREEN, and it must not ask a second
            // time. Android answers a repeat request for a permission it is already
            // asking about immediately and EMPTILY, which the first version of this
            // screen reported as a refusal - while the person was still looking at the
            // real dialog, and with the Settings jump taken on the strength of it. So
            // a rebuilt screen asks nothing and judges nothing: it reads the permission
            // as it stands and gets on with the start if it is held.
            var rebuilt = savedInstanceState is not null;
            if (rebuilt)
                Android.Util.Log.Info(Tag, "rebuilt mid-question - not asking again");

            // EVERY WAY OUT SAYS WHICH ONE IT WAS. LinkConsentActivity's silent exits
            // cost an evening; this screen has the same shape and the same rule.
            var caller = CallingPackage;
            var grants = CircleNeuronLinkService.Grants;
            if (string.IsNullOrEmpty(caller) || grants is null)
            {
                Android.Util.Log.Warn(Tag,
                    $"refused - caller={(string.IsNullOrEmpty(caller) ? "unknown (not started for result?)" : caller)} "
                    + $"grants={(grants is null ? "not wired" : "ready")}");
                return;
            }

            var signature = CircleNeuronLinkService.SignatureDigestOf(this, caller!);
            if (signature is null)
            {
                Android.Util.Log.Warn(Tag, $"refused - cannot read the signature of '{caller}' (package visibility?)");
                return;
            }

            var decision = await new LinkGate(grants, CircleNeuronLinkService.FirstPartySignatures)
                .DecideAsync(new LinkRequest(caller!, signature, LinkScope.Voice), DateTimeOffset.UtcNow)
                .ConfigureAwait(true);
            if (!MicrophoneAsk.MayAsk(decision.Kind))
            {
                Android.Util.Log.Warn(Tag, $"refused - '{caller}' is not approved for Voice ({decision.Reason})");
                return;
            }

            // WHY THIS LAUNCH HAPPENED. Quiet: nobody pressed anything, so a phone
            // that will not ask again is left alone. Start: the permission is already
            // held and this screen is here to make the start happen while it is up.
            var quiet = Intent?.GetBooleanExtra(LinkIpc.QuietExtra, false) == true;
            var toStart = Intent?.GetBooleanExtra(LinkIpc.StartExtra, false) == true;

            var allowed = CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
            if (allowed)
            {
                Android.Util.Log.Info(Tag, $"already allowed; asked by '{caller}'{(toStart ? " to start listening" : "")}");
            }
            else if (rebuilt)
            {
                Android.Util.Log.Info(Tag, "not allowed as things stand, and this screen was rebuilt - leaving it alone");
            }
            else
            {
                // READ BEFORE, AND READ AFTER. One reading cannot tell the three cases
                // apart - see MicrophoneAsk.ShouldOpenSettings, which is the truth
                // table and is tested. Read before the request, because afterwards
                // "the phone showed nothing" and "they just said never again" are the
                // same false.
                var rationaleBefore = ShouldShowRequestPermissionRationale(Android.Manifest.Permission.RecordAudio);
                var askedBefore = MicrophonePrefs.HasAsked(this);

                Android.Util.Log.Info(Tag, $"asking, for '{caller}'");
                MicrophonePrefs.Asked(this);

                var answer = await AskAsync().ConfigureAwait(true);
                allowed = answer is true;
                Android.Util.Log.Info(Tag, allowed ? "allowed" : answer is null ? "dismissed" : "not allowed");

                // THE PHONE WILL NOT ASK AGAIN. After "don't ask again" the request
                // returns no at once and shows nothing, so the button would look
                // broken for ever. The switch still exists, on this app's own page in
                // Settings, so that is where the person is taken - WHEN THEY PRESSED
                // SOMETHING, and when no dialog could have been shown.
                //
                // That last clause is the fix. This used to read the rationale only
                // AFTER the request, and it is false in three different situations:
                // the permission is fixed and nothing was shown (the one case this is
                // for), the person was shown the dialog and has just chosen "don't ask
                // again" (so Settings opened on top of their own decision), and -
                // Android 11 and later - the dialog was shown and dismissed, which
                // changes no flag at all and would have been shown again on the next
                // press.
                var rationaleAfter = ShouldShowRequestPermissionRationale(Android.Manifest.Permission.RecordAudio);
                if (!allowed && MicrophoneAsk.ShouldOpenSettings(
                        pressed: !quiet, askedBefore: askedBefore, dismissed: answer is null,
                        rationaleBefore: rationaleBefore, rationaleAfter: rationaleAfter))
                    OpenAppInfo();
            }

            if (!allowed) return;
            SetResult(Result.Ok);

            // LISTEN NOW, WHILE THIS SCREEN IS STILL VISIBLE, AND BRING THE SERVICE UP
            // EVEN IF THERE IS NOTHING TO LISTEN WITH YET.
            //
            // The second half is the one that was missing, and it is why a fresh
            // install on Android 11+ stayed deaf. On the P30 the microphone was allowed
            // thirteen seconds into setup with the wake bundle still arriving, so the
            // start below refused - "the wake word is not downloaded" - and returned
            // before CircleNeuronService.Start. The bundle landed thirteen seconds
            // later and the service started itself from a download thread, which is
            // the background start the platform gives no microphone to.
            //
            // A startForegroundService made while an activity of this package is
            // visible is allowed, and Android LATCHES that allowance on the service
            // record until the service stops - so bringing the service up here, now,
            // is what makes the later background start legal. It costs nothing when
            // the service is already up.
            //
            // Only if the owner has not switched listening off.
            if (!ResidentPrefs.WasRunning(this))
            {
                Android.Util.Log.Info(Tag, "not starting the wake word: the owner has it off");
                return;
            }

            try { CircleNeuronService.Start(this); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "could not bring the service up while visible: " + ex.Message);
            }

            // SAID WHEN THERE IS NOTHING TO START, because it would otherwise be a
            // silent nothing: a head that wires CircleNeuronLinkService.Resident to
            // something else - or not at all - has a microphone granted and no listener
            // to start, and the person's phone is deaf for a reason no log line names.
            if (CircleNeuronLinkService.Resident is not { } resident)
            {
                Android.Util.Log.Warn(Tag,
                    "the microphone is allowed and there is no resident listener wired to start");
                return;
            }

            // THE VISIBLE OVERLOAD WHERE THERE IS ONE. Only this screen can say "the
            // start is being made from a visible window", and only the device listener
            // knows what to do with that.
            var start = resident is ResidentListeningControl device
                ? device.StartAsync(fromVisibleScreen: true)
                : resident.StartAsync();

            // BOUNDED. See StartPatience: this screen is invisible and swallows
            // touches, so it must not wait for ever on a start that has stuck.
            if (ReferenceEquals(await Task.WhenAny(start, Task.Delay(StartPatience)).ConfigureAwait(true), start))
            {
                var status = await start.ConfigureAwait(true);
                Android.Util.Log.Info(Tag, $"wake word: {status.State} - {status.Status}");
            }
            else
            {
                Android.Util.Log.Warn(Tag,
                    $"the wake word has not started within {StartPatience.TotalSeconds:0} s - "
                    + "leaving it to the service and closing this screen");
            }
        }
        catch (Exception ex)
        {
            // An allowed microphone stays allowed whatever failed after it, so the
            // result is left as it is.
            Android.Util.Log.Error(Tag, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Raise the system dialog and wait for the answer.</summary>
    /// <remarks>
    /// AWAITED SO THE DIALOG SURVIVES. OnCreate finishes this screen in its finally
    /// block, and a dialog raised by a screen that has finished is dismissed before
    /// anybody reads it - the lesson LinkConsentActivity paid for first.
    /// <para>
    /// THREE ANSWERS. True is allowed, false is refused, and NULL is "no answer at
    /// all" - an empty result array, which Android sends when the interaction was
    /// interrupted rather than answered. Treating that as a refusal is what sent
    /// people to Settings for tapping outside a dialog.
    /// </para>
    /// </remarks>
    private Task<bool?> AskAsync()
    {
        _answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions(new[] { Android.Manifest.Permission.RecordAudio }, MicrophoneRequest);
        return _answer.Task;
    }

    /// <summary>Where the microphone can still be switched on once the phone has stopped asking.</summary>
    private void OpenAppInfo()
    {
        try
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings,
                                     Android.Net.Uri.FromParts("package", PackageName!, null)));
            Android.Util.Log.Info(Tag, "the phone will not ask again - opened this app's page in Settings");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, "the phone will not ask again, and its Settings page did not open: " + ex.Message);
        }
    }

    private const int MicrophoneRequest = 0x31d;
    private TaskCompletionSource<bool?>? _answer;

    /// <inheritdoc />
    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode == MicrophoneRequest)
        {
            // AN EMPTY ANSWER IS THE SYSTEM CANCELLING THE REQUEST, NOT A NO - and it
            // is the difference between "the phone refuses to ask" and "they tapped
            // beside the dialog". Android documents it: "it is possible that the
            // permissions request interaction with the user is interrupted. In this
            // case you will receive empty permissions and results arrays."
            _answer?.TrySetResult(grantResults.Length == 0
                ? null
                : grantResults[0] == Permission.Granted);
            return;
        }

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}

/// <summary>Remembers that this package has raised the microphone dialog.</summary>
/// <remarks>
/// ONE BIT, AND WITHOUT IT THE DECISION CANNOT BE MADE. Android's
/// shouldShowRequestPermissionRationale returns false both for a permission it will
/// no longer ask about AND for one nobody has ever been asked about, and only the
/// first is a reason to send somebody to Settings. Nothing in the platform
/// distinguishes them; this does.
/// </remarks>
public static class MicrophonePrefs
{
    private const string File = "circleai.microphone";
    private const string Key  = "asked";

    private static ISharedPreferences? Prefs(Context c) =>
        (c.ApplicationContext ?? c).GetSharedPreferences(File, FileCreationMode.Private);

    /// <summary>Whether the dialog has ever been raised by this package.</summary>
    public static bool HasAsked(Context context)
    {
        try { return Prefs(context)?.GetBoolean(Key, false) ?? false; }
        catch { return false; }
    }

    /// <summary>Records that it is being raised now.</summary>
    /// <remarks>
    /// WRITTEN BEFORE THE ANSWER, deliberately. The process can be killed while the
    /// dialog is up, and a request that was made and forgotten reads as never asked -
    /// which would let the next press jump to Settings on the strength of a false
    /// reading.
    /// </remarks>
    public static void Asked(Context context)
    {
        try { Prefs(context)?.Edit()?.PutBoolean(Key, true)?.Apply(); }
        catch { /* the decision degrades to "never asked", which never opens Settings */ }
    }
}
