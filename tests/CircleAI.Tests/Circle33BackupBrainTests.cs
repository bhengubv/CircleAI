// Circle33BackupBrainTests.cs
//
// (3.3.0) Tests for BackupBrainOrchestrator runtime LLM failover.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.CloudFallback;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class Circle33BackupBrainTests
{
    private static readonly IReadOnlyList<ChatMessage> Msgs = new[] { new ChatMessage("user", "hi") };

    [Fact]
    public async Task GenerateAsync_PrimaryAnswers_UsesPrimary()
    {
        var primary = new FakeBrain("primary", "p-answer");
        var backup  = new FakeBrain("backup",  "b-answer");
        var orch = new BackupBrainOrchestrator(new IChatGenerator[] { primary, backup });

        var result = await orch.GenerateAsync(Msgs);

        Assert.Equal("p-answer", result);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task GenerateAsync_PrimaryThrows_FallsBackToBackup()
    {
        var primary = new FakeBrain("primary", "x") { ThrowsOnGenerate = true };
        var backup  = new FakeBrain("backup",  "b-answer");
        var orch = new BackupBrainOrchestrator(new IChatGenerator[] { primary, backup });

        var result = await orch.GenerateAsync(Msgs);

        Assert.Equal("b-answer", result);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, backup.Calls);
    }

    [Fact]
    public async Task ConsecutiveFailures_MarkPrimaryDegraded()
    {
        var now = DateTimeOffset.UtcNow;
        var primary = new FakeBrain("primary", "x") { ThrowsOnGenerate = true };
        var backup  = new FakeBrain("backup",  "b-answer");
        var orch = new BackupBrainOrchestrator(
            new IChatGenerator[] { primary, backup },
            policy: new BackupBrainPolicy(DegradedAfterFailures: 2),
            clock:  () => now);

        await orch.GenerateAsync(Msgs);
        await orch.GenerateAsync(Msgs);

        var statuses = orch.Statuses;
        Assert.Equal(BrainHealth.Degraded, statuses[0].Health);
    }

    [Fact]
    public async Task DegradedPrimary_SkippedUntilCoolDownExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var primary = new FakeBrain("primary", "x") { ThrowsOnGenerate = true };
        var backup  = new FakeBrain("backup",  "b-answer");
        var orch = new BackupBrainOrchestrator(
            new IChatGenerator[] { primary, backup },
            policy: new BackupBrainPolicy(DegradedAfterFailures: 1, CoolDownDuration: TimeSpan.FromSeconds(5)),
            clock:  () => now);

        await orch.GenerateAsync(Msgs); // marks primary degraded.
        primary.Calls = 0;

        await orch.GenerateAsync(Msgs);
        Assert.Equal(0, primary.Calls); // skipped while in cool-down
        Assert.True(backup.Calls >= 1);

        now = now + TimeSpan.FromSeconds(6);
        primary.ThrowsOnGenerate = false; // primary now healthy
        primary.NextAnswer = "p-recovered";
        var r = await orch.GenerateAsync(Msgs);
        Assert.Equal("p-recovered", r);
    }

    [Fact]
    public async Task SuccessAfterFailures_ResetsCounter()
    {
        var primary = new FakeBrain("primary", "p-answer");
        var orch = new BackupBrainOrchestrator(
            new IChatGenerator[] { primary },
            policy: new BackupBrainPolicy(DegradedAfterFailures: 3));

        await orch.GenerateAsync(Msgs);
        var s = orch.Statuses;
        Assert.Equal(0, s[0].ConsecutiveFailures);
        Assert.Equal(BrainHealth.Healthy, s[0].Health);
    }

    [Fact]
    public async Task AllBrainsFail_ReturnsFallbackMessage()
    {
        var brains = new[]
        {
            new FakeBrain("a", "x") { ThrowsOnGenerate = true },
            new FakeBrain("b", "x") { ThrowsOnGenerate = true },
        };
        var orch = new BackupBrainOrchestrator(brains);

        var r = await orch.GenerateAsync(Msgs);
        Assert.Contains("All brains failed", r);
    }

    [Fact]
    public void Constructor_EmptyBrains_Throws()
    {
        Assert.Throws<ArgumentException>(() => new BackupBrainOrchestrator(Array.Empty<IChatGenerator>()));
    }

    [Fact]
    public async Task StreamAsync_PrimarySucceeds_StreamsAllChunks()
    {
        var primary = new FakeBrain("primary", "hello world", chunks: new[] { "hello ", "world" });
        var backup  = new FakeBrain("backup",  "ignored");
        var orch = new BackupBrainOrchestrator(new IChatGenerator[] { primary, backup });

        var collected = new List<string>();
        await foreach (var chunk in orch.StreamAsync(Msgs))
        {
            collected.Add(chunk);
        }

        Assert.Equal(new[] { "hello ", "world" }, collected);
        Assert.Equal(0, backup.Calls);
    }

    // ===== Fake brain =====

    private sealed class FakeBrain : IChatGenerator
    {
        public string Name { get; }
        public string NextAnswer { get; set; }
        public string[]? Chunks { get; }
        public int Calls;
        public bool ThrowsOnGenerate;

        public FakeBrain(string name, string answer, string[]? chunks = null)
        {
            Name = name; NextAnswer = answer; Chunks = chunks;
        }

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            if (ThrowsOnGenerate) throw new InvalidOperationException($"{Name} failing");
            return Task.FromResult(NextAnswer);
        }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            if (ThrowsOnGenerate) throw new InvalidOperationException($"{Name} failing");

            if (Chunks is not null)
            {
                foreach (var c in Chunks)
                {
                    await Task.Yield();
                    yield return c;
                }
            }
            else
            {
                await Task.Yield();
                yield return NextAnswer;
            }
        }

        public void Dispose() { }
    }
}
