// MnnWorkingMemoryTests.cs
//
// OPEN-GAPS A11: TurboQuant working memory on MNN brains was built end to end - the bridge maps the
// mode onto MNN's own attention_mode - and never switched on. QwenTextGenerator now sets the mode
// before load. This measures what 4 bits do to the brain the P30 runs, Qwen3.5-0.8B-MNN, here on the
// desktop bridge: the same questions, greedily, with the working memory off and at 4 bits.
//
// Measured 2026-11-01: off, six answers the same twice over; at 4 bits, not one of six the same, and
// most worse - "seven times eight" came back "7", two answers fell into loops. So an MNN brain loads
// with its working memory OFF unless a caller names another mode, and this test holds that default.
//
// Needs the 0.8B under %LOCALAPPDATA%\CircleAI\models (says SKIPPED otherwise) and the win-x64 MNN
// bridge; opt in with CIRCLEAI_MNN_MEASURE=1, since it loads the model twice.

using CircleAI.Core;
using CircleAI.Inference;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class MnnWorkingMemoryTests(ITestOutputHelper output)
{
    private static readonly string[] Questions =
    [
        "What is the capital of France?",
        "Name three colours of the rainbow.",
        "What is seven times eight?",
        "Write one sentence about the sea.",
        "Who wrote Romeo and Juliet?",
        "Give me a word that rhymes with cat.",
    ];

    [Fact]
    public async Task An_MNN_brain_keeps_its_working_memory_off_because_4_bits_cost_the_0_8B_its_answers()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CircleAI", "models", "Qwen3.5-0.8B-MNN", "config.json");
        if (Environment.GetEnvironmentVariable("CIRCLEAI_MNN_MEASURE") != "1" || !OperatingSystem.IsWindows() || !File.Exists(config))
        {
            output.WriteLine("SKIPPED: set CIRCLEAI_MNN_MEASURE=1, on Windows, with Qwen3.5-0.8B-MNN in the model store.");
            return;
        }

        // MNN reads its sampler at load: greedy, so two runs can be told apart only by the memory.
        var sampler = QwenTextGenerator.SamplerJsonForTest;
        QwenTextGenerator.SamplerJsonForTest = """{"sampler_type":"greedy"}""";
        try
        {
            var off = await Answers(config, null); // the default, which is Off
            var offAgain = await Answers(config, KvCompressionMode.Off);
            var quarter = await Answers(config, KvCompressionMode.TurboQuant4Bit);
            int stable = 0, same = 0;
            for (var i = 0; i < Questions.Length; i++)
            {
                if (off[i] == offAgain[i]) stable++;
                if (off[i] == quarter[i]) same++;
                output.WriteLine($"{Questions[i]}\n  off:    {off[i]}\n  4 bits: {quarter[i]}");
            }
            output.WriteLine($"off twice: {stable} of {Questions.Length} alike; off against 4 bits: {same} of {Questions.Length} word for word the same");
            Assert.Equal(Questions.Length, stable); // otherwise the comparison measures the sampler, not the memory
            Assert.All(quarter, a => Assert.False(string.IsNullOrWhiteSpace(a)));
        }
        finally
        {
            QwenTextGenerator.SamplerJsonForTest = sampler;
        }
    }

    private async Task<string[]> Answers(string config, KvCompressionMode? mode)
    {
        using var generator = new QwenTextGenerator(config, contextSize: 1024, threads: null, templateEngine: null, kvMode: mode);
        Assert.Equal(mode ?? KvCompressionMode.Off, generator.KvMode);
        var answers = new string[Questions.Length];
        for (var i = 0; i < Questions.Length; i++)
        {
            answers[i] = (await generator.GenerateAsync([new ChatMessage("user", Questions[i])],
                new GenerationOptions { Temperature = 0, MaxTokens = 40, Budget = PowerBudget.None })).Trim();
        }
        return answers;
    }
}
