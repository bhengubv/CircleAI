// CerebrasChatGenerator.cs
//
// (3.3.0) IChatGenerator backed by Cerebras Inference API
// (OpenAI-compatible at /v1/chat/completions).

using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CircleAI.Hosting.CloudFallback;

public sealed class CerebrasChatGenerator : OpenAiCompatibleChatGeneratorBase
{
    private readonly CerebrasChatOptions _options;

    public CerebrasChatGenerator(HttpClient http, CerebrasChatOptions options, ILogger<CerebrasChatGenerator>? logger = null)
        : base(http, options?.BaseAddress!, logger!)
    {
        _options = options ?? throw new System.ArgumentNullException(nameof(options));
    }

    public override string Id          => "cerebras";
    public override string EngineLabel => $"Cerebras · {_options.Model}";
    protected override string?  ApiKey             => _options.ApiKey;
    protected override string   Model              => _options.Model;
    protected override float    DefaultTemperature => _options.Temperature;
    protected override int      DefaultMaxTokens   => _options.MaxTokens;
}
