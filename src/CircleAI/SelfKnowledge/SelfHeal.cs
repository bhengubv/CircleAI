// SelfHeal.cs
//
// WOLVERINE, ON THE PHONE: SWITCH OFF WHAT TRIPPED, AND TRY IT AGAIN LATER.
//
// The API-side pipeline is built and running (AppInfo/Wolverine/README.md): capture,
// classify, attempt, escalate, approve, deploy, verify, close. Its client-side twin -
// the one for "mobile/web apps that can't retry in-process" - is a document that says
// "PLANNING PHASE (No Code Yet)", and Circle AI Service is a client.
//
// WHAT A CLIENT CAN ACTUALLY DO. An API heals by retrying: the request is still in
// its hand. A phone's failure has usually already cost somebody their answer, so
// retrying the same thing the same way is not healing, it is the same failure again.
// What a phone CAN do is the Tier 1 of that architecture - "disable broken
// functionality without a deployment", which is WolverineAPI's own SWITCH_TO_FALLBACK
// action ("hosts toggle feature flags / swap providers") - and it can do it in
// milliseconds, by itself, with no network at all. That matters here more than most
// places: this product works with no signal by design.
//
// AND THE HALF THAT MAKES IT HEALING RATHER THAN DEGRADING. A switch turned off and
// never turned back on is not a fix, it is a product quietly getting worse - and
// nobody ever notices, because nothing is broken. The API side already knows this:
// its circuit breaker opens for five minutes, then goes HALF-OPEN and lets one
// request through to find out. This does the same with a longer hand on the clock,
// because a phone's trips are things like "mmap SIGSEGV'd MNN" rather than "the
// database blinked": switch it off, remember why, and let the hourly idle review ask
// again when it costs nobody anything to find out.
//
// WHAT IT NEVER DOES. It does not decide that a thing is permanently broken, and it
// does not touch anything a person chose. The owner's settings are theirs; these are
// the switches the SOFTWARE set for the SOFTWARE's reasons, and every one of them is
// written down with its reason so a person - or a developer reading a report - can
// see exactly what their phone turned off on their behalf and why.

namespace CircleAI.SelfKnowledge;

/// <summary>Something the software switched off because it tripped.</summary>
/// <param name="Name">What was switched off, as a stable key.</param>
/// <param name="Why">The trip, in the words a person could be shown.</param>
/// <param name="Trips">How many times this has tripped, ever, on this device.</param>
/// <param name="SinceUtc">When it was last switched off.</param>
/// <param name="RetryAfterUtc">When it is worth trying again.</param>
public sealed record Healed(
    string Name,
    string Why,
    int Trips,
    DateTimeOffset SinceUtc,
    DateTimeOffset RetryAfterUtc)
{
    /// <summary>Whether it is still off right now.</summary>
    public bool IsOff(DateTimeOffset now) => now < RetryAfterUtc;
}

/// <summary>What Circle AI Service switches off when something trips, and when it tries again.</summary>
public static class SelfHeal
{
    /// <summary>The things that can be switched off. One owner for every key.</summary>
    /// <remarks>
    /// NAMED CONSTANTS BECAUSE A TYPO IS A SWITCH THAT NEVER COMES BACK ON. The side
    /// that trips and the side that asks "is this off" have to agree on a string, and
    /// this repository's most repeated defect is two halves agreeing on a string in
    /// two places.
    /// </remarks>
    public static class Switch
    {
        /// <summary>Memory-mapping the weights. Has SIGSEGV'd MNN's threadpool before.</summary>
        public const string MemoryMapping = "mmap";

        /// <summary>The GPU backend. OpenCL has failed to build on some drivers.</summary>
        public const string GpuBackend = "gpu";

        /// <summary>The streaming recogniser's wake path, which falls back to the keyword spotter.</summary>
        public const string TranscribingWake = "wake.transcribing";

        /// <summary>The prefix cache, which needs kvcache mmap and has broken MNN before.</summary>
        public const string PrefixCache = "prefixcache";

        /// <summary>Claiming the microphone foreground-service type.</summary>
        public const string MicrophoneForegroundType = "fgs.microphone";

