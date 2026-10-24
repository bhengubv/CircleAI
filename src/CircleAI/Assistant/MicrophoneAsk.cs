// MicrophoneAsk.cs
//
// Giving Circle AI the microphone: who may raise the question, when it is raised,
// and what is said around it.
//
// THE WAKE WORD COULD NOT LISTEN ON A FRESH INSTALL, AND NOTHING SAID WHY. The
// microphone belongs to the SERVICE's package, a runtime permission needs an Activity
// to ask for it, and the only Activity that asked was the link-approval screen -
// which our own app never sees, because an app signed with our key is approved
// without a prompt. So on the P30 on 2026-10-23, with both 1.1 pairs: "resuming: the
// owner had it on", then silence; RECORD_AUDIO granted=false; no screen anywhere
// that could change it.
//
// The Android half (MicrophoneActivity in the service, ServiceMicrophone in a client)
// is untestable off a phone. The decisions and the words are here, where a test
// reaches them - and the review of 2026-10-23 is why there are now DECISIONS here at
// all. Four of its findings were one defect each in a condition written inline at the
// call site, guarded only by a test that searched the source for a string:
//
//   "the phone will not ask again" was judged AFTER the request, so a person's own
//   "don't ask again", and a dialog they dismissed, both opened Settings;
//   the wake screen's arrival ask was indistinguishable from its button's;
//   the tour offered the microphone step whatever the owner had chosen.
//
// Every one of those is a truth table, and a truth table belongs where xUnit can
// read it.

using CircleAI.Linking;

namespace CircleAI.Assistant;

/// <summary>Who may make the phone ask for Circle AI's microphone, when, and the words around it.</summary>
public static class MicrophoneAsk
{
    /// <summary>The tour route that asks for the microphone where the button is.</summary>
    /// <remarks>A "#" route acts on the spot; anything else is a place to go.</remarks>
    public const string Route = "#microphone";

    /// <summary>The words on every button that asks.</summary>
    public const string Action = "Allow the microphone";

    /// <summary>Said where it was asked, once it may listen.</summary>
    public const string Allowed = "It can hear you now.";

    /// <summary>Said where it was asked, when it may not - and what still works.</summary>
    public const string NotAllowed = "Not allowed. It can still be typed to.";

    /// <summary>Said where it was asked when the question is still on screen somewhere.</summary>
    /// <remarks>
    /// "NOT ALLOWED" WAS PRINTED OVER AN UNANSWERED DIALOG. ServiceMicrophone gives up
    /// waiting after two minutes and reads the permission, which is the right thing to
    /// do with a screen that must come back - but a person who put the phone down and
    /// came back to allow it was then told, under their own Allow, that it was not
    /// allowed. A pending question is its own answer and says so.
    /// </remarks>
    public const string StillAsking = "Still asking. Allow it when the phone asks.";

    /// <summary>
    /// Said before the phone asks on the person's behalf, so the dialog is not a surprise.
    /// </summary>
    /// <remarks>
    /// IT NAMED NEITHER THE ALWAYS-ON PART NOR THE APP DOING THE ASKING. The system
    /// dialog reads "Allow Circle AI Service to record audio?" - a different name from
    /// the app in front of the person - and nothing in this line said the microphone
    /// stays open with the screen off, which is the whole of what is being agreed to.
    /// Both facts are in the sentence now, because it is the last thing read before
    /// the decision.
    /// </remarks>
    public const string Announce =
        "It listens for its name even with the screen off, so Circle AI Service will "
        + "now ask for the microphone.";

    /// <summary>The large line when the service cannot listen for want of the microphone.</summary>
    public const string Needed = "CircleAI cannot use the microphone";

    /// <summary>The quiet line under it.</summary>
    /// <remarks>
    /// IT SAID "APPROVE THE LINK AGAIN FROM THE APP AND ALLOW THE MICROPHONE WHEN
    /// ASKED", which nobody using our own app could do: that app is approved without
    /// a screen, so there was no link to approve again and nothing that would ask.
    /// Advice that cannot be followed is the silent failure with words on it.
    /// </remarks>
    public const string NeededHint = "Allow the microphone and it will answer to its name.";

