// CircleNeuronLinkService.cs
//
// The cross-app door to the shared brain.
//
// CircleNeuronService owns the models in one process; this exported service is
// the only thing that lets a DIFFERENT app reach them. Exported is not trust:
// every incoming turn is judged by the LinkGate/LinkAuthorizer against the
// OS-reported package + signing certificate before a single token is served.
//
// THIS SERVICE NEVER PROMPTS. A biometric sheet cannot be shown from a background
// service, so the approval happens in LinkConsentActivity — launched by the
// foreground client — which mints the grant into the same store this reads. Here,
// a caller either already has a grant (serve) or does not (told to link first).
//
// LOW-LEVEL BINDER, ON PURPOSE. A Messenger delivers the message AFTER the binder
// transaction returns, so Binder.CallingUid is gone by the time the handler runs.
// Binder.OnTransact runs INSIDE the transaction, where the uid is reliable. The
// wire is a hand-marshalled string map (LinkTurnCodec) behind an enforced token.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using CircleAI.Assistant;
using CircleAI.Hosting.Chat;
using CircleAI.Linking;
using CircleAI.Memory;
using CircleAI.Skills;
using Java.Security;


namespace CircleAI.Device;


/// <summary>Exported bound service that serves the shared brain to a linked app.</summary>
[Service(Name = "ai.circle.CircleNeuronLinkService", Exported = true)]
[IntentFilter(new[] { LinkIpc.BindAction })]
public sealed class CircleNeuronLinkService : Service
{
    private const string Tag = "CircleAI.Link";

    /// <summary>Where standing grants live. The host sets this before first bind.</summary>
    public static ILinkGrantStore? Grants { get; set; }

    /// <summary>Signing digests trusted without a prompt (our own apps, same key).</summary>
    public static IReadOnlySet<string>? FirstPartySignatures { get; set; }

    /// <summary>How long a minted grant lives. Zero = does not expire.</summary>
    public static TimeSpan GrantLifetime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// The person's long-term memory, for the recall / remember verbs. The host wires
    /// this to the SAME store the app itself uses, so a linked app recalls and writes
    /// the person's real memory — not a second copy. Null means the memory verbs
    /// answer "memory not available" rather than inventing a store.
    /// </summary>
    public static IMemoryService? Memory { get; set; }

    /// <summary>
    /// The skill library for the skills verb. Defaults to the built-in consumer pack
    /// (a prebuilt database, no model), so the verb works with zero host wiring; a
    /// host may set its own store before first bind.
    /// </summary>
    public static ISkillStore? Skills { get; set; }

    /// <summary>
    /// The capability catalogue for the discovery verb. Defaults to the honest
    /// embedded manifest, so "what can you do" answers from fact with no wiring.
    /// </summary>
    public static ICapabilityCatalog? Catalog { get; set; }

    /// <summary>
    /// Speech for the audio transaction: the recogniser and the voice. Null means
    /// audio is refused with "speech not available" rather than a silent empty
    /// transcript, which a client cannot tell from a quiet room.
    /// </summary>
    /// <remarks>
    /// THIS IS THE EXPENSIVE HALF, and the reason the transaction exists. A recogniser
    /// and a voice are hundreds of megabytes; the client keeps the microphone, which
    /// is free, and sends what it captured here.
    /// </remarks>
    public static ILinkSpeech? Speech { get; set; }

    /// <summary>
    /// Setting this app up: what it still needs, what it holds, and fetching it.
    /// </summary>
    /// <remarks>
    /// THIS APP HAS NO SCREENS, so the person who decides what it should be able to
    /// do is always in some other app. Null means the setup verbs answer "setup not
    /// available" rather than pretending there is nothing to download - which is what
    /// an empty plan would say, and it is a different sentence entirely.
    /// </remarks>
    public static ISetup? Setup { get; set; }

    /// <summary>
    /// The wake phrases this device listens for. Set by the host; null refuses.
    /// </summary>
    /// <remarks>
    /// THE ROW THAT USED TO SAY "OPEN CIRCLEAI" is why this exists. A client can see
    /// that the wake phrase belongs to the service - that part was always honest -
    /// but it could do nothing about it, and the app it pointed at has no launcher
    /// icon to open. Judging a phrase needs the KWS model's own tokeniser, which
    /// lives here with the model, so the judgement travels rather than the model.
    /// </remarks>
    public static IWakePhrases? WakePhrases { get; set; }

