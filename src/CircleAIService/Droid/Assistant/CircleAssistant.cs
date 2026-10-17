// CircleAssistant.cs
//
// CIRCLE AI AS THE PHONE'S ASSISTANT: THE OS'S OWN ENTRY POINT.
//
// Until now the only way in was the app or the wake word. Android has one sanctioned
// door for "the assistant" - a VoiceInteractionService, held by whoever owns the
// assistant role - and it is what the long-press of home, the assist gesture and a
// headset button reach. These classes are that door, kept LIGHT: no models here. A
// session hands off to the Circle AI app when the phone is unlocked, and runs a spoken
// turn through the same queue as the wake word when it is not.
//
// STOCK ANDROID NEEDS THE ROLE; CIRCLE OS GRANTS IT DELIBERATELY. AssistantRoleActivity
// asks for ROLE_ASSISTANT where the platform offers it, and opens the assistant
// settings where it does not.

using Android.App;
using Android.App.Roles;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Service.Voice;
using Android.Speech;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Linking;

namespace CircleAI.Service.Droid.Assistant;

/// <summary>The assistant service the OS binds when Circle AI holds the assistant role.</summary>
[Service(Name = "ai.circle.CircleAssistantService", Permission = "android.permission.BIND_VOICE_INTERACTION", Exported = true)]
[IntentFilter(new[] { "android.service.voice.VoiceInteractionService" })]
[MetaData("android.voice_interaction", Resource = "@xml/circle_voice_interaction")]
public sealed class CircleAssistantService : VoiceInteractionService
{
    /// <inheritdoc />
    public override void OnReady()
    {
        base.OnReady();
        global::Android.Util.Log.Info("CircleAI.Assistant", "the OS bound Circle AI as the phone's assistant");
    }
}

/// <summary>Creates a session for each assist request.</summary>
[Service(Name = "ai.circle.CircleAssistSessionService", Permission = "android.permission.BIND_VOICE_INTERACTION", Exported = true)]
public sealed class CircleAssistSessionService : VoiceInteractionSessionService
{
    /// <inheritdoc />
    public override VoiceInteractionSession OnNewSession(Bundle? args) => new CircleAssistSession(this);
}

/// <summary>One assist request: hand off to the app, or answer by voice.</summary>
public sealed class CircleAssistSession : VoiceInteractionSession
{
    /// <summary>Runs a spoken turn exactly as the wake word does. Set by the service's startup.</summary>
    public static Func<string, Task>? RunSpokenTurn { get; set; }

    /// <summary>The Circle AI app that a session hands off to.</summary>
    public static string AppPackage { get; set; } = "com.bhengubv.circleai.hybrid";

    /// <summary>The extra the app reads to know it was opened as the assistant.</summary>
    public const string AssistExtra = "com.bhengubv.circleai.extra.ASSIST";

    /// <summary>A session over the assistant's context.</summary>
    public CircleAssistSession(Context context) : base(context) { }

    /// <inheritdoc />
    public override void OnShow(Bundle? args, [global::Android.Runtime.GeneratedEnum] ShowFlags showFlags)
    {
        base.OnShow(args, showFlags);
        try
        {
            var keyguard = (KeyguardManager?)Context.GetSystemService(Context.KeyguardService);
            var locked = keyguard?.IsKeyguardLocked ?? false;
            var launch = Context.PackageManager?.GetLaunchIntentForPackage(AppPackage);

            // UNLOCKED, WITH THE APP THERE: the app is where a conversation is seen.
            if (!locked && launch is not null)
            {
                launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
                launch.PutExtra(AssistExtra, true);
                if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartAssistantActivity(launch);
                else Context.StartActivity(launch);
                global::Android.Util.Log.Info("CircleAI.Assistant", "assist: handed off to the app");
            }
            // LOCKED, OR NO APP: answer by voice, through the same queue as "Hey B".
            else if (RunSpokenTurn is { } turn)
            {
                _ = turn("assist").ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                global::Android.Util.Log.Info("CircleAI.Assistant", "assist: answering by voice");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("CircleAI.Assistant", "assist failed: " + ex.GetType().Name);
        }
        finally
        {
            Finish();
        }
    }
}

/// <summary>
/// The recogniser the assistant declares. A real one: it records until the speaker
/// stops, and transcribes with the same speech stack the link serves.
/// </summary>
[Service(Name = "ai.circle.CircleRecognitionService", Permission = "android.permission.BIND_VOICE_INTERACTION", Exported = true)]
[IntentFilter(new[] { "android.speech.RecognitionService" })]
[MetaData("android.speech", Resource = "@xml/circle_recognition")]
public sealed class CircleRecognitionService : RecognitionService
{
    private const int Rate = 16000;
    private static readonly TimeSpan NoSpeech = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(1200);

    /// <summary>The transcriber. Set by the service's startup.</summary>
    public static Func<ILinkSpeech?>? Speech { get; set; }

    /// <summary>True while the wake listener holds the microphone. Set by the service's startup.</summary>
    public static Func<bool>? MicrophoneBusy { get; set; }

    private CancellationTokenSource? _cancel;