    /// <summary>The large line when the phone will not let it listen from the background.</summary>
    /// <remarks>
    /// THE STATE THE CODE COULD NOT NAME, AND THE ONE ANDROID 11 PUT EVERY PHONE IN.
    /// From Android 11 a foreground service STARTED FROM THE BACKGROUND gets no
    /// microphone: on 11 the recorder is refused, on 12 and 13 it is silenced, and
    /// from 14 the microphone-typed start is refused outright. The wake word's bundle
    /// lands minutes after the app has gone, so that is precisely the start this
    /// product makes - and it reported "Listening" while it heard a silent room.
    /// <para>
    /// Nothing is wrong with the phone and nothing is wrong with the permission. It
    /// needs one visible moment to open the microphone in, which the next launch
    /// gives it - so this says so instead of claiming to listen.
    /// </para>
    /// </remarks>
    public const string NeedsForeground = "Waiting to start listening";

    /// <summary>The quiet line under it.</summary>
    public const string NeedsForegroundHint =
        "This phone only lets it open the microphone while an app of ours is on "
        + "screen. It starts itself the next time you open Circle AI.";

    /// <summary>What listening does with the sound, in one sentence that is true.</summary>
    /// <remarks>
    /// THE SHADE, THE CARD, THE WAKE SCREEN AND TWO HINTS ALL MADE THIS CLAIM AND IT
    /// WAS NOT TRUE. Five places said "nothing is kept or sent" about a feature whose
    /// spoken questions are written to asks.db with their answers and purged after
    /// <see cref="Requests.ServiceLimits.Retention"/> - seven days. The audio is not
    /// kept, which is the narrow thing the code does and the broad thing the words
    /// claimed.
    /// <para>
    /// So the claim is split in two and both halves live here:
    /// <see cref="SoundNotKept"/> is about the listening, and <see cref="AsksKept"/>
    /// is about what you say to it. Any screen making one of these claims reads it
    /// from here.
    /// </para>
    /// </remarks>
    public const string SoundNotKept = "Listening happens on this phone, and that sound is not kept.";

    /// <summary>What becomes of a question once it has been asked.</summary>
    public static string AsksKept { get; } =
        "What you ask after its name is kept on this phone, with its answer, for "
        + Days(Requests.ServiceLimits.Default.Retention) + " days.";

    /// <summary>Whole days in a retention window, for a sentence.</summary>
    private static int Days(TimeSpan window) => (int)System.Math.Round(window.TotalDays);

    /// <summary>The microphone disclosure for the notification shade.</summary>
    /// <param name="phrase">What it is listening for, in the listener's own words.</param>
    /// <remarks>
    /// THE LINE SOMEBODY READS ALL DAY, so it is the one that most had to be true -
    /// and it said "nothing is kept or sent" while asks.db held what they had asked
    /// it. Short, because it is a notification: the listening is what the shade is
    /// disclosing, and the listening really does keep nothing.
    /// </remarks>
    public static string Shade(string phrase) => $"Listening for “{phrase}” — that sound is not kept";

    /// <summary>The shade's line when it is wanted and not running.</summary>
    /// <param name="because">
    /// Why, in a fragment that follows "Not listening" - "since the phone restarted",
    /// "until an app of ours is open". Empty for no reason given.
    /// </param>
    /// <remarks>
    /// TWO CAUSES, ONE LINE, AND IT USED TO NAME ONLY THE REBOOT. From Android 11 a
    /// wake word can also be waiting for a visible screen to open the microphone in,
    /// which looks identical from the shade and needs the same single tap.
    /// </remarks>
    public static string NotListening(string? because) =>
        string.IsNullOrWhiteSpace(because)
            ? "Not listening — tap to switch it on"
            : $"Not listening {because.Trim()} — tap to switch it on";

    /// <summary>The first-run step that asks, offered while the service does not have it.</summary>
    /// <remarks>
    /// NAMELESS ON PURPOSE, like the tour's other wake step: the phrase has one
    /// source and it is the wake screen, not a literal here.
    /// </remarks>
    public static TourStep Step { get; } = new(
        "Let it hear its name",
        "Say its name and it answers, even with the screen off. " + SoundNotKept + " " + AsksKept,
        Action,
        Route);

    /// <summary>Whether a caller the link gate judged <paramref name="decision"/> may raise the question.</summary>
    /// <remarks>
    /// THE SAME LINE THE LINK DRAWS FOR VOICE. Our own apps (AutoApprove) and apps a
    /// person approved for Voice (Allow) may ask. Anything else is refused without a
    /// prompt: a permission dialog raised on behalf of an app nobody approved to
    /// listen is a permission harvested on the back of nothing.
    /// </remarks>
    public static bool MayAsk(LinkDecisionKind decision) =>
        decision is LinkDecisionKind.Allow or LinkDecisionKind.AutoApprove;