    /// <summary>The resident listener, so a client can turn listening on and off.</summary>
    /// <remarks>
    /// "Answer to its name" used to tell the person to open CircleAI - an app with no
    /// launcher icon. The microphone and the foreground service are here; the switch
    /// has to be reachable from somewhere with a screen.
    /// </remarks>
    public static IResidentAssistant? Resident
    {
        get => _resident;
        set
        {
            if (ReferenceEquals(_resident, value)) return;
            if (_resident is not null) _resident.Woke -= OnWoke;
            _resident = value;
            if (value is not null) value.Woke += OnWoke;
        }
    }

    private static IResidentAssistant? _resident;

    /// <summary>How many times this service has heard its name since it started.</summary>
    /// <remarks>
    /// THE WAKE SCREEN HAD NOTHING TO WATCH. A binder is request/response: the phrase
    /// lands in this process and there is no channel back, so a linked client could
    /// only ever be told the listener was ON, never that it had just heard something
    /// — on the one screen whose entire purpose is to say the name and see it react.
    /// <para>
    /// A COUNT RATHER THAN A FLAG, so a client that polls cannot miss one between two
    /// asks and cannot be fooled by a second wake into thinking nothing happened. It
    /// resets with the process, which is honest: it counts this run, not all time.
    /// </para>
    /// </remarks>
    public static int Heard => _heard;

    private static int _heard;

    private static void OnWoke(object? sender, string phrase)
        => System.Threading.Interlocked.Increment(ref _heard);

