// ServiceMicrophoneTests.cs
//
// THE WAKE WORD COULD NOT LISTEN ON A FRESH INSTALL. On the P30 on 2026-10-23, with
// both 1.1 pairs, the service logged "resuming: the owner had it on" and then nothing:
// RECORD_AUDIO was never granted to com.bhengubv.circleai.service, because the only
// screen that asked for it was the link-approval sheet, and our own app is approved
// without that sheet. Every "Allow the microphone" in the app returned false without
// asking anybody.
//
// The fix has a portable half - who may make the phone ask, WHEN, what a tour button
// does, the words - tested here on every leg, and an Android half (the service's
// screen, the client that starts it, the head's wiring) that the test legs do not
// compile, so it is guarded at the source.
//
// AND THE PORTABLE HALF GOT BIGGER ON PURPOSE. The review of 2026-10-23 found four
// separate defects in conditions that were written inline at a call site and
// "guarded" by searching the source for a string - a guard that passes whatever the
// condition means. Every rule that is a truth table now IS one, in MicrophoneAsk,
// asserted below by value:
//
//   ShouldOpenSettings   when a refusal may send somebody to Settings (C10, C12, U1)
//   ShouldAsk            when a client raises the question at all      (U12)
//   TourFor              whether the first-run card is offered         (C4, U19)
//
// The source guards that remain are for things no test on this box can reach: an
// Android manifest, the ORDER of calls inside an Activity, and which verb a client
// sends.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class ServiceMicrophoneTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string Ours = "GEEK-SIGNING-KEY";
    private const string Theirs = "SOME-OTHER-KEY";
    private const string OurApp = "com.bhengubv.circleai.hybrid";
    private const string TheirApp = "com.example.notes";

    // ── who may make the phone ask ───────────────────────────────────────────

    [Theory]
    [InlineData(LinkDecisionKind.Allow, true)]
    [InlineData(LinkDecisionKind.AutoApprove, true)]
    [InlineData(LinkDecisionKind.NeedAuth, false)]
    [InlineData(LinkDecisionKind.Deny, false)]
    public void Only_a_caller_trusted_with_voice_may_raise_the_question(LinkDecisionKind kind, bool may)
        => Assert.Equal(may, MicrophoneAsk.MayAsk(kind));

    private static async Task<bool> MayAsk(LinkGrant? standing, string package, string signature)
    {
        var store = new InMemoryLinkGrantStore();
        if (standing is not null) await store.SaveAsync(standing);

        var gate = new LinkGate(store, new HashSet<string>(StringComparer.Ordinal) { Ours });
        var decision = await gate.DecideAsync(new LinkRequest(package, signature, LinkScope.Voice), Now);
        return MicrophoneAsk.MayAsk(decision.Kind);
    }

    [Fact]
    public async Task Our_own_app_may_ask_although_it_never_saw_a_consent_screen()
        => Assert.True(await MayAsk(null, OurApp, Ours));

    [Fact]
    public async Task Our_own_app_linked_for_chat_alone_may_still_ask()
        => Assert.True(await MayAsk(new LinkGrant(OurApp, Ours, LinkScope.Chat, Now), OurApp, Ours));

    [Fact]
    public async Task An_app_a_person_approved_for_voice_may_ask()
        => Assert.True(await MayAsk(new LinkGrant(TheirApp, Theirs, LinkScope.Chat | LinkScope.Voice, Now), TheirApp, Theirs));

    [Fact]
    public async Task An_app_approved_for_chat_alone_may_not()
        => Assert.False(await MayAsk(new LinkGrant(TheirApp, Theirs, LinkScope.Chat, Now), TheirApp, Theirs));

    [Fact]
    public async Task An_app_nobody_approved_may_not()
        => Assert.False(await MayAsk(null, TheirApp, Theirs));

    [Fact]
    public async Task An_impostor_of_an_approved_app_may_not()
        => Assert.False(await MayAsk(new LinkGrant(TheirApp, Theirs, LinkScope.Voice, Now), TheirApp, "FORGED-KEY"));

    [Fact]
    public async Task A_lapsed_voice_approval_is_no_approval()
        => Assert.False(await MayAsk(
            new LinkGrant(TheirApp, Theirs, LinkScope.Voice, Now.AddDays(-40), Now.AddDays(-1)), TheirApp, Theirs));

    // ── when a refusal may send somebody to Settings ─────────────────────────
    //
    // THE DECISION THAT WAS MADE FROM ONE READING AND NEEDS TWO. Android's
    // shouldShowRequestPermissionRationale is false in three unrelated situations, and
    // only one of them is "the phone showed nothing". Read before AND after, with the
    // knowledge of whether this package has ever asked and whether the answer came
    // back empty, they separate - and the names below are the cases the review found.

    [Fact]
    public void A_permanent_refusal_made_before_today_is_the_one_case_that_opens_settings()
        => Assert.True(MicrophoneAsk.ShouldOpenSettings(
            pressed: true, askedBefore: true, dismissed: false,
            rationaleBefore: false, rationaleAfter: false));

    [Fact]
    public void A_fresh_dont_ask_again_is_left_alone_because_they_just_said_it()
        // The dialog WAS shown (rationale true before) and they chose "don't ask
        // again" in it, which clears the flag. Opening Settings here argues with a
        // decision somebody made one second ago, over the top of their own app.
        => Assert.False(MicrophoneAsk.ShouldOpenSettings(
            pressed: true, askedBefore: true, dismissed: false,
            rationaleBefore: true, rationaleAfter: false));

    [Fact]
    public void A_first_refusal_is_left_alone()
        => Assert.False(MicrophoneAsk.ShouldOpenSettings(
            pressed: true, askedBefore: false, dismissed: false,
            rationaleBefore: false, rationaleAfter: true));

    [Fact]
    public void A_dialog_tapped_beside_or_backed_out_of_is_not_a_phone_that_will_not_ask()
        // Android 11+: a dismissal reports CANCELED, changes no flag, and the next
        // press shows the dialog again. The log line "the phone will not ask again"
        // was simply false here, and App info opened on top of a live question.
        => Assert.False(MicrophoneAsk.ShouldOpenSettings(
            pressed: true, askedBefore: true, dismissed: true,
            rationaleBefore: false, rationaleAfter: false));

    [Fact]
    public void Nothing_opens_settings_when_nobody_pressed_anything()
    {
        // The first-run ask is made on the person's behalf. Whatever the phone did
        // with it, a no is a no.
        foreach (var asked in new[] { true, false })
        foreach (var dismissed in new[] { true, false })
        foreach (var before in new[] { true, false })
        foreach (var after in new[] { true, false })
            Assert.False(MicrophoneAsk.ShouldOpenSettings(false, asked, dismissed, before, after),
                $"asked={asked} dismissed={dismissed} before={before} after={after}");
    }

    [Fact]
    public void A_permission_never_asked_about_never_opens_settings()
        => Assert.False(MicrophoneAsk.ShouldOpenSettings(
            pressed: true, askedBefore: false, dismissed: false,
            rationaleBefore: false, rationaleAfter: false));

    // ── when a client raises the question at all ─────────────────────────────

    [Theory]
    // The one state it is for: the service wants the microphone and has not got it.
    [InlineData(ResidentState.NeedsPermission, true, false, false, true)]
    // Already granted: the service is refusing for some other reason.
    [InlineData(ResidentState.NeedsPermission, true, true, false, false)]
    // Asked once on this visit already - a "no" must not meet the dialog again at 1 Hz.
    [InlineData(ResidentState.NeedsPermission, true, false, true, false)]
    // "Not installed" and "not linked yet" are the client's own words, not the
    // service's, and they are not about a microphone.
    [InlineData(ResidentState.NeedsPermission, false, false, false, false)]
    [InlineData(ResidentState.Listening, true, false, false, false)]
    [InlineData(ResidentState.Off, true, false, false, false)]
    [InlineData(ResidentState.NeedsForeground, true, false, false, false)]
    [InlineData(ResidentState.Unsupported, true, false, false, false)]
    [InlineData(ResidentState.Failed, true, false, false, false)]
    public void A_client_asks_only_when_the_service_says_it_needs_the_microphone(
        ResidentState state, bool fromService, bool granted, bool askedThisVisit, bool asks)
        => Assert.Equal(asks, MicrophoneAsk.ShouldAsk(state, fromService, granted, askedThisVisit));

    // ── whether the first-run card is offered ────────────────────────────────

    [Fact]
    public void A_phone_without_the_service_is_told_to_install_it()
    {
        var steps = MicrophoneAsk.TourFor(false, false, false, true);
        Assert.Equal("Install CircleAI", Assert.Single(steps).Title);
    }

    [Fact]
    public void An_unapproved_link_is_asked_for_before_a_microphone()
    {
        var steps = MicrophoneAsk.TourFor(true, false, false, true);
        Assert.Equal("Approve CircleAI", Assert.Single(steps).Title);
    }

    [Fact]
    public void The_microphone_is_offered_once_the_link_is_there_and_it_has_none()
        => Assert.Same(MicrophoneAsk.Step, Assert.Single(MicrophoneAsk.TourFor(true, true, false, true)));

    [Fact]
    public void Nothing_is_offered_once_the_microphone_is_allowed()
        => Assert.Empty(MicrophoneAsk.TourFor(true, true, true, true));

    [Fact]
    public void Nothing_is_offered_to_somebody_who_turned_listening_off()
        // It kept offering a microphone dialog for a feature they had declined, on
        // every download, because the step was chosen from the permission alone.
        => Assert.Empty(MicrophoneAsk.TourFor(true, true, false, ownerWantsListening: false));

    // ── what the tour button does ────────────────────────────────────────────

    [Theory]
    [InlineData("#microphone", true)]
    [InlineData("#background", true)]
    [InlineData("settings", false)]
    [InlineData("wake", false)]
    [InlineData("#", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_hash_route_is_done_here_and_anything_else_is_somewhere_to_go(string? route, bool here)
        => Assert.Equal(here, TourActions.ActsHere(route));

    [Fact]
    public async Task The_microphone_step_asks_and_says_it_can_hear_you()
    {
        var setup = new AnsweringSetup { Microphone = true };
        Assert.Equal(MicrophoneAsk.Allowed, await TourActions.RunAsync(setup, MicrophoneAsk.Route));
        Assert.Equal(1, setup.MicrophoneAsks);
    }

    [Fact]
    public async Task A_microphone_that_was_not_allowed_says_what_still_works()
    {
        var setup = new AnsweringSetup { Microphone = false };
        Assert.Equal(MicrophoneAsk.NotAllowed, await TourActions.RunAsync(setup, MicrophoneAsk.Route));
    }

    [Fact]
    public async Task A_question_still_on_screen_is_not_reported_as_a_refusal()
    {
        // The two-minute wait gives out with the dialog still up, or this head cannot
        // ask at all. Both used to print "Not allowed. It can still be typed to."
        var setup = new AnsweringSetup { Microphone = null };
        Assert.Equal(MicrophoneAsk.StillAsking, await TourActions.RunAsync(setup, MicrophoneAsk.Route));
    }

    [Fact]
    public async Task The_background_step_says_where_to_look_only_when_there_was_no_screen()
    {
        Assert.Null(await TourActions.RunAsync(new AnsweringSetup { Opens = true }, TourActions.BackgroundRoute));

        var none = await TourActions.RunAsync(new AnsweringSetup { Opens = false }, TourActions.BackgroundRoute);
        Assert.Contains("battery settings", none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_place_to_go_is_not_acted_on()
    {
        var setup = new AnsweringSetup();
        Assert.Null(await TourActions.RunAsync(setup, "languages"));
        Assert.Equal(0, setup.MicrophoneAsks + setup.BackgroundAsks);
    }

    [Fact]
    public async Task A_hash_route_nothing_handles_says_so_instead_of_doing_nothing()
    {
        // Both halves of this were silent: the switch returns null for an unknown
        // route, and null is also "it worked and there is nothing to say" - so a
        // renamed route left a live button that was a no-op.
        var said = new List<string>();
        var was = TourActions.Unknown;
        TourActions.Unknown = said.Add;
        try
        {
            Assert.Null(await TourActions.RunAsync(new AnsweringSetup(), "#nosuchthing"));
            Assert.Contains("#nosuchthing", Assert.Single(said), StringComparison.Ordinal);

            // A place to go is not an unhandled verb.
            said.Clear();
            await TourActions.RunAsync(new AnsweringSetup(), "languages");
            Assert.Empty(said);
        }
        finally { TourActions.Unknown = was; }
    }

    // ── the words ────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_run_step_asks_on_the_spot_and_names_no_phrase()
    {
        var step = MicrophoneAsk.Step;
        Assert.True(TourActions.ActsHere(step.Route), "the step would be a link to nowhere");
        Assert.Equal(MicrophoneAsk.Action, step.Action);

        // The phrase has one source, the wake screen; a literal here drifted before.
        Assert.DoesNotContain("Hey", step.Title + step.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_read_before_the_dialog_does_not_claim_that_nothing_is_kept()
    {
        // IT SAID "nothing is kept or sent", AND WHAT YOU ASK IT IS KEPT. The spoken
        // question and its answer go to asks.db and are purged after the retention
        // window - seven days - so the sentence a person reads immediately before
        // deciding was false about the one thing they would care about.
        var body = MicrophoneAsk.Step.Body;
        Assert.DoesNotContain("nothing is kept", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MicrophoneAsk.SoundNotKept, body, StringComparison.Ordinal);
        Assert.Contains(MicrophoneAsk.AsksKept, body, StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_kept_is_stated_with_the_window_the_service_actually_purges_on()
    {
        // ONE OWNER FOR THE NUMBER. A sentence saying "7 days" beside a service that
        // purges on a different window is the same defect as any other fact with two
        // owners, and it is one a person is entitled to rely on.
        var days = (int)Math.Round(CircleAI.Assistant.Requests.ServiceLimits.Default.Retention.TotalDays);
        Assert.Contains($"{days} days", MicrophoneAsk.AsksKept, StringComparison.Ordinal);
        Assert.Contains("this phone", MicrophoneAsk.AsksKept, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_line_before_the_dialog_says_what_is_being_agreed_to_and_who_asks()
    {
        // It said only "It listens for its name, so the phone will ask about the
        // microphone" - neither that it listens with the screen OFF, which is the
        // whole of what is being agreed to, nor that the dialog names a different app
        // from the one in front of them ("Circle AI Service").
        Assert.Contains("screen off", MicrophoneAsk.Announce, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Circle AI Service", MicrophoneAsk.Announce, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shade_does_not_promise_more_than_the_listening_keeps()
    {
        var shade = MicrophoneAsk.Shade("Hey B");
        Assert.Contains("Hey B", shade, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing is kept", shade, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" sent", shade, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_waiting_listener_names_its_own_reason_on_the_shade()
    {
        // "Not listening since the phone restarted" was hardcoded, and then a second
        // cause arrived that it describes wrongly: from Android 11 a wake word can be
        // waiting for a visible screen rather than for a power cycle, and rebooting
        // does not help.
        Assert.Contains("since the phone restarted",
            MicrophoneAsk.NotListening("since the phone restarted"), StringComparison.Ordinal);
        Assert.Contains("until an app of ours is open",
            MicrophoneAsk.NotListening("until an app of ours is open"), StringComparison.Ordinal);
        Assert.DoesNotContain("restarted", MicrophoneAsk.NotListening(null), StringComparison.Ordinal);

        foreach (var because in new string?[] { null, "", "   ", "since the phone restarted" })
            Assert.Contains("tap", MicrophoneAsk.NotListening(because), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_hint_is_advice_anybody_can_follow()
    {
        // It said "Approve the link again from the app", which our own app - approved
        // without a screen - could never do.
        Assert.StartsWith(MicrophoneAsk.Action, MicrophoneAsk.NeededHint, StringComparison.Ordinal);
        Assert.DoesNotContain("link", MicrophoneAsk.NeededHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approve", MicrophoneAsk.NeededHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Waiting_for_a_visible_screen_is_explained_as_something_that_fixes_itself()
    {
        // It is not a failure and there is nothing for the person to do, so the words
        // must not read as either.
        Assert.DoesNotContain("fail", MicrophoneAsk.NeedsForegroundHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", MicrophoneAsk.NeedsForegroundHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("open", MicrophoneAsk.NeedsForegroundHint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A setup that answers the two "#" questions and nothing else.</summary>
    private sealed class AnsweringSetup : ISetup
    {
        /// <summary>Null is "nothing was answered" - see ISetup.AllowMicrophoneAsync.</summary>
        public bool? Microphone { get; init; }
        public bool Opens { get; init; }
        public int MicrophoneAsks;
        public int BackgroundAsks;

        public bool IsRunning => false;

        public Task<bool?> AllowMicrophoneAsync(CancellationToken ct = default)
        {
            MicrophoneAsks++;
            return Task.FromResult(Microphone);
        }

        public Task<bool> AllowBackgroundAsync(CancellationToken ct = default)
        {
            BackgroundAsks++;
            return Task.FromResult(Opens);
        }

        public Task<Readiness> ReadinessAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Census> CensusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> LinkApprovedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ApproveLinkAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── the Android half, guarded at the source ──────────────────────────────

    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CircleAI.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Source(params string[] parts)
    {
        var path = Path.Combine(Repo(), Path.Combine(parts));
        Assert.True(File.Exists(path), path + " has moved - this guard is now pointing at nothing");
        return File.ReadAllText(path);
    }

    /// <summary>A source file with its comments removed, so a guard cannot match prose.</summary>
    /// <remarks>
    /// EVERY ONE OF THESE GUARDS USED TO BE SATISFIABLE BY A COMMENT. They search for
    /// a call, and this file's own paragraphs quote the calls they are about - so a
    /// deleted line could stay "covered" by the note explaining why it exists.
    /// </remarks>
    private static string Code(params string[] parts)
    {
        var text = Source(parts);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[ \t]*///.*$", "", RegexOptions.Multiline);
        return text;
    }

    [Fact]
    public void The_service_has_a_screen_that_asks_and_only_for_callers_trusted_with_voice()
    {
        var activity = Code("src", "CircleAI", "Device", "MicrophoneActivity.cs");

        Assert.Contains("Name = \"ai.circle.MicrophoneActivity\"", activity, StringComparison.Ordinal);
        Assert.Contains("Exported = true", activity, StringComparison.Ordinal);
        Assert.Contains("LinkIpc.MicrophoneAction", activity, StringComparison.Ordinal);

        var voice  = activity.IndexOf("LinkScope.Voice", StringComparison.Ordinal);
        var gate   = activity.IndexOf("MicrophoneAsk.MayAsk(", StringComparison.Ordinal);
        var ask    = activity.IndexOf("await AskAsync()", StringComparison.Ordinal);
        var ok     = activity.IndexOf("SetResult(Result.Ok)", StringComparison.Ordinal);
        var listen = activity.IndexOf("await Task.WhenAny(start,", StringComparison.Ordinal);
        var finish = activity.IndexOf("Finish();", StringComparison.Ordinal);

        Assert.True(voice >= 0 && gate > voice, "the caller is not judged for Voice before anything else");
        Assert.True(ask > gate, "the dialog can be raised for a caller nobody trusted with Voice");
        Assert.True(ok > ask, "Ok is returned before the microphone was allowed");

        // While the screen is still visible, which is when Android 11+ lets a
        // foreground service hold the microphone - and AWAITED, so the start happens
        // before the window goes rather than after it.
        Assert.True(listen > ok && finish > listen, "the listener starts after this screen has gone");

        // EVERY REFUSAL BRANCH ENDS THE METHOD. The gate is a sequence of "log it and
        // return"s, and a lost `return` would let any app on the phone raise Circle
        // AI's microphone dialog with the branch still reading as a refusal.
        foreach (var refusal in Regex.Matches(activity, @"Log\.Warn\(Tag,\s*\$?""refused[^;]*;", RegexOptions.Singleline))
        {
            var after = activity[(((Match)refusal).Index + ((Match)refusal).Length)..];
            Assert.StartsWith("return;", after.TrimStart(), StringComparison.Ordinal);
        }

        // The caller is whoever Android says it is, never an extra on the intent.
        Assert.Contains("var caller = CallingPackage;", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("GetStringExtra(\"caller\")", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void Whether_a_refusal_opens_settings_is_decided_by_the_tested_rule()
    {
        var activity = Code("src", "CircleAI", "Device", "MicrophoneActivity.cs");

        // THE WHOLE EXPRESSION, NOT A FRAGMENT. The old guard searched for
        // "!allowed && !quiet && !ShouldShowRequestPermissionRationale(" - which is
        // the broken condition it was meant to protect, and would have passed on it
        // for ever. The decision is now a function with a truth table above.
        Assert.Contains("MicrophoneAsk.ShouldOpenSettings(", activity, StringComparison.Ordinal);
        Assert.Contains("ActionApplicationDetailsSettings", activity, StringComparison.Ordinal);
        Assert.Contains("GetBooleanExtra(LinkIpc.QuietExtra, false)", activity, StringComparison.Ordinal);

        // Read BEFORE the request as well as after it: one reading cannot tell "the
        // phone showed nothing" from "they just said never again".
        var before  = activity.IndexOf("var rationaleBefore = ShouldShowRequestPermissionRationale(", StringComparison.Ordinal);
        var request = activity.IndexOf("await AskAsync()", StringComparison.Ordinal);
        var after   = activity.IndexOf("var rationaleAfter = ShouldShowRequestPermissionRationale(", StringComparison.Ordinal);
        Assert.True(before >= 0 && request > before && after > request,
            "the rationale is not read on both sides of the request");

        // And "has this package ever asked" is recorded, because the platform does
        // not distinguish "will not ask" from "was never asked".
        Assert.Contains("MicrophonePrefs.Asked(this)", activity, StringComparison.Ordinal);
        Assert.Contains("MicrophonePrefs.HasAsked(this)", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interrupted_question_is_not_an_answer()
    {
        var activity = Code("src", "CircleAI", "Device", "MicrophoneActivity.cs");

        // Android sends an EMPTY result array when the interaction was interrupted
        // rather than answered - a tap outside the dialog, or Back on Android 11+.
        // Read as a refusal, it sent people to Settings for tapping beside a dialog.
        Assert.Contains("grantResults.Length == 0", activity, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource<bool?>", activity, StringComparison.Ordinal);

        // A screen rebuilt under its own dialog must not ask again: Android answers a
        // repeat request immediately and emptily.
        Assert.Contains("savedInstanceState is not null", activity, StringComparison.Ordinal);
        Assert.Contains("ConfigChanges.UiMode", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_that_asks_is_also_the_one_that_starts_listening()
    {
        var activity = Code("src", "CircleAI", "Device", "MicrophoneActivity.cs");

        // THE HALF THAT WAS MISSING, and why a fresh install on Android 11+ stayed
        // deaf. The service is brought up WHILE THIS SCREEN IS VISIBLE even when the
        // wake bundle has not arrived, because Android latches the while-in-use
        // allowance on the service record at the start that was made from a visible
        // window - and the start that actually opens the microphone comes minutes
        // later, from a download thread.
        var serviceUp = activity.IndexOf("CircleNeuronService.Start(this)", StringComparison.Ordinal);
        var visible   = activity.IndexOf("fromVisibleScreen: true", StringComparison.Ordinal);
        Assert.True(serviceUp >= 0, "the service is not brought up from the visible screen");
        Assert.True(visible > serviceUp, "the listener is not told the start came from a visible screen");

        // And never against an owner who switched listening off.
        var wanted = activity.IndexOf("ResidentPrefs.WasRunning(this)", StringComparison.Ordinal);
        Assert.True(wanted >= 0 && wanted < serviceUp, "a foreground service is started for a feature they turned off");

        // The start is bounded: this screen is invisible and swallows touches.
        Assert.Contains("StartPatience", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_manifests_put_the_screen_in_the_service_and_not_in_the_app()
    {
        var service = Source("src", "CircleAIService", "Platforms", "Android", "AndroidManifest.xml");
        Assert.Contains("android.permission.RECORD_AUDIO", service, StringComparison.Ordinal);

        // The app references the product, so the activity's attributes would declare
        // an exported microphone screen in the app too. It is removed by name, like
        // every other service component.
        var app = Source("samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.App", "Platforms", "Android", "AndroidManifest.xml");
        Assert.Contains("<activity android:name=\"ai.circle.MicrophoneActivity\" tools:node=\"remove\" />", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wake_screen_asks_quietly_on_arrival_and_loudly_only_at_a_press()
    {
        var wake = Code("src", "CircleAI", "Client", "LinkedWakeWord.cs");

        Assert.Contains("ServiceMicrophone.RequestAsync(", wake, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.FromResult(false)", wake, StringComparison.Ordinal);

        // THE ASK ON ARRIVAL IS QUIET. Opening this screen asks for the microphone -
        // the same reading the start makes - and with one path for the arrival and the
        // button, a permanently refused phone opened Circle AI Service's page in
        // Settings on EVERY VISIT, with nothing pressed.
        var listen = wake.IndexOf("public async Task ListenAsync(", StringComparison.Ordinal);
        Assert.True(listen >= 0, "ListenAsync has moved");
        var body = wake[listen..];
        Assert.Contains("MicrophoneAsk.ShouldAsk(", body, StringComparison.Ordinal);
        Assert.Contains("RequestMicrophoneAsync(quietly: true)", body, StringComparison.Ordinal);

        // And the screen fixes the one state it is in a position to fix.
        Assert.Contains("ResidentState.NeedsForeground", body, StringComparison.Ordinal);
        Assert.Contains("ServiceMicrophone.StartListening(", body, StringComparison.Ordinal);

        // The screen itself presses for the loud one.
        var razor = Source("samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.Shared", "Pages", "WakeWord.razor");
        Assert.Contains("RequestMicrophoneAsync(quietly: false)", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_it_on_asks_and_resuming_restores_a_choice_without_making_one()
    {
        var resident = Code("src", "CircleAI", "Client", "LinkedResidentAssistant.cs");

        var start   = resident.IndexOf("public async Task<ResidentStatus> StartAsync(", StringComparison.Ordinal);
        var stop    = resident.IndexOf("public async Task<ResidentStatus> StopAsync(", StringComparison.Ordinal);
        var resume  = resident.IndexOf("public async Task<ResidentStatus> ResumeAsync(", StringComparison.Ordinal);
        var refresh = resident.IndexOf("public async Task<ResidentStatus> RefreshAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start && resume > stop && refresh > resume, "LinkedResidentAssistant has been reshaped");

        var startBody = resident[start..stop];
        Assert.Contains("ServiceMicrophone.RequestAsync(", startBody, StringComparison.Ordinal);

        // "Not linked yet" is NeedsPermission too, and it is not about a microphone.
        Assert.Contains("fromService", startBody, StringComparison.Ordinal);

        // A SWITCH SWITCHES: ON, OFF, NOTHING ELSE. This method is what both
        // microphone switches are wired to, and asking loudly meant that flipping one
        // on, on a phone where the microphone had been refused for good, took the
        // person out of the app and onto Circle AI Service's page in Android's
        // Settings. The dialog still comes up; the trip to Settings belongs to a
        // button whose label is that verb.
        Assert.Contains("quietly: true", startBody, StringComparison.Ordinal);

        // RESUME SENDS A VERB THAT CAN SAY NO. There was no resume verb, so this sent
        // ResidentStart - which reads no preference and records the owner as wanting
        // the microphone on its way out. It runs on every cold launch, so an explicit
        // "off" lasted until the next time the app was opened.
        var resumeBody = resident[resume..refresh];
        Assert.Contains("ResidentResumeAsync(ct)", resumeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResidentStartAsync(ct)", resumeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceMicrophone.RequestAsync(", resumeBody, StringComparison.Ordinal);

        // An older service answers "unknown verb"; the fallback must not be a start.
        Assert.Contains("ResidentStatusAsync(ct)", resumeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_resume_verb_exists_on_both_sides_and_is_gated_on_voice()
    {
        var verbs = Code("src", "CircleAI", "Linking", "LinkVerbs.cs");
        Assert.Contains("ResidentResume,", verbs, StringComparison.Ordinal);
        Assert.Contains("LinkVerb.ResidentResume => LinkScope.Voice,", verbs, StringComparison.Ordinal);

        // The service maps it to ResumeAsync, which is the method that reads the
        // owner's choice. Mapped to StartAsync it would be the same bug with a new
        // name on it.
        var service = Code("src", "CircleAI", "Device", "CircleNeuronLinkService.cs");
        Assert.Contains("LinkVerb.ResidentResume => await resident.ResumeAsync()", service, StringComparison.Ordinal);
    }

    [Fact]
    public void The_setup_tour_offers_the_microphone_through_the_tested_rule_and_asks_once()
    {
        var setup = Code("samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.App", "Services", "ServiceSetup.cs");

        var allow = setup.IndexOf("public Task<bool?> AllowMicrophoneAsync(", StringComparison.Ordinal);
        Assert.True(allow >= 0, "AllowMicrophoneAsync has moved");
        Assert.Contains("ServiceMicrophone.RequestAsync(", setup[allow..setup.IndexOf(';', allow)], StringComparison.Ordinal);

        // THE DECISION IS THE PRODUCT'S, not a condition written here. It used to be
        // "granted ? none : [the step]", which offered a microphone dialog to somebody
        // who had switched listening off.
        Assert.Contains("MicrophoneAsk.TourFor(", setup, StringComparison.Ordinal);

        // Asked on the person's behalf, quietly, and ONCE IN AN INSTALLATION'S LIFE.
        var offer = setup.IndexOf("public Task<bool?> OfferMicrophoneAsync(", StringComparison.Ordinal);
        Assert.True(offer >= 0, "the first-run ask is gone from the head");
        var offerBody = setup[offer..setup.IndexOf("\n    }", offer, StringComparison.Ordinal)];
        Assert.Contains("quietly: true", offerBody, StringComparison.Ordinal);
        Assert.Contains("OfferedOnce.Take()", offerBody, StringComparison.Ordinal);

        // A flag in memory would be exactly as absent as no flag, because the person
        // who said no reopens the app in a new process.
        Assert.Contains("GetSharedPreferences(", setup, StringComparison.Ordinal);

        // One answer must never be taken for the other.
        var microphone = Code("src", "CircleAI", "Client", "ServiceMicrophone.cs");
        var mine    = Regex.Match(microphone, @"RequestCode = (0x[0-9a-fA-F]+);").Groups[1].Value;
        var consent = Regex.Match(setup, @"ConsentRequest = (0x[0-9a-fA-F]+);").Groups[1].Value;
        Assert.NotEqual(string.Empty, mine);
        Assert.NotEqual(string.Empty, consent);
        Assert.NotEqual(consent, mine);
    }

    [Fact]
    public void A_timed_out_question_does_not_block_the_next_one()
    {
        var microphone = Code("src", "CircleAI", "Client", "ServiceMicrophone.cs");

        // The shared answer stayed, uncompleted, for the life of the process - and a
        // new dialog is only launched when the current one is COMPLETE. So after one
        // lost answer, every microphone button in the app waited two minutes behind a
        // dialog that was never raised and then said "Not allowed".
        var timeout = microphone.IndexOf("catch (TimeoutException)", StringComparison.Ordinal);
        Assert.True(timeout >= 0, "the wait no longer times out");
        var body = microphone[timeout..];
        Assert.Contains("Forget(answer)", body, StringComparison.Ordinal);

        // And "no answer" is not "no".
        Assert.Contains("public static async Task<bool?> RequestAsync(", microphone, StringComparison.Ordinal);
        Assert.Contains("Granted(activity) ? true : null", microphone, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_hands_the_answer_back_and_gives_both_askers_the_activity_in_front()
    {
        var program = Source("samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.App", "MauiProgram.cs");

        Assert.Contains("OnActivityResult(", program, StringComparison.Ordinal);
        Assert.Contains("ServiceMicrophone.Answered(requestCode, resultCode)", program, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"new CircleAI\.Client\.LinkedWakeWord\([^;]*Platform\.CurrentActivity", RegexOptions.Singleline), program);
        Assert.Matches(new Regex(@"new CircleAI\.Client\.LinkedResidentAssistant\([^;]*Platform\.CurrentActivity", RegexOptions.Singleline), program);
    }

    [Fact]
    public void The_service_says_why_it_is_not_listening_and_distinguishes_every_reason()
    {
        var control = Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs");

        Assert.Contains("MicrophoneAsk.NeededHint", control, StringComparison.Ordinal);
        Assert.Contains("not listening: the microphone has not been allowed", control, StringComparison.Ordinal);
        Assert.Contains("not listening: the wake word is not downloaded yet", control, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve the link again", control, StringComparison.Ordinal);

        // FOUR REASONS, NOT ONE. A wanted listener that is not running is waiting for
        // the microphone, for its bundle, or for a visible screen - and "Turn it on to
        // wake it by name" is wrong for all three.
        var refresh = control.IndexOf("public Task<ResidentStatus> RefreshAsync(", StringComparison.Ordinal);
        Assert.True(refresh >= 0, "RefreshAsync has moved");
        var body = control[refresh..control.IndexOf("private bool Wanted()", refresh, StringComparison.Ordinal)];
        Assert.Contains("Wanted()", body, StringComparison.Ordinal);
        Assert.Contains("NeedsMicrophone()", body, StringComparison.Ordinal);
        Assert.Contains("NotDownloaded()", body, StringComparison.Ordinal);
        Assert.Contains("WaitingForAScreen()", body, StringComparison.Ordinal);

        // And the refusal no longer advises a setup that would skip the bundle.
        Assert.DoesNotContain("Set CircleAI up and it will fetch", control, StringComparison.Ordinal);
    }

    [Fact]
    public void An_off_is_recorded_whether_or_not_anything_was_listening()
    {
        var control = Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs");

        var stop = control.IndexOf("public async Task<ResidentStatus> StopAsync(", StringComparison.Ordinal);
        Assert.True(stop >= 0, "StopAsync has moved");
        var body = control[stop..control.IndexOf("public Task<ResidentStatus> ResumeAsync(", stop, StringComparison.Ordinal)];

        // IT SAT INSIDE `if (_ears is not null)`, AND WasRunning DEFAULTS TO YES. So an
        // "off" given while nothing happened to be listening wrote nothing at all, the
        // default won, and every later path - the resume at process start, the
        // microphone grant, the fetch's landed hook - read the owner as wanting it on.
        var guard  = body.IndexOf("if (_ears is not null)", StringComparison.Ordinal);
        var record = body.IndexOf("Remember(false)", StringComparison.Ordinal);
        Assert.True(record >= 0, "an off is no longer recorded");
        Assert.True(guard >= 0 && record > body.IndexOf('}', guard), "the record is still inside the listener guard");
    }

    [Fact]
    public void Resuming_reads_the_owners_choice_and_starting_does_not()
    {
        var control = Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs");

        var resume = control.IndexOf("public Task<ResidentStatus> ResumeAsync(", StringComparison.Ordinal);
        Assert.True(resume >= 0, "ResumeAsync has moved");
        var body = control[resume..control.IndexOf("public Task<ResidentStatus> RefreshAsync(", resume, StringComparison.Ordinal)];

        // It forwarded straight to StartAsync, which reads no preference and writes
        // true - so resuming MADE the choice it is documented to restore.
        Assert.Contains("Wanted()", body, StringComparison.Ordinal);
        Assert.Contains("Off()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_listener_is_installed_before_the_service_is_started()
    {
        var control = Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs");

        var start = control.IndexOf("public async Task<ResidentStatus> StartAsync(bool fromVisibleScreen", StringComparison.Ordinal);
        Assert.True(start >= 0, "the start has been reshaped");
        var body = control[start..control.IndexOf("private async Task LetGoAsync()", start, StringComparison.Ordinal)];

        // THE FOREGROUND-SERVICE TYPE WAS DECIDED BY A RACE. ClaimedTypes reads the
        // listener to decide whether to claim FOREGROUND_SERVICE_TYPE_MICROPHONE, and
        // OnStartCommand runs on the main looper while this method runs on whatever
        // thread asked - so starting the service before installing the listener left
        // which of the two won to chance, and the losing order claimed dataSync only.
        // A foreground service without the microphone type gets no microphone on any
        // Android from 11 on.
        var listener = body.IndexOf("CircleNeuronService.Listener = ears", StringComparison.Ordinal);
        var service  = body.IndexOf("CircleNeuronService.Start(context)", StringComparison.Ordinal);
        Assert.True(listener >= 0 && service > listener,
            "the service is started before the listener is installed, so its type is a race");

        // AND THE KEYWORD FILE IS REFRESHED BEFORE ITS EXISTENCE IS TESTED. The line
        // that builds the listener chooses between the owner's phrase and the bundle's
        // built-in one by asking whether that file is there, and on a fresh install
        // EnsureCurrent is what puts it there - so the other order makes every first
        // start listen for the shipped phrase whatever the owner chose.
        var refresh = body.IndexOf("EnsureCurrent(\"en\")", StringComparison.Ordinal);
        var tested  = body.IndexOf("File.Exists(keywords)", StringComparison.Ordinal);
        Assert.True(refresh >= 0 && tested > refresh,
            "the keyword file is tested for before it is written, so the owner's phrase is skipped");

        // And it is cleared on every way out, or a later start claims a type the
        // platform will refuse and the service does not come up at all.
        Assert.Contains("LetGoAsync()", body, StringComparison.Ordinal);
        Assert.Contains("CircleNeuronService.Listener = null;",
            Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_microphone_the_platform_is_silencing_is_not_reported_as_listening()
    {
        var control = Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs");

        // GRANTED IS NOT ALLOWED, and from Android 12 the difference is silence with
        // no error: RECORD_AUDIO's app-op sits at MODE_FOREGROUND and is evaluated
        // against what this uid is doing, so a foreground service started from the
        // background records zeros while CheckSelfPermission still answers GRANTED.
        Assert.Contains("UnsafeCheckOpNoThrow(", control, StringComparison.Ordinal);
        Assert.Contains("OpstrRecordAudio", control, StringComparison.Ordinal);

        var check = control.IndexOf("private bool CanBeHeard()", StringComparison.Ordinal);
        Assert.True(check >= 0, "CanBeHeard has moved");
        var body = control[check..control.IndexOf("public async Task<ResidentStatus> StopAsync(", check, StringComparison.Ordinal)];

        // NOTHING OLDER THAN ANDROID 11 IS ASKED. The P30 is API 29 and has a measured,
        // working wake word started from the background; a check that could answer "no"
        // there would switch it off on the one phone this product is calibrated against.
        Assert.Contains("BuildVersionCodes.R", body, StringComparison.Ordinal);

        // And a phone that will not answer keeps its wake word.
        Assert.Contains("return true;", body[body.IndexOf("catch (Exception ex)", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_microphone_type_does_not_take_the_brain_down_with_it()
    {
        var service = Code("src", "CircleAI", "Device", "CircleNeuronService.cs");

        var command = service.IndexOf("public override StartCommandResult OnStartCommand(", StringComparison.Ordinal);
        Assert.True(command >= 0, "OnStartCommand has moved");
        var body = service[command..service.IndexOf("private async System.Threading.Tasks.Task BuildNodeAsync()", command, StringComparison.Ordinal)];

        // THE PARAMETERLESS CALL IS NOT UNTYPED. Service.startForeground(int,
        // Notification) passes FOREGROUND_SERVICE_TYPE_MANIFEST, which Android expands
        // to the manifest's types - dataSync AND microphone - so the fallback for a
        // refused microphone type was retried with the same microphone bit in it and
        // threw again. The service then never went foreground at all.
        var typed    = body.IndexOf("StartForeground(NotificationId, notification, claimed)", StringComparison.Ordinal);
        var dataSync = body.IndexOf("ForegroundService.TypeDataSync)", typed < 0 ? 0 : typed, StringComparison.Ordinal);
        Assert.True(typed >= 0 && dataSync > typed, "a refused microphone type does not fall back to an explicit dataSync");

        // AND State = Failed IS THE BRAIN'S WORD, not the foreground type's. It is what
        // a client reads as "stop waiting, it will never answer", and one refused
        // microphone claim turned into a device that answered no questions at all.
        Assert.DoesNotContain("State  = ServiceState.Failed;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("State = ServiceState.Failed;", body, StringComparison.Ordinal);

        // The probe and the memory callbacks are what the brain is built from, and
        // returning early skipped all of them.
        var probe = body.IndexOf("AndroidDeviceMemory.Install(this)", StringComparison.Ordinal);
        Assert.True(probe > 0, "the device probe is no longer installed");
        Assert.Contains("if (foreground) KeepNotificationAlive();", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_head_with_nothing_to_offer_asks_nothing_and_says_nothing_happened()
    {
        // The default the browser head and every test double inherit. It returned
        // FALSE, which a screen renders as "Not allowed. It can still be typed to." -
        // about a question that was never asked.
        var setup = new AnsweringSetup { Microphone = true };
        Assert.Null(await ((ISetup)setup).OfferMicrophoneAsync());
        Assert.Equal(0, setup.MicrophoneAsks);
    }
}