    /// <summary>
    /// Whether to send somebody to this app's page in Settings after a refusal.
    /// </summary>
    /// <param name="pressed">
    /// True when a person pressed a microphone control. False when the phone asked on
    /// their behalf, where a no is simply a no.
    /// </param>
    /// <param name="askedBefore">Whether this package has ever raised the dialog.</param>
    /// <param name="dismissed">
    /// True when the request came back with NO answer at all - an empty result array,
    /// which Android sends when the interaction was interrupted: a tap outside the
    /// dialog, or Back on Android 11 and later.
    /// </param>
    /// <param name="rationaleBefore">
    /// <c>shouldShowRequestPermissionRationale</c> read BEFORE the request.
    /// </param>
    /// <param name="rationaleAfter">The same call read after it.</param>
    /// <remarks>
    /// READ BEFORE AND AFTER, BECAUSE ONE READING CANNOT TELL THE TWO CASES APART.
    /// Android returns false from that call both when it will no longer show the
    /// dialog AND for a permission nobody has ever denied - so judged only after the
    /// request it is false in three completely different situations:
    /// <list type="bullet">
    /// <item>the permission is fixed and no dialog was shown - the one case Settings
    /// is the honest answer to;</item>
    /// <item>the person had denied once, was shown the dialog, and has just chosen
    /// "don't ask again" - so Settings opens over the top of their own decision;</item>
    /// <item>Android 11+ only: the dialog was shown and dismissed, which changes no
    /// flag at all - and the NEXT press would have shown it again, so the log line
    /// "the phone will not ask again" was simply false.</item>
    /// </list>
    /// Before AND after separates them. Rationale true before means the dialog was
    /// shown, whatever the answer; an empty answer means it was shown and dismissed.
    /// Only false-before, false-after, asked-before is "the phone showed nothing",
    /// and only a press earns the jump even then.
    /// <para>
    /// Android's own guidance is the floor here: "respect the user's decision. Don't
    /// link to system settings in an effort to convince the user to change their
    /// decision."
    /// </para>
    /// </remarks>
    public static bool ShouldOpenSettings(
        bool pressed, bool askedBefore, bool dismissed,
        bool rationaleBefore, bool rationaleAfter)
        => pressed && askedBefore && !dismissed && !rationaleBefore && !rationaleAfter;

    /// <summary>
    /// Whether a client should raise the question for what the service just said.
    /// </summary>
    /// <param name="state">The state the service reported.</param>
    /// <param name="fromService">
    /// False when the status is the client's own ("not installed", "not linked yet") -
    /// those are also NeedsPermission and are not about a microphone.
    /// </param>
    /// <param name="granted">Whether the service already holds RECORD_AUDIO.</param>
    /// <param name="askedThisVisit">Whether this screen has already asked once.</param>
    /// <remarks>
    /// ONE RULE, TWO CLIENTS. The wake screen and the settings switch each decided
    /// this inline, and the guards for both were string searches - so dropping
    /// <c>asked</c> would have raised the dialog on every one-second poll with the
    /// tests still green.
    /// </remarks>
    public static bool ShouldAsk(ResidentState state, bool fromService, bool granted, bool askedThisVisit)
        => fromService && !granted && !askedThisVisit && state == ResidentState.NeedsPermission;

    /// <summary>
    /// What to offer while setup runs, for a phone in this condition.
    /// </summary>
    /// <param name="serviceInstalled">Whether Circle AI is on the phone at all.</param>
    /// <param name="linkApproved">Whether this app may use it.</param>
    /// <param name="microphoneGranted">Whether the service holds RECORD_AUDIO.</param>
    /// <param name="ownerWantsListening">
    /// Whether the owner has listening ON. A microphone for a feature somebody
    /// switched off is a permission asked for nothing.
    /// </param>
    /// <remarks>
    /// IT KEPT OFFERING THE MICROPHONE TO SOMEBODY WHO HAD TURNED LISTENING OFF, and
    /// the step is a button that opens a permission dialog. The owner's choice is the
    /// first question, not an afterthought.
    /// </remarks>
    public static IReadOnlyList<TourStep> TourFor(
        bool serviceInstalled, bool linkApproved, bool microphoneGranted, bool ownerWantsListening)
    {
        if (!serviceInstalled)
            return new[]
            {
                new TourStep("Install CircleAI",
                    "It holds the models and does the thinking, so this app stays small.",
                    "Get CircleAI", null),
            };

        if (!linkApproved)
            return new[]
            {
                new TourStep("Approve CircleAI",
                    "It is installed; this app needs your say-so to use it. One tap, once.",
                    "Approve", "settings"),
            };

        return microphoneGranted || !ownerWantsListening
            ? System.Array.Empty<TourStep>()
            : new[] { Step };
    }
}
