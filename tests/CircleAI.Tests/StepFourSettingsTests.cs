// StepFourSettingsTests.cs
//
// STEP 4'S SETTINGS, OFF A PHONE: the person's preferences and the gate that enforces two of them,
// the Settings scope and the guard that no verb falls to the chat floor, the live answer to "what
// can you do right now", and the rows all of it - and every item on the setup board - travel in.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CircleAI.Assistant;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class ServicePreferencesTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "circleai-prefs-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* temp */ }
    }

    [Fact]
    public void Every_preference_starts_on_its_default_and_everything_is_on()
    {
        var prefs = new ServicePreferences(_file);
        Assert.True(prefs.Flag(ServicePreferences.MobileData));
        Assert.True(prefs.Flag(ServicePreferences.Balance));
        Assert.Equal(0, prefs.Number(ServicePreferences.StorageCapGb));
        Assert.Equal(string.Empty, prefs.Get(ServicePreferences.LogLevels));
        Assert.Empty(prefs.AbilitiesOff);
        Assert.Equal(ServicePreferences.Known.Count, prefs.Rows().Count);
    }

    [Fact]
    public void A_value_is_checked_stored_the_way_it_reads_back_and_kept_across_a_restart()
    {
        var prefs = new ServicePreferences(_file);
        var changed = new List<string>();
        prefs.Changed += (key, value) => changed.Add(key + "=" + value);

        Assert.True(prefs.TrySet(ServicePreferences.MobileData, "off", out var row, out _));
        Assert.Equal("0", row!.Value);
        Assert.True(prefs.TrySet(ServicePreferences.StorageCapGb, "2.5", out _, out _));
        prefs.SetAbility("Seeing", on: false);

        Assert.False(prefs.TrySet(ServicePreferences.MobileData, "sometimes", out _, out var why));
        Assert.Contains("on or off", why);
        Assert.False(prefs.TrySet(ServicePreferences.StorageCapGb, "-1", out _, out _));
        Assert.False(prefs.TrySet("no.such.key", "1", out _, out why));
        Assert.Contains(ServicePreferences.MobileData, why);

        var again = new ServicePreferences(_file);
        Assert.False(again.Flag(ServicePreferences.MobileData));
        Assert.Equal(2.5, again.Number(ServicePreferences.StorageCapGb));
        Assert.Contains("Seeing", again.AbilitiesOff);
        Assert.Contains("downloads.mobileData=0", changed);

        again.SetAbility("Seeing", on: true);
        Assert.Empty(new ServicePreferences(_file).AbilitiesOff);
    }

    [Fact]
    public void The_gate_waits_for_wifi_only_when_the_person_said_so_and_the_network_is_metered()
    {
        static PreferenceDownloadGate Gate(bool allowed, bool? metered, double cap = 0, long used = 0) =>
            new(() => allowed, () => metered, () => cap, () => used);

        Assert.Null(Gate(allowed: true, metered: true).BlockReason(400_000_000));
        Assert.Contains("Wi-Fi", Gate(allowed: false, metered: true).BlockReason(400_000_000));
        Assert.Null(Gate(allowed: false, metered: false).BlockReason(400_000_000));
        Assert.Null(Gate(allowed: false, metered: null).BlockReason(400_000_000));   // cannot tell: never stops a desktop

        // THE STORAGE LIMIT: 1.5 GB used, 0.6 GB more passes 2 GB.
        Assert.Contains("set aside", Gate(true, false, cap: 2, used: 1_500_000_000).BlockReason(600_000_000));
        Assert.Null(Gate(true, false, cap: 3, used: 1_500_000_000).BlockReason(600_000_000));
    }
}

public sealed class LinkScopeGuardTests
{
    [Fact]
    public void No_verb_falls_through_to_the_chat_floor_by_default()
    {
        // A NEW VERB WITH NO SCOPE OF ITS OWN WOULD BE CHAT: any app that may ask a question could
        // call it. Every verb this build knows declares its scope, or this fails.
        var undeclared = Enum.GetValues<LinkVerb>().Where(v => LinkVerbs.DeclaredScope(v) is null).ToList();
        Assert.True(undeclared.Count == 0, "verbs with no declared scope: " + string.Join(", ", undeclared));
    }

