// LinkingTests.cs
//
// The cross-app link authorization core: who may use the shared brain, and how
// the decision (LinkGate) and the act (LinkAuthorizer) stay testable without a
// phone. The one Android-shaped thing — the fingerprint/PIN prompt — is a
// callback here, so a test says "approved" or "declined" and the mint logic runs
// in full.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class LinkingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private const string FirstParty = "GEEK-SIGNING-KEY";
    private const string ThirdParty = "SOME-OTHER-KEY";

    private static (LinkGate Gate, ILinkGrantStore Store) NewGate()
    {
        var store = new InMemoryLinkGrantStore();
        var firstParty = new HashSet<string>(StringComparer.Ordinal) { FirstParty };
        return (new LinkGate(store, firstParty), store);
    }

    private static readonly Func<CancellationToken, Task<bool>> Approve = _ => Task.FromResult(true);
    private static readonly Func<CancellationToken, Task<bool>> Decline = _ => Task.FromResult(false);

    // ---- store ----

    [Fact]
    public async Task Store_saves_finds_lists_and_revokes()
    {
        var store = new InMemoryLinkGrantStore();
        var g = new LinkGrant("com.app.a", ThirdParty, LinkScope.Chat, Now);
        await store.SaveAsync(g);

        Assert.Equal(g, await store.FindAsync("com.app.a"));
        Assert.Single(await store.ListAsync());

        await store.RevokeAsync("com.app.a");
        Assert.Null(await store.FindAsync("com.app.a"));
        Assert.Empty(await store.ListAsync());
    }

    // ---- grant predicates ----

    [Theory]
    [InlineData(LinkScope.Chat, LinkScope.Chat, true)]
    [InlineData(LinkScope.Chat | LinkScope.Memory, LinkScope.Chat, true)]
    [InlineData(LinkScope.Chat, LinkScope.Memory, false)]
    public void Grant_covers_is_a_subset_check(LinkScope have, LinkScope want, bool covers)
        => Assert.Equal(covers, new LinkGrant("p", "s", have, Now).Covers(want));

    [Fact]
    public void Grant_is_live_until_its_expiry_and_forever_when_null()
    {
        var expiring = new LinkGrant("p", "s", LinkScope.Chat, Now, Now.AddHours(1));
        Assert.True(expiring.IsLiveAt(Now));
        Assert.False(expiring.IsLiveAt(Now.AddHours(2)));

        var forever = new LinkGrant("p", "s", LinkScope.Chat, Now);
        Assert.True(forever.IsLiveAt(Now.AddYears(10)));
    }

    // ---- gate: the decision ----

    [Fact]
    public async Task Gate_denies_bad_requests()
    {
        var (gate, _) = NewGate();
        Assert.Equal(LinkDecisionKind.Deny, (await gate.DecideAsync(new LinkRequest("", "", LinkScope.Chat), Now)).Kind);
        Assert.Equal(LinkDecisionKind.Deny, (await gate.DecideAsync(new LinkRequest("com.app", "", LinkScope.Chat), Now)).Kind);
        Assert.Equal(LinkDecisionKind.Deny, (await gate.DecideAsync(new LinkRequest("com.app", ThirdParty, LinkScope.None), Now)).Kind);
    }

    [Fact]
    public async Task Gate_first_party_auto_approves_without_a_grant()
    {
        var (gate, _) = NewGate();
        var d = await gate.DecideAsync(new LinkRequest("com.geek.app", FirstParty), Now);
        Assert.Equal(LinkDecisionKind.AutoApprove, d.Kind);
    }

    [Fact]
    public async Task Gate_third_party_needs_auth_without_a_grant()
    {
        var (gate, _) = NewGate();
        var d = await gate.DecideAsync(new LinkRequest("com.other.app", ThirdParty), Now);
        Assert.Equal(LinkDecisionKind.NeedAuth, d.Kind);
    }

    [Fact]
    public async Task Gate_allows_when_a_live_grant_covers_the_request()
    {
        var (gate, store) = NewGate();
        await store.SaveAsync(new LinkGrant("com.other.app", ThirdParty, LinkScope.Chat, Now));
        var d = await gate.DecideAsync(new LinkRequest("com.other.app", ThirdParty, LinkScope.Chat), Now);
        Assert.Equal(LinkDecisionKind.Allow, d.Kind);
    }

    [Fact]
    public async Task Gate_denies_a_signature_mismatch_impostor()
    {
        var (gate, store) = NewGate();
        await store.SaveAsync(new LinkGrant("com.bank.app", "REAL-KEY", LinkScope.Chat, Now));
        var d = await gate.DecideAsync(new LinkRequest("com.bank.app", "IMPOSTOR-KEY", LinkScope.Chat), Now);
        Assert.Equal(LinkDecisionKind.Deny, d.Kind);
    }

    [Fact]
    public async Task Gate_needs_auth_on_scope_escalation()
    {
        var (gate, store) = NewGate();
        await store.SaveAsync(new LinkGrant("com.other.app", ThirdParty, LinkScope.Chat, Now));
        var d = await gate.DecideAsync(
            new LinkRequest("com.other.app", ThirdParty, LinkScope.Chat | LinkScope.Memory), Now);
        Assert.Equal(LinkDecisionKind.NeedAuth, d.Kind);
    }

    [Fact]
    public async Task Gate_needs_auth_when_the_grant_has_expired()
    {
        var (gate, store) = NewGate();
        await store.SaveAsync(new LinkGrant("com.other.app", ThirdParty, LinkScope.Chat, Now, Now.AddHours(1)));
        var d = await gate.DecideAsync(new LinkRequest("com.other.app", ThirdParty, LinkScope.Chat), Now.AddHours(2));
        Assert.Equal(LinkDecisionKind.NeedAuth, d.Kind);
    }

    // ---- authorizer: the act ----

    [Fact]
    public async Task Authorizer_third_party_prompts_then_mints()
    {
        var (gate, store) = NewGate();
        var auth = new LinkAuthorizer(store, gate);
        var prompted = false;
        Func<CancellationToken, Task<bool>> spy = _ => { prompted = true; return Task.FromResult(true); };

        var grant = await auth.AuthorizeAsync(
            new LinkRequest("com.other.app", ThirdParty, LinkScope.Chat), spy, Now);

        Assert.True(prompted);
        Assert.NotNull(grant);
        Assert.Equal(LinkScope.Chat, grant!.Scope);
        Assert.Equal(grant, await store.FindAsync("com.other.app"));
    }

    [Fact]
    public async Task Authorizer_third_party_decline_mints_nothing()
    {
        var (gate, store) = NewGate();
        var auth = new LinkAuthorizer(store, gate);

        var grant = await auth.AuthorizeAsync(new LinkRequest("com.other.app", ThirdParty), Decline, Now);

        Assert.Null(grant);
        Assert.Null(await store.FindAsync("com.other.app"));
    }

    [Fact]
    public async Task Authorizer_first_party_mints_without_prompting()
    {
        var (gate, store) = NewGate();
        var auth = new LinkAuthorizer(store, gate);
        var prompted = false;
        Func<CancellationToken, Task<bool>> spy = _ => { prompted = true; return Task.FromResult(true); };

        var grant = await auth.AuthorizeAsync(new LinkRequest("com.geek.app", FirstParty), spy, Now);

        Assert.False(prompted);
        Assert.NotNull(grant);
        Assert.Equal(grant, await store.FindAsync("com.geek.app"));
    }

    [Fact]
    public async Task Authorizer_denied_request_never_prompts()
    {
        var (gate, store) = NewGate();
        var auth = new LinkAuthorizer(store, gate);
        var prompted = false;
        Func<CancellationToken, Task<bool>> spy = _ => { prompted = true; return Task.FromResult(true); };

        var grant = await auth.AuthorizeAsync(new LinkRequest("", "", LinkScope.Chat), spy, Now);

        Assert.False(prompted);
        Assert.Null(grant);
    }

    [Fact]
    public async Task Authorizer_reuses_an_existing_grant_without_prompting()
    {
        var (gate, store) = NewGate();
        await store.SaveAsync(new LinkGrant("com.other.app", ThirdParty, LinkScope.Chat, Now));
        var auth = new LinkAuthorizer(store, gate);
        var prompted = false;
        Func<CancellationToken, Task<bool>> spy = _ => { prompted = true; return Task.FromResult(true); };

        var grant = await auth.AuthorizeAsync(new LinkRequest("com.other.app", ThirdParty, LinkScope.Chat), spy, Now);

        Assert.False(prompted);
        Assert.NotNull(grant);
    }

    [Fact]
    public async Task Authorizer_sets_expiry_from_lifetime_and_zero_means_forever()
    {
        var (gate, store) = NewGate();

        var expiring = await new LinkAuthorizer(store, gate, TimeSpan.FromDays(30))
            .AuthorizeAsync(new LinkRequest("com.a.app", ThirdParty), Approve, Now);
        Assert.Equal(Now.AddDays(30), expiring!.ExpiresAt);

        var forever = await new LinkAuthorizer(store, gate)   // default lifetime = zero
            .AuthorizeAsync(new LinkRequest("com.b.app", ThirdParty), Approve, Now);
        Assert.Null(forever!.ExpiresAt);
    }

    // ---- turn codec: the marshalling semantics for the Android Bundle hop ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Codec_round_trips_a_request(bool agentic)
    {
        var req = new LinkTurnRequest("sess-1", "what are my work rights?", agentic);

        var back = LinkTurnCodec.TryDecodeRequest(LinkTurnCodec.Encode(req));

        Assert.NotNull(back);
        Assert.Equal(req, back);
    }

    [Fact]
    public void Codec_decodes_a_request_with_no_message_as_null()
    {
        var map = new Dictionary<string, string> { [LinkTurnCodec.KeySession] = "s" };
        Assert.Null(LinkTurnCodec.TryDecodeRequest(map));
    }

    [Fact]
    public void Codec_round_trips_a_success_reply()
    {
        var reply = LinkTurnReply.Success("Paris.");
        var back = LinkTurnCodec.DecodeReply(LinkTurnCodec.Encode(reply));
        Assert.True(back.Ok);
        Assert.Equal("Paris.", back.Reply);
    }

    [Fact]
    public void Codec_round_trips_a_failure_reply_and_a_missing_ok_is_failure()
    {
        var back = LinkTurnCodec.DecodeReply(LinkTurnCodec.Encode(LinkTurnReply.Failure("busy")));
        Assert.False(back.Ok);
        Assert.Equal("busy", back.Error);

        // A map with no ok key must never read as a false success.
        var empty = LinkTurnCodec.DecodeReply(new Dictionary<string, string>());
        Assert.False(empty.Ok);
    }

    // ---- verb codec: the memory / skills / discovery channel ----

    [Fact]
    public void VerbCodec_round_trips_a_recall_request()
    {
        var req = new LinkVerbRequest(LinkVerb.Recall, Query: "visit the clinic", Limit: 3);
        var back = LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(req));
        Assert.Equal(req, back);
    }

    [Fact]
    public void VerbCodec_round_trips_a_remember_request()
    {
        var req = new LinkVerbRequest(LinkVerb.Remember, Text: "PIN reset needs the branch", Subject: "reset:pin");
        var back = LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(req));
        Assert.Equal(req, back);
    }

    [Fact]
    public void VerbCodec_round_trips_skills_and_capabilities_requests()
    {
        var skills = new LinkVerbRequest(LinkVerb.Skills, Query: "grant");
        Assert.Equal(skills, LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(skills)));

        var caps = new LinkVerbRequest(LinkVerb.Capabilities);
        Assert.Equal(caps, LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(caps)));
    }

    [Fact]
    public void VerbCodec_decodes_a_missing_or_unknown_verb_as_null()
    {
        Assert.Null(LinkVerbCodec.TryDecodeRequest(new Dictionary<string, string>()));
        Assert.Null(LinkVerbCodec.TryDecodeRequest(
            new Dictionary<string, string> { [LinkVerbCodec.KeyVerb] = "teleport" }));
    }

    [Fact]
    public void VerbCodec_defaults_a_missing_or_bad_limit_to_a_sane_value()
    {
        var map = new Dictionary<string, string> { [LinkVerbCodec.KeyVerb] = "recall" };   // no limit
        Assert.Equal(5, LinkVerbCodec.TryDecodeRequest(map)!.Limit);

        map[LinkVerbCodec.KeyLimit] = "0";
        Assert.Equal(5, LinkVerbCodec.TryDecodeRequest(map)!.Limit);
    }

    [Fact]
    public void VerbCodec_round_trips_multi_field_rows()
    {
        var reply = LinkRowsReply.Success(new IReadOnlyList<string>[]
        {
            new[] { "self.healing", "partial", "categorise a failure, recommend a fix" },
            new[] { "model.selection", "shipping", "pick the best model this device can hold" },
        });

        var back = LinkVerbCodec.DecodeReply(LinkVerbCodec.Encode(reply));

        Assert.True(back.Ok);
        Assert.Equal(2, back.Rows.Count);
        Assert.Equal(new[] { "self.healing", "partial", "categorise a failure, recommend a fix" }, back.Rows[0]);
        Assert.Equal("model.selection", back.Rows[1][0]);
    }

    [Fact]
    public void VerbCodec_round_trips_an_empty_success_like_a_remember()
    {
        var back = LinkVerbCodec.DecodeReply(
            LinkVerbCodec.Encode(LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>())));
        Assert.True(back.Ok);
        Assert.Empty(back.Rows);
    }

    [Fact]
    public void VerbCodec_a_failure_and_a_missing_ok_both_read_as_failure()
    {
        var back = LinkVerbCodec.DecodeReply(LinkVerbCodec.Encode(LinkRowsReply.Failure("memory not available")));
        Assert.False(back.Ok);
        Assert.Equal("memory not available", back.Error);

        Assert.False(LinkVerbCodec.DecodeReply(new Dictionary<string, string>()).Ok);
    }

    [Theory]
    [InlineData(LinkVerb.Recall, LinkScope.Memory)]
    [InlineData(LinkVerb.Remember, LinkScope.Memory)]
    [InlineData(LinkVerb.Skills, LinkScope.Skills)]
    [InlineData(LinkVerb.Capabilities, LinkScope.Chat)]
    public void Verbs_declare_the_scope_each_one_costs(LinkVerb verb, LinkScope expected)
        => Assert.Equal(expected, LinkVerbs.RequiredScope(verb));

    // ---- persistent grant store ----

    [Fact]
    public async Task File_store_persists_a_grant_across_a_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-grants-{Guid.NewGuid():N}.json");
        try
        {
            var grant = new LinkGrant("com.app.a", ThirdParty,
                LinkScope.Chat | LinkScope.Memory, Now, Now.AddDays(30));
            await new FileLinkGrantStore(path).SaveAsync(grant);

            // A fresh store over the same file — as if the process was killed and restarted.
            var reloaded = await new FileLinkGrantStore(path).FindAsync("com.app.a");
            Assert.Equal(grant, reloaded);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task File_store_revoke_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-grants-{Guid.NewGuid():N}.json");
        try
        {
            var store = new FileLinkGrantStore(path);
            await store.SaveAsync(new LinkGrant("com.app.a", ThirdParty, LinkScope.Chat, Now));
            await store.RevokeAsync("com.app.a");

            Assert.Null(await new FileLinkGrantStore(path).FindAsync("com.app.a"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task File_store_loads_a_corrupt_file_as_empty_rather_than_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-grants-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not valid json");
        try
        {
            var store = new FileLinkGrantStore(path);
            Assert.Empty(await store.ListAsync());
        }
        finally { File.Delete(path); }
    }
}
