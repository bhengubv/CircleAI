# CircleAI.Hosting

DI composition + `IAIService` facade tying the on-device inference,
embeddings, memory, voice, and companion layers together for app hosts.

```bash
dotnet add package CircleAI.Hosting
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Hosting;

services.AddCircleAI(opts =>
{
    opts.ModelPath = "./qwen3-7b.gguf";
    opts.MaxOutputTokens = 512;
});

var ai = serviceProvider.GetRequiredService<IAIService>();
var reply = await ai.ChatAsync("Hello");
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
