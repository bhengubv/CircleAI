// NeuronVoiceTests.cs — the voice -> brain seam. VoiceCompanionListener forwards
// every transcription to ICompanionSession.SendAsync; this proves that call
// reaches the Neuron brain (an AIService) and comes back with an answer. The
// wake-word + STT + TTS stages are VoicePipeline's own (already-tested) concern.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Companion.Tests;

public sealed class NeuronVoiceTests
{
    /// <summary>Minimal generator that records that the brain was reached.</summary>
    private sealed class EchoGenerator : IChatGenerator
    {
        private readonly string _reply;
        public EchoGenerator(string reply) => _reply = reply;
        public bool WasCalled { get; private set; }

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> m, GenerationOptions? o = null, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(_reply);
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> m, GenerationOptions? o = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            WasCalled = true;
            await Task.Yield();
            yield return _reply;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task VoiceSend_ReachesNeuronBrain()
    {
        var modelPath = Path.GetTempFileName();
        try
        {
            var gen = new EchoGenerator("BRAINREPLY");
            var opts = new AIOptions { ModelPath = modelPath, WarmOnStart = false };
            await using var brain = new AIService(opts, generatorFactory: _ => gen);
            await brain.StartAsync();

            // Exactly what VoiceCompanionListener does on each transcription:
            // forward the utterance to a CompanionSession riding the Neuron brain.
            await using var session = new CompanionSession(
                "u", "User", default, preferredLanguage: null, ai: brain);
            var reply = await session.SendAsync("hey b, what's up?");

            Assert.True(gen.WasCalled);                       // voice -> companion -> Neuron brain
            Assert.False(string.IsNullOrWhiteSpace(reply));
        }
        finally { try { File.Delete(modelPath); } catch { /* best-effort */ } }
    }
}
