// Circle34HerJarvisRealTests.cs
//
// (3.4.0) Unit tests for the 24 real HER/Jarvis implementations added
// alongside the Null* defaults in HerJarvisRealImplementations.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;
using Xunit;

namespace CircleAI.Tests;

public class Circle34HerJarvisRealTests
{
    // 1. HeartbeatAlwaysOnPresence
    [Fact]
    public async Task HeartbeatAlwaysOnPresence_StartsAndStops()
    {
        var p = new HeartbeatAlwaysOnPresence(TimeSpan.FromMilliseconds(50));
        Assert.False(p.IsRunning);
        await p.StartAsync();
        Assert.True(p.IsRunning);
        await Eventually.TrueAsync(() => p.Heartbeats >= 2,
            "the 50 ms heartbeat to fire at least twice");
        Assert.True(p.Heartbeats >= 2);
        await p.StopAsync();
        Assert.False(p.IsRunning);
    }

    // 2. ChannelFusedPerception
    [Fact]
    public async Task ChannelFusedPerception_StreamsPublishedFrames()
    {
        var pub = new ChannelFusedPerception();
        pub.Publish(new FusedPercept(DateTimeOffset.UtcNow, Vision: "frame1", Audio: null, Text: null,
            Sensors: new Dictionary<string, double>()));
        pub.Complete();

        var received = new List<FusedPercept>();
        await foreach (var f in pub.StreamAsync()) received.Add(f);
        Assert.Single(received);
        Assert.Equal("frame1", received[0].Vision);
    }

    // 3. JsonIdentitySync
    [Fact]
    public async Task JsonIdentitySync_PullReturnsDeltasSinceCursor()
    {
        var s = new JsonIdentitySync();
        await s.PushAsync("{\"k\":\"v1\"}");
        await s.PushAsync("{\"k\":\"v2\"}");
        var json = await s.PullAsync("0");
        Assert.Contains("v1", json);
        Assert.Contains("v2", json);
        Assert.Contains("cursor", json);
    }

    // 4. EwaContinuousLearner
    [Fact]
    public async Task EwaContinuousLearner_AveragesRewards()
    {
        var l = new EwaContinuousLearner(alpha: 0.5);
        await l.RegisterFeedbackAsync("turn-1", 1.0, "{}");
        await l.RegisterFeedbackAsync("turn-1", 0.0, "{}");
        Assert.Equal(0.5, l.AverageRewardOf("turn-1"));
        Assert.Equal(2, l.ObservationsOf("turn-1"));
    }

    // 5. FrequencyWorldModel
    [Fact]
    public async Task FrequencyWorldModel_PicksMostFrequentOutcome()
    {
        var m = new FrequencyWorldModel();
        m.Observe(new[] { "rainy=true" }, "umbrella");
        m.Observe(new[] { "rainy=true" }, "umbrella");
        m.Observe(new[] { "rainy=true" }, "boots");
        var pred = await m.PredictAsync("{\"rainy\":true}");
        Assert.Equal("umbrella", pred.Outcome);
        Assert.True(pred.Probability >= 0.5);
    }

    // 6. InMemoryGoalPursuer
    [Fact]
    public async Task InMemoryGoalPursuer_RegistersAndReplans()
    {
        var g = new InMemoryGoalPursuer();
        var goal = await g.RegisterAsync("Ship MVP", DateTimeOffset.UtcNow.AddDays(30));
        Assert.Contains("milestones", goal.PlanJson);
        var fetched = await g.CurrentAsync(goal.Id);
        Assert.NotNull(fetched);
        await g.ReplanAsync(goal.Id);
        var replanned = await g.CurrentAsync(goal.Id);
        Assert.Contains("milestones", replanned!.PlanJson);
    }

    // 7. TfEpisodicMemory
    [Fact]
    public async Task TfEpisodicMemory_RecallsBySimilarity()
    {
        var m = new TfEpisodicMemory();
        await m.RecordAsync(new EpisodeRecord("e1", DateTimeOffset.UtcNow, "Coffee with Alice", "{\"loc\":\"cafe\"}"));
        await m.RecordAsync(new EpisodeRecord("e2", DateTimeOffset.UtcNow, "Dinner with Bob",   "{\"loc\":\"home\"}"));
        var hits = await m.RecallAsync("Alice");
        Assert.Single(hits);
        Assert.Equal("e1", hits[0].Id);
    }

    // 8. EnergyBandVoiceIdentity (MFCC)
    [Fact]
    public async Task EnergyBandVoiceIdentity_IdentifiesEnrolledVoice()
    {
        var v = new EnergyBandVoiceIdentity();
        var audio = SynthVoice(220, 16000, 1000);  // 1s sine at 220Hz
        await v.EnrollAsync("alice", audio, 16000);
        var match = await v.IdentifyAsync(audio, 16000);
        Assert.Equal("alice", match);
    }

