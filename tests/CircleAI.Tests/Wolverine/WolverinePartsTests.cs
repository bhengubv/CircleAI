// WolverinePartsTests.cs
//
// The smaller parts of S6, each where something could quietly go wrong: a fingerprint that
// survives a rebuild (no compiler state-machine numbers in it), the marker written before a
// risky step and read back when the step never ended, the healing verbs' scopes and rows, and
// the thinking budget honoured by the C# engine itself.

using System.Runtime.CompilerServices;
using CircleAI.Assistant;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Linking;
using Xunit;
using Xunit.Abstractions;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

public sealed class HealthEventSiteTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ThrowsAfterAnAwaitAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("late");
    }

    [Fact]
    public async Task An_async_method_is_its_own_site_not_its_state_machine()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(ThrowsAfterAnAwaitAsync);

        var site = HealthEvents.SiteOf(ex);

        Assert.Equal($"{nameof(HealthEventSiteTests)}.{nameof(ThrowsAfterAnAwaitAsync)}", site);
        Assert.DoesNotContain("d__", site);
        Assert.DoesNotContain("MoveNext", site);
    }

    [Fact]
    public void A_local_function_keeps_its_name_under_its_method()
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Inner() => throw new ArgumentException("x");
        var ex = Assert.Throws<ArgumentException>(Inner);

        Assert.Equal($"{nameof(HealthEventSiteTests)}.{nameof(A_local_function_keeps_its_name_under_its_method)}.Inner", HealthEvents.SiteOf(ex));
    }

    [Fact]
    public void A_lambda_is_the_method_that_wrote_it()
    {
        Action fail = () => throw new FormatException("x");
        var ex = Assert.Throws<FormatException>(fail);

        Assert.Equal($"{nameof(HealthEventSiteTests)}.{nameof(A_lambda_is_the_method_that_wrote_it)}", HealthEvents.SiteOf(ex));
    }

    [Fact]
    public void A_failure_that_threw_nothing_has_the_same_shape_of_fingerprint()
    {
        var e = HealthEvents.Of("Engine", "decode m", "IllegalInstruction", "InferenceWorker.Decode", "signal=4 at 0x7f00", subject: "m");

        Assert.Equal("CircleAI:IllegalInstruction:InferenceWorker.Decode", e.Fingerprint);
        Assert.Equal("m", e.Subject);
        Assert.DoesNotMatch(@"\d", e.Template);
    }
}

[Collection(nameof(WolverineCollection))]
public sealed class RiskyStepsTests : IDisposable
{
    private readonly string _dir = TempDir("risky");

    public RiskyStepsTests() => Reset();

    public void Dispose()
    {
        Reset();
        Delete(_dir);
    }

    [Fact]
    public void A_step_that_survived_leaves_no_marker()
    {
        RiskySteps.Directory = RiskySteps.For(_dir, "inference");
        using (RiskySteps.Begin("generate m | scalar kernels")) Assert.Equal("generate m | scalar kernels", RiskySteps.Leftover(RiskySteps.Directory));

        Assert.Null(RiskySteps.Leftover(RiskySteps.Directory));
    }

    [Fact]
    public void A_step_that_never_ended_is_the_fault_read_at_the_next_look()
    {
        var dir = RiskySteps.For(_dir, "inference");
        RiskySteps.Directory = dir;
        RiskySteps.Begin("load | threads <= 2");   // the process "dies" here: nothing disposes it

        RiskySteps.Directory = null;               // a new process...
        Assert.Equal("load | threads <= 2", RiskySteps.Leftover(dir));
        RiskySteps.Clear(dir);
        Assert.Null(RiskySteps.Leftover(dir));
    }

    [Fact]
    public void Its_slot_is_not_the_supervisors()
    {
        // The supervisor keeps "loading <model>" in the state folder's one slot; a step marker
        // there would overwrite the model it names.
        DeviceDiagnostics.BeginRisky(_dir, "loading m");
        RiskySteps.Directory = RiskySteps.For(_dir, "inference");
        using (RiskySteps.Begin("generate m | nothing turned down")) { }

        Assert.Equal("loading m", DeviceDiagnostics.PreviousCrash(_dir));
    }

