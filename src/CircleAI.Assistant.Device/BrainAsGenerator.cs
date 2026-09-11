// BrainAsGenerator.cs
//
// The device brain, presented as the raw generator a library expects.
//
// WHY A SHIM AND NOT A SECOND MODEL. LlmTranslationEngine takes an
// IChatGenerator - the inference-level interface that owns native model state -
// because it was written to sit directly on top of one. The device does not
// have a spare: DeviceBrain owns the single loaded model, serialises access to
// it behind a gate, and unloads it when the app goes idle. Handing the
// translation engine its own IChatGenerator would mean a SECOND copy of a
// 544 MB model on a phone with 3,7 GB, which is not a trade anybody would make
// to avoid twenty lines.
//
// So the engine gets the brain wearing the interface it asked for. Every call
// goes through the same gate, the same model and the same idle unload as a chat
// turn, because it IS a chat turn - it just has a translation in the prompt.
//
// ONLY THE TWO MEMBERS THAT ARE REQUIRED. IChatGenerator's other members are
// default interface methods, and the two that matter here - saving and loading
// a native session - are meaningless for something that does not own the model.
// Overriding them to lie would be worse than inheriting a default that does the
// honest thing.

using CircleAI.Inference;

namespace CircleAI.Assistant.Device;

/// <summary>Presents an <see cref="IBrain"/> as an <see cref="IChatGenerator"/>.</summary>
internal sealed class BrainAsGenerator(IBrain brain) : IChatGenerator
{
    private readonly IBrain _brain = brain ?? throw new ArgumentNullException(nameof(brain));

    /// <inheritdoc />
    public Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return _brain.AskAsync(Prompt(messages), null, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // IBrain streams through a CALLBACK and this interface streams through
        // an enumerator, so the pieces are queued as they arrive and drained as
        // the caller pulls. A channel rather than a lock and a list: the brain's
        // callback runs on the decode thread and must not be made to wait for a
        // consumer that is rendering.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        var work = Task.Run(async () =>
        {
            try
            {
                await _brain.AskAsync(Prompt(messages), p => channel.Writer.TryWrite(p), ct)
                            .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        await foreach (var piece in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return piece;

        await work.ConfigureAwait(false);
    }

    /// <summary>The last user message, which is the whole prompt here.</summary>
    /// <remarks>
    /// IBrain.AskAsync takes one string and keeps its own history; the engines
    /// that consume IChatGenerator build a single self-contained user message
    /// and expect no memory between calls. Flattening the roles into a
    /// transcript would put "user:" and "assistant:" labels into a translation
    /// prompt and get them translated.
    /// </remarks>
    private static string Prompt(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                return messages[i].Content;

        return messages.Count > 0 ? messages[^1].Content : string.Empty;
    }

    /// <inheritdoc />
    /// <remarks>
    /// NOTHING TO DISPOSE, AND DISPOSING THE BRAIN HERE WOULD BE A BUG. This
    /// shim borrows a model it does not own; unloading it because a translation
    /// finished would take the chat model out from under every other screen.
    /// </remarks>
    public void Dispose() { }
}
