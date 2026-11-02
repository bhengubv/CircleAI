// PicturesToABrainThatCanSeeTests.cs
//
// S9 ON THE P30, 2026-11-02: A QUESTION ABOUT A PHOTO THROUGH THE APP'S OWN PATH WAS ANSWERED BY A
// BRAIN THAT NEVER SAW IT. The picture rode the link, the queue and the worker wire intact, and the
// inference process - no router, so no second slot - handed it to the one brain it had loaded, the
// MNN text model, whose generator never reads a picture. It answered anyway. These pin both halves of
// the fix: the service routes a picture, and only a picture, to a brain that can see (SightRouter),
// and a brain that cannot see is never let answer about one (AIService.CannotSeeIt).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Hosting;
using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class PicturesToABrainThatCanSeeTests : IDisposable
{
    private readonly string _modelPath = Path.GetTempFileName();

    public void Dispose()
    {
        try { File.Delete(_modelPath); } catch { /* temp */ }
    }

    private static readonly ChatMessage[] Picture =
        [new("user", "What is in this picture?") { ImageBytes = [1, 2, 3] }];

    /// <summary>A generator that says whether it can see and records what it was handed.</summary>
    private sealed class Eyes(bool canSee) : IChatGenerator, ISeeingGenerator
    {
        public bool CanSee => canSee;
        public int Calls;
        public IReadOnlyList<ChatMessage>? Last;

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            Last = messages;
            return Task.FromResult("A red circle beside a blue square.");
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
                                                         [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            Last = messages;
            await Task.Yield();
            yield return "A red circle beside a blue square.";
        }

        public void Dispose() { }
    }

    // NO WARM-UP: it is one generation of its own at start, and these count what the brain was handed.
    private AIService Service(IChatGenerator generator) =>
        new(new AIOptions { ModelPath = _modelPath, WarmOnStart = false, SystemPrompt = "You are B!, a helpful on-device assistant." },
            generatorFactory: _ => generator);

    private static async Task<string> Joined(IAsyncEnumerable<string> pieces)
    {
        var all = new List<string>();
        await foreach (var piece in pieces) all.Add(piece);
        return string.Concat(all);
    }

    [Fact]
    public async Task A_picture_handed_to_a_brain_that_cannot_see_is_refused_in_words_and_never_answered()
    {
        // The MNN text generator's case: no ISeeingGenerator, so no eyes.
        var text = new FakeChatGenerator("A red circle.", ["A red ", "circle."]);
        await using var svc = Service(text);

        Assert.Equal(AIService.CannotSeeIt, await Joined(svc.StreamAsync(Picture)));
        Assert.Equal(AIService.CannotSeeIt, await svc.ChatAsync(Picture));
        var fragments = new List<ChatFragment>();
        await foreach (var fragment in svc.StreamFragmentsAsync(Picture)) fragments.Add(fragment);
        Assert.Equal(AIService.CannotSeeIt, string.Concat(fragments.Select(f => f.Text)));
        Assert.Equal(0, text.StreamCallCount);
        Assert.Equal(0, text.GenerateCallCount);

        // A question with no picture still reaches it.
        await svc.ChatAsync([new("user", "Hello?")]);
        Assert.Equal(1, text.GenerateCallCount);
    }

    [Fact]
    public async Task A_brain_that_can_see_is_handed_the_picture_and_one_whose_eyes_are_missing_is_not()
    {
        var seeing = new Eyes(canSee: true);
        await using (var svc = Service(seeing))
        {
            Assert.Equal("A red circle beside a blue square.", await Joined(svc.StreamAsync(Picture)));
            Assert.Equal(1, seeing.Calls);
            Assert.Contains(seeing.Last!, m => m.ImageBytes is { Length: 3 });
        }

        // The C# engine without its tower beside it: it says it cannot see, so it is not asked.
        var towerless = new Eyes(canSee: false);
        await using (var svc = Service(towerless))
        {
            Assert.Equal(AIService.CannotSeeIt, await Joined(svc.StreamAsync(Picture)));
            Assert.Equal(0, towerless.Calls);
        }
    }

    [Fact]
    public void Only_a_picture_is_routed_and_only_to_a_brain_that_can_see()
    {
        // NOT THE FULL CONCIERGE: long prompts and reasoning cues stay with the brain, so a phone
        // loads no model nobody planned for. A picture alone goes to a vision specialist.
        var router = new SightRouter();
        var picture = router.Route(new RouteContext("What is this?", HasImage: true));
        Assert.Equal(Organ.Specialist, picture.Organ);
        Assert.Equal(ChatCapability.Vision, picture.Capability);

        Assert.Equal(Organ.Generalist, router.Route(new RouteContext("Solve this equation step by step: 3x + 2 = 11")).Organ);
        Assert.Equal(Organ.Generalist, router.Route(new RouteContext(new string('a', 9000))).Organ);
    }

    [Fact]
    public void Both_hosts_route_pictures_and_the_generators_that_see_say_so()
    {
        var service = Source("src", "CircleAIService", "ServiceApplication.cs");
        Assert.Contains("Router = new CircleAI.Hosting.Neuron.SightRouter()", service, StringComparison.Ordinal);
        var desktop = Source("src", "CircleAIService", "Host", "Program.cs");
        Assert.Contains("Router                = new CircleAI.Hosting.Neuron.SightRouter()", desktop, StringComparison.Ordinal);

        Assert.True(typeof(ISeeingGenerator).IsAssignableFrom(typeof(KimiVlGenerator)));
        Assert.True(typeof(ISeeingGenerator).IsAssignableFrom(typeof(CircleAI.Inference.Managed.ManagedChatGenerator)));
        Assert.False(typeof(ISeeingGenerator).IsAssignableFrom(typeof(QwenTextGenerator)));
    }

    private static string Source(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "CircleAIService")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("the repository root was not found above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir, Path.Combine(parts)));
    }
}
