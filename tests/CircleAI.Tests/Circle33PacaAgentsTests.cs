// Circle33PacaAgentsTests.cs
//
// (3.3.0) Tests for Paca agent-member surface.

using System;
using System.Linq;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaAgentsTests
{
    [Fact]
    public void AddHuman_StoresMember()
    {
        var store = new InMemoryPacaMemberStore();
        var m = store.AddHuman("u1", "p1", "Sipho", "@sipho");
        Assert.Equal(MemberKind.Human, m.Kind);
        Assert.Equal("Sipho", m.DisplayName);
    }

    [Fact]
    public void AddAgent_StoresMemberAndProfile()
    {
        var store = new InMemoryPacaMemberStore();
        var profile = AgentTemplates.DevelopmentAgent("placeholder", "test-key");
        store.AddAgent("a1", "p1", "Dev Bot", "@dev-bot", profile);

        var member = store.GetMember("a1");
        Assert.NotNull(member);
        Assert.Equal(MemberKind.Agent, member!.Kind);

        var stored = store.GetAgentProfile("a1");
        Assert.NotNull(stored);
        Assert.Equal("a1", stored!.MemberId);
        Assert.True(stored.Capabilities.CanCloneRepos);
    }

    [Fact]
    public void ListMembers_FiltersByKindAndProject()
    {
        var store = new InMemoryPacaMemberStore();
        store.AddHuman("u1", "p1", "Sipho",  "@sipho");
        store.AddHuman("u2", "p2", "Naledi", "@naledi");
        store.AddAgent("a1", "p1", "Dev",    "@dev",
            AgentTemplates.DevelopmentAgent("placeholder", "x"));

        var humans = store.ListMembers("p1", MemberKind.Human);
        Assert.Single(humans);
        Assert.Equal("u1", humans[0].Id);

        var agents = store.ListMembers("p1", MemberKind.Agent);
        Assert.Single(agents);

        var all = store.ListMembers("p1");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void RemoveMember_SoftHides()
    {
        var store = new InMemoryPacaMemberStore();
        store.AddHuman("u1", "p1", "Sipho", "@sipho");
        store.RemoveMember("u1");
        Assert.Null(store.GetMember("u1"));
    }

    [Fact]
    public void UpdateAgentProfile_NotAnAgent_Throws()
    {
        var store = new InMemoryPacaMemberStore();
        store.AddHuman("u1", "p1", "Sipho", "@sipho");
        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateAgentProfile("u1",
                AgentTemplates.DevelopmentAgent("u1", "x")));
    }

    [Fact]
    public void Add_DuplicateId_Throws()
    {
        var store = new InMemoryPacaMemberStore();
        store.AddHuman("u1", "p1", "Sipho", "@sipho");
        Assert.Throws<InvalidOperationException>(() => store.AddHuman("u1", "p1", "x", "@x"));
    }

    [Fact]
    public void AgentTemplates_PresetNames_AreUnique()
    {
        var names = AgentTemplates.PresetNames;
        Assert.Equal(5, names.Count);
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public void DevelopmentAgent_HasGitIdentity()
    {
        var p = AgentTemplates.DevelopmentAgent("m1", "k");
        Assert.NotNull(p.GitIdentity);
        Assert.False(string.IsNullOrEmpty(p.GitIdentity.Email));
    }

    [Fact]
    public void DesignerAgent_CannotCloneRepos()
    {
        var p = AgentTemplates.DesignerAgent("m1", "k");
        Assert.False(p.Capabilities.CanCloneRepos);
    }

    [Fact]
    public void QaAgent_TriggerForChatHasQaKeyword()
    {
        var p = AgentTemplates.QaAgent("m1", "k");
        Assert.Equal("@qa", p.Triggers.ChatMention);
    }

    [Fact]
    public void AddHuman_EmptyHandle_Throws()
    {
        var store = new InMemoryPacaMemberStore();
        Assert.Throws<ArgumentException>(() => store.AddHuman("u1", "p1", "Sipho", ""));
    }
}