        /// <summary>Every switch there is, so a status can report on all of them.</summary>
        /// <remarks>
        /// WRITTEN OUT RATHER THAN REFLECTED. Reflection over the consts above would
        /// never drift, but these ship into a trimmed, AOT'd Android head where field
        /// metadata is exactly the kind of thing that goes missing - and a status that
        /// silently reports on four switches instead of five is worse than no status.
        /// <para>
        /// The cost is that a sixth switch could be added above and forgotten here.
        /// <c>SelfHealTests.AllCoversEverySwitchConstant</c> asserts this list against
        /// the constants so the drift is a failing test rather than a blind spot.
        /// </para>
        /// </remarks>
        public static readonly string[] All =
        [
            MemoryMapping,
            GpuBackend,
            TranscribingWake,
            PrefixCache,
            MicrophoneForegroundType,
        ];
    }

    /// <summary>How a switch is doing.</summary>
    /// <remarks>
    /// WHY THERE IS A MIDDLE LIGHT. "Green or red" is the question people ask, and for
    /// a switch that never tripped and a switch that is off right now those two answers
    /// are enough. But a switch that tripped this morning, waited out its hour and is
    /// back on has not been PROVEN - nothing has run through it since. Calling that
    /// green loses the fact that this device has a history of it failing; calling it red
    /// cries wolf about something that is working. So it gets its own word.
    /// </remarks>
    public enum HealthLight
    {
        /// <summary>Never tripped on this device. Nothing was switched off.</summary>
        Green,

        /// <summary>Tripped before and is on again, but nothing has exercised it since.</summary>
        Amber,

        /// <summary>Switched off right now.</summary>
        Red,
    }

    /// <summary>One switch, and how it is doing.</summary>
    /// <param name="Name">The switch, one of <see cref="Switch"/>.</param>
    /// <param name="Light">Green, amber or red.</param>
    /// <param name="Trips">How many times it has tripped here, ever. Zero for green.</param>
    /// <param name="Why">The last trip's reason, or empty when it has never tripped.</param>
    /// <param name="BackOnUtc">When a red switch comes back on. Null unless red.</param>
    public sealed record SwitchHealth(
        string Name,
        HealthLight Light,
        int Trips,
        string Why,
        DateTimeOffset? BackOnUtc);

    /// <summary>What Wolverine's client-side half has switched off, as a status.</summary>
    /// <param name="Overall">The worst light of any switch.</param>
    /// <param name="AtUtc">When this was taken.</param>
    /// <param name="Switches">Every switch, always — see <see cref="Status"/>.</param>
    public sealed record HealthReport(
        HealthLight Overall,
        DateTimeOffset AtUtc,
        IReadOnlyList<SwitchHealth> Switches)
    {
        /// <summary>The whole status in one line, for a log or a report.</summary>
        /// <remarks>
        /// NAMES THE SWITCHES THAT ARE NOT GREEN, because "WOLVERINE AMBER" on its own
        /// sends a person to the source to find out which of five things it meant.
        /// </remarks>
        public string OneLine
        {
            get
            {
                var green = Switches.Count(s => s.Light == HealthLight.Green);
                var bad = Switches.Where(s => s.Light != HealthLight.Green)
                                  .Select(s => $"{s.Name} {s.Light.ToString().ToLowerInvariant()}")
                                  .ToList();

                return bad.Count == 0
                    ? $"WOLVERINE {Overall.ToString().ToUpperInvariant()} - all {Switches.Count} switches on, none has ever tripped here"
                    : $"WOLVERINE {Overall.ToString().ToUpperInvariant()} - {green} of {Switches.Count} green; {string.Join(", ", bad)}";
            }
        }
    }

