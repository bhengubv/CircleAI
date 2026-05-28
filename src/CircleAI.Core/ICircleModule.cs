namespace CircleAI.Core
{
    public interface ICircleModule : IDisposable
    {
        string ModuleName { get; }
        Task InitAsync(CircleEngine engine);
        bool IsModelLoaded { get; }
    }
}