// Circle33PacaProjectsTests.cs
//
// (3.3.0) Tests for InMemoryPacaStore — projects + tasks with prefix-based references.

using System;
using System.Linq;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaProjectsTests
{
    [Fact]
    public void CreateProject_AssignsFieldsAndAllowsLookup()
    {
        var store = new InMemoryPacaStore();
        var p = store.CreateProject("p1", "Circle AI", "CAI");

        Assert.Equal("p1", p.Id);
        Assert.Equal("CAI", p.Prefix);
        Assert.Null(p.DeletedAtUtc);

        var fetched = store.GetProject("p1");
        Assert.NotNull(fetched);
        Assert.Equal("CAI", fetched!.Prefix);
    }

    [Fact]
    public void CreateProject_DuplicateId_Throws()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "X");
        Assert.Throws<InvalidOperationException>(() => store.CreateProject("p1", "x", "X"));
    }

    [Fact]
    public void DeleteProject_SoftHidesIt()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "X");
        store.DeleteProject("p1");

        Assert.Null(store.GetProject("p1"));
    }

    [Fact]
    public void AddTask_AutoNumbersAndPersists()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "PACA");

        var t1 = store.AddTask("p1", "First task");
        var t2 = store.AddTask("p1", "Second task");

        Assert.Equal(1, t1.Number);
        Assert.Equal(2, t2.Number);
        Assert.Equal("PACA-1", t1.Reference("PACA"));
        Assert.Equal("PACA-2", t2.Reference("PACA"));
    }

    [Fact]
    public void AddTask_UnknownProject_Throws()
    {
        var store = new InMemoryPacaStore();
        Assert.Throws<InvalidOperationException>(() => store.AddTask("ghost", "title"));
    }

    [Fact]
    public void ListTasks_ReturnsLiveOnlyInNumberOrder()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "PACA");
        var t1 = store.AddTask("p1", "a");
        var t2 = store.AddTask("p1", "b");
        var t3 = store.AddTask("p1", "c");
        store.DeleteTask("p1", t2.Number);

        var list = store.ListTasks("p1");
        Assert.Equal(2, list.Count);
        Assert.Equal(t1.Number, list[0].Number);
        Assert.Equal(t3.Number, list[1].Number);
    }

    [Fact]
    public void GetTaskByReference_ResolvesPrefixedRef()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "PACA");
        store.AddTask("p1", "first");
        store.AddTask("p1", "second");

        var t = store.GetTaskByReference("p1", "PACA-2");
        Assert.NotNull(t);
        Assert.Equal(2, t!.Number);
    }

    [Fact]
    public void GetTaskByReference_WrongPrefix_ReturnsNull()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "PACA");
        store.AddTask("p1", "first");

        Assert.Null(store.GetTaskByReference("p1", "OTHER-1"));
    }

    [Fact]
    public void UpdateProjectSettings_PersistsNewJson()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "X", settingsJson: """{"theme":"dark"}""");
        var updated = store.UpdateProjectSettings("p1", """{"theme":"light"}""");
        Assert.Contains("light", updated.SettingsJson);
    }

    [Fact]
    public void UpdateTask_AppliesChanges()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "PACA");
        var t = store.AddTask("p1", "Initial");
        var updated = t with { Title = "Renamed", Status = "in_progress" };

        store.UpdateTask(updated);

        var fetched = store.GetTaskByReference("p1", t.Reference("PACA"));
        Assert.Equal("Renamed",     fetched!.Title);
        Assert.Equal("in_progress", fetched.Status);
    }

    [Fact]
    public void DeleteTask_SoftHidesIt()
    {
        var store = new InMemoryPacaStore();
        store.CreateProject("p1", "x", "PACA");
        var t = store.AddTask("p1", "x");
        store.DeleteTask("p1", t.Number);

        Assert.Empty(store.ListTasks("p1"));
    }

    [Fact]
    public void CreateProject_EmptyArguments_Throw()
    {
        var store = new InMemoryPacaStore();
        Assert.Throws<ArgumentException>(() => store.CreateProject("",  "x", "X"));
        Assert.Throws<ArgumentException>(() => store.CreateProject("p", "",  "X"));
        Assert.Throws<ArgumentException>(() => store.CreateProject("p", "x", "" ));
    }
}
