// IMicrophoneAccess.cs
//
// Whether this app may listen, asked of whoever can actually ask.
//
// WHY THIS IS A CONTRACT AND NOT A STATIC. The turn loop has to know before it
// opens a recorder, and on Android finding out can mean putting a dialog in
// front of somebody - which needs an Activity, a lifecycle and a screen. A
// library that runs the assistant has none of those and should not pretend to.
//
// It was a static helper calling into MAUI's permission API, and that one static
// was enough to keep the turn loop, the wake word and first-run setup inside a
// MAUI application project. The question - may I listen - belongs to the
// assistant. The asking belongs to the head.
//
// WHY IT MATTERS THAT THE ANSWER IS HONEST. Without the permission AudioRecord
// does not fail. It hands back silence, indefinitely, and a wake word listening
// to silence looks exactly like a wake word that does not work. That is the
// failure this interface exists to make impossible to skip.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Samples.It;

/// <summary>Whether the microphone may be opened, and asking if it may not.</summary>
public interface IMicrophoneAccess
{
    /// <summary>
    /// True when this app may record; asks for permission if it has not already.
    /// </summary>
    /// <remarks>
    /// ASKS, rather than only reporting. A turn that is about to listen wants the
    /// permission granted, not a description of why it cannot proceed - and the
    /// first turn somebody ever takes is exactly when it has not been asked yet.
    /// An implementation with no way to prompt returns whatever is already true.
    /// </remarks>
    Task<bool> GrantedAsync(CancellationToken ct = default);
}

/// <summary>
/// A head that cannot ask — the browser, a service, and every test.
/// </summary>
/// <remarks>
/// SAYS NO RATHER THAN YES. A null object that claimed the microphone was
/// available would send the turn loop on to open a recorder that hands back
/// silence, which is the failure above. Refusing is the honest answer for
/// something with no microphone to grant.
/// </remarks>
public sealed class NoMicrophone : IMicrophoneAccess
{
    /// <inheritdoc />
    public Task<bool> GrantedAsync(CancellationToken ct = default) => Task.FromResult(false);
}
