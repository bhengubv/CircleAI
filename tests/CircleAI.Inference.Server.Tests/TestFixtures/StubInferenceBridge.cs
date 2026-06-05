// StubInferenceBridge.cs
//
// In-memory IInferenceBridge for endpoint integration tests. CompleteAsync
// echoes a deterministic reply; StreamCompletionAsync yields four chunks
// so the SSE writer is exercised end-to-end.

using System.Runtime.CompilerServices;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Inference.Server.Tests.TestFixtures;

public sealed class StubInferenceBridge : IInferenceBridge
{
    private readonly ModelDescriptor _descriptor;
    private readonly string _replyTemplate;

    public StubInferenceBridge(string modelId, string replyTemplate = "echo: {0}")
    {
        _descriptor    = new ModelDescriptor(
            ModelId: modelId,
            Version: "stub",
            Format: ModelFormat.Gguf,
            ContextWindowTokens: 4096,
            VocabSize: 32000,
            ParameterCount: 0L,
            QuantisationLabel: "Q4",
            ApproximateMemoryBytes: 1L * 1024 * 1024 * 1024);
        _replyTemplate = replyTemplate;
    }

    public Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelDescriptor>>(new[] { _descriptor });

    public Task<bool> IsModelLoadedAsync(string modelId, CancellationToken ct = default) =>
        Task.FromResult(string.Equals(_descriptor.ModelId, modelId, StringComparison.Ordinal));

    public Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
    {
        var output = string.Format(_replyTemplate, request.Prompt);
        return Task.FromResult(new InferenceResponse(
            RequestId: request.Id,
            ModelId: request.ModelId,
            OutputText: output,
            OutputTokenCount: Math.Max(1, output.Length / 4),
            PromptTokenCount: Math.Max(1, request.Prompt.Length / 4),
            Status: InferenceStatus.Completed,
            InferenceMillis: 0.5,
            FailureMessage: null,
            CompletedAt: DateTimeOffset.UtcNow));
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var chunk in new[] { "hello", " ", "from", " stub" })
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default) =>
        Task.FromResult(new DeviceCapabilities(
            OsName: "Test", OsVersion: "1",
            PhysicalMemoryBytes: 0, CpuCoreCount: 1,
            HasGpu: false, GpuName: null, GpuMemoryBytes: null,
            HasNpu: false, NpuName: null,
            HasTransportLayerEncryption: true));
}
