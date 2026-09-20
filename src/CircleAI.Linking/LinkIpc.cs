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
    /// The package that hosts the CircleAI service a client binds to — the
    /// standalone host. A client resolves the service by this package + the
    /// action. (Today the hybrid app; if the standalone is split to its own
    /// package this is the one constant that moves.)
    /// </summary>
    public const string HostPackage = "com.bhengubv.circleai.hybrid";
}
