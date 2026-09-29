// ServiceAnnouncer.cs
//
// Announcing is the service's notification, not this app's.
//
// AndroidAnnouncer wrote into CircleNeuronService's notification — the same one
// whose content line discloses that the microphone is held. This app neither runs
// that service nor holds the microphone for listening, so it has no notification to
// write into and inventing one would put a second, quieter voice on the shade.

using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceAnnouncer : IAnnounces
{
    /// <inheritdoc />
    /// <remarks>
    /// Nothing. The screen a person is looking at is this app's own announcement; the
    /// shade belongs to the service that is actually doing the work.
    /// </remarks>
    public Task SayingAsync(string what, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Done() { }
}
