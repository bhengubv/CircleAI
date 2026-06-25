// Circle34UbiquityRailsRealTests.cs
//
// (3.4.0) Unit tests for the 15 upgraded Default* classes (now with real
// state instead of CompletedTask) and 7 newly-added Default* classes in
// UbiquityRailsMissingDefaults.cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Distribution.Ubiquity;
using Xunit;

namespace CircleAI.Tests;

public class Circle34UbiquityRailsRealTests
{
    // ── Upgraded defaults (in-place) ───────────────────────────────────

    [Fact]
    public async Task DefaultAiPersonalityWizard_RemembersSelection()
    {
        var w = new DefaultAiPersonalityWizard();
        await w.SelectAsync("sess-1", new PersonalityChoice("warm"));
        Assert.Equal("warm", w.Selected("sess-1")?.Name);
    }

    [Fact]
    public async Task DefaultAiPersonalityWizard_RejectsUnknownPersonality()
    {
        var w = new DefaultAiPersonalityWizard();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => w.SelectAsync("sess", new PersonalityChoice("snarky")).AsTask());
    }

    [Fact]
    public async Task DefaultOfflineQueuedOperation_QueuesAndDequeues()
    {
        var q = new DefaultOfflineQueuedOperation();
        await q.EnqueueAsync("{\"op\":\"sync\"}");
        Assert.Single(q.Pending);
        Assert.True(q.TryDequeue(out var op));
        Assert.Equal("{\"op\":\"sync\"}", op);
        Assert.Empty(q.Pending);
    }

    [Fact]
    public async Task DefaultSmsFallback_RecordsOutbox()
    {
        var s = new DefaultSmsFallback();
        await s.AnswerViaSmsAsync("+27821234567", "What's my balance?");
        Assert.Single(s.Sent);
        Assert.Equal("+27821234567", s.Sent[0].Phone);
    }

    [Fact]
    public async Task DefaultUssdFallback_NavigatesMenuTree()
    {
        var u = new DefaultUssdFallback();
        var root    = await u.RespondAsync("sess", "");
        var balance = await u.RespondAsync("sess", "1");
        var back    = await u.RespondAsync("sess", "0");
        Assert.Contains("CircleAI", root);
        Assert.Contains("Balance",  balance);
        Assert.Contains("CircleAI", back);
    }

    [Fact]
    public async Task DefaultWhatsAppIntegration_ValidatesE164AndRecords()
    {
        var w = new DefaultWhatsAppIntegration();
        await Assert.ThrowsAsync<ArgumentException>(
            () => w.SendAsync("not-a-phone", "hi").AsTask());
        await w.SendAsync("+27821234567", "hi");
        Assert.Single(w.Outbox);
    }

    [Fact]
    public async Task DefaultTelegramIntegration_RecordsOutbox()
    {
        var t = new DefaultTelegramIntegration();
        await t.SendAsync("chat-1", "hi");
        Assert.Single(t.Outbox);
    }

    [Fact]
    public async Task DefaultLostDeviceFlow_MarksDeviceWiped()
    {
        var f = new DefaultLostDeviceFlow();
        Assert.False(f.IsWiped("dev-1"));
        await f.RemoteWipeAsync("dev-1");
        Assert.True(f.IsWiped("dev-1"));
    }

    [Fact]
    public async Task DefaultInheritanceProtocol_StoresDesignee()
    {
        var p = new DefaultInheritanceProtocol();
        await p.DesignateAsync("owner-1", "heir-1");
        Assert.Equal("heir-1", p.DesigneeFor("owner-1"));
    }

    [Fact]
    public async Task DefaultInheritanceProtocol_RejectsSelfDesignation()
    {
        var p = new DefaultInheritanceProtocol();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.DesignateAsync("owner-1", "owner-1").AsTask());
    }

    [Fact]
    public async Task DefaultVerifiableWipe_ProducesSha256Certificate()
    {
        var w = new DefaultVerifiableWipe();
        var cert = await w.WipeAndCertifyAsync("owner-1");
        Assert.Equal(32, cert.Length);  // SHA-256 = 32 bytes
    }

    [Fact]
    public async Task DefaultDataPortabilityExport_ReturnsRealJsonBundle()
    {
        var e = new DefaultDataPortabilityExport();
        using var stream = await e.ExportAsync("owner-1");
        Assert.NotEqual(System.IO.Stream.Null, stream);
        using var reader = new StreamReader(stream);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("owner_id", body);
        Assert.Contains("owner-1", body);
    }

    [Fact]
    public async Task DefaultAccountCompromiseRecovery_TracksActiveState()
    {
        var r = new DefaultAccountCompromiseRecovery();
        await r.BeginAsync("owner-1");
        Assert.True(r.InRecovery("owner-1"));
        r.Complete("owner-1");
        Assert.False(r.InRecovery("owner-1"));
    }

    [Fact]
    public async Task DefaultImpairedUserMode_TracksEngagedUsers()
    {
        var m = new DefaultImpairedUserMode();
        await m.EngageAsync("user-1");
        Assert.True(m.IsEngaged("user-1"));
        await m.DisengageAsync("user-1");
        Assert.False(m.IsEngaged("user-1"));
    }

    [Fact]
    public void DefaultAbusiveEnvironmentMode_GeneratesDeterministicSafetyPhrase()
    {
        var m = new DefaultAbusiveEnvironmentMode();
        var phrase1 = m.SafetyPhrase("user-1");
        var phrase2 = m.SafetyPhrase("user-1");
        Assert.Equal(phrase1, phrase2);
        Assert.NotEqual(phrase1, m.SafetyPhrase("user-2"));
    }

    [Fact]
    public async Task DefaultQuietMode_TracksActiveWindow()
    {
        var q = new DefaultQuietMode();
        var now = DateTimeOffset.UtcNow;
        await q.EngageAsync("lunch", TimeSpan.FromMinutes(60));
        Assert.True(q.IsQuietAt(now.AddMinutes(30)));
        Assert.False(q.IsQuietAt(now.AddMinutes(120)));
        Assert.Single(q.ActiveWindows);
    }

    [Fact]
    public async Task DefaultPublicTransparency_StoresEvidenceLinks()
    {
        var t = new DefaultPublicTransparency();
        await t.LinkEvidenceAsync("90% private", new Uri("https://trust.circle.ai/audit"));
        Assert.Single(t.Linked);
    }

    // ── Newly-added missing-default classes ───────────────────────────

    [Fact]
    public async Task DefaultAppStoreSubmitter_AcceptsKnownStore()
    {
        var s = new DefaultAppStoreSubmitter();
        var ok = await s.SubmitAsync(new AppStorePackage(
            StoreName: "PlayStore", PackagePath: "/tmp/app.aab", Version: "1.0",
            Metadata: new Dictionary<string, string>()));
        Assert.True(ok);
        Assert.Single(s.Submitted);
    }

    [Fact]
    public async Task DefaultAppStoreSubmitter_RejectsUnknownStore()
    {
        var s = new DefaultAppStoreSubmitter();
        var ok = await s.SubmitAsync(new AppStorePackage(
            StoreName: "Pirate Bay", PackagePath: "x", Version: "1.0",
            Metadata: new Dictionary<string, string>()));
        Assert.False(ok);
    }

    [Fact]
    public async Task DefaultSignedDeltaUpdater_AppliesValidSignedUpdate()
    {
        var key = new byte[32];
        new Random(42).NextBytes(key);
        var u = new DefaultSignedDeltaUpdater(key);
        var payload = new byte[] { 1, 2, 3 };
        using var hmac = new System.Security.Cryptography.HMACSHA256(key);
        var msg = System.Text.Encoding.UTF8.GetBytes("stable|0|1.0|").Concat(payload).ToArray();
        var sig = hmac.ComputeHash(msg);
        var ok = await u.ApplyAsync(new DeltaUpdate("stable", "0", "1.0", payload, sig));
        Assert.True(ok);
        Assert.Equal("1.0", u.CurrentVersion("stable"));
    }

    [Fact]
    public async Task DefaultPhonePinBiometricOnboarding_HashesAndVerifies()
    {
        var o = new DefaultPhonePinBiometricOnboarding();
        var sess = await o.StartAsync("+27821234567");
        await o.CompleteAsync(sess.SessionId, "1234", biometricOk: true);
        Assert.True(o.VerifyPin("+27821234567", "1234"));
        Assert.False(o.VerifyPin("+27821234567", "9999"));
    }

    [Fact]
    public async Task DefaultVoiceLedSetup_AcceptsSupportedLanguages()
    {
        var s = new DefaultVoiceLedSetup();
        Assert.True(await s.RunAsync("en"));
        Assert.True(await s.RunAsync("zu"));
        Assert.False(await s.RunAsync("klingon"));
    }

    [Fact]
    public async Task DefaultPersonalDataImport_RejectsUnknownSource()
    {
        var i = new DefaultPersonalDataImport();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => i.ImportAsync("sess-1", "shady-source").AsTask());
    }

    [Fact]
    public async Task DefaultFamilyOnboarding_ValidatesRoles()
    {
        var f = new DefaultFamilyOnboarding();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.CreateHouseholdAsync("owner-1",
                new[] { new HouseholdMember("m1", "Alice", "supreme-leader") }).AsTask());
        await f.CreateHouseholdAsync("owner-1",
            new[] { new HouseholdMember("m1", "Alice", "parent") });
        Assert.Single(f.MembersOf("owner-1"));
    }

    [Fact]
    public async Task DefaultPerCallTransparency_RecordsAndReturnsReceipt()
    {
        var t = new DefaultPerCallTransparency();
        t.Record(new TransparencyReceipt("call-1", new[] { "search" }, new[] { "wiki.org" }, 0.01m));
        var r = await t.ReceiptFor("call-1");
        Assert.Single(r.ActionsTaken);
    }
}
