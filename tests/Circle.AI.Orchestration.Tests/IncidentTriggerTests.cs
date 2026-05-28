using Circle.AI.Memory;
using Circle.AI.Orchestration;
using Xunit;

namespace Circle.AI.Orchestration.Tests;

public sealed class IncidentTriggerTests
{
    // -----------------------------------------------------------------------
    // 1. No tags → empty list
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_NoTags_ReturnsEmptyList()
    {
        var entry = MakeEntry(tags: null);

        var result = IncidentTrigger.FromMemoryEntry(entry);

        Assert.Empty(result);
    }

    [Fact]
    public void FromMemoryEntry_NonCrashTags_ReturnsEmptyList()
    {
        var entry = MakeEntry(tags: new Dictionary<string, string>
        {
            ["locale"] = "en-ZA",
            ["sentiment"] = "positive",
        });

        var result = IncidentTrigger.FromMemoryEntry(entry);

        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // 2. "crash" tag → exactly one Operations task
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_CrashTag_ReturnsSingleOperationsTask()
    {
        var entry = MakeEntry(tags: new Dictionary<string, string> { ["crash"] = "true" });

        var result = IncidentTrigger.FromMemoryEntry(entry);

        Assert.Single(result);
        Assert.Equal(AgentRole.Operations, result[0].Role);
    }

    // -----------------------------------------------------------------------
    // 3. "crash" + "auth_failure" → two tasks (Operations + Security)
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_CrashAndAuthFailure_ReturnsTwoTasks()
    {
        var entry = MakeEntry(tags: new Dictionary<string, string>
        {
            ["crash"]        = "true",
            ["auth_failure"] = "true",
        });

        var result = IncidentTrigger.FromMemoryEntry(entry);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Role == AgentRole.Operations);
        Assert.Contains(result, t => t.Role == AgentRole.Security);
    }

    // -----------------------------------------------------------------------
    // 4. Security task has Critical priority
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_SecurityTask_HasCriticalPriority()
    {
        var entry = MakeEntry(tags: new Dictionary<string, string>
        {
            ["crash"]       = "true",
            ["injection"]   = "true",
        });

        var result = IncidentTrigger.FromMemoryEntry(entry);

        var securityTask = result.Single(t => t.Role == AgentRole.Security);
        Assert.Equal(AgentPriority.Critical, securityTask.Priority);
    }

    // -----------------------------------------------------------------------
    // 5. Operations task has High priority
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_OperationsTask_HasHighPriority()
    {
        var entry = MakeEntry(tags: new Dictionary<string, string> { ["crash"] = "true" });

        var result = IncidentTrigger.FromMemoryEntry(entry);

        var opsTask = result.Single(t => t.Role == AgentRole.Operations);
        Assert.Equal(AgentPriority.High, opsTask.Priority);
    }

    // -----------------------------------------------------------------------
    // 6. Task inputs contain episode_id
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_AllTasks_ContainEpisodeIdInput()
    {
        var entry = MakeEntry(tags: new Dictionary<string, string>
        {
            ["crash"]            = "true",
            ["permission_denied"] = "true",
        });

        var result = IncidentTrigger.FromMemoryEntry(entry);

        Assert.All(result, task =>
        {
            Assert.True(task.Inputs.ContainsKey("episode_id"),
                $"Task for role {task.Role} is missing 'episode_id' input.");
            Assert.Equal(entry.Id.ToString(), task.Inputs["episode_id"]);
        });
    }

    // -----------------------------------------------------------------------
    // Null guard
    // -----------------------------------------------------------------------

    [Fact]
    public void FromMemoryEntry_NullEntry_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            IncidentTrigger.FromMemoryEntry(null!));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static EpisodicMemoryEntry MakeEntry(Dictionary<string, string>? tags = null)
        => new()
        {
            UserText      = "test user text",
            AssistantText = "test assistant text",
            AppContext    = "test.app",
            Tags          = tags,
        };
}
