// MultiplayerHub.cs
//
// (3.2.0) SignalR hub for live collaboration. Direct lift of CircleUp's
// CollabHub — "note" generalised to "document", auth via injected
// IMultiplayerPeerIdentity. Per-document group, LWW-by-rev edits, live
// cursors, presence.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace CircleAI.Hosting.Multiplayer;

/// <summary>
/// (3.2.0) Multiplayer collaboration hub. Mount at e.g.
/// <c>app.MapHub&lt;MultiplayerHub&gt;("/hubs/multiplayer")</c>.
///
/// Channels:
///   <list type="bullet">
///     <item><c>JoinDocument(docId)</c> — client subscribes to a per-doc group.</item>
///     <item><c>LeaveDocument(docId)</c> — client unsubscribes.</item>
///     <item><c>SendCursor(docId, line, ch)</c> — broadcasts cursor pos to peers.</item>
///     <item><c>SendEdit(docId, content, rev)</c> — broadcasts content + rev (LWW).</item>
///   </list>
///
/// Outgoing events to peers:
///   <list type="bullet">
///     <item><c>CursorChanged(connectionId, displayName, color, line, ch)</c></item>
///     <item><c>EditApplied(docId, content, rev, fromConnectionId)</c></item>
///     <item><c>PeerJoined / PeerLeft(docId, connectionId, displayName, color?)</c></item>
///   </list>
/// </summary>
public sealed class MultiplayerHub : Hub
{
    private readonly IMultiplayerPeerIdentity _peerIdentity;

    private static readonly ConcurrentDictionary<string, DocRevState> RevByDoc  = new();
    private static readonly ConcurrentDictionary<string, PeerState>   PeerByConn = new();

    public MultiplayerHub(IMultiplayerPeerIdentity peerIdentity)
    {
        _peerIdentity = peerIdentity ?? throw new ArgumentNullException(nameof(peerIdentity));
    }

    public override Task OnConnectedAsync()
    {
        PeerByConn[Context.ConnectionId] = new PeerState(
            ConnectionId: Context.ConnectionId,
            DisplayName:  _peerIdentity.DisplayName,
            Color:        ColourFor(_peerIdentity.PeerId),
            DocId:        null);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (PeerByConn.TryRemove(Context.ConnectionId, out var peer) &&
            !string.IsNullOrEmpty(peer.DocId))
        {
            await Clients.OthersInGroup(DocGroup(peer.DocId))
                .SendAsync("PeerLeft", peer.DocId, peer.ConnectionId, peer.DisplayName);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinDocument(string docId)
    {
        if (string.IsNullOrWhiteSpace(docId)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, DocGroup(docId));
        if (PeerByConn.TryGetValue(Context.ConnectionId, out var peer))
        {
            PeerByConn[Context.ConnectionId] = peer with { DocId = docId };
            await Clients.OthersInGroup(DocGroup(docId))
                .SendAsync("PeerJoined", docId, peer.ConnectionId, peer.DisplayName, peer.Color);
        }
    }

    public async Task LeaveDocument(string docId)
    {
        if (string.IsNullOrWhiteSpace(docId)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, DocGroup(docId));
        if (PeerByConn.TryGetValue(Context.ConnectionId, out var peer))
        {
            PeerByConn[Context.ConnectionId] = peer with { DocId = null };
            await Clients.OthersInGroup(DocGroup(docId))
                .SendAsync("PeerLeft", docId, peer.ConnectionId, peer.DisplayName);
        }
    }

    public Task SendCursor(string docId, int line, int ch)
    {
        if (!PeerByConn.TryGetValue(Context.ConnectionId, out var peer)) return Task.CompletedTask;
        return Clients.OthersInGroup(DocGroup(docId))
            .SendAsync("CursorChanged", peer.ConnectionId, peer.DisplayName, peer.Color, line, ch);
    }

    /// <summary>
    /// Apply an edit if its rev is greater than the server's current
    /// rev. Returns the new rev (or the server's current rev if the
    /// client's rev was stale).
    /// </summary>
    public async Task<long> SendEdit(string docId, string content, long rev)
    {
        var newRev = RevByDoc.AddOrUpdate(
            docId,
            _ => new DocRevState(Math.Max(rev, 1), DateTimeOffset.UtcNow),
            (_, prev) =>
            {
                if (rev <= prev.Rev) return prev;
                return new DocRevState(rev, DateTimeOffset.UtcNow);
            });

        if (newRev.Rev != rev)
        {
            // Rejected — client gets current rev back and can rebase.
            return newRev.Rev;
        }

        await Clients.OthersInGroup(DocGroup(docId))
            .SendAsync("EditApplied", docId, content, rev, Context.ConnectionId);
        return rev;
    }

    /// <summary>(3.2.0) Snapshot of who is currently in a document.</summary>
    public static IReadOnlyList<PeerState> Peers(string docId)
        => PeerByConn.Values
            .Where(p => string.Equals(p.DocId, docId, StringComparison.Ordinal))
            .ToList();

    /// <summary>(3.2.0) Current server-known rev for a document (0 if never touched).</summary>
    public static long CurrentRev(string docId)
        => RevByDoc.TryGetValue(docId, out var state) ? state.Rev : 0;

    /// <summary>(3.2.0) Test/admin hook — wipes static state. Do NOT call in production.</summary>
    public static void ResetStateForTesting()
    {
        RevByDoc.Clear();
        PeerByConn.Clear();
    }

    private static string DocGroup(string docId) => $"doc:{docId}";

    /// <summary>
    /// Stable hash → HSL hue, so each peer lands on a different cursor
    /// colour without a database column. Saturation + lightness fixed
    /// so the colour reads on both dark and light themes.
    /// </summary>
    internal static string ColourFor(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return "#5a4fcf";
        unchecked
        {
            var h = 0;
            foreach (var c in peerId) h = h * 31 + c;
            var hue = ((h % 360) + 360) % 360;
            return $"hsl({hue}, 70%, 55%)";
        }
    }

    public sealed record PeerState(string ConnectionId, string DisplayName, string Color, string? DocId);
    private sealed record DocRevState(long Rev, DateTimeOffset UpdatedAt);
}