    // 9. HistoricalCalibratedConfidence
    [Fact]
    public async Task HistoricalCalibratedConfidence_ReturnsBand()
    {
        var c = new HistoricalCalibratedConfidence();
        var band = await c.EvaluateAsync("definitive answer with citation", "{\"src\":\"doc\"}");
        Assert.InRange(band.Lower, 0.0, 1.0);
        Assert.InRange(band.Upper, band.Lower, 1.0);
    }

    // 10. BeliefTrackerTheoryOfMind
    [Fact]
    public async Task BeliefTrackerTheoryOfMind_ExtractsBeliefs()
    {
        var t = new BeliefTrackerTheoryOfMind();
        var est = await t.EstimateAsync("alice", "Alice thinks the deadline is friday. She wants the report early.");
        Assert.True(est.Confidence > 0);
        Assert.Contains("deadline", est.LikelyBeliefJson, StringComparison.OrdinalIgnoreCase);
    }

    // 11. KeywordEmotionSensor
    [Fact]
    public async Task KeywordEmotionSensor_DetectsLabelledEmotion()
    {
        var e = new KeywordEmotionSensor();
        var frame = await e.SenseAsync("user feels happy and excited about the news");
        Assert.Equal("joy", frame.Label);
        Assert.True(frame.Valence > 0);
    }

    // 12. DemoStoreSkillAcquisition
    [Fact]
    public async Task DemoStoreSkillAcquisition_StoresAndLists()
    {
        var s = new DemoStoreSkillAcquisition();
        var skill = await s.AcquireAsync("{\"name\":\"send-tea\",\"steps\":[]}");
        Assert.Equal("send-tea", skill.Name);
        var all = await s.ListAsync();
        Assert.Single(all);
    }

    // 13. TemplateInnerMonologue
    [Fact]
    public async Task TemplateInnerMonologue_ProducesReflection()
    {
        var m = new TemplateInnerMonologue();
        var r = await m.ReflectAsync("{\"user\":\"asks for help\"}");
        Assert.NotEmpty(r.Thought);
    }

