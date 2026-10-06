namespace CircleAI.Core
{
    public interface IEmbeddingService : ICircleModule
    {
        float[] GenerateEmbedding(string text);
        int EmbeddingSize { get; }
    }
}