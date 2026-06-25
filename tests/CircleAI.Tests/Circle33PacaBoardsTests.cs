// Circle33PacaBoardsTests.cs
//
// (3.3.0) Tests for PacaBoard.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaBoardsTests
{
    private static (InMemoryPacaStore Tasks, PacaBoard Board, string Project) Bootstrap()
    {
        var tasks = new InMemoryPacaStore();
        tasks.CreateProject("p1", "Circle AI", "CAI");
        return (tasks, new PacaBoard(tasks), "p1");
    }

    [Fact]
    public void Columns_DefaultsCoverSixStatuses()
    {
        var (_, board, _) = Bootstrap();
        var names = board.Columns.Select(c => c.Name).ToArray();
        Assert.Contains("todo",        names);
        Assert.Contains("in_progress", names);
        Assert.Contains("in_review",   names);
        Assert.Contains("done",        names);
        Assert.Contains("cancelled",   names);
        Assert.Contains("blocked",     names);
    }

    [Fact]
    public void MoveTask_ChangesStatus_AndStoresPosition()
    {
        var (tasks, board, project) = Bootstrap();
        var t = tasks.AddTask(project, "Implement signup");

        board.MoveTask(project, t.Number, "in_progress", newPosition: 3);

        var updated = tasks.ListTasks(project).First();
        Assert.Equal("in_progress", updated.Status);
        var meta = board.GetTaskMetadata(project, t.Number);
        Assert.NotNull(meta);
        Assert.Equal(3, meta!.PositionInColumn);
    }

    [Fact]
    public void MoveTask_UnknownStatus_Throws()
    {
        var (tasks, board, project) = Bootstrap();
        var t = tasks.AddTask(project, "x");
        Assert.Throws<ArgumentException>(() =>
            board.MoveTask(project, t.Number, "nonexistent", 0));
    }

    [Fact]
    public void TasksInColumn_RespectsPagination()
    {
        var (tasks, board, project) = Bootstrap();
        for (int i = 0; i < 5; i++)
        {
            var t = tasks.AddTask(project, $"task-{i}");
            board.SetTaskMetadata(new TaskBoardMetadata(
                project, t.Number, 0, 3, null, null, null, null,
                Array.Empty<string>(), new Dictionary<string, string>(), PositionInColumn: i));
        }
        var page = board.TasksInColumn(project, "todo", skip: 2, take: 2);
        Assert.Equal(2, page.Count);
    }

    [Fact]
    public void Sprint_LifecycleTransitions()
    {
        var (_, board, project) = Bootstrap();
        var s = board.CreateSprint("s1", project, "Sprint 1", "Ship voice loop", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));
        Assert.Equal(SprintState.Planning, s.State);

        var active = board.StartSprint("s1");
        Assert.Equal(SprintState.Active, active.State);

        var done = board.CompleteSprint("s1");
        Assert.Equal(SprintState.Completed, done.State);
    }

    [Fact]
    public void TasksInSprint_GroupsByMetadata()
    {
        var (tasks, board, project) = Bootstrap();
        board.CreateSprint("s1", project, "Sprint 1", "Goal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));
        var t = tasks.AddTask(project, "first");
        board.SetTaskMetadata(new TaskBoardMetadata(project, t.Number, 3, 4, null, null, null, "s1",
            Array.Empty<string>(), new Dictionary<string, string>(), 0));

        var inSprint = board.TasksInSprint("s1");
        Assert.Single(inSprint);
        Assert.Equal(t.Number, inSprint[0].Number);
    }

    [Fact]
    public void Views_RoundTrip()
    {
        var (_, board, _) = Bootstrap();
        var view = new BoardView("my-view", "voice", "u1", "importance", true,
            VisibleColumns: new[] { "todo", "in_progress" },
            VisibleFields:  new[] { "title", "story_points" });
        board.SaveView(view);

        var fetched = board.GetView("my-view");
        Assert.NotNull(fetched);
        Assert.True(fetched!.SortDescending);
        Assert.Equal(2, fetched.VisibleColumns.Count);
    }

    [Fact]
    public void Tags_AndCustomFields_StoredInMetadata()
    {
        var (tasks, board, project) = Bootstrap();
        var t = tasks.AddTask(project, "Customer-facing change");
        board.SetTaskMetadata(new TaskBoardMetadata(
            project, t.Number, 5, 4, "u1", "u2", null, null,
            Tags:        new[] { "voice", "compliance" },
            CustomFields: new Dictionary<string, string> { ["region"] = "ZA" },
            PositionInColumn: 0));

        var meta = board.GetTaskMetadata(project, t.Number);
        Assert.NotNull(meta);
        Assert.Contains("voice", meta!.Tags);
        Assert.Equal("ZA", meta.CustomFields["region"]);
    }

    [Fact]
    public void CollapseColumn_PersistsState()
    {
        var (_, board, _) = Bootstrap();
        board.CollapseColumn("done", true);
        var done = board.Columns.First(c => c.Name == "done");
        Assert.True(done.Collapsed);
    }

    [Fact]
    public void ParentChild_RecordedInMetadata()
    {
        var (tasks, board, project) = Bootstrap();
        var parent = tasks.AddTask(project, "Epic");
        var child  = tasks.AddTask(project, "Subtask");
        board.SetTaskMetadata(new TaskBoardMetadata(project, child.Number, 0, 3, null, null,
            ParentTaskNumber: parent.Number, SprintId: null,
            Array.Empty<string>(), new Dictionary<string, string>(), 0));

        var meta = board.GetTaskMetadata(project, child.Number);
        Assert.Equal(parent.Number, meta!.ParentTaskNumber);
    }
}
