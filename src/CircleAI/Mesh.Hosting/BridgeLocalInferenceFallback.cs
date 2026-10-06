// BridgeLocalInferenceFallback.cs
//
// The missing serve-side adapter: it makes CircleAI.Mesh's ILocalInferenceFallback
// real by running a borrowed turn through the loaded model. Until this existed the
// only implementation was NullLocalInferenceFallback — every node could borrow a
// brain but none could serve one, so the whole offload engine was unreachable.

using CircleAI.Hosting.InferenceBridge;

namespace CircleAI.Mesh.Hosting;

/// <summary>
/// Serves an offloaded turn on THIS device by completing the borrower's prompt
/// through the raw <see cref="IInferenceBridge"/>.
/// </summary>
/// <remarks>
/// WHY THE RAW BRIDGE, NOT <c>IAIService</c>. <c>IAIService.AskAsync</c> /
/// <c>ChatAsync</c> enrich every answer with the serving node's own RAG memory,
/// persona and device context. That is correct for the owner's own turns and a
/// privacy leak for a peer's — the borrower asked a plain question and must get a
/// plain answer, never a window into the server-owner's memory.
/// <see cref="IInferenceBridge.CompleteAsync"/> is the bare prompt→completion the
/// offload contract was built to adapt (see CircleAI.Mesh <c>Contracts.cs</c>), so
/// nothing of this node rides back on the reply.
/// <para>
/// No-throw for serving failures (returns a failed <see cref="OffloadResult"/>, as
/// the interface asks); only cancellation propagates.
/// </para>
/// </remarks>
public sealed class BridgeLocalInferenceFallback : ILocalInferenceFallback
{
    private readonly IInferenceBridge _bridge;

    /// <param name="bridge">The device's loaded-model inference daemon.</param>
    public BridgeLocalInferenceFallback(IInferenceBridge bridge)
        => _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    /// <inheritdoc/>
    public async Task<OffloadResult> CompleteAsync(OffloadTurn turn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        try
        {
            // A faithful 1:1 map — every sampling knob carries across. Metadata is
            // deliberately empty: a served turn takes the prompt and nothing else.
            var request = new InferenceRequest(
                Guid.NewGuid(),
                turn.ModelId,
                turn.Prompt,
                turn.MaxOutputTokens,
                turn.Temperature,
                turn.TopP,
                turn.StopSequences,
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow);

            var response = await _bridge.CompleteAsync(request, ct).ConfigureAwait(false);

            // Completed / StoppedByToken / StoppedByLength all yielded usable text;
            // Failed / Cancelled did not.
            var produced = response.Status
                is InferenceStatus.Completed
                or InferenceStatus.StoppedByToken
                or InferenceStatus.StoppedByLength;

            if (!produced)
            {
                return OffloadResult.Fail(
                    response.FailureMessage ?? $"local inference {response.Status}",
                    OffloadServedBy.LocalFallback,
                    response.InferenceMillis);
            }

            return new OffloadResult(
                Success: true,
                OutputText: response.OutputText,
                ServedBy: OffloadServedBy.LocalFallback,
                ServingPeerId: null,
                OutputTokenCount: response.OutputTokenCount,
                ElapsedMilliseconds: response.InferenceMillis,
                FailureReason: null,
                ReasoningText: response.ReasoningText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OffloadResult.Fail(
                $"local inference failed: {ex.Message}", OffloadServedBy.LocalFallback);
        }
    }
}
