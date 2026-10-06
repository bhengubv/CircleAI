using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Embeddings
{
    public interface ITextEmbedder
    {
        Task<float[]> GenerateAsync(string text, CancellationToken ct = default);
    }
}