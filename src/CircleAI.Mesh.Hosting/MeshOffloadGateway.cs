// MeshOffloadGateway.cs
//
// The borrow side, wired to the mesh: when this phone cannot run the chat model
// itself and the person has turned borrowing on and named a trusted node, it
// borrows an answer from that node. The browser-safe seam is IOffloadGateway
// (CircleAI.Assistant); this is the phone's implementation over the offload
// engine — the counterpart to BridgeLocalInferenceFallback on the serve side.
//
// It takes the local-chat status as a delegate rather than IAIService directly:
// the "can this phone serve chat, and with which model" decision is product logic
// that lives in the DI extension (over IAIService.PlanFor), and the gateway stays
// a small, delegate-driven policy that a unit test can drive without a model host.

using CircleAI.Assistant;   // IOffloadGateway, OffloadConsent

namespace CircleAI.Mesh.Hosting;

/// <summary>Whether this device can run the chat model right now, and the model it would use.</summary>
/// <param name="CanServe">True when the local device can run chat itself (no borrow needed).</param>
/// <param name="IntendedModelId">The chat model the device would use, when one is catalogued; else null.</param>
public readonly record struct LocalChatStatus(bool CanServe, string? IntendedModelId);

/// <summary>Tunables for <see cref="MeshOffloadGateway"/>.</summary>
public sealed class MeshOffloadGatewayOptions
{
    /// <summary>How long to wait for the chosen node's answer. Default 30s.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Advisory model id sent when no chat model is catalogued on this device. A
    /// modality marker, not a specific model to match — the peer runs whatever chat
    /// model it has loaded (the serve side may downshift), so this never pins a
    /// version and never hardcodes a real model name.
    /// </summary>
    public string FallbackModelId { get; set; } = "chat";
}

/// <summary>
/// Borrows a chat answer from a hand-picked trusted node when this phone cannot
/// run the chat model itself.
/// </summary>
public sealed class MeshOffloadGateway : IOffloadGateway
{
    private readonly IMeshOffloadClient _client;
    private readonly Func<LocalChatStatus> _localChat;
    private readonly Func<OffloadConsent> _consent;
    private readonly MeshOffloadGatewayOptions _options;

    /// <param name="client">The mesh transport half of the offload engine.</param>
    /// <param name="localChat">Reads whether this phone can serve chat, and its model.</param>
    /// <param name="consent">Reads the CURRENT consent each turn (on/off + chosen node).</param>
    /// <param name="options">Timeout and fallback-model tunables.</param>
    public MeshOffloadGateway(
        IMeshOffloadClient client,
        Func<LocalChatStatus> localChat,
        Func<OffloadConsent> consent,
        MeshOffloadGatewayOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _localChat = localChat ?? throw new ArgumentNullException(nameof(localChat));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _options = options ?? new MeshOffloadGatewayOptions();
    }

    /// <inheritdoc/>
    public async Task<string?> TryBorrowAsync(string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;

        // Off, or no node chosen — the person has not opted in.
        var consent = _consent();
        if (!consent.CanBorrow) return null;

        // Borrow ONLY when this phone cannot run the chat model itself; borrowing a
        // brain you already have is pointless.
        var local = _localChat();
        if (local.CanServe) return null;

        // The RAW question only — nothing composed, nothing of this phone's memory.
        var modelId = local.IntendedModelId ?? _options.FallbackModelId;
        var turn = OffloadTurn.Create(modelId, question);

        try
        {
            var result = await _client
                .RequestAsync(consent.ChosenNodeId!, turn, _options.RequestTimeout, ct)
                .ConfigureAwait(false);

            // A failed borrow returns null so the turn falls back to the local head's
            // own "can't serve" handling, rather than dead-ending here.
            return result.Success ? result.OutputText : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
