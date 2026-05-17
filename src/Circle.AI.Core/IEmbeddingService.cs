namespace Circle.AI.Core
{
    public interface IEmbeddingService : ICircleModule
    {
        float[] GenerateEmbedding(string text);
        int EmbeddingSize { get; }
    }
}