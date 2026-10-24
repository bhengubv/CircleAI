// LinkIpc.cs
//
// The wire constants both sides of the cross-app link agree on.
//
// Kept here, in the portable core, so the client library and the CircleAI service
// never drift on the interface token or the transaction code. The Android side
// uses a low-level binder transaction — Binder.OnTransact on the service,
// IBinder.Transact on the client — because that is the one path where the OS
// reports the caller's uid RELIABLY (a Messenger delivers the message after the
// binder transaction has returned, and the calling identity is gone by then).
// Nothing here touches Android, so this compiles and is referenced from desktop.

namespace CircleAI.Linking;

/// <summary>Constants shared by the CircleAI link service and its client.</summary>
public static class LinkIpc
{
    /// <summary>
    /// Interface token — the client writes it, the service enforces it on every
    /// transaction, so a stray transact meant for another interface is refused.
    /// Bump the version suffix on a breaking wire change.
    /// </summary>
    public const string Descriptor = "com.bhengubv.circleai.link.v1";

    /// <summary>
    /// The protocol revision this build speaks, reported by <see cref="LinkVerb.Hello"/>.
    /// </summary>
    /// <remarks>
    /// NOT THE DESCRIPTOR, which only changes on a BREAKING change. Revision 2 added
    /// questions as records (AskSubmit/AskStatus/AskCancel/AskList), Status and
    /// Health; a client that reads 1, or gets "unknown verb" for Hello, uses the
    /// blocking <see cref="TransactAsk"/> as before.
    /// </remarks>
    public const int Protocol = 2;

    /// <summary>Transaction code: ask the shared brain one turn.</summary>
    public const int TransactAsk = 1;

    /// <summary>
    /// Transaction code: a structured verb beyond chat — recall / remember (memory),
    /// skills (the library), capabilities (discovery). The verb and its arguments
    /// ride the same flat string map as a turn; the verb name is a field
    /// (<see cref="LinkVerbCodec.KeyVerb"/>), so one code carries them all and the
    /// service dispatches on the verb. Each verb enforces its own scope.
    /// </summary>
    public const int TransactVerb = 2;

    /// <summary>
    /// Transaction code: audio in or out — transcribe what this app recorded, or
    /// speak some text with the device voice.
    /// </summary>
    /// <remarks>
    /// ITS OWN CODE RATHER THAN ANOTHER VERB, because the verb transaction carries a
    /// flat map of STRINGS and audio is bytes. Base64 through that map would inflate
    /// every clip by a third against a budget already measured in tens of seconds,
    /// and would hide the size check that has to happen before the transact. See
    /// <see cref="LinkAudio.MaxAudioBytes"/>.
    /// </remarks>
    public const int TransactAudio = 3;

    /// <summary>The intent action a client binds the link service by.</summary>
    public const string BindAction = "com.bhengubv.circleai.action.LINK";

    /// <summary>
    /// The intent action a client launches (FOR RESULT) to approve a link. The
    /// consent screen must run in Circle AI's app and be started by the foreground
    /// client, because a biometric sheet cannot be shown from a background service
    /// — and starting it for result is how the OS tells the consent screen, via
    /// getCallingPackage, which app is really asking.
    /// </summary>
    public const string ConsentAction = "com.bhengubv.circleai.action.LINK_CONSENT";

    /// <summary>Intent extra (int) carrying the requested <c>LinkScope</c> on a consent launch.</summary>
    public const string ScopeExtra = "com.bhengubv.circleai.extra.SCOPE";

    /// <summary>
    /// The intent action a client launches (FOR RESULT) to give Circle AI the
    /// microphone. Same shape as <see cref="ConsentAction"/> and for the same
    /// reasons: only an Activity can raise a runtime permission dialog, the service
    /// has no other screen, and starting it for result is how the OS says which app
    /// is asking. <c>Result.Ok</c> means the microphone is now allowed.
    /// </summary>
    public const string MicrophoneAction = "com.bhengubv.circleai.action.MICROPHONE";

    /// <summary>
    /// Intent extra (bool) on a <see cref="MicrophoneAction"/> launch: asked without
    /// anybody pressing anything, so the screen must not take the person to Settings
    /// when the phone will not ask again. Only a press earns that.
    /// </summary>
    public const string QuietExtra = "com.bhengubv.circleai.extra.QUIET";

    /// <summary>
    /// Intent extra (bool) on a <see cref="MicrophoneAction"/> launch: this launch is
    /// here to START LISTENING from a visible screen, not only to ask for the
    /// permission. The screen does its work and finishes without showing anything.
    /// </summary>
    /// <remarks>
    /// THE PERMISSION WAS NEVER THE WHOLE JOB. From Android 11 a foreground service
    /// started from the BACKGROUND is given no microphone - refused on 11, silenced on
    /// 12 and 13, and from 14 the microphone-typed start is rejected outright - and
    /// every start the service makes by itself is a background start: the resume when
    /// its process comes up, the ResidentStart verb, and the fetch's "your wake bundle
    /// just landed". So a phone could have the permission, the bundle and the owner's
    /// yes, and still hear silence while reporting that it was listening.
    /// <para>
    /// What the platform wants is a start made while an activity of the SERVICE's own
    /// package is up, which latches the while-in-use allowance for the life of the
    /// service record. This app has exactly one such activity, and it is the
    /// microphone screen - so a client that is in front and hears NeedsForeground
    /// launches it with this extra, and it starts the listener on its way through.
    /// Nothing is asked and nothing is shown: the permission is already held.
    /// </para>
    /// </remarks>
    public const string StartExtra = "com.bhengubv.circleai.extra.START_LISTENING";

    /// <summary>
    /// The package that hosts the CircleAI service a client binds to. A client
    /// resolves the service by this package plus <see cref="BindAction"/>.
    /// </summary>
    /// <remarks>
    /// THE CONSTANT MOVED, exactly as the note here anticipated. It named
    /// <c>com.bhengubv.circleai.hybrid</c> — the sample — so the shared brain on a
    /// device was something a person got by installing a demo, and any second app
    /// wanting it had to hope the demo was still installed. The host is now an app of
    /// its own with its own store listing, and the sample is one of its clients.
    /// <para>
    /// IT MUST MATCH the service head's <c>ApplicationId</c>
    /// (<c>samples/CircleAIService</c>). They are one fact: if they differ,
    /// <c>CircleAiLinkClient.IsInstalled</c> reads false on a device where the service
    /// is installed and running, no bind is ever attempted, and nothing reports an
    /// error — the client simply behaves as though CircleAI were absent.
    /// </para>
    /// <para>
    /// A client also needs a <c>&lt;queries&gt;</c> entry for this package in its own
    /// manifest, or Android 11+ package visibility hides it and
    /// <c>IsInstalled</c> lies in the same silent way.
    /// </para>
    /// </remarks>
    public const string HostPackage = "com.bhengubv.circleai.service";
}