    [Theory]
    [InlineData(LinkVerb.SettingsGet)]
    [InlineData(LinkVerb.SettingsSet)]
    [InlineData(LinkVerb.AbilitySet)]
    [InlineData(LinkVerb.CanDoNow)]
    public void Step_four_verbs_need_the_settings_scope(LinkVerb verb)
    {
        Assert.Equal(LinkScope.Settings, LinkVerbs.RequiredScope(verb));
        Assert.False(new LinkGrant("app", "sig", LinkScope.Chat, DateTimeOffset.UtcNow).Covers(LinkVerbs.RequiredScope(verb)));
    }

    [Fact]
    public void The_new_verbs_travel_by_name_and_the_old_ones_keep_their_scope()
    {
        foreach (var verb in new[] { LinkVerb.SettingsGet, LinkVerb.SettingsSet, LinkVerb.AbilitySet, LinkVerb.CanDoNow })
        {
            var back = LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(new LinkVerbRequest(verb, Query: "k", Text: "v")));
            Assert.Equal(verb, back!.Verb);
        }
        Assert.Equal(LinkScope.Chat, LinkVerbs.RequiredScope(LinkVerb.Verdict));
        Assert.Equal(LinkScope.Setup, LinkVerbs.RequiredScope(LinkVerb.SetupStart));
        Assert.Equal(LinkScope.None, LinkVerbs.RequiredScope(LinkVerb.Hello));
    }
}

public sealed class CanDoNowTests
{
    private static readonly AbilityRow[] Abilities =
    [
        new("Talking", "", AbilityState.On),
        new("Listening", "", AbilityState.Available),
        new("Answering", "", AbilityState.On),
        new("Seeing", "", AbilityState.Available),
        new("Waking", "", AbilityState.Ready),
        new("Music", "", AbilityState.On),
    ];

    private static bool NeedsBrain(string title) => title == "Answering";

    private static SetupItemStatus Arriving(string id, double fraction) =>
        new(id, "the ears", SetupItemState.Downloading, (long)(fraction * 1000), 1000, 100, TimeSpan.FromMinutes(4), "", 1, DateTimeOffset.UtcNow);

    [Fact]
    public void Each_ability_says_what_it_can_do_now_from_the_brain_the_switches_the_balance_and_the_board()
    {
        var brain = new ComponentState(ReadinessComponents.Brain, BrainPhase.Loading, LoadStep.Warming, null,
            PauseReason.None, "", "b", DateTimeOffset.UtcNow);
        var rows = CanDoNow.Answer(Abilities, brain, NeedsBrain,
            switchedOff: new HashSet<string> { "Music" },
            resting: new Dictionary<string, string> { ["Seeing"] = "resting while the phone is hot" },
            arriving: title => title == "Listening" ? Arriving("ears", 0.4) : null);

        CanDoRow Row(string t) => rows.Single(r => r.Title == t);
        Assert.Equal(NowState.Now, Row("Talking").State);
        Assert.Equal(NowState.Soon, Row("Listening").State);
        Assert.Equal(0.4, Row("Listening").Progress!.Value, 3);
        Assert.Equal(NowState.Soon, Row("Answering").State);           // the brain is warming
        Assert.Equal(ReadinessSentences.Warming, Row("Answering").Detail);
        Assert.Equal(NowState.Resting, Row("Seeing").State);
        Assert.Equal(NowState.NotOn, Row("Waking").State);
        Assert.Equal(NowState.Off, Row("Music").State);

        var summary = CanDoNow.Summary(rows);
        Assert.Equal(string.Empty, summary.Title);
        Assert.Equal(NowState.Now, summary.State);
        Assert.StartsWith("Right now: talking", summary.Detail);
        Assert.Contains("on its way: listening and answering", summary.Detail);
        Assert.Contains("resting to spare the phone: seeing", summary.Detail);
    }