    /// <summary>
    /// Green or red, per switch and overall — without anything having to fail first.
    /// </summary>
    /// <remarks>
    /// EVERY SWITCH, EVERY TIME. The obvious implementation reports on what is in the
    /// off-list, which is empty on a healthy device - and an empty status reads exactly
    /// like a status that could not be taken. This repository has been bitten by that
    /// shape before: a silent zero is not a quiet room. So a switch that has never
    /// tripped is listed, explicitly green, and the count in <see cref="HealthReport.OneLine"/>
    /// is what tells a reader the status actually ran.
    /// </remarks>
    public static HealthReport Status(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var list = new List<SwitchHealth>(Switch.All.Length);

        lock (Gate)
        {
            foreach (var name in Switch.All)
            {
                if (!Off.TryGetValue(name, out var healed))
                {
                    list.Add(new SwitchHealth(name, HealthLight.Green, 0, "", null));
                    continue;
                }

                var red = healed.IsOff(at);
                list.Add(new SwitchHealth(
                    name,
                    red ? HealthLight.Red : HealthLight.Amber,
                    healed.Trips,
                    healed.Why,
                    red ? healed.RetryAfterUtc : null));
            }
        }

        var overall = list.Any(s => s.Light == HealthLight.Red) ? HealthLight.Red
                    : list.Any(s => s.Light == HealthLight.Amber) ? HealthLight.Amber
                    : HealthLight.Green;

        return new HealthReport(overall, at, list);
    }

