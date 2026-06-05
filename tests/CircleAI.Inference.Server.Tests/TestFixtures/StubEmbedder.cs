// StubEmbedder.cs
//
// Deterministic embedder used in tests — vectorises each input as the
// SHA-256 of its UTF-8 bytes truncated to 8 floats in [-1, 1].

using System.Security.Cryptography;
using System.Text;
using CircleAI.Embeddings;

namespace CircleAI.Inference.Server.Tests.TestFixtures;

public sealed class StubEmbedder : ITextEmbedder
{
    public Task<float[]> GenerateAsync(string text, CancellationToken ct = default)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        var vec  = new float[8];
        for (var i = 0; i < vec.Length; i++)
        {
            var byteIdx = i * 4;
            var word = BitConverter.ToUInt32(hash, byteIdx);
            vec[i] = (word / (float)uint.MaxValue) * 2f - 1f;
        }
        return Task.FromResult(vec);
    }
}
