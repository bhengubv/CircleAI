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

    /// <summary>
    /// The last thing this device decided for itself, and a number that only
    /// goes up.
    /// </summary>
    /// <remarks>
    /// PART 3: IT SAYS SO, OUT LOUD. Everything this device works out about its
    /// own models - that the big brain will not fit, that it was killed by one
    /// and will not try again - was reachable only by ASKING. SelfRequest
    /// answers "which brain are you using?" perfectly well and nobody thinks to
    /// ask it. The bar is Alexa: it decides, it acts, and it tells you.
    ///
    /// HOLD-AND-POLL, NOT PUSH, the same shape as Heard directly above: a binder
    /// is request/response and there is no channel back.
    ///
    /// THE POLICY IS DecisionLog'S, not this file's. Do-not-repeat-yourself and
    /// never-go-backwards are the only logic here, and an Android Service cannot
    /// be reached by the net9/net10 suites - the same reason the brain warm-up
    /// wait moved to ReadinessWait.
    /// </remarks>
    private static readonly CircleAI.Assistant.DecisionLog Decisions = new();

    /// <summary>The most recent decision, or null.</summary>
    public static string? LastVerdict => Decisions.Last;

    /// <summary>How many distinct decisions this run has announced.</summary>
    public static int VerdictSeq => Decisions.Sequence;

    /// <summary>Record something this device decided, for a client to speak.</summary>
    /// <param name="sentence">What to say, from AssistantPersona.</param>
    /// <param name="awaitingAnswer">
    /// True when the sentence ends in an offer, so a plain "yes" counts as the
    /// answer to it.
    /// </param>
    public static void Decided(string? sentence, bool awaitingAnswer = false)
    {
        if (Decisions.Decided(sentence, awaitingAnswer)) Log.Info(Tag, "decided: " + sentence);
    }

    /// <summary>Is the assistant waiting for an answer to something it offered?</summary>
    public static bool AwaitingAnswer => Decisions.AwaitingAnswer;

    /// <summary>How much room models this phone cannot run are taking, if any.</summary>
    /// <summary>
    /// Say that something unusable is taking up room, if it is. Never deletes.
    /// </summary>
    /// <remarks>
    /// PART 5: SAY WHAT IS DEAD WEIGHT, OFFER, NEVER ACT. DeadWeightHere and
    /// ClearWhatCannotRun have both existed for a while and both only ran when
    /// somebody ASKED - so a phone could sit with 2.85 GB of a brain it had been
    /// killed by, know it precisely, and never mention it. That is the P30 right
    /// now.
    ///
    /// Housekeeping deliberately will not reclaim a model that stopped fitting -
    /// "free space comes back, a fit rule gets corrected, and the bytes are
    /// already paid for" - and a model refused BY MEASUREMENT is the one case
    /// where that reasoning does not hold, because the correction already
    /// happened and went the other way.
    ///
    /// SO IT SAYS SO AND STOPS. The sentence AssistantPersona.DeadWeight builds
    /// ends in an offer, and the only path to a deletion is still somebody
    /// answering it - SelfAsk.ClearIt. Nothing here removes a byte.
    /// </remarks>
    public static void AnnounceDeadWeightIfAny()
    {
        var bytes = DeadWeightHere();
        if (bytes <= 0) return;

        // awaitingAnswer: the sentence ends in an offer, so "yes" means yes
        // until something else is decided or the offer is taken up.
        Decided(CircleAI.Assistant.AssistantPersona.DeadWeight(
            CircleAI.Assistant.CensusRow.Size(bytes)), awaitingAnswer: true);
    }

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

    /// <summary>How long an older app's blocking ask holds its binder thread.</summary>
    /// <remarks>
    /// A cold load plus a long answer on the P30; past it the ask carries on in the queue.
    /// Bounded because the binder pool is shared with every cheap verb.
    /// </remarks>
    private static readonly TimeSpan LegacyAskBudget = TimeSpan.FromSeconds(150);

    // THE START-AND-WAIT USED TO BE A PRIVATE METHOD HERE, AND THAT IS WHY THE
    // WAKE TURN NEVER GOT IT. It was correct, it was device-proven, and it was
    // unreachable: a private static on an Android Service that neither test leg
    // can see and no other surface can call. WakeTurn.AskAsync answered the same
    // question with `return null` for a month, and nothing could have caught it
    // except somebody standing in front of a P30 with the service killed.
    //
    // It is CircleAI.Assistant.ColdTurn now - one owner, both surfaces, both legs.
    // What remains in this file is the Android: what "ready" means here, which
    // context starts the service, and where the lines go.
    //
    // The budget stays bounded because this runs on a binder thread: the pool is
    // 16 and a turn that waited forever would eventually take the link down for
    // every caller.

    /// <summary>Stream one answer out of the resident node, cleaned for every reader.</summary>
    /// <remarks>
    /// CLEANED HERE, WHERE IT IS PRODUCED, because this answer has three consumers
    /// and they must not each strip it their own way: the chat bubble, "Read it
    /// out", and the spoken turn that runs with the screen off. A P30 rendered
    /// "**Joke:**" literally in the bubble on 2026-10-02, and the same string is
    /// what the voice would have said aloud, asterisks included.
    /// </remarks>
    private static async Task<string?> StreamTheAnswerAsync(string message, CancellationToken ct)
    {
        var node = CircleNeuronService.Node;
        if (node is null) return null;

        var sb = new StringBuilder();
        await foreach (var chunk in node.StreamAsync(new[] { new ChatTurn("user", message) })
                           .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(chunk);
        }

        return CircleAI.Assistant.PlainReply.Clean(sb.ToString());
    }

    private async Task<LinkTurnReply> ServeAskAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var turn = LinkTurnCodec.TryDecodeRequest(requestMap);
        Log.Info(Tag, "serve: ask received");
        if (turn is null) return LinkTurnReply.Failure("no message");

        var denied = await AuthorizeAsync(uid, LinkScope.Chat).ConfigureAwait(false);
        if (denied is not null) { Log.Info(Tag, "serve: denied - " + denied); return LinkTurnReply.Failure(denied); }

        // AN OLDER APP'S BLOCKING ASK IS STILL A RECORD. It is submitted like every
        // other question - saved, queued, answered by the same worker - and this
        // thread waits for it, as that app expects. If the wait runs out, the ask is
        // NOT dropped: it is answered anyway and kept, and the app is told it is
        // still being worked on rather than that it failed.
        if (CircleNeuronService.Asks is { } asks && CallerKeyOf(uid) is { } caller)
        {
            var admission = asks.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                caller, ClientRequestId: null, turn.SessionId ?? string.Empty,
                CircleAI.Assistant.Requests.AskLane.Foreground, turn.Message, Agentic: turn.Agentic));
            if (!admission.Accepted || admission.Record is null)
                return LinkTurnReply.Failure(admission.Refusal ?? CircleAI.Assistant.Requests.AskSentences.Busy);

            var done = await asks.WaitAsync(caller, admission.Record.Id, LegacyAskBudget, CancellationToken.None).ConfigureAwait(false);
            return done switch
            {
                { State: CircleAI.Assistant.Requests.AskState.Succeeded } => LinkTurnReply.Success(done.Text),
                { IsFinal: true } => LinkTurnReply.Failure(done.Sentence ?? CircleAI.Assistant.Requests.AskSentences.CouldNotAnswer),
                _ => LinkTurnReply.Failure(CircleAI.Assistant.Requests.AskSentences.StillLoading),
            };
        }

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
        // THE OFFER'S OWN ANSWER IS ONLY HEARD IF WE SAY ONE IS OPEN. Without
        // this argument a person replying "yes" to "say clear it and I will"
        // was answered by the brain as though it were a new conversation - which
        // is exactly what happened the first time anybody heard the offer.
        switch (CircleAI.Assistant.SelfRequest.Of(turn.Message, AwaitingAnswer))
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
                Decisions.Answered();   // the offer has been taken up
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
        // ONE OWNER FOR BOTH SURFACES. This block was the start-then-wait, and the
        // screen-off wake turn had its own version of the same question that
        // answered it with silence. ColdTurn is that rule, lifted somewhere both
        // can reach and both test legs can see; what is left here is the Android.
        //
        // NO VOICE ON THIS PATH, deliberately: the app on the other end does the
        // speaking, so the excuse travels back in the reply instead of being said.
        var served = await CircleAI.Assistant.ColdTurn.AnswerAsync(
            ready:   () => CircleNeuronService.Node?.IsReady == true,
            start:   () => CircleNeuronService.Start(this),
            ask:     token => StreamTheAnswerAsync(turn.Message, token),
            say:     null,
            budget:  BrainWarmUp,
            givenUp: () => CircleNeuronService.State == CircleNeuronService.ServiceState.Failed,
            log:     line => Log.Info(Tag, "serve: the brain is " + line)).ConfigureAwait(false);

        if (!served.Answered)
        {
            Log.Warn(Tag, $"serve: the brain did not come up within {BrainWarmUp.TotalSeconds:N0}s");
            return LinkTurnReply.Failure(served.Excuse ?? CircleAI.Assistant.AssistantPersona.CouldNotStart);
        }

        return LinkTurnReply.Success(served.Answer!);
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

        // THE HANDSHAKE IS ANSWERED BEFORE AUTHORISATION. It says what this service
        // speaks and nothing private, and a client that cannot learn that cannot know
        // whether to use questions-as-records or the blocking ask.
        if (req.Verb == LinkVerb.Hello) return Hello();

        var denied = await AuthorizeAsync(uid, LinkVerbs.RequiredScope(req.Verb)).ConfigureAwait(false);
        if (denied is not null) return LinkRowsReply.Failure(denied);

        if (LinkVerbs.FirstPartyOnly(req.Verb) && !IsFirstParty(uid))
            return LinkRowsReply.Failure("only Circle AI's own apps may do that");

        switch (req.Verb)
        {
            case LinkVerb.AskSubmit:
            case LinkVerb.AskStatus:
            case LinkVerb.AskCancel:
            case LinkVerb.AskList:
                return await ServeAskVerbAsync(uid, req).ConfigureAwait(false);

            case LinkVerb.Status:
            {
                var readiness = CircleNeuronService.Readiness;
                if (readiness is null) return LinkRowsReply.Failure("status not available");
                var after = long.TryParse(req.Arg(LinkAskRows.ArgAfterSeq), out var a) ? a : -1;
                var now = readiness.Current;
                if (after >= 0 && now.Seq <= after) return LinkRowsReply.Success(new[] { LinkAskRows.Unchanged(now.Seq) });

                // THIS CALLER'S OWN GRANTS ride along, so an app can see what it may do.
                var scopes = CallerScopes(uid);
                var rows = LinkAskRows.Status(now).ToList();
                if (scopes is not null) rows.Add(new[] { "setting", CircleAI.Assistant.Lifecycle.ReadinessSettings.CallerScopes, scopes });
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Health:
                return LinkRowsReply.Success((HealthFacts?.Invoke() ?? Array.Empty<KeyValuePair<string, string>>())
                    .Select(p => LinkAskRows.Health(p.Key, p.Value)).ToList());

            case LinkVerb.QualityRun:
            {
                var asks = CircleNeuronService.Asks;
                if (asks is null) return LinkRowsReply.Failure("questions are not available");
                var key = CallerKeyOf(uid) ?? "internal:quality";

                // A RUN IS MINUTES OF WORK ON A PHONE, so it is started here and its
                // verdicts are logged and kept; the reply says it began.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var report = await new CircleAI.Assistant.Quality.QualityRunner(asks, key)
                            .RunAsync(null, TimeSpan.FromMinutes(3), CancellationToken.None).ConfigureAwait(false);
                        LastQuality = report;
                        foreach (var item in report.Items)
                            Log.Info(Tag, $"QUALITY {item.Id} {(item.Passed ? "PASS" : "FAIL")} {item.Reason} "
                                          + (item.Timing is { } t ? t.ToLogLine(item.Id) : string.Empty));
                        Log.Info(Tag, $"QUALITY summary {report.Passed}/{report.Total} model={report.ModelId}");
                    }
                    catch (Exception ex) { Log.Warn(Tag, "quality run failed: " + ex.GetType().Name); }
                });

                var last = LastQuality;
                var rows = new List<IReadOnlyList<string>>();
                if (last is not null)
                {
                    rows.AddRange(last.Items.Select(i => LinkAskRows.Quality(i.Id, i.Passed, i.Reason, i.Answer, i.Timing)));
                    rows.Add(LinkAskRows.QualitySummary(last.Passed, last.Total, last.ModelId));
                }
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.SetupCancel:
            {
                lock (_setupLock)
                {
                    try { _setupCancel?.Cancel(); } catch (ObjectDisposedException) { }
                }
                Log.Info(Tag, "setup: cancelled by the caller; downloaded bytes are kept");
                return LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>());
            }

            case LinkVerb.ResidentStatus:
            case LinkVerb.ResidentStart:
            case LinkVerb.ResidentStop:
            case LinkVerb.ResidentResume:
            {
                var resident = Resident;
                if (resident is null) return LinkRowsReply.Failure("listening not available");

                // RESUME IS NOT START, and it is a separate verb because it has to be
                // able to say no. The client calls it at every launch; mapping it onto
                // ResidentStart is what turned an owner's explicit "off" back on each
                // time the app was opened.
                var status = req.Verb switch
                {
                    LinkVerb.ResidentStart  => await resident.StartAsync().ConfigureAwait(false),
                    LinkVerb.ResidentStop   => await resident.StopAsync().ConfigureAwait(false),
                    LinkVerb.ResidentResume => await resident.ResumeAsync().ConfigureAwait(false),
                    _                       => await resident.RefreshAsync().ConfigureAwait(false),
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

            case LinkVerb.Verdict:
            {
                // NO AUTHORISATION CHECK BEYOND THE ONE ABOVE, and nothing here
                // reads the person's data: it is one sentence this device wrote
                // about its own models. Cheap enough to poll, which is the point.
                //
                // EMPTY IS THE NORMAL ANSWER. A phone that has decided nothing
                // worth saying says nothing, rather than repeating "I am ready"
                // at whatever rate the client happens to poll.
                var said = LastVerdict;
                if (string.IsNullOrWhiteSpace(said)) return LinkRowsReply.Success([]);

                return LinkRowsReply.Success(
                    [[VerdictSeq.ToString(System.Globalization.CultureInfo.InvariantCulture), said!]]);
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
                    _setupCancel?.Dispose();
                    _setupCancel = new CancellationTokenSource();
                    var stop = _setupCancel.Token;

                    var progress = new Progress<SetupProgressReport>(p => _setupAt = p);
                    _setupRun = Task.Run(async () =>
                    {
                        try
                        {
                            await setup.RunAsync(progress, stop).ConfigureAwait(false);

                            // SAID ON SUCCESS TOO. Only a failure was logged, so on the
                            // P30 on 2026-10-16 a run that finished and a run still going
                            // looked identical from outside the phone.
                            Log.Info(Tag, "setup run finished");
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
        if (signature is null) return null;

        // ONE UID, SEVERAL PACKAGES, ONE SIGNATURE - or nobody. A shared uid is how a
        // system component can arrive here as several packages; taking the first one
        // silently would key a caller's grants and conversation to the wrong package.
        // Packages under one uid that are signed differently are refused outright.
        for (var i = 1; i < packages.Length; i++)
            if (packages[i] is { } other && SignatureDigestOf(this, other) != signature) return null;

        return (package, signature);
    }

    // ── questions as records ─────────────────────────────────────────────────

    /// <summary>The last quality run's report, for the next QualityRun call to return.</summary>
    private static CircleAI.Assistant.Quality.QualityReport? LastQuality;

    /// <summary>The service's own health facts, for the Health verb. Set by the host.</summary>
    public static Func<IReadOnlyList<KeyValuePair<string, string>>>? HealthFacts { get; set; }

    /// <summary>This build's versionCode, for the handshake. Set by the host.</summary>
    public static long VersionCode { get; set; }

    private static CancellationTokenSource? _setupCancel;

    /// <summary>
    /// The queue's name for a caller: package and a short signing digest. Derived from
    /// the binder's calling uid - never from anything the caller says about itself.
    /// </summary>
    private string? CallerKeyOf(int uid) =>
        CallerIdentity(uid) is { } id ? id.Package + ":" + id.Signature[..Math.Min(16, id.Signature.Length)] : null;

    private bool IsFirstParty(int uid) =>
        CallerIdentity(uid) is { } id && FirstPartySignatures?.Contains(id.Signature) == true;

    private string? CallerScopes(int uid)
    {
        if (CallerIdentity(uid) is not { } id) return null;
        if (FirstPartySignatures?.Contains(id.Signature) == true) return "first-party";
        return null;
    }

    private static LinkRowsReply Hello()
    {
        var verbs = Enum.GetNames<LinkVerb>();
        var boot = CircleNeuronService.Readiness?.BootId ?? string.Empty;
        return LinkRowsReply.Success(new[]
        {
            LinkAskRows.Hello(LinkIpc.Protocol, VersionCode, boot, global::Android.OS.Process.MyPid(), verbs),
        });
    }

    // A BOUNDED NUMBER OF WAITING POLLS. A poll may wait a moment for news, which holds
    // a binder thread; past this many at once the rest are answered straight away, so
    // polling can never starve the pool the cheap verbs share.
    private static readonly SemaphoreSlim Waiters = new(6, 6);

    private async Task<LinkRowsReply> ServeAskVerbAsync(int uid, LinkVerbRequest req)
    {
        var asks = CircleNeuronService.Asks;
        if (asks is null) return LinkRowsReply.Failure("questions are not available yet");
        var caller = CallerKeyOf(uid);
        if (caller is null) return LinkRowsReply.Failure("unknown caller");

        switch (req.Verb)
        {
            case LinkVerb.AskSubmit:
            {
                var message = req.Arg(LinkAskRows.ArgMessage) ?? req.Text;
                if (string.IsNullOrWhiteSpace(message)) return LinkRowsReply.Failure("no message");

                var lane = Enum.TryParse<CircleAI.Assistant.Requests.AskLane>(req.Arg(LinkAskRows.ArgLane), out var l)
                    ? l : CircleAI.Assistant.Requests.AskLane.Foreground;
                // THE ASSISTANT LANE IS FOR SOMEBODY WHO SPOKE TO THE PHONE, and only
                // the phone itself puts questions there; Background needs its grant.
                if (lane == CircleAI.Assistant.Requests.AskLane.Assistant) lane = CircleAI.Assistant.Requests.AskLane.Foreground;
                if (lane == CircleAI.Assistant.Requests.AskLane.Background
                    && await AuthorizeAsync(uid, LinkScope.Background).ConfigureAwait(false) is not null)
                    lane = CircleAI.Assistant.Requests.AskLane.Foreground;

                var admission = asks.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                    CallerKey: caller,
                    ClientRequestId: req.Arg(LinkAskRows.ArgRequestId),
                    SessionId: req.Arg(LinkAskRows.ArgSession) ?? string.Empty,
                    Lane: lane,
                    Message: message!,
                    Question: req.Arg(LinkAskRows.ArgQuestion),
                    Spoken: req.Arg(LinkAskRows.ArgSpoken) == "1",
                    Agentic: req.Arg(LinkAskRows.ArgAgentic) == "1",
                    SideEffectFree: req.Arg(LinkAskRows.ArgSideEffectFree) != "0"));

                if (!admission.Accepted || admission.Record is null)
                    return LinkRowsReply.Failure(admission.Refusal ?? CircleAI.Assistant.Requests.AskSentences.Busy);
                return LinkRowsReply.Success(new[]
                {
                    LinkAskRows.Ask(admission.Record, admission.Position, textFrom: 0, includeText: true, duplicate: admission.Duplicate),
                });
            }

            case LinkVerb.AskStatus:
            {
                var id = req.Query;
                if (string.IsNullOrWhiteSpace(id)) return LinkRowsReply.Failure("no ask id");
                var since = int.TryParse(req.Arg(LinkAskRows.ArgSince), out var s) ? s : 0;
                var after = long.TryParse(req.Arg(LinkAskRows.ArgAfterSeq), out var a) ? a : -1;
                var waitMs = int.TryParse(req.Arg(LinkAskRows.ArgWaitMs), out var w) ? Math.Clamp(w, 0, 1500) : 0;

                CircleAI.Assistant.Requests.AskRecord? ask;
                if (waitMs > 0 && after >= 0 && await Waiters.WaitAsync(0).ConfigureAwait(false))
                {
                    try { ask = await asks.WaitForChangeAsync(caller, id!, after, TimeSpan.FromMilliseconds(waitMs), CancellationToken.None).ConfigureAwait(false); }
                    finally { Waiters.Release(); }
                }
                else ask = asks.Status(caller, id!);

                if (ask is null) return LinkRowsReply.Failure("no such ask");
                return LinkRowsReply.Success(new[] { LinkAskRows.Ask(ask, asks.PositionOf(ask.Id), since) });
            }

            case LinkVerb.AskCancel:
            {
                var id = req.Query;
                if (string.IsNullOrWhiteSpace(id)) return LinkRowsReply.Failure("no ask id");
                asks.Cancel(caller, id!);
                var ask = asks.Status(caller, id!);
                return ask is null
                    ? LinkRowsReply.Failure("no such ask")
                    : LinkRowsReply.Success(new[] { LinkAskRows.Ask(ask, -1, int.MaxValue) });
            }

            default:   // AskList
            {
                var open = req.Arg(LinkAskRows.ArgOpenOnly) == "1";
                return LinkRowsReply.Success(asks.List(caller, req.Limit, open)
                    .Select(r => LinkAskRows.Ask(r, asks.PositionOf(r.Id), includeText: false)).ToList());
            }
        }
    }

    /// <summary>
    /// The things a person says about the assistant itself, answered without the brain -
    /// from the queue, like every other shortcut.
    /// </summary>
    /// <remarks>
    /// MODEL ADMINISTRATION IS FIRST PARTY. "Clear it" deletes files and "try the big one
    /// again" lifts every refusal; any Chat-scoped app saying "yes" while an offer was
    /// open used to trigger the deletion. Now only the phone itself (the wake turn and
    /// the assistant session) and our own apps can - an offer is answered by somebody
    /// who could have heard it.
    /// </remarks>
    public sealed class SelfRequests : CircleAI.Assistant.Requests.IAskShortcut
    {
        /// <inheritdoc />
        public string Name => "self";

        /// <inheritdoc />
        public ValueTask<string?> TryAnswerAsync(CircleAI.Assistant.Requests.AskRecord ask, CancellationToken ct)
        {
            var admin = ask.CallerKey.StartsWith("internal:", StringComparison.Ordinal)
                        || (FirstPartySignatures is { } trusted
                            && trusted.Any(sig => ask.CallerKey.EndsWith(":" + sig[..Math.Min(16, sig.Length)], StringComparison.Ordinal)));

            switch (CircleAI.Assistant.SelfRequest.Of(ask.Asked, admin && AwaitingAnswer))
            {
                case CircleAI.Assistant.SelfAsk.WhichBrain:
                {
                    var dead = DeadWeightHere();
                    return ValueTask.FromResult<string?>(CircleAI.Assistant.AssistantPersona.WhichBrain(
                        ready:             CircleNeuronService.Brain?.Status.Phase == CircleAI.Assistant.Lifecycle.BrainPhase.Ready
                                           || CircleNeuronService.Node?.IsReady == true,
                        running:           !string.IsNullOrWhiteSpace(CircleNeuronService.RunningModel),
                        gaveUpOnSomething: AnythingRefused(),
                        deadWeightSize:    dead > 0 ? CircleAI.Assistant.CensusRow.Size(dead) : null));
                }

                case CircleAI.Assistant.SelfAsk.ClearIt when admin:
                {
                    var freed = ClearWhatCannotRun();
                    Decisions.Answered();
                    Log.Info(Tag, "asked to clear dead weight; freed " + freed + " bytes");
                    return ValueTask.FromResult<string?>(CircleAI.Assistant.AssistantPersona.ClearIt(
                        freed > 0 ? CircleAI.Assistant.CensusRow.Size(freed) : null));
                }

                case CircleAI.Assistant.SelfAsk.TryAgain when admin:
                {
                    var pardoned = PardonEverythingRefused();
                    Log.Info(Tag, "asked to try again; pardoned " + pardoned + " model(s)");
                    return ValueTask.FromResult<string?>(CircleAI.Assistant.AssistantPersona.TryAgain(pardoned > 0));
                }

                default:
                    return ValueTask.FromResult<string?>(null);
            }
        }
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