    /// <summary>
    /// How long a switch stays off before it is worth asking again.
    /// </summary>
    /// <remarks>
    /// LONGER EACH TIME, AND CAPPED. The API side opens its breaker for five minutes
    /// because its failures are transient by nature - a connection, a timeout. A
    /// phone's are not: a driver that SIGSEGVs on mmap will do it again this
    /// afternoon, and asking every five minutes is asking the person to crash their
    /// assistant twelve times an hour to find that out.
    /// <para>
    /// So: an hour, then six, then a day, then a week, and no further. A week is the
    /// scale on which the thing that would actually fix it arrives - an app update, a
    /// firmware update, a model replaced - and a switch that never asks again is the
    /// permanent degradation this whole type exists to avoid.
    /// </para>
    /// </remarks>
    public static TimeSpan OffFor(int trips) => trips switch
    {
        <= 1 => TimeSpan.FromHours(1),
        2    => TimeSpan.FromHours(6),
        3    => TimeSpan.FromDays(1),
        _    => TimeSpan.FromDays(7),
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Healed> Off = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a line goes. Null discards it.</summary>
    public static Action<string>? Say { get; set; }

    /// <summary>
    /// Where a trip is reported onward, for the things a phone cannot fix itself.
    /// </summary>
    /// <remarks>
    /// NULL TODAY, AND HONESTLY SO. WolverineAPI's /api/errors is the right
    /// destination and it is ServiceActorOnly - the line above it in Program.cs says
    /// "Nothing in the codebase posts here" - so a phone has no credential it may
    /// hold to reach it. Rather than inventing one, this is the seam: a host that is
    /// given a door wires it, and until then every trip is still recorded on the
    /// device (SelfModel) where a report can be built from it later.
    /// </remarks>
    public static Action<Healed>? Report { get; set; }

    /// <summary>
    /// Something tripped: switch it off, and say when to ask again.
    /// </summary>
    /// <param name="name">One of <see cref="Switch"/>.</param>
    /// <param name="why">What happened, in words a person could be shown.</param>
    /// <param name="now">The clock, so a test does not have to wait an hour.</param>
    /// <returns>What was switched off, for a caller that wants to log or report it.</returns>
    public static Healed Trip(string name, string why, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var at = now ?? DateTimeOffset.UtcNow;

        Healed healed;
        lock (Gate)
        {
            var trips = (Off.TryGetValue(name, out var was) ? was.Trips : 0) + 1;
            healed = new Healed(name, why ?? "", trips, at, at + OffFor(trips));
            Off[name] = healed;

            // IT TRIPPED AGAIN, SO IT IS WORTH ANNOUNCING AGAIN when its longer window
            // runs out. Leaving it in here would make the second trip the silent one.
            Announced.Remove(name);
        }

        // healed.Why, NOT why: the record normalised a null reason to "" on the way in,
        // and reading it back is what keeps one answer to "what tripped and why".
        Say?.Invoke($"switched {name} off for {Human(OffFor(healed.Trips))}: {healed.Why}");

        // THE DEVICE REMEMBERS IT, whether or not anything is listening. A trip that
        // is only in a log line is a trip the next launch knows nothing about, and
        // the switch comes back on into the same crash. SelfModel.LoadAsync reads
        // these back at start and calls Restore.
        SelfModel.Current?.LearnTrip(name, healed.Why);

        try { Report?.Invoke(healed); }
        catch (Exception ex) { Say?.Invoke("could not report the trip: " + ex.Message); }

        return healed;
    }

    /// <summary>Whether something is switched off right now.</summary>
    /// <remarks>
    /// THE QUESTION EVERY GUARD ASKS, and it is deliberately the only one: a caller
    /// asks "is this off", not "how many times has it tripped". Anything that reasons
    /// about trip counts is making a policy decision, and the policy lives here.
    /// </remarks>
    public static bool IsOff(string name, DateTimeOffset? now = null)
    {
        lock (Gate)
            return Off.TryGetValue(name, out var healed) && healed.IsOff(now ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Everything currently switched off, and everything waiting to be retried.</summary>
    public static IReadOnlyList<Healed> All()
    {
        lock (Gate) return Off.Values.OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What is due to be tried again, for the hourly review to act on.
    /// </summary>
    /// <remarks>
    /// THE HALF-OPEN TICK, and the reason this is healing rather than degrading. The
    /// idle review already runs hourly on a phone nobody is using, which is exactly
    /// when it costs nothing to find out whether the thing that crashed this morning
    /// still crashes.
    /// </remarks>
    public static IReadOnlyList<Healed> DueToRetry(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (Gate) return Off.Values.Where(h => !h.IsOff(at)).ToList();
    }

    /// <summary>
    /// The same list, but each switch comes back only once — for a caller that announces
    /// or acts on it rather than merely asking.
    /// </summary>
    /// <remarks>
    /// BECAUSE THE CLOCK DOES NOT STOP RINGING. Nothing has to flip a switch back on -
    /// <see cref="IsOff"/> answers by the clock, so a window that has run out is already
    /// on again - which means <see cref="DueToRetry"/> keeps returning it on every tick
    /// until it either works or trips again. A caller that logs that is a caller that
    /// writes the same line every fifteen minutes for a week, and this repository's rule
    /// is that nothing is silent; it is not that everything is said forever.
    /// <para>
    /// A fresh trip clears the mark, so the SECOND time something breaks is announced
    /// too — the trip that matters most is never the first one.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Healed> TakeDueToRetry(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            var due = Off.Values.Where(h => !h.IsOff(at) && !Announced.Contains(h.Name)).ToList();
            foreach (var one in due) Announced.Add(one.Name);
            return due;
        }
    }

    /// <summary>It worked: stop holding it off, and forget the count.</summary>
    /// <remarks>
    /// THE COUNT GOES TOO. A switch that came back and worked has no history worth
    /// holding against it - keeping the count would put the next single trip straight
    /// onto a week, which is how a device that recovered ends up permanently degraded
    /// by one bad afternoon.
    /// </remarks>
    public static void Worked(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        bool had;
        lock (Gate)
        {
            had = Off.Remove(name);
            Announced.Remove(name);
        }

        if (had) Say?.Invoke($"{name} works again; it is back on");
    }

    /// <summary>
    /// Puts back what this device had already switched off, from its long-term memory.
    /// </summary>
    /// <param name="name">The switch.</param>
    /// <param name="trips">How many times it has tripped here, ever.</param>
    /// <param name="lastUtc">When it last tripped.</param>
    /// <remarks>
    /// THE LINE THAT MAKES THIS LONG-TERM RATHER THAN A CACHE. Without it the process
    /// starts every launch believing nothing has ever failed, which on a phone that
    /// SIGSEGVs on mmap means crashing once per launch forever - and looking, from the
    /// outside, exactly like software with no healing in it at all.
    /// <para>
    /// THE CLOCK RUNS FROM WHEN IT TRIPPED, NOT FROM THIS LAUNCH. A switch that went
    /// off an hour ago with a one-hour window is due now, and restarting the window at
    /// start-up would mean a phone that reboots often never retries anything.
    /// </para>
    /// </remarks>
    public static void Restore(string name, int trips, DateTimeOffset lastUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || trips <= 0) return;

        lock (Gate)
            Off[name] = new Healed(
                name, "switched off on an earlier run", trips, lastUtc, lastUtc + OffFor(trips));
    }

    /// <summary>Forgets everything. For a test, or a host starting clean.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Off.Clear();
            Announced.Clear();
        }
    }

    private static string Human(TimeSpan window) =>
        window.TotalDays >= 1 ? $"{window.TotalDays:0} day(s)" : $"{window.TotalHours:0} hour(s)";
}
