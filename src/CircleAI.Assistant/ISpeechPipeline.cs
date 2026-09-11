// ISpeechPipeline.cs
//
// Rendering and playing as two separate acts, so the next sentence can be
// rendered while this one is still being heard.
//
// A CAPABILITY, NOT A CHANGE TO IVoiceHost. SayAsync renders and plays in one
// call, which is the right shape for a greeting on the language screen and the
// wrong shape for a reply: with one call per sentence, sentence two cannot start
// rendering until sentence one has finished PLAYING, and on a phone where
// rendering runs slower than real time that leaves a gap between every
// sentence. A host that can split the two implements this as well; a host that
// cannot - the browser, which has no synthesiser to hand - simply does not, and
// the conversation falls back to one call per sentence. The same pattern as
// IReportsNearMisses: the interface everyone has stays small.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Samples.It;

/// <summary>A voice host that can render speech ahead of playing it.</summary>
public interface ISpeechPipeline
{
    /// <summary>
    /// Renders <paramref name="text"/> in <paramref name="tag"/> to something
    /// <see cref="PlayAsync"/> can play, or null when it could not be rendered.
    /// </summary>
    /// <remarks>
    /// Safe to call while a previous render's result is still playing - that is
    /// the whole point. What comes back is opaque to the caller: a file path on
    /// a phone, whatever else on another host.
    /// </remarks>
    Task<string?> RenderAsync(string tag, string text, CancellationToken ct = default);

    /// <summary>Plays one rendered item to the end, or until cancelled.</summary>
    Task PlayAsync(string rendered, CancellationToken ct = default);
}
