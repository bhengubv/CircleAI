// TogetherChatGenerator.cs
//
// (3.3.0) IChatGenerator backed by Together AI (OpenAI-compatible at /v1/chat/completions).

using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CircleAI.Hosting.CloudFallback;

public sealed class TogetherChatGenerator : OpenAiCompatibleChatGeneratorBase
{
    private readonly TogetherChatOptions _options;

    public TogetherChatGenerator(HttpClient http, TogetherChatOptions options, ILogger<TogetherChatGenerator>? logger = null)
        : base(http, options?.BaseAddress!, logger!)
    {
        _options = options ?? throw new System.ArgumentNullException(nameof(options));
    }

    public override string Id          => "together";
    public override string EngineLabel => $"Together · {_options.Model}";
    protected override string?  ApiKey             => _options.ApiKey;
    protected override string   Model              => _options.Model;
    protected override float    DefaultTemperature => _options.Temperature;
    protected override int      DefaultMaxTokens   => _options.MaxTokens;
}
