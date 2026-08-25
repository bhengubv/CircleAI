// BrowserProfile.cs
//
// The profile lives on the phone with the store that holds it.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// EMPTY, AND NOT WRITABLE. A name, a phone number and an employment history are
/// the most personal things this app holds; a browser build keeps no copy and
/// offers no way to enter one, rather than collecting them somewhere the promise
/// about staying on your device does not reach.
/// </remarks>
public sealed class BrowserProfile : IProfile
{
    /// <inheritdoc />
    public Task<Profile> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(new Profile([], 0));

    /// <inheritdoc />
    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task ForgetAsync(CancellationToken ct = default) => Task.CompletedTask;
}
