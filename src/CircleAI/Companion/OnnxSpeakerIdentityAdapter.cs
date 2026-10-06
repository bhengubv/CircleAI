// OnnxSpeakerIdentityAdapter.cs
//
// (Phase E5) Adapter exposing CircleAI.Voice's neural ECAPA-TDNN speaker
// embedder through the HER/Jarvis IVoiceIdentity contract. Kept here
// rather than in CircleAI.Voice so the Voice project doesn't have to
// depend on Companion's contracts.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;
using CircleAI.Voice;

namespace CircleAI.Companion;

public sealed class OnnxSpeakerIdentityAdapter : IVoiceIdentity
{
    private readonly ISpeakerIdentity _inner;

    public OnnxSpeakerIdentityAdapter(ISpeakerIdentity inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public ValueTask<string?> IdentifyAsync(
        ReadOnlyMemory<byte> audioPcm16,
        int sampleRateHz,
        CancellationToken ct = default)
        => _inner.IdentifyAsync(audioPcm16, sampleRateHz, ct);

    public ValueTask EnrollAsync(
        string userId,
        ReadOnlyMemory<byte> audioPcm16,
        int sampleRateHz,
        CancellationToken ct = default)
        => _inner.EnrollAsync(userId, audioPcm16, sampleRateHz, ct);
}