    [Fact]
    public void A_brain_that_is_missing_failed_for_good_or_coming_says_which()
    {
        var abilities = new[] { new AbilityRow("Answering", "", AbilityState.Available) };
        var none = new HashSet<string>();
        var calm = new Dictionary<string, string>();

        Assert.Equal(NowState.Missing, CanDoNow.Answer(abilities, null, NeedsBrain, none, calm, _ => null)[0].State);
        Assert.Equal(NowState.Soon, CanDoNow.Answer(abilities, null, NeedsBrain, none, calm, _ => Arriving("b", 0.5))[0].State);

        var dead = new ComponentState(ReadinessComponents.Brain, BrainPhase.Failed, LoadStep.None, FailureKind.NativeWorkerCrashed,
            PauseReason.None, "", "b", DateTimeOffset.UtcNow);
        Assert.Equal(NowState.Cannot, CanDoNow.Answer(abilities, dead, NeedsBrain, none, calm, _ => null)[0].State);
        Assert.Equal(NowState.Soon, CanDoNow.Answer(abilities, dead with { RetryAfter = TimeSpan.FromSeconds(30) },
            NeedsBrain, none, calm, _ => null)[0].State);
        Assert.Equal(NowState.Missing, CanDoNow.Summary(CanDoNow.Answer(abilities, null, NeedsBrain, none, calm, _ => null)).State);
    }
}

/// <summary>Every Step 4 row carries every column of its in-process type, and reads back the same.</summary>
public sealed class LinkSettingsRowsTests
{
    // THE GUARD: a member added to the type and not to the row fails here.
    private static int Members<T>() =>
        typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Max(c => c.GetParameters().Length);

    [Fact]
    public void A_setup_item_row_carries_every_member_and_round_trips()
    {
        var item = new SetupItemStatus("Ternary-Bonsai-2-2B", "the brain", SetupItemState.Downloading,
            123_456_789, 429_183_456, 1_234_567.5, TimeSpan.FromSeconds(240), "carrying on from where it stopped", 2,
            DateTimeOffset.FromUnixTimeMilliseconds(1_762_000_000_000));

        var row = LinkSettingsRows.Item(item);
        Assert.Equal(Members<SetupItemStatus>() + 1, row.Count);   // the tag, then every member
        Assert.Equal(item, LinkSettingsRows.ReadItem(row));
    }

    [Fact]
    public void A_setting_row_carries_every_member_and_round_trips()
    {
        var setting = new SettingRow(ServicePreferences.MobileData, "0", "1", PreferenceKind.Flag, "Download over mobile data", "words");
        var row = LinkSettingsRows.Setting(setting);
        Assert.Equal(Members<SettingRow>(), row.Count);
        Assert.Equal(setting, LinkSettingsRows.ReadSetting(row));
    }

    [Fact]
    public void A_can_do_row_carries_every_member_and_round_trips_with_and_without_progress()
    {
        var soon = new CanDoRow("Seeing", NowState.Soon, "the eyes - 40%", 0.4);
        var row = LinkSettingsRows.CanDo(soon);
        Assert.Equal(Members<CanDoRow>(), row.Count);
        Assert.Equal(soon, LinkSettingsRows.ReadCanDo(row));

        var now = soon with { State = NowState.Now, Progress = null };
        Assert.Equal(now, LinkSettingsRows.ReadCanDo(LinkSettingsRows.CanDo(now)));
    }

    [Fact]
    public void Item_rows_ride_after_the_six_column_summary_and_an_old_reader_sees_only_row_zero()
    {
        var item = new SetupItemStatus("m", "the ears", SetupItemState.Queued, 0, 10, 0, TimeSpan.Zero, "", 1,
            DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var rows = new List<IReadOnlyList<string>>
        {
            LinkSetupRows.Progress(0, 1, "the ears", 0, 0, "Fetching"),
            LinkSettingsRows.Item(item),
        };

        Assert.Equal(6, rows[0].Count);
        Assert.Null(LinkSettingsRows.ReadItem(rows[0]));            // the summary is not an item
        Assert.Equal(new[] { item }, LinkSettingsRows.ReadItems(rows));
        Assert.Null(LinkSettingsRows.ReadItem(["item"]));           // a short row is skew, not a crash
    }
}
