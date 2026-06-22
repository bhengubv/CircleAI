// Contracts.cs
//
// (3.2.0) Peer-identity surface used by the multiplayer hub. Hosts
// implement IMultiplayerPeerIdentity to plug in whatever auth they
// have (TGN OIDC, anonymous Guest, etc.). Default impl returns a
// per-connection Guest identity.

namespace CircleAI.Hosting.Multiplayer;

/// <summary>
/// (3.2.0) Resolves the human-visible identity of the peer making a
/// hub call. Implementations typically pull from the active auth
/// context (e.g. <c>HttpContext.User</c>) or return a guest record.
/// </summary>
public interface IMultiplayerPeerIdentity
{
    /// <summary>Stable id (used to derive a colour).</summary>
    string PeerId { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
}

/// <summary>
/// (3.2.0) Anonymous guest identity. Hosts can register this directly
/// if no auth is configured.
/// </summary>
public sealed class GuestPeerIdentity : IMultiplayerPeerIdentity
{
    public GuestPeerIdentity(string? peerId = null, string? displayName = null)
    {
        PeerId      = peerId      ?? Guid.NewGuid().ToString("N");
        DisplayName = displayName ?? "Guest";
    }

    public string PeerId      { get; }
    public string DisplayName { get; }
}