    /// <summary>How much room models this phone cannot run are taking, if any.</summary>
    private static long DeadWeightHere()
    {
        try
        {
            var root = CircleAI.Device.ModelFetchService.StorageDirectory;
            if (CircleNeuronService.Catalogue is not { } cat || string.IsNullOrWhiteSpace(root))
                return 0;

            var sum = 0L;
            foreach (var d in CircleAI.Inference.DeadWeight.On(cat, root!)) sum += d.Bytes;
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>Delete the bytes of every model this phone cannot run, on request.</summary>
    /// <remarks>
    /// THE VERDICT IS KEPT. Clearing the bytes does not pardon the model - it stopped
    /// this phone and that is still true - so the same version will not be offered
    /// again. Pardoning is a separate word somebody says on purpose.
    /// </remarks>
    private static long ClearWhatCannotRun()
    {
        try
        {
            var cat = CircleNeuronService.Catalogue;
            var root = CircleAI.Device.ModelFetchService.StorageDirectory;
            if (cat is null || string.IsNullOrWhiteSpace(root)) return 0;

            return CircleAI.Inference.DeadWeight.Clear(
                cat, root!, line => Log.Info(Tag, line));
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "could not clear: " + ex.Message);
            return 0;
        }
    }

    /// <summary>Has this device given up on a model after it stopped the phone?</summary>
    /// <remarks>Never throws: a device with no catalogue has refused nothing.</remarks>
    private static bool AnythingRefused()
    {
        try
        {
            var cat = CircleNeuronService.Catalogue;
            if (cat is null) return false;
            foreach (var a in cat.AllAssessed()) if (a.Refused) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>Withdraw every refusal, and report how many there were.</summary>
    /// <remarks>
    /// EVERY ONE, BECAUSE A PERSON SAID "THE BIG ONE" AND NOT AN ID. Asking which of
    /// two refused models they meant is the kind of question that makes somebody stop
    /// talking to a thing. In practice a phone refuses one model - the one too big for
    /// it - and lifting all of them is what the words mean.
    ///
    /// It only clears the verdict. The next start tries the model again and, if it
    /// aborts again, CrashVerdict writes the refusal straight back - so the worst case
    /// is one more crash, which is exactly what the person asked for.
    /// </remarks>
    private static int PardonEverythingRefused()
    {
        try
        {
            var cat = CircleNeuronService.Catalogue;
            if (cat is null) return 0;

            var n = 0;
            foreach (var a in cat.AllAssessed())
            {
                if (!a.Refused) continue;
                cat.Pardon(a.Entry.Name);
                n++;
            }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>What this device can do and what Circle AI holds on it.</summary>
    /// <remarks>
    /// The client's own answer was "Nothing for this yet" - truthful, since it holds
    /// no models, and useless, since the question is about the device.
    /// </remarks>
    public static IDeviceFacts? Facts { get; set; }

    /// <summary>
    /// The run started by <see cref="LinkVerb.SetupStart"/>, and the last thing it said.
    /// </summary>
    /// <remarks>
    /// A DOWNLOAD OUTLIVES THE CALL THAT STARTED IT, by minutes. The binder thread
    /// cannot wait for it, so the run is held here and the caller polls. Static
    /// because the client may go away and come back - a person who closes the app
    /// mid-download and reopens it should find the same run, not a second one.
    /// </remarks>
    private static Task? _setupRun;
    private static SetupProgressReport? _setupAt;
    private static string? _setupFailed;
    private static readonly object _setupLock = new();

    /// <inheritdoc/>
    public override IBinder OnBind(Intent? intent) => new LinkBinder(this);

    private sealed class LinkBinder : Binder
    {
        private readonly CircleNeuronLinkService _service;
        public LinkBinder(CircleNeuronLinkService service) => _service = service;

        protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
        {
            if (data is null || (code != LinkIpc.TransactAsk
                                 && code != LinkIpc.TransactVerb
                                 && code != LinkIpc.TransactAudio))
                return base.OnTransact(code, data, reply, flags);

            data.EnforceInterface(LinkIpc.Descriptor);

            var uid = Binder.CallingUid;   // reliable inside the transaction

            // AUDIO IS NOT A STRING MAP, so it is read before the map path runs at
            // all. Reading the map first would consume the parcel's leading int as a
            // pair count and then read audio bytes as UTF-16 keys.
            if (code == LinkIpc.TransactAudio)
            {
                var audioRequest = LinkAudioCodec.TryReadRequest(new ParcelReader(data));
                reply?.WriteNoException();

                LinkAudioReply audioResult;
                if (audioRequest is null)
                {
                    // Either a wire version this build does not speak, or audio past
                    // the limit from a client that skipped its own check.
                    audioResult = LinkAudioReply.Failure(
                        "CircleAI could not read that audio request — check the app "
                        + "and CircleAI are both up to date.");
                }
                else
                {
                    try
                    {
                        audioResult = _service.ServeAudioAsync(uid, audioRequest)
                                              .GetAwaiter().GetResult();
                    }
                    catch (Exception ex) { audioResult = LinkAudioReply.Failure(ex.Message); }
                }

                if (reply is not null) LinkAudioCodec.WriteReply(new ParcelWriter(reply), audioResult);
                return true;
            }

            var request = ReadMap(data);
            reply?.WriteNoException();

            if (code == LinkIpc.TransactAsk)
            {
                LinkTurnReply result;
                try { result = _service.ServeAskAsync(uid, request).GetAwaiter().GetResult(); }
                catch (Exception ex) { result = LinkTurnReply.Failure(ex.Message); }
                WriteMap(reply, LinkTurnCodec.Encode(result));
            }
            else   // LinkIpc.TransactVerb
            {
                LinkRowsReply result;
                try { result = _service.ServeVerbAsync(uid, request).GetAwaiter().GetResult(); }
                catch (Exception ex) { result = LinkRowsReply.Failure(ex.Message); }
                WriteMap(reply, LinkVerbCodec.Encode(result));
            }
            return true;
        }
    }

    /// <summary>
    /// Judge a caller for a given scope, never prompting. Returns null when the
    /// caller may proceed, or the reason it may not.
    /// </summary>
    /// <remarks>
    /// NEVER PROMPT HERE. First-party callers auto-mint; everyone else must have
    /// approved the link already via LinkConsentActivity, so the auth callback just
    /// says no and an un-approved caller is told to link first — for the exact scope
    /// the verb needs, so a Chat-only grant cannot reach memory or the library.
    /// </remarks>
    private async Task<string?> AuthorizeAsync(int uid, LinkScope required)
    {
        var identity = CallerIdentity(uid);
        if (identity is null) return "unknown caller";

        var grants = Grants;
        if (grants is null) return "linking not available";

        var gate = new LinkGate(grants, FirstPartySignatures);
        var authorizer = new LinkAuthorizer(grants, gate, GrantLifetime);
        var grant = await authorizer.AuthorizeAsync(
            new LinkRequest(identity.Value.Package, identity.Value.Signature, required),
            static _ => Task.FromResult(false),
            DateTimeOffset.UtcNow).ConfigureAwait(false);

        return grant is null ? $"not linked for {required} — approve in Circle AI first" : null;
    }

    /// <summary>How long a question waits for a cold brain before giving up.</summary>
    /// <remarks>
    /// MEASURED, NOT CHOSEN: a cold model load is 13-23 s on the P30 and about 19 s
    /// for a 2 B on a Tensor G2. The budget is comfortably past the slow end,
    /// because the cost of waiting is a person watching a thinking bubble and the
    /// cost of not waiting is being told to ask again - which is what this replaces.
    /// </remarks>
    private static readonly TimeSpan BrainWarmUp = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The resident node once it is ready, or null if it never got there.
    /// </summary>
    /// <remarks>
    /// THE WAITING IS ReadinessWait'S, NOT THIS METHOD'S. The first version of
    /// this was its own poll loop right here - device-proven and impossible to
    /// test, because this is a private static on an Android Service and the
    /// net9/net10 suites cannot see it. A budget, a poll interval and an
    /// off-by-one at each end should not rest on one person watching a phone.
    /// <para>
    /// What is left here is the only part that IS Android: what "ready" means on
    /// this device, and where the lines go. ReadinessWaitTests covers the rest.
    /// </para>
    /// <para>
    /// This runs on a binder thread, which is why the budget is bounded: the pool
    /// is 16 and a turn that waited forever would eventually take the link down
    /// for every caller.
    /// </para>
    /// </remarks>
    private static async Task<CircleAI.Hosting.Neuron.NeuronNode?> WaitForBrainAsync(TimeSpan budget)
    {
        var up = await CircleAI.Inference.ReadinessWait.UntilAsync(
            ready: () => CircleNeuronService.Node?.IsReady == true,
            budget: budget,
            say:   line => Log.Info(Tag, "serve: the brain is " + line)).ConfigureAwait(false);

        return up ? CircleNeuronService.Node : null;
    }

    private async Task<LinkTurnReply> ServeAskAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var turn = LinkTurnCodec.TryDecodeRequest(requestMap);
        Log.Info(Tag, "serve: ask received");
        if (turn is null) return LinkTurnReply.Failure("no message");

        var denied = await AuthorizeAsync(uid, LinkScope.Chat).ConfigureAwait(false);
        if (denied is not null) { Log.Info(Tag, "serve: denied - " + denied); return LinkTurnReply.Failure(denied); }
        Log.Info(Tag, "serve: authorised, starting the brain");

        // A GREETING IS NOT A QUESTION, AND IT NEVER REACHES THE MODEL.
        //
        // On a P30 on 2026-10-03 somebody said "Hey B" into the app. Whisper gave
        // back "Hey, B." and those seven characters were put to the brain: three
        // SaaS skills matched on the substring "hey", the prompt reached 573 tokens,
        // prefill took 26,256 ms and the whole turn 38.5 seconds - to say hello.
        //
        // HERE, BECAUSE THIS IS THE SEAM EVERY APP CROSSES. Fixing it in the sample
        // would fix one caller; this one covers every app that ever links, including
        // the ones nobody has written yet.
        // THE PHRASE THIS DEVICE IS ACTUALLY LISTENING FOR, asked of the listener
        // rather than copied: somebody can change it, and a second copy would stop
        // recognising the moment they did. Null before the listener starts, which
        // only costs the greeting shortcut on a phrase nobody is being woken by.
        if (CircleAI.Assistant.Opener.IsNothingButHello(
                turn.Message, CircleNeuronService.Listener?.Describe))
        {
            Log.Info(Tag, "serve: a greeting, answered without the brain");
            return LinkTurnReply.Success(CircleAI.Assistant.AssistantPersona.Greeting);
        }

        // AND THE THINGS A PERSON SAYS ABOUT THE ASSISTANT ITSELF, which have to be
        // answered HERE for the same reason the greeting is - except more so.
        //
        // Every one of these is about the state of the model, and the state worth
        // asking about is "it did not come up". Below this point the method returns
        // "brain warming up, try again shortly" when the node is not ready - so a
        // person asking "are you ready?" of a phone whose brain has died would be
        // told to try again shortly, forever, and a person saying "try the big one
        // again" could never reach the refusal they were trying to lift. The one
        // moment these matter is the moment there is nothing to answer with.
        //
        // THE REFUSAL HAD NO WAY BACK BEFORE THIS. CrashVerdict writes one when a
        // load aborts the process; IModelCatalog.Pardon withdraws it; nothing
        // reached Pardon, so a device that refused something wrongly refused it
        // forever and the only recovery was deleting a database over adb.
        switch (CircleAI.Assistant.SelfRequest.Of(turn.Message))
        {
            case CircleAI.Assistant.SelfAsk.WhichBrain:
            {
                var dead = DeadWeightHere();
                var said = CircleAI.Assistant.AssistantPersona.WhichBrain(
                    ready:            CircleNeuronService.Node?.IsReady == true,
                    running:          !string.IsNullOrWhiteSpace(CircleNeuronService.RunningModel),
                    gaveUpOnSomething: AnythingRefused(),
                    deadWeightSize:   dead > 0 ? CircleAI.Assistant.CensusRow.Size(dead) : null);

                Log.Info(Tag, "serve: asked about itself, answered without the brain");
                return LinkTurnReply.Success(said);
            }

            case CircleAI.Assistant.SelfAsk.ClearIt:
            {
                // THE ONLY PATH TO A DELETION, and it starts with somebody saying so.
                // Circle AI offers this when it finds a model it cannot run taking up
                // room; nothing reaches DeadWeight.Clear without the word.
                var freed = ClearWhatCannotRun();
                Log.Info(Tag, "serve: asked to clear dead weight; freed " + freed + " bytes");
                return LinkTurnReply.Success(CircleAI.Assistant.AssistantPersona.ClearIt(
                    freed > 0 ? CircleAI.Assistant.CensusRow.Size(freed) : null));
            }

            case CircleAI.Assistant.SelfAsk.TryAgain:
            {
                // ANNOUNCED, NOT CONFIRMED - the spoken instruction IS the
                // authorisation. It does it and says what it did.
                var pardoned = PardonEverythingRefused();
                Log.Info(Tag, "serve: asked to try again; pardoned " + pardoned + " model(s)");
                return LinkTurnReply.Success(
                    CircleAI.Assistant.AssistantPersona.TryAgain(pardoned > 0));
            }
        }


        // THE QUESTION IS THE INSTRUCTION TO START, AND THEN TO WAIT FOR WHAT
        // STARTED. This block used to call Start and check IsReady in the very next
        // statement - so on any phone where the brain was not already resident it
        // could not possibly be ready, and the person got
        //
        //     brain warming up, try again shortly
        //
        // A cold model load is THIRTEEN TO TWENTY-THREE SECONDS on the P30. Asking
        // again inside that window returns the same sentence, so "shortly" meant
        // "keep asking until you happen to catch it", which is the same defect as
        // the Turn it on button: the app knowing exactly what is happening and
        // handing the work back to the person. (2026-10-09, after an iAware kill
        // restarted the service mid-conversation.)
        try { CircleNeuronService.Start(this); }
        catch (Exception ex) { Log.Warn(Tag, "start: " + ex.Message); }

        var node = await WaitForBrainAsync(BrainWarmUp).ConfigureAwait(false);
        if (node is null)
        {
            // Still not up after the budget. This one IS worth saying, because
            // something is actually wrong rather than merely slow.
            Log.Warn(Tag, $"serve: the brain did not come up within {BrainWarmUp.TotalSeconds:N0}s");
            return LinkTurnReply.Failure(
                "Circle AI could not get its brain started on this phone. Ask again in a moment.");
        }

        var sb = new StringBuilder();
        await foreach (var chunk in node.StreamAsync(new[] { new ChatTurn("user", turn.Message) })
                           .ConfigureAwait(false))
            sb.Append(chunk);

        // CLEANED HERE, WHERE IT IS PRODUCED, because this answer has three consumers
        // and they must not each strip it their own way: the chat bubble, "Read it
        // out", and the spoken turn that runs with the screen off. A P30 rendered
        // "**Joke:**" literally in the bubble on 2026-10-02, and the same string is
        // what the voice would have said aloud, asterisks included.
        return LinkTurnReply.Success(CircleAI.Assistant.PlainReply.Clean(sb.ToString()));
    }

    /// <summary>
    /// Serve a structured verb — recall / remember (memory), skills (library),
    /// capabilities (discovery). Each is gated on its own scope; the model-free
    /// verbs answer instantly, so a linked app reaches the same memory, skills, and
    /// honest self-catalogue it would get in-process.
    /// </summary>
    private async Task<LinkRowsReply> ServeVerbAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var req = LinkVerbCodec.TryDecodeRequest(requestMap);
        if (req is null) return LinkRowsReply.Failure("unknown verb");

        var denied = await AuthorizeAsync(uid, LinkVerbs.RequiredScope(req.Verb)).ConfigureAwait(false);
        if (denied is not null) return LinkRowsReply.Failure(denied);

        switch (req.Verb)
        {
            case LinkVerb.ResidentStatus:
            case LinkVerb.ResidentStart:
            case LinkVerb.ResidentStop:
            {
                var resident = Resident;
                if (resident is null) return LinkRowsReply.Failure("listening not available");

                var status = req.Verb switch
                {
                    LinkVerb.ResidentStart => await resident.StartAsync().ConfigureAwait(false),
                    LinkVerb.ResidentStop  => await resident.StopAsync().ConfigureAwait(false),
                    _                      => await resident.RefreshAsync().ConfigureAwait(false),
                };

                return LinkRowsReply.Success(new[]
                {
                    LinkSetupRows.Resident(
                        status.State.ToString(), status.Status, status.Hint, _heard),
                });
            }

            case LinkVerb.Abilities:
            {
                var facts = Facts;
                if (facts is null) return LinkRowsReply.Failure("device facts not available");
                var rows = await facts.AbilitiesAsync().ConfigureAwait(false);
                return LinkRowsReply.Success(rows
                    .Select(a => LinkSetupRows.Ability(a.Title, a.Blurb, a.State.ToString(), a.Bytes, a.TryRoute))
                    .ToList());
            }

            case LinkVerb.Permissions:
            {
                var facts = Facts;
                if (facts is null) return LinkRowsReply.Failure("device facts not available");
                var rows = await facts.PermissionsAsync().ConfigureAwait(false);
                return LinkRowsReply.Success(rows
                    .Select(r => LinkSetupRows.Permission(r.Title, r.Why, r.Granted, r.Runtime))
                    .ToList());
            }

            case LinkVerb.Footprint:
            {
                var facts = Facts;
                if (facts is null) return LinkRowsReply.Failure("device facts not available");
                var report = await facts.StorageAsync().ConfigureAwait(false);

                // THE TOTALS RIDE AS THE LAST ROW rather than as their own verb: one
                // round trip, and a screen that has the lines always has the total
                // that goes with them. Marked by an empty label, which no real line has.
                var rows = report.Lines
                    .Select(l => LinkSetupRows.Storage(l.Label, l.Size, l.Regenerable))
                    .ToList();
                rows.Add(LinkSetupRows.Storage(string.Empty, report.Total, false));
                rows.Add(LinkSetupRows.Storage(string.Empty, report.Freeable, true));
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.WakePhrasesFor:
            {
                var phrases = WakePhrases;
                if (phrases is null) return LinkRowsReply.Failure("wake phrases not available");
                var all = await phrases.ForAsync(req.Query ?? "en").ConfigureAwait(false);
                return LinkRowsReply.Success(all
                    .Select(o => LinkSetupRows.WakePhrase(
                        o.Text, o.Chosen, o.BuiltIn, o.Quality.ToString(), o.Advice))
                    .ToList());
            }

            case LinkVerb.WakePhraseCheck:
            case LinkVerb.WakePhraseAdd:
            {
                var phrases = WakePhrases;
                if (phrases is null) return LinkRowsReply.Failure("wake phrases not available");
                if (string.IsNullOrWhiteSpace(req.Text)) return LinkRowsReply.Failure("no phrase");

                var verdict = req.Verb == LinkVerb.WakePhraseAdd
                    ? await phrases.AddAsync(req.Query ?? "en", req.Text!).ConfigureAwait(false)
                    : await phrases.CheckAsync(req.Query ?? "en", req.Text!).ConfigureAwait(false);

                return LinkRowsReply.Success(new[]
                {
                    LinkSetupRows.WakeVerdict(verdict.Added, verdict.Quality.ToString(), verdict.Advice),
                });
            }

            case LinkVerb.WakePhraseChoose:
            case LinkVerb.WakePhraseRemove:
            {
                var phrases = WakePhrases;
                if (phrases is null) return LinkRowsReply.Failure("wake phrases not available");
                if (string.IsNullOrWhiteSpace(req.Text)) return LinkRowsReply.Failure("no phrase");

                if (req.Verb == LinkVerb.WakePhraseChoose)
                    await phrases.ChooseAsync(req.Query ?? "en", req.Text!).ConfigureAwait(false);
                else
                    await phrases.RemoveAsync(req.Query ?? "en", req.Text!).ConfigureAwait(false);

                return LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>());
            }

            case LinkVerb.SetupReadiness:
            {
                var setup = Setup;
                if (setup is null) return LinkRowsReply.Failure("setup not available");
                var r = await setup.ReadinessAsync().ConfigureAwait(false);
                return LinkRowsReply.Success(new[]
                {
                    LinkSetupRows.Readiness(r.Stage.ToString(), r.Headline, r.Caption, r.CanTalk),
                });
            }

            case LinkVerb.SetupPlan:
            {
                var setup = Setup;
                if (setup is null) return LinkRowsReply.Failure("setup not available");
                var plan = await setup.PlanAsync().ConfigureAwait(false);
                return LinkRowsReply.Success(
                    plan.Select(i => LinkSetupRows.PlanItem(i.Title, i.Bytes)).ToList());
            }

            case LinkVerb.SetupCensus:
            {
                var setup = Setup;
                if (setup is null) return LinkRowsReply.Failure("setup not available");
                var census = await setup.CensusAsync().ConfigureAwait(false);
                return LinkRowsReply.Success(
                    census.Rows.Select(c => LinkSetupRows.CensusRow(c.Title, c.Present, c.Bytes, c.Detail, c.Have))
                               .ToList());
            }

            case LinkVerb.SetupStart:
            {
                var setup = Setup;
                if (setup is null) return LinkRowsReply.Failure("setup not available");

                lock (_setupLock)
                {
                    // ALREADY RUNNING IS A SUCCESS, NOT AN ERROR. Two screens, or one
                    // screen after a rotate, must not start a second download of the
                    // same gigabytes onto the same phone.
                    if (_setupRun is { IsCompleted: false })
                        return LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>());

                    _setupAt = null;
                    _setupFailed = null;

                    var progress = new Progress<SetupProgressReport>(p => _setupAt = p);
                    _setupRun = Task.Run(async () =>
                    {
                        try
                        {
                            await setup.RunAsync(progress, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // KEPT, NOT THROWN. Nothing is awaiting this task, so an
                            // unobserved exception would vanish and the caller would
                            // poll a run that had died - forever.
                            _setupFailed = ex.Message;
                            Log.Warn(Tag, "setup run failed: " + ex.Message);
                        }
                    });
                }

                Log.Info(Tag, "setup: started");
                return LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>());
            }

            case LinkVerb.SetupProgress:
            {
                var run = _setupRun;
                var failed = _setupFailed;
                if (failed is not null) return LinkRowsReply.Failure(failed);
                if (run is null) return LinkRowsReply.Failure("nothing is being set up");

                var at = _setupAt;
                if (run.IsCompleted && at is null)
                    // Finished before it reported anything: a plan with nothing in it.
                    return LinkRowsReply.Success(new[]
                    {
                        LinkSetupRows.Progress(0, 0, "Done", 1, 0, nameof(SetupPhase.Done)),
                    });

                if (at is null)
                    return LinkRowsReply.Success(new[]
                    {
                        LinkSetupRows.Progress(0, 0, "Starting", 0, 0, nameof(SetupPhase.Fetching)),
                    });

                // THE PHASE IS FORCED TO Done WHEN THE TASK IS, because the last
                // report a run makes is not always its final phase, and a screen that
                // never sees Done sits on 99% for ever.
                var phase = run.IsCompleted ? SetupPhase.Done : at.Phase;
                return LinkRowsReply.Success(new[]
                {
                    LinkSetupRows.Progress(at.Index, at.Count, at.Title,
                                           run.IsCompleted ? 1 : at.Fraction,
                                           at.Remaining.TotalSeconds, phase.ToString()),
                });
            }

            case LinkVerb.Capabilities:
            {
                var catalog = Catalog ?? CapabilityCatalog.Default;
                var rows = catalog.All()
                    .Select(c => (IReadOnlyList<string>)new[] { c.Id, c.Status, c.Summary })
                    .ToList();
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Skills:
            {
                var store = Skills ?? ConsumerSkillPack.Shared;
                var hits = string.IsNullOrWhiteSpace(req.Query)
                    ? await store.ListAsync().ConfigureAwait(false)
                    : await store.SearchAsync(req.Query!).ConfigureAwait(false);
                var rows = hits
                    .Select(s => (IReadOnlyList<string>)new[] { s.Id, s.Name })
                    .ToList();
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Recall:
            {
                var memory = Memory;
                if (memory is null) return LinkRowsReply.Failure("memory not available");
                var result = await memory.RecallAsync(
                    new Situation(Text: req.Query ?? string.Empty),
                    new RecallBudget(MaxAtoms: req.Limit)).ConfigureAwait(false);
                var rows = result.Atoms
                    .Select(a => (IReadOnlyList<string>)new[] { a.Text })
                    .ToList();
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Remember:
            {
                var memory = Memory;
                if (memory is null) return LinkRowsReply.Failure("memory not available");
                if (string.IsNullOrWhiteSpace(req.Text)) return LinkRowsReply.Failure("nothing to remember");
                await memory.RememberAsync(new MemoryAtom { Text = req.Text!, Subject = req.Subject })
                    .ConfigureAwait(false);
                return LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>());
            }

            default:
                return LinkRowsReply.Failure("unknown verb");
        }
    }

    private (string Package, string Signature)? CallerIdentity(int uid)
    {
        var packages = PackageManager?.GetPackagesForUid(uid);
        if (packages is null || packages.Length == 0) return null;

        var package = packages[0]!;
        var signature = SignatureDigestOf(this, package);
        return signature is null ? null : (package, signature);
    }

    /// <summary>
    /// SHA-256 of a package's first signing certificate, or null. Shared with
    /// LinkConsentActivity so the service and the consent screen judge a caller's
    /// identity the same way.
    /// </summary>
    /// <summary>
    /// This app's own signing digest — the definition of "first party".
    /// </summary>
    /// <remarks>
    /// COMPUTED, NEVER WRITTEN DOWN. A digest pasted into source is wrong the day
    /// the key is rotated and nobody notices until a person is asked to approve
    /// their own assistant. Asking the OS for it costs one PackageManager call at
    /// startup and cannot drift.
    /// <para>
    /// Null when the signature cannot be read, which is the safe way round: the
    /// caller then has no first-party entry and everything is prompted, exactly as
    /// before.
    /// </para>
    /// </remarks>
    public static string? OwnSignatureDigest(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.PackageName is { Length: > 0 } package
            ? SignatureDigestOf(context, package)
            : null;
    }

    internal static string? SignatureDigestOf(Context context, string package)
    {
        try
        {
            var pm = context.PackageManager;
            if (pm is null) return null;

            byte[]? first = null;
            if ((int)Build.VERSION.SdkInt >= 28)
            {
                var info = pm.GetPackageInfo(package, PackageInfoFlags.SigningCertificates);
                var signers = info?.SigningInfo?.GetApkContentsSigners();
                if (signers is { Length: > 0 }) first = signers[0].ToByteArray();
            }
            else
            {
#pragma warning disable CS0618 // Signatures deprecated; still the only path < API 28
                var info = pm.GetPackageInfo(package, PackageInfoFlags.Signatures);
                var sigs = info?.Signatures;
#pragma warning restore CS0618
                if (sigs is { Count: > 0 }) first = sigs[0].ToByteArray();
            }

            if (first is null) return null;
            var sha = MessageDigest.GetInstance("SHA-256")!;
            return Convert.ToHexString(sha.Digest(first)!);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "signature: " + ex.Message);
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadMap(Parcel data)
    {
        var count = data.ReadInt();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = data.ReadString();
            var value = data.ReadString();
            if (key is not null) map[key] = value ?? string.Empty;
        }
        return map;
    }

    private static void WriteMap(Parcel? reply, IReadOnlyDictionary<string, string> map)
    {
        if (reply is null) return;
        reply.WriteInt(map.Count);
        foreach (var pair in map)
        {
            reply.WriteString(pair.Key);
            reply.WriteString(pair.Value);
        }
    }

    /// <summary>Adapts a <see cref="Parcel"/> to the codec's writer.</summary>
    /// <remarks>
    /// The codec is deliberately free of Android types so the wire can be tested off
    /// a device; these two adapters are the only place the two meet.
    /// </remarks>
    private sealed class ParcelWriter(Parcel parcel) : LinkAudioCodec.IWriter
    {
        public void WriteInt(int value) => parcel.WriteInt(value);
        public void WriteString(string? value) => parcel.WriteString(value);
        public void WriteBytes(byte[] value) => parcel.WriteByteArray(value);
    }

    /// <summary>Adapts a <see cref="Parcel"/> to the codec's reader.</summary>
    private sealed class ParcelReader(Parcel parcel) : LinkAudioCodec.IReader
    {
        public int ReadInt() => parcel.ReadInt();
        public string? ReadString() => parcel.ReadString();
        public byte[] ReadBytes() => parcel.CreateByteArray() ?? Array.Empty<byte>();
    }

    /// <summary>Serve one audio request: transcribe what a client recorded, or speak.</summary>
    /// <remarks>
    /// VOICE IS ITS OWN SCOPE, so a Chat-only grant cannot reach the microphone's
    /// contents. What is being approved differs in kind: chat is "ask a question",
    /// voice is "take whatever my microphone picked up", which can include a
    /// conversation nobody meant to share.
    /// </remarks>
    private async Task<LinkAudioReply> ServeAudioAsync(int uid, LinkAudioRequest request)
    {
        var denied = await AuthorizeAsync(uid, LinkScope.Voice).ConfigureAwait(false);
        if (denied is not null) return LinkAudioReply.Failure(denied);

        var speech = Speech;
        if (speech is null)
            return LinkAudioReply.Failure(
                "Speech is not available on this device yet.");

        switch (request.Verb)
        {
            case LinkAudioVerb.Transcribe:
            {
                if (request.Audio.Length == 0)
                    return LinkAudioReply.Failure("No audio was sent.");

                var text = await speech.TranscribeAsync(request.Audio, request.Language)
                                       .ConfigureAwait(false);
                // AN EMPTY TRANSCRIPT IS A RESULT. Silence, a cough, an empty room —
                // all of it recognises to nothing, and reporting that as a failure
                // would tell somebody CircleAI is broken when it merely heard no words.
                return LinkAudioReply.Transcribed(text ?? string.Empty);
            }

            case LinkAudioVerb.Speak:
            {
                if (string.IsNullOrWhiteSpace(request.Text))
                    return LinkAudioReply.Failure("There were no words to say.");

                var audio = await speech.SpeakAsync(request.Text, request.Language)
                                        .ConfigureAwait(false);
                // Oversized synthesis is refused by the codec with a readable reason
                // rather than throwing inside the reply, which would leave the client
                // holding a dead binder and no explanation.
                return LinkAudioReply.Spoke(audio ?? Array.Empty<byte>());
            }

            default:
                return LinkAudioReply.Failure("Unknown audio request.");
        }
    }
}