    /// <inheritdoc />
    protected override void OnStartListening(Intent? recognizerIntent, Callback? listener)
    {
        if (listener is null) return;
        if (MicrophoneBusy?.Invoke() == true) { listener.Error(SpeechRecognizerError.RecognizerBusy); return; }
        var speech = Speech?.Invoke();
        if (speech is null) { listener.Error(SpeechRecognizerError.Client); return; }

        _cancel?.Cancel();
        _cancel = new CancellationTokenSource();
        var ct = _cancel.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var pcm = Record(listener, ct);
                if (pcm is null) { listener.Error(SpeechRecognizerError.SpeechTimeout); return; }
                var text = await speech.TranscribeAsync(pcm, null, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) { listener.Error(SpeechRecognizerError.NoMatch); return; }

                var results = new Bundle();
                results.PutStringArrayList(SpeechRecognizer.ResultsRecognition, new List<string> { text.Trim() });
                listener.Results(results);
            }
            catch (System.OperationCanceledException) { }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("CircleAI.Assistant", "recognition failed: " + ex.GetType().Name);
                try { listener.Error(SpeechRecognizerError.Client); } catch { }
            }
        }, ct);
    }

    /// <inheritdoc />
    protected override void OnCancel(Callback? listener) => _cancel?.Cancel();

    /// <inheritdoc />
    protected override void OnStopListening(Callback? listener) => _cancel?.Cancel();

    // RECORD UNTIL THEY STOP. Energy is enough to tell speech from a quiet room for a
    // single request; the wake listener's richer gate is busy holding its own mic.
    private static byte[]? Record(Callback listener, CancellationToken ct)
    {
        var size = Math.Max(AudioRecord.GetMinBufferSize(Rate, ChannelIn.Mono, Encoding.Pcm16bit), Rate / 5 * 2);
        using var recorder = new AudioRecord(AudioSource.VoiceRecognition, Rate, ChannelIn.Mono, Encoding.Pcm16bit, size);
        if (recorder.State != State.Initialized) return null;

        var all = new MemoryStream();
        var frame = new byte[Rate / 10 * 2];   // 100 ms
        var started = DateTime.UtcNow;
        DateTime? spoke = null;
        var lastLoud = started;
        recorder.StartRecording();
        listener.ReadyForSpeech(new Bundle());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = recorder.Read(frame, 0, frame.Length);
                if (read <= 0) continue;
                all.Write(frame, 0, read);

                var now = DateTime.UtcNow;
                if (Rms(frame, read) > 900)
                {
                    lastLoud = now;
                    if (spoke is null) { spoke = now; listener.BeginningOfSpeech(); }
                }

                if (spoke is null && now - started > NoSpeech) return null;
                if (spoke is not null && now - lastLoud > Silence) break;
                if (now - started > Longest) break;
            }
        }
        finally
        {
            try { recorder.Stop(); } catch { }
            listener.EndOfSpeech();
        }
        return spoke is null ? null : all.ToArray();
    }

    private static double Rms(byte[] pcm, int length)
    {
        double sum = 0;
        var n = length / 2;
        for (var i = 0; i + 1 < length; i += 2)
        {
            var s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += s * (double)s;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}

/// <summary>Asks for the assistant role, from an app that has a screen.</summary>
[Activity(Name = "ai.circle.AssistantRoleActivity", Exported = true, NoHistory = true,
          Theme = "@android:style/Theme.Translucent.NoTitleBar", ExcludeFromRecents = true)]
[IntentFilter(new[] { AssistantRole.RequestAction }, Categories = new[] { Intent.CategoryDefault })]
public sealed class AssistantRoleActivity : Activity
{
    private const int RoleRequest = 0xA551;

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29)
                && GetSystemService(RoleService) is RoleManager roles
                && roles.IsRoleAvailable(RoleManager.RoleAssistant))
            {
                if (roles.IsRoleHeld(RoleManager.RoleAssistant)) { SetResult(Result.Ok); Finish(); return; }
                StartActivityForResult(roles.CreateRequestRoleIntent(RoleManager.RoleAssistant), RoleRequest);
                return;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("CircleAI.Assistant", "role request failed: " + ex.GetType().Name);
        }

        OpenSettings();
    }

    /// <inheritdoc />
    protected override void OnActivityResult(int requestCode, [global::Android.Runtime.GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RoleRequest) return;
        if (AssistantRole.IsHeld(this)) { SetResult(Result.Ok); Finish(); }
        else OpenSettings();
    }

    // WHERE THE ROLE CANNOT BE ASKED FOR, the person is taken to where it is chosen.
    private void OpenSettings()
    {
        try
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionVoiceInputSettings)
                .AddFlags(ActivityFlags.NewTask));
        }
        catch { /* no such screen on this phone */ }
        SetResult(Result.Canceled);
        Finish();
    }
}

/// <summary>Whether Circle AI is the phone's assistant, and saying so to readiness.</summary>
public static class AssistantRole
{
    /// <summary>The action an app starts to ask for the role.</summary>
    public const string RequestAction = "com.bhengubv.circleai.action.ASSISTANT_ROLE";

    /// <summary>Whether this app holds the assistant role.</summary>
    public static bool IsHeld(Context context)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29)
                && context.GetSystemService(Context.RoleService) is RoleManager roles)
                return roles.IsRoleHeld(RoleManager.RoleAssistant);

            var current = global::Android.Provider.Settings.Secure.GetString(context.ContentResolver, "assistant");
            return current?.StartsWith(context.PackageName + "/", StringComparison.Ordinal) == true;
        }
        catch { return false; }
    }

    /// <summary>Report the assistant component.</summary>
    public static void Report(IReadinessTracker readiness, Context context)
    {
        var held = IsHeld(context);
        readiness.Report(ComponentState.Of(ReadinessComponents.Assistant,
            held ? BrainPhase.Ready : BrainPhase.Installed,
            held ? "Circle AI is this phone's assistant." : "Circle AI is not the phone's assistant yet.",
            DateTimeOffset.UtcNow));
    }
}
