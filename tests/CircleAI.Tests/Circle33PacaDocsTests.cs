// Circle33PacaDocsTests.cs
//
// (3.3.0) Tests for Paca doc service.

using System;
using System.Linq;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaDocsTests
{
    [Fact]
    public void CreateDocument_StoresAndTracksCreation()
    {
        var s = new PacaDocService();
        var d = s.CreateDocument("d1", "p1", parentId: null, title: "ADR-001", contentJson: """{"text":"hello"}""", authorMemberId: "u1");

        Assert.Equal("d1", d.Id);
        Assert.False(d.IsFolder);

        var activity = s.Activity("d1");
        Assert.Single(activity);
        Assert.Equal("created", activity[0].Action);
    }

    [Fact]
    public void CreateFolder_StoresAndAllowsChildren()
    {
        var s = new PacaDocService();
        var f = s.CreateFolder("f1", "p1", parentId: null, title: "Specs");
        var d = s.CreateDocument("d1", "p1", parentId: "f1", title: "Auth", contentJson: "{}", authorMemberId: "u1");
        Assert.True(f.IsFolder);
        Assert.Equal("f1", d.ParentId);

        var kids = s.ListChildren("p1", "f1");
        Assert.Single(kids);
    }

    [Fact]
    public void Edit_WritesVersionAndExtractsMentions()
    {
        var s = new PacaDocService();
        s.CreateDocument("d1", "p1", null, "X", contentJson: "{}", authorMemberId: "u1");

        var mentions = s.Edit("d1", """{"text":"Hi @sipho and @billing-agent"}""", authorMemberId: "u2");

        Assert.Contains("sipho",        mentions);
        Assert.Contains("billing-agent", mentions);

        var versions = s.Versions("d1");
        Assert.Single(versions);
    }

    [Fact]
    public void Edit_AiEdit_FlagsActivityCorrectly()
    {
        var s = new PacaDocService();
        s.CreateDocument("d1", "p1", null, "X", "{}", "u1");
        s.Edit("d1", """{"text":"new"}""", "agent1", isAiEdit: true);
        var actions = s.Activity("d1").Select(a => a.Action).ToArray();
        Assert.Contains("ai-edited", actions);
    }

    [Fact]
    public void Edit_FolderFails()
    {
        var s = new PacaDocService();
        s.CreateFolder("f1", "p1", null, "Specs");
        Assert.Throws<InvalidOperationException>(() => s.Edit("f1", "{}", "u1"));
    }

    [Fact]
    public void Diff_ProducesAddedAndRemovedLines()
    {
        var s = new PacaDocService();
        var (added, removed) = s.DiffLines("a\nb\nc", "a\nB\nc\nd");
        Assert.Contains("B", added);
        Assert.Contains("d", added);
        Assert.Contains("b", removed);
    }

    [Fact]
    public void Link_DocSectionToTask_RecordedAndListed()
    {
        var s = new PacaDocService();
        s.CreateDocument("d1", "p1", null, "Doc", "{}", "u1");
        var link = s.Link("d1", "#background", "p1", taskNumber: 5);

        Assert.Equal("d1", link.DocId);
        Assert.Equal(5, link.TaskNumber);
        Assert.Single(s.Links("d1"));

        var actions = s.Activity("d1").Select(a => a.Action).ToArray();
        Assert.Contains("linked", actions);
    }

    [Fact]
    public void Versions_Empty_BeforeEdit()
    {
        var s = new PacaDocService();
        s.CreateDocument("d1", "p1", null, "Doc", "{}", "u1");
        Assert.Empty(s.Versions("d1"));
    }
}