    [Fact]
    public void With_no_folder_it_writes_nothing()
    {
        using (RiskySteps.Begin("generate m")) { }
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }
}

public sealed class HealingVerbTests
{
    [Fact]
    public void Reading_the_healing_is_the_chat_floor_and_changing_it_is_model_admin_by_our_own_apps()
    {
        Assert.Equal(LinkScope.Chat, LinkVerbs.RequiredScope(LinkVerb.Healing));
        Assert.Equal(LinkScope.ModelAdmin, LinkVerbs.RequiredScope(LinkVerb.HealingSet));
        Assert.True(LinkVerbs.FirstPartyOnly(LinkVerb.HealingSet));
        Assert.False(LinkVerbs.FirstPartyOnly(LinkVerb.Healing));
    }

    [Fact]
    public void The_verbs_and_their_arguments_cross_the_wire_by_name()
    {
        var sent = new LinkVerbRequest(LinkVerb.HealingSet, Args: new Dictionary<string, string>
        {
            [LinkHealingRows.ArgReports] = "off",
            [LinkHealingRows.ArgAutonomy] = AutonomyLevels.SuggestOnly,
        });

        var back = LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(sent));

        Assert.Equal(LinkVerb.HealingSet, back!.Verb);
        Assert.Equal("off", back.Arg(LinkHealingRows.ArgReports));
        Assert.Equal(AutonomyLevels.SuggestOnly, back.Arg(LinkHealingRows.ArgAutonomy));
    }

    [Fact]
    public void The_rows_read_back_column_for_column()
    {
        var at = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var reply = LinkVerbCodec.DecodeReply(LinkVerbCodec.Encode(LinkRowsReply.Success(
        [
            LinkHealingRows.SummaryRow(AutonomyLevels.AutoFixSafe, true, 3, false, null, 5, 2, 1, at),
            LinkHealingRows.KnobRow(BrainTuning.Knobs.Threads, "2", at),
            LinkHealingRows.ItemRow("id1", at, "Engine", "CircleAI:IllegalInstruction:InferenceWorker.Decode", Remedies.ScalarKernels,
                "auto-fixed — scalar-kernels", "Healed", false, false, Remedies.Describe(Remedies.ScalarKernels)),
        ])));

        var summary = reply.Rows[0];
        Assert.Equal(LinkHealingRows.Summary, LinkHealingRows.KindOf(summary));
        Assert.True(LinkSetupRows.Flag(summary, 2));
        Assert.Equal(3, LinkSetupRows.Number(summary, 3));
        Assert.Null(LinkHealingRows.TimeAt(summary, 5));
        Assert.Equal(at, LinkHealingRows.TimeAt(summary, 9));
        Assert.Equal("2", reply.Rows[1][2]);
        Assert.Equal(Remedies.ScalarKernels, reply.Rows[2][5]);
        Assert.Equal("Healed", reply.Rows[2][7]);
    }
}

public sealed class ManagedThinkingBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_thinking_budget_closes_the_reasoning_and_an_answer_follows_it()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var g = ManagedChatGenerator.Load(path, new ManagedGeneratorOptions
        {
            ContextLength = 512, Thinking = true, ThinkingBudget = 4, PrimedStartDirectory = string.Empty,
        });
        var response = await g.GenerateResponseAsync([new("user", "Hello!")],
            new GenerationOptions { Temperature = 1f, Seed = 7, MaxTokens = 32, Budget = PowerBudget.None, IncludeReasoning = true });

        output.WriteLine($"reasoning: {response.ReasoningContent}");
        output.WriteLine($"answer: {response.Text}");
        Assert.True(g.Tokenizer.Encode(response.ReasoningContent ?? string.Empty).Count <= 4 + 1, "the reasoning ran past its budget");
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "no answer followed the closed reasoning");
    }
}