    // 14. HistogramPredictiveEngine
    [Fact]
    public async Task HistogramPredictiveEngine_AnticipatesObservedNeed()
    {
        var p = new HistogramPredictiveEngine();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) p.Observe("morning-coffee", now);
        var hits = await p.AnticipateAsync(horizonMinutes: 60);
        Assert.Single(hits);
        Assert.Equal("morning-coffee", hits[0].Description);
    }

    // 15. AdjacencyPersonalKnowledgeGraph
    [Fact]
    public async Task AdjacencyPersonalKnowledgeGraph_TraversesNeighbours()
    {
        var g = new AdjacencyPersonalKnowledgeGraph();
        await g.UpsertNodeAsync(new KnowledgeNode("alice", "person", "Alice", new Dictionary<string, string>()));
        await g.UpsertNodeAsync(new KnowledgeNode("acme",  "company", "Acme", new Dictionary<string, string>()));
        await g.UpsertRelationAsync(new KnowledgeRelation("alice", "acme", "works-at"));
        var ns = await g.NeighboursAsync("alice");
        Assert.Single(ns);
        Assert.Equal("acme", ns[0].Id);
    }

    // 16. TopicLiveWorldKnowledge
    [Fact]
    public async Task TopicLiveWorldKnowledge_DeliversPublishedFacts()
    {
        var k = new TopicLiveWorldKnowledge();
        using var cts = new CancellationTokenSource();
        var received = new List<WorldFact>();
        var task = Task.Run(async () =>
        {
            await foreach (var f in k.SubscribeAsync(new[] { "weather" }, cts.Token))
            {
                received.Add(f);
                if (received.Count >= 1) break;
            }
        });
        // SubscribeAsync attaches asynchronously, so a single Publish can land before
        // anyone is listening and the fact is simply lost — the 100 ms sleep was
        // betting the attach won that race. Republish until it is heard: this
        // consumer breaks after the first fact, so repeats cannot inflate the count.
        await Eventually.TrueAsync(
            () =>
            {
                k.Publish(new WorldFact("weather", "{\"temp\":25}", DateTimeOffset.UtcNow));
                return task.IsCompleted;
            },
            "the subscriber to attach and receive a published fact");
        await Eventually.CompletesAsync(task, "the subscription loop to finish");
        cts.Cancel();
        Assert.Single(received);
    }

    // 17. ChannelBioSignalStream
    [Fact]
    public async Task ChannelBioSignalStream_StreamsPublishedSignals()
    {
        var b = new ChannelBioSignalStream();
        b.Publish(new BioSignal("hr", 72, DateTimeOffset.UtcNow));
        b.Complete();
        var hits = new List<BioSignal>();
        await foreach (var s in b.StreamAsync()) hits.Add(s);
        Assert.Single(hits);
        Assert.Equal(72, hits[0].Value);
    }

    // 18. RegistryPhysicalActuator
    [Fact]
    public async Task RegistryPhysicalActuator_DispatchesToRegisteredHandler()
    {
        var a = new RegistryPhysicalActuator();
        a.RegisterDevice("lamp", (cmd, _) => ValueTask.FromResult(new PhysicalCommandResult(true, null)));
        var r = await a.InvokeAsync(new PhysicalCommand("lamp", "on", new Dictionary<string, string>()));
        Assert.True(r.Succeeded);
    }

    [Fact]
    public async Task RegistryPhysicalActuator_FailsCleanlyForUnknownDevice()
    {
        var a = new RegistryPhysicalActuator();
        var r = await a.InvokeAsync(new PhysicalCommand("ghost", "on", new Dictionary<string, string>()));
        Assert.False(r.Succeeded);
        Assert.Contains("Unknown", r.Error);
    }

    // 19. MailboxAgentPeerNetwork
    [Fact]
    public async Task MailboxAgentPeerNetwork_DeliversToRecipient()
    {
        var net = new MailboxAgentPeerNetwork();
        await net.SendAsync(new AgentToAgentMessage("alice", "bob", "hi", DateTimeOffset.UtcNow));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var m in net.ReceiveAsync("bob", cts.Token))
        {
            Assert.Equal("hi", m.Payload);
            return;
        }
        Assert.Fail("no message received");
    }

    // 20. InMemoryFederatedFineTuner
    [Fact]
    public async Task InMemoryFederatedFineTuner_RunsTrainingToCompletion()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllLines(tempFile, new[] { "a", "b", "c" });
            var t = new InMemoryFederatedFineTuner();
            var jobId = await t.StartAsync("base-model", tempFile);
            // 50 polls x 50 ms was a 2.5 s budget — a scheduling guess like any other.
            await Eventually.TrueAsync(
                async () => (await t.StatusAsync(jobId)).Progress >= 1.0,
                "the in-memory fine-tune job to reach 100%");
            Assert.Null((await t.StatusAsync(jobId)).Error);
        }
        finally { System.IO.File.Delete(tempFile); }
    }

    // 21. SlidingP50FirstTokenOptimizer
    [Fact]
    public async Task SlidingP50FirstTokenOptimizer_TracksMedianLatency()
    {
        var o = new SlidingP50FirstTokenOptimizer(targetMs: 100, windowSize: 5);
        foreach (var ms in new[] { 50, 60, 70, 80, 90 }) o.RecordFirstTokenLatency(ms);
        var budget = await o.CurrentAsync();
        Assert.Equal(100, budget.TargetMs);
        Assert.Equal(70, budget.CurrentP50Ms);
    }

    // 22. EcdsaCryptoDelegation
    [Fact]
    public void EcdsaCryptoDelegation_IssueAndVerifyRoundTrip()
    {
        using var c = new EcdsaCryptoDelegation("test-issuer");
        var cred = c.Issue("user-1", "read:memory", TimeSpan.FromMinutes(5));
        Assert.True(c.Verify(cred));
    }

    [Fact]
    public void EcdsaCryptoDelegation_TamperedCredentialIsRejected()
    {
        using var c = new EcdsaCryptoDelegation("test-issuer");
        var cred = c.Issue("user-1", "read:memory", TimeSpan.FromMinutes(5));
        var tampered = cred with { Scope = "admin:everything" };
        Assert.False(c.Verify(tampered));
    }

    // 23. SyntaxCheckingCodeGenerationLoop
    [Fact]
    public async Task SyntaxCheckingCodeGenerationLoop_AcceptsBalancedSnippet()
    {
        var loop = new SyntaxCheckingCodeGenerationLoop(
            generator: (p, _) => ValueTask.FromResult("public class X { void M() {} }"));
        var job = await loop.RunAsync("write a class");
        Assert.True(job.TestsPass);
    }

    [Fact]
    public async Task SyntaxCheckingCodeGenerationLoop_RejectsUnbalancedSnippet()
    {
        var loop = new SyntaxCheckingCodeGenerationLoop(
            generator: (p, _) => ValueTask.FromResult("public class X { void M() {"));
        var job = await loop.RunAsync("write a class");
        Assert.False(job.TestsPass);
    }

    // 24. TrackingSelfImprovementLoop
    [Fact]
    public async Task TrackingSelfImprovementLoop_TracksNewBestScore()
    {
        var loop = new TrackingSelfImprovementLoop(
            runBench: (_, _) => ValueTask.FromResult(0.75));
        var verdict = await loop.CycleAsync("bench-1");
        Assert.Equal(0.75, verdict.NewBenchScore);
        Assert.Equal(0.75, loop.BestScoreFor("bench-1"));
    }

    // ── Helpers ────────────────────────────────────────────────────────
    private static byte[] SynthVoice(double freqHz, int sampleRateHz, int durationMs)
    {
        var samples = sampleRateHz * durationMs / 1000;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * freqHz * i / sampleRateHz) * 16000);
            pcm[i * 2]     = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }
}
