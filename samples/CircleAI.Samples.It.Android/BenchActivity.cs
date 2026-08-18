#nullable enable

// BenchActivity.cs
//
// How fast, how right, and what it costs the phone — for ONE pinned model.
//
// THE BAR IS NOT "DOES IT FIT". A model that loads and then makes somebody wait
// seventy seconds for a wrong answer has failed, and it failed at the only thing
// that matters: a person asked for help and did not get it. So this measures the
// three things a person actually experiences —
//
//   how long until it starts answering   (time to first token)
//   how fast the answer arrives          (tokens per second)
//   whether the answer is any good       (a fixed question set, kept verbatim)
//
// — plus the one thing they experience without knowing it: whether their OTHER
// apps survived. A brain that evicts the messaging app to answer a question has
// taken more than it gave.
//
// TIME TO FIRST TOKEN IS THE HEADLINE, not total time. Speech starts at the first
// sentence, so the wait a person feels is the wait before anything happens; what
// comes after is reading speed. A model with a 3 s first token and a slow tail
// feels alive. One with a 30 s first token and an instant tail feels broken, and
// no throughput number rescues it.
//
// HEADLESS AND DRIVEN FROM adb, one model per run, because the selector is the
// variable being removed. The same handset picked a 0.6B model at 73% battery
// and a 1.5B at 100%; timings taken across that are not comparable to anything.
//
//   adb shell am start -n <pkg>/crc64….BenchActivity --es model Qwen3-1.7B-MNN
//
// Results go to logcat under CircleAI.Bench, one line per question plus a summary
// line, so a run can be collected without touching the screen.

using System;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;   // System.Diagnostics.Activity collides with Android's
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "Bench", Exported = true)]
public class BenchActivity : Activity
{
    const string Tag = "CircleAI.Bench";

    /// <summary>
    /// What a person actually opens this for, in the order they would ask.
    /// </summary>
    /// <remarks>
    /// NOT A BENCHMARK SUITE. No MMLU, no reasoning puzzles. Somebody holding a
    /// R1,500 phone at a taxi rank wants a fact, a direction, some arithmetic, or
    /// company — and wants it in their own language. Every question here has a
    /// checkable answer or an obvious failure mode, so "smartest" can be judged by
    /// reading the log rather than by trusting a score.
    ///
    /// The last one is isiZulu on purpose. A model that answers the first four and
    /// then echoes the fifth back is not usable here, whatever it scores.
    /// </remarks>
    /// <summary>How the answer to a probe is checked.</summary>
    private enum Check
    {
        /// <summary>The claim must appear in the sentence that answers.</summary>
        Phrase,
        /// <summary>The number asserted as the answer must be the right one.</summary>
        Number,
        /// <summary>The reply must be in the asked language and be about the thing asked.</summary>
        Language,
    }

    /// <summary>One probe: what to ask, and how to tell whether the answer is right.</summary>
    /// <param name="Q">The question, as a person would put it.</param>
    /// <param name="Expect">
    /// What a correct answer asserts. For <see cref="Check.Number"/> these are the
    /// acceptable values; for <see cref="Check.Phrase"/>, any one of them.
    /// </param>
    /// <param name="How">Which check applies.</param>
    /// <param name="Reject">
    /// Claims that mean the model got it wrong even if a right word appears
    /// somewhere too. Anywhere in the answer, not just the answering sentence:
    /// asserting the wrong thing later is still asserting it.
    /// </param>
    /// <param name="Lang">The language the reply must come back in.</param>
    /// <param name="Anchor">
    /// For a language probe, what the reply has to be ABOUT. Without this the
    /// check degenerates into "did it produce something Zulu-shaped".
    /// </param>
    private readonly record struct Probe(
        string Q,
        string[] Expect,
        Check How = Check.Phrase,
        string[]? Reject = null,
        string? Lang = null,
        string[]? Anchor = null);

    /// <summary>
    /// Questions with answers that can be CHECKED, not just timed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Speed was the only thing measured until now, which meant a model could
    /// win the table by being confidently wrong very quickly. The whole point of
    /// this product is that a person gets help — being fast at refusing to name
    /// the capital of France is not help, and Qwen2.5-1.5B did exactly that.
    /// </para>
    /// <para>
    /// EVERY ANSWER IS CHECKABLE WITHOUT A HUMAN AND WITHOUT A JUDGE MODEL. A
    /// grader model would cost more than the thing being graded and would need
    /// to be trusted itself.
    /// </para>
    /// <para>
    /// SUBSTRING MATCHING WAS THE FIRST ATTEMPT AND IT GRADED NONSENSE AS
    /// CORRECT. Three real answers from the P30, all scored RIGHT by "does the
    /// text contain the word":
    /// </para>
    /// <code>
    /// "In what year did the Second World War end?"
    ///   -> "The Second World War ended in 1945. Specifically, it concluded with
    ///       the armistice of peace on August 23 (the first day of September)"
    ///                                                    contains "1945" -> RIGHT
    ///
    /// "How many days are in a leap year?"
    ///   -> "There are 365 days in an average non-leap year. A standard calendar
    ///       has 365 days ... though leap years occasionally exist (e.g. 366)"
    ///                                                     contains "366" -> RIGHT
    ///
    /// "Ngicela ungitshele ngeTheku."
    ///   -> "Ugikishwa nge-ukuba uMamutshana ngamanje."
    ///                                       detected as isiZulu -> RIGHT
    /// </code>
    /// <para>
    /// The middle one ANSWERED 365 and mentioned 366 in an aside; the last is
    /// isiZulu-shaped and means nothing — "Ugikishwa" and "uMamutshana" are not
    /// words. A score built on those is worse than no score, because it gets
    /// quoted.
    /// </para>
    /// <para>
    /// SO GRADE THE CLAIM, NOT THE TEXT. Three rules, all still deterministic:
    /// the check reads the sentence that ANSWERS rather than the whole ramble; a
    /// numeric answer is the number the model asserts, not any number it happens
    /// to print; and a wrong claim anywhere disqualifies, so mentioning the right
    /// word after asserting the wrong one no longer passes.
    /// </para>
    /// <para>
    /// WHAT THIS STILL CANNOT DO: judge whether a sentence in isiZulu is
    /// well-formed. The language probe now checks that the reply is in isiZulu
    /// AND is about the thing that was asked, which catches gibberish that
    /// wanders off-topic but not gibberish that stays on it. Grammar needs an ear
    /// or a lexicon we do not ship. The answer is printed in full for exactly
    /// that reason — the log is the evidence and a person settles the rest.
    /// </para>
    /// </remarks>
    static readonly Probe[] Probes =
    {
        // Plain facts a small model should hold. Rejects are the confident wrong
        // answers actually seen, not every city on earth.
        new("What is the capital of France?", new[] { "paris" },
            Reject: new[] { "lyon", "marseille", "nice", "bordeaux" }),
        new("What is the largest ocean on Earth?", new[] { "pacific" },
            Reject: new[] { "atlantic", "indian ocean", "arctic" }),
        new("In what year did the Second World War end?", new[] { "1945" },
            How: Check.Number, Reject: new[] { "1991" }),
        // Arithmetic, where a wrong answer is unmistakable.
        new("What is 17 times 4?", new[] { "68" }, How: Check.Number),
        // The leap-year probe is the one that exposed the old grader: the answer
        // it gave asserted 365 and mentioned 366 in passing.
        new("How many days are in a leap year?", new[] { "366" }, How: Check.Number),
        // Instruction following. A model that cannot obey one short instruction
        // will not obey "answer in isiZulu" or "keep it to two sentences".
        new("Reply with only the word: yes", new[] { "yes" }),
        // South Africa. Local knowledge, not just translated Wikipedia.
        // Johannesburg is the wrong answer this model actually gave.
        new("What is the capital city of South Africa?",
            new[] { "pretoria", "cape town", "bloemfontein" },
            Reject: new[] { "johannesburg", "durban" }),
        // The one that matters most here: asked in isiZulu, and it has to come
        // back in isiZulu AND be about Durban, which is what was asked about.
        new("Ngicela ungitshele ngeTheku.", Array.Empty<string>(),
            How: Check.Language, Lang: "zu",
            Anchor: new[] { "theku", "durban", "thekwini" }),
    };

    TextView _out = null!;
    readonly System.Text.StringBuilder _log = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var scroll = new ScrollView(this);
        _out = Ui.Label(this, "", 12f, Ui.Ink);
        _out.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12));
        scroll.AddView(_out);
        scroll.SetBackgroundColor(Ui.Bg);
        SetContentView(scroll);

        // KEEP THE SCREEN ON. A run takes minutes and the phone sleeping mid-run
        // throttles the CPU, which quietly turns a timing test into a test of
        // Huawei's power manager.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        var model  = Intent?.GetStringExtra("model");
        var lean   = Intent?.GetBooleanExtra("lean", false) ?? false;
        // Room to finish thinking. The product ships a 160-token cap because a
        // spoken answer should be two sentences, but a reasoning model spends
        // its budget on a silent trace and gets cut off before it ever answers —
        // measured, three of five turns came back as 7 characters. Judging it on
        // that is judging it with a gag on.
        var maxTok = Intent?.GetIntExtra("maxtok", 512) ?? 512;
        // Delete the bundle afterwards, so a ladder can walk past what the
        // phone can store. 33 GB free does not hold 14B and 30B and 35B.
        var purge  = Intent?.GetBooleanExtra("purge", false) ?? false;

        // Clear the store and stop. The ladder needs this between phases: the
        // rungs already measured are still holding ~13 GB, and the 35B needs
        // 22.8 GB of the 29 the phone has. Without it the big models are
        // "skipped, not enough disk" — which looks like a verdict and is not.
        if (Intent?.GetBooleanExtra("purgeall", false) ?? false)
        {
            PurgeAll();
            return;
        }

        // What is ACTUALLY on disk. The app is not debuggable, so run-as and
        // adb push cannot see inside its own storage — which left "the model
        // did not load" and "a file never arrived" indistinguishable from the
        // outside. MNN said tokenizer.mtok was missing; nothing available could
        // confirm or deny that without asking the app itself.
        if (Intent?.GetBooleanExtra("ls", false) ?? false)
        {
            ListStore(model);
            return;
        }

        _ = RunAsync(model, lean, maxTok, purge);
    }

    void Say(string line)
    {
        Log.Info(Tag, line);
        RunOnUiThread(() => { _log.AppendLine(line); _out.Text = _log.ToString(); });
    }

    async Task RunAsync(string? model, bool lean, int maxTok, bool purge)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            Say("no model given — pass --es model <RegistryId>");
            return;
        }

        Say($"=== {model}{(lean ? "  [lean]" : "")} maxtok={maxTok} ===");
        Say($"disk:   {FreeGb():F1} GB free");
        Say($"before: {MemLine()}");

        // FETCHING AND LOADING ARE DIFFERENT QUESTIONS AND WERE ON ONE CLOCK.
        //
        // StartAsync downloads the bundle if it is absent and then loads it, and
        // the stopwatch below used to wrap both. So a model that was not yet on
        // the phone reported its download as load time: Qwen3-1.7B came back as
        // "loaded in 238.8 s" on a 1.24 GB bundle over a 6 MB/s link — about
        // 207 s of that was the network, and the load itself was around 30 s. It
        // was recorded, and reported, as a model too slow to load.
        //
        // At 22.8 GB the same conflation would read as an hour-long load and
        // condemn the model outright. So the fetch is done first and timed on its
        // own clock; by the time the loader runs, the bundle is already on disk
        // and the second number means only what it says.
        await FetchAsync(model);

        ItSession session;
        var load = Stopwatch.StartNew();
        var freeBeforeLoad = FreeGb();
        try
        {
            session = await Task.Run(async () =>
            {
                var s = new ItSession(
                    ApplicationInfo?.NativeLibraryDir,
                    batteryPercent: () => 100,
                    pinModelId: model,
                    lean: lean,
                    maxTokens: maxTok);
                await s.StartAsync();
                return s;
            });
        }
        catch (Exception ex)
        {
            // THE FAILURES ARE THE POINT. Walking up the ladder until something
            // breaks is how the ceiling gets found, so a model that will not load
            // is a result and gets recorded like one — not a crash.
            load.Stop();
            Say($"LOAD FAILED after {load.ElapsedMilliseconds} ms: {Innermost(ex)}");
            Say($"after:  {MemLine()}");
            Say($"disk:   {FreeGb():F1} GB free (was {freeBeforeLoad:F1} before load)");
            Say($"scratch:{ScratchLine(model)}");
            Say($"RESULT {model} load=FAIL");
            return;
        }
        load.Stop();

        // WHAT THE LOAD COST IN DISK, not just in seconds. MNN's mmap path writes
        // a scratch copy of the weights into tmp_path, so on a big bundle the
        // load needs the model AND roughly its size again in free space — which
        // is what killed this model the first time, and reads from the outside
        // as an unexplained SIGKILL rather than as running out of room.
        Say($"loaded in {load.ElapsedMilliseconds / 1000.0:F1} s — {session.StatusLine}");
        Say($"after:  {MemLine()}");
        Say($"self:   {SelfPssMb()} MB");
        Say($"disk:   {FreeGb():F1} GB free (was {freeBeforeLoad:F1} before load)");
        Say($"scratch:{ScratchLine(model)}");

        double ttftTotal = 0, charsTotal = 0, wallTotal = 0;
        var answered = 0;
        var correct  = 0;

        foreach (var probe in Probes)
        {
            var q  = probe.Q;
            var sw = Stopwatch.StartNew();
            long ttft = -1, tlast = -1;
            var chars = 0;
            var chunks = 0;
            // WHAT THE MODEL SAID, TAKEN FROM THE RETURN VALUE, NOT FROM THE
            // CHUNKS. The callback also carries the host's own decoration —
            // RunTurnStreamingAsync emits "IT! > " and a trailing newline through
            // the same channel, because that channel is what draws the chat. So
            // an accumulated transcript starts "IT! > " and grading it graded
            // that: every answer's first sentence came out as "it", because the
            // "!" in the speaker label ends a sentence. It failed "yes" against
            // an answer of "yes", and the whole run scored 1/8.
            //
            // The chunks are still what timing needs — when the first one landed
            // and how many there were. The words are a different question, and
            // the method already returns them clean: "IT! > " and the trailing
            // newline reach onChunk only, never the StringBuilder it returns.
            var said = string.Empty;

            try
            {
                said = await session.RunTurnStreamingAsync(
                    q,
                    _ => { },
                    chunk =>
                    {
                        // First chunk with actual content — whitespace-only chunks
                        // are real and must not be counted as the answer starting.
                        if (ttft < 0 && !string.IsNullOrWhiteSpace(chunk))
                            ttft = sw.ElapsedMilliseconds;
                        // WHEN each chunk lands, and HOW MANY. ttft has equalled
                        // total on every measurement so far, which either means
                        // the answer arrives as one lump at the end, or that it
                        // arrives in pieces that are all bunched at the end. The
                        // gap between the first and last chunk tells them apart.
                        tlast = sw.ElapsedMilliseconds;
                        chunks++;
                    },
                    _ => { });
            }
            catch (Exception ex)
            {
                Say($"Q  {q}");
                Say($"   TURN FAILED: {Innermost(ex)}");
                continue;
            }
            sw.Stop();

            // ~4 characters per token is close enough for a rate; the exact
            // tokeniser count is not what is being compared here. Measured on
            // the model's own words, so the host's speaker label does not get
            // counted as seven characters the model never produced.
            chars = said.Length;
            var tokens = chars / 4.0;
            var secs   = sw.ElapsedMilliseconds / 1000.0;
            var rate   = secs > 0 ? tokens / secs : 0;

            var (ok, why) = Grade(probe, said);
            if (ok) correct++;

            Say($"Q  {q}");
            Say($"   ttft {ttft} ms | last {tlast} ms | total {secs:F1} s | " +
                $"~{rate:F1} tok/s | {chars} chars in {chunks} chunks");
            Say($"   {(ok ? "RIGHT" : "WRONG")}  {why}");
            // The answer itself. A score with no evidence is a number to be
            // argued with; the words are what settle it — and for the language
            // probe the words are the ONLY thing that can settle it, since
            // nothing here can tell well-formed isiZulu from a fluent-looking
            // invention. 160 characters was not enough to see the claim and what
            // the model did to it afterwards.
            Say($"   > {Squash(said, 400)}");

            if (ttft >= 0) { ttftTotal += ttft; answered++; }
            charsTotal += chars;
            wallTotal  += secs;
        }

        var meanTtft = answered > 0 ? ttftTotal / answered : -1;
        var meanRate = wallTotal > 0 ? (charsTotal / 4.0) / wallTotal : 0;

        Say($"after all: {MemLine()}");
        Say($"self:      {SelfPssMb()} MB");
        Say($"RESULT {model} load=OK loadms={load.ElapsedMilliseconds} " +
            $"ttft={meanTtft:F0} tokps={meanRate:F1} pss={SelfPssMb()} " +
            $"score={correct}/{Probes.Length}");

        await session.DisposeAsync();
        if (purge) Purge(model);
    }

    /// <summary>Was the answer right, and by what test.</summary>
    /// <remarks>
    /// WHAT THE MODEL CLAIMED, NOT WHAT ITS TEXT CONTAINED. The old version
    /// asked "does this string contain 'paris'", which passes an answer that
    /// says the capital is Lyon and mentions Paris afterwards — and did pass
    /// three real nonsense answers, quoted on the Probes table above.
    ///
    /// Four rules, all deterministic and all cheap:
    ///
    ///   THE ANSWERING SENTENCE, NOT THE ESSAY. A direct question is answered in
    ///   the first sentence and everything after it is elaboration. "There are
    ///   365 days in an average non-leap year ... (e.g. 366)" asserted 365; the
    ///   366 in the tail is not the answer and must not score as one.
    ///
    ///   A NUMBER ANSWER IS THE NUMBER ASSERTED. Taken as the LAST number of the
    ///   answering sentence, because models restate the question before
    ///   answering it — "17 times 4 is 68" has to grade on 68, not on 17.
    ///
    ///   A WRONG CLAIM ANYWHERE DISQUALIFIES. Rejects are checked across the
    ///   whole answer, not just the first sentence: saying Johannesburg later is
    ///   still saying it.
    ///
    ///   NEGATION FLIPS IT. "The capital is not Paris" contains Paris and is
    ///   wrong. Only a short window before the match is examined, because that
    ///   is where a negation that governs it can be.
    ///
    /// Case and punctuation are stripped so "Paris." and "paris" both count. The
    /// failure this avoids is scoring a correct answer wrong for its full stop,
    /// which would quietly favour whichever model happens to be terser.
    ///
    /// Still no judge model, and still nothing here that can tell a grammatical
    /// isiZulu sentence from a fluent-looking invented one. See the note on the
    /// Probes table; the full answer is logged so a person can settle that.
    /// </remarks>
    static (bool Ok, string Why) Grade(Probe p, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return (false, "said nothing");

        // DEGENERATE REPETITION IS NOT AN ANSWER, and it fooled this grader the
        // first time it ran. Qwen2.5-1.5B replied to the isiZulu probe with
        // "Ngikusekile ngiThembu" repeated for 512 tokens; LanguageGuess saw
        // isiZulu and scored it RIGHT. A model stuck in a loop would have
        // ranked above a model that answered briefly and correctly.
        //
        // Checked before anything else, because a loop can satisfy a substring
        // test too — repeat "Paris, Paris, Paris" and the fact test passes.
        if (IsLooping(answer, out var loopWhy)) return (false, loopWhy);

        var a = answer.ToLowerInvariant();

        // The sentence that answers. Everything after it is elaboration, and
        // elaboration is where the right word turns up in the wrong answers.
        var claim = FirstSentence(a);

        // A CLAIM THE ANSWER SHOULD NOT MAKE — in the answering sentence.
        //
        // Scoped there rather than across the whole reply, which was the first
        // attempt and was too blunt: "The capital is Cape Town. The country has
        // other large cities, such as Johannesburg" is a CORRECT answer that
        // happens to name a rejected city, and failing it would trade one kind
        // of wrong score for another. What must fail is asserting it —
        // "The capital of South Africa is Johannesburg", which is what this
        // model actually said.
        foreach (var no in p.Reject ?? Array.Empty<string>())
            if (claim.Contains(no, StringComparison.Ordinal))
                return (false, $"answers '{no}'");

        if (p.How == Check.Language)
        {
            // Graded by the same detector the product uses to pick a reply
            // language. A model that answers an isiZulu question in English has
            // failed at the thing this exists for, however fast it was.
            var got = CircleAI.Samples.It.LanguageGuess.Detect(answer);
            if (got != p.Lang)
                return (false, $"wanted {p.Lang}, answered in {got ?? "something unrecognised"}");

            // AND ABOUT THE RIGHT THING. Language alone let "Ugikishwa nge-ukuba
            // uMamutshana ngamanje" — isiZulu-shaped, meaningless — score as a
            // correct answer about Durban. An answer that never mentions what it
            // was asked about has not answered, in any language.
            var anchors = p.Anchor ?? Array.Empty<string>();
            if (anchors.Length > 0 && !anchors.Any(x => a.Contains(x, StringComparison.Ordinal)))
                return (false, $"in {p.Lang} but not about {anchors[0]}");

            // SAY WHAT WAS ACTUALLY VERIFIED. This passed
            // "Ngičele kuthengiso ngitheku" — the question's own words, lightly
            // mangled, in an orthography isiZulu does not use. It is in the right
            // language and mentions the right place, and it means nothing.
            //
            // Both halves of that are as far as a machine gets here without a
            // lexicon we do not ship, so the verdict says so instead of reading
            // as a clean pass. The answer is printed above it; a person decides.
            return (true, $"in {p.Lang}, on topic — meaning needs an ear");
        }

        if (p.How == Check.Number)
        {
            // THE NUMBER IT ASSERTS, and finding it takes two rules because
            // models answer arithmetic in two shapes.
            //
            // A WORKED ANSWER PUTS THE RESULT AFTER AN EQUALS SIGN, often on its
            // own line: "you can multiply them using basic arithmetic:\n
            // $$17 \times 4 = 68$$". Graded on the answering sentence alone that
            // scores 4 — the 4 restated from the question — because the newline
            // ended the sentence before the model reached the result.
            //
            // ANY computed result, not the first and not the last. Both were
            // tried against real answers on the P30 and each failed the other's
            // case: taking the LAST scored "$8.5 + 10.5 = 17$" from a fractions
            // aside that followed a correct 68, and taking the FIRST scored
            // "1 \times 4 = 4" from the opening step of a long multiplication.
            // Neither position is reliably the answer in a worked solution.
            //
            // What the probe actually asks is whether the model computed the
            // right value, so a run that reaches it anywhere in its working has
            // done that. A wrong result cannot be rescued this way: it has to
            // produce the number, and if it never does, the answering sentence
            // decides — which is where a model that just asserts lands.
            var results = Results(a);
            var hit = p.Expect.FirstOrDefault(e => results.Contains(e, StringComparer.Ordinal));
            if (hit is not null) return (true, $"computes {hit}");

            var said = LastNumber(claim) ?? (results.Count > 0 ? results[^1] : null);
            if (said is null) return (false, "gave no number");

            return p.Expect.Contains(said, StringComparer.Ordinal)
                ? (true,  $"answered {said}")
                : (false, $"answered {said}, wanted {string.Join(" or ", p.Expect)}");
        }

        foreach (var want in p.Expect)
        {
            var at = claim.IndexOf(want, StringComparison.Ordinal);
            if (at < 0) continue;
            if (Negated(claim, at)) return (false, $"denies '{want}'");

            // ANSWERED, THEN ARGUED WITH ITSELF. Seen on the P30: "The largest
            // ocean on Earth is the Pacific Ocean ... It is second only to the
            // Arctic Ocean." The answering sentence is right and the reply is
            // still not usable.
            //
            // Said rather than scored, because the two shapes are not separable
            // without semantics: "Cape Town is the capital; other cities include
            // Johannesburg" names a rejected term in a sentence that contradicts
            // nothing. Failing on the mention would trade one wrong verdict for
            // another, so the verdict stays on the claim and the reader is told
            // where to look.
            var later = (p.Reject ?? Array.Empty<string>())
                .FirstOrDefault(no => a.Contains(no, StringComparison.Ordinal));

            return (true, later is null ? $"says '{want}'"
                                        : $"says '{want}' — but also mentions '{later}'");
        }

        return (false, $"does not answer with any of: {string.Join(", ", p.Expect)}");
    }

    /// <summary>The sentence that answers — the first one, or the whole reply.</summary>
    /// <remarks>
    /// Split on a full stop, question mark or newline. A decimal point is not a
    /// sentence end, which matters because these probes ask for numbers: "3.5"
    /// must not become "3".
    /// </remarks>
    static string FirstSentence(string lower)
    {
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            if (c == '\n' || c == '?' || c == '!') return lower[..i];
            if (c != '.') continue;

            // A full stop between two digits is a decimal point, and one before
            // a non-space is usually an abbreviation rather than an ending.
            var digitBefore = i > 0 && char.IsDigit(lower[i - 1]);
            var digitAfter  = i + 1 < lower.Length && char.IsDigit(lower[i + 1]);
            if (digitBefore && digitAfter) continue;
            if (i + 1 < lower.Length && !char.IsWhiteSpace(lower[i + 1])) continue;

            return lower[..i];
        }
        return lower;
    }

    /// <summary>Every number stated as a result — those written after "=".</summary>
    /// <remarks>
    /// Only an equals sign counts. "is" and "equals" were tried and both match
    /// the restatement as readily as the result — "17 times 4 is what we are
    /// computing" — while "=" is only ever written by a model that has finished
    /// computing something.
    /// </remarks>
    static IReadOnlyList<string> Results(string text) =>
        System.Text.RegularExpressions.Regex
            .Matches(text, @"=\s*\**\s*(\d+)")
            .Select(m => m.Groups[1].Value)
            .ToList();

    /// <summary>The last whole number in a stretch of text, or null.</summary>
    /// <remarks>
    /// Whole numbers only, and matched on boundaries: "1945" must not be found
    /// inside "11945", and "68" must not match the 68 in "1968".
    /// </remarks>
    static string? LastNumber(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\d+");
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    /// <summary>Whether a negation governs the match at this position.</summary>
    /// <remarks>
    /// Looks back a few words only. Further than that and the negation belongs to
    /// a different clause — "it is not in Europe, the capital is Paris" is a
    /// correct answer and must not be failed by a "not" eight words earlier.
    /// </remarks>
    static bool Negated(string claim, int at)
    {
        var from = Math.Max(0, at - 24);
        var before = claim[from..at];
        return before.Contains(" not ", StringComparison.Ordinal)
            || before.Contains("n't ", StringComparison.Ordinal)
            || before.Contains(" never ", StringComparison.Ordinal)
            || before.Contains(" isn", StringComparison.Ordinal);
    }

    /// <summary>Whether the answer is a loop rather than a reply.</summary>
    /// <remarks>
    /// VOCABULARY, NOT PATTERNS. A model that has locked into a cycle keeps
    /// saying the same few words however long it runs, so the share of DISTINCT
    /// words collapses — that holds whatever the cycle length is, which
    /// searching for a repeated phrase would not.
    ///
    /// Only applied past twenty words. Below that a low ratio is just a short
    /// answer: "Yes" and "366" have every right to a small vocabulary, and
    /// failing them would penalise exactly the brevity the product asks for.
    /// </remarks>
    static bool IsLooping(string answer, out string why)
    {
        why = string.Empty;
        var words = System.Text.RegularExpressions.Regex
            .Split(answer.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0)
            .ToArray();

        if (words.Length < 20) return false;

        var ratio = words.Distinct().Count() / (double)words.Length;
        if (ratio >= 0.25) return false;

        why = $"looping — {words.Distinct().Count()} distinct words in {words.Length} " +
              $"({ratio:P0} unique)";
        return true;
    }

    /// <summary>One line of an answer, short enough to read in a log.</summary>
    static string Squash(string s, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..max] + "...";
    }

    /// <summary>Every file in the store, with its size — the ground truth.</summary>
    void ListStore(string? model)
    {
        try
        {
            var root = ModelDir();
            if (!System.IO.Directory.Exists(root)) { Say($"no store at {root}"); return; }

            foreach (var dir in System.IO.Directory.GetDirectories(root))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(model) && !string.Equals(name, model, StringComparison.Ordinal))
                    continue;

                var files = System.IO.Directory.GetFiles(dir, "*", System.IO.SearchOption.AllDirectories);
                var total = files.Sum(f => new System.IO.FileInfo(f).Length);
                Say($"{name}  —  {files.Length} files, {total / 1e9:F2} GB");
                foreach (var f in files.OrderBy(f => f))
                {
                    var fi = new System.IO.FileInfo(f);
                    Say($"   {fi.Name,-26} {fi.Length,15:N0}");
                }
            }
            Say($"disk: {FreeGb():F1} GB free");
        }
        catch (Exception ex) { Say("ls failed: " + Innermost(ex)); }
    }

    /// <summary>Downloads the bundle if it is not already here, on its own clock.</summary>
    /// <remarks>
    /// Separated from the load so neither number lies about the other — see the
    /// note at the call site. Reports throughput too, because "is this slow" and
    /// "is this link slow" are different findings and only one of them is about
    /// the model.
    /// </remarks>
    async Task FetchAsync(string model)
    {
        try
        {
            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            using var loader   = new CircleAI.Inference.BundleModelLoader(ModelDir(), registry);

            if (loader.ModelExists(model)) { Say("fetch:  already on the phone"); return; }

            var entry = registry.AllModels.FirstOrDefault(m => m.Name == model);
            var gb    = entry is null ? 0 : entry.TotalBytes / 1e9;
            Say($"fetch:  {gb:F2} GB to download");

            var sw   = Stopwatch.StartNew();
            var last = 0;
            var progress = new Progress<float>(f =>
            {
                // Every ten percent, so a long fetch shows life without flooding
                // the log with a line per buffer.
                var pct = (int)(Math.Clamp(f, 0f, 1f) * 100);
                if (pct < last + 10) return;
                last = pct;
                var mbps = gb * 1e3 * Math.Clamp(f, 0f, 1f) / Math.Max(1, sw.Elapsed.TotalSeconds);
                Say($"fetch:  {pct,3}%  {sw.Elapsed.TotalMinutes:F1} min  {mbps:F1} MB/s");
            });

            await Task.Run(() => loader.DownloadModelAsync(model, progress));
            sw.Stop();

            var rate = gb * 1e3 / Math.Max(1, sw.Elapsed.TotalSeconds);
            Say($"fetch:  done in {sw.Elapsed.TotalMinutes:F1} min ({rate:F1} MB/s)");
        }
        catch (Exception ex)
        {
            // Not fatal here: the loader will try again inside StartAsync and
            // fail there with its own message if the bundle really is unusable.
            Say($"fetch:  {Innermost(ex)}");
        }
    }

    /// <summary>How much disk the load left behind beside the model, and where.</summary>
    /// <remarks>
    /// The mmap scratch is the difference between a model that fits and one that
    /// is killed for reasons nothing prints. Naming the files and their sizes
    /// turns "SIGKILL" into an arithmetic problem somebody can act on.
    /// </remarks>
    string ScratchLine(string model)
    {
        try
        {
            var dir = System.IO.Path.Combine(ModelDir(), model, "mmap");
            if (!System.IO.Directory.Exists(dir)) return " none (no mmap scratch written)";

            var files = System.IO.Directory.GetFiles(dir, "*", System.IO.SearchOption.AllDirectories);
            if (files.Length == 0) return " empty";

            var total = files.Sum(f => new System.IO.FileInfo(f).Length);
            var names = string.Join(", ", files.Take(3).Select(f =>
                $"{System.IO.Path.GetFileName(f)} {new System.IO.FileInfo(f).Length / 1e9:F2} GB"));
            return $" {files.Length} file(s), {total / 1e9:F2} GB — {names}";
        }
        catch (Exception ex) { return " unreadable: " + Innermost(ex); }
    }

    /// <summary>Empties the model store, then says what that bought.</summary>
    void PurgeAll()
    {
        try
        {
            var root = ModelDir();
            if (!System.IO.Directory.Exists(root)) { Say($"purgeall: no store at {root}"); return; }

            Say($"purgeall: {FreeGb():F1} GB free before");
            foreach (var dir in System.IO.Directory.GetDirectories(root))
            {
                try
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                    Say("  removed " + System.IO.Path.GetFileName(dir));
                }
                catch (Exception ex) { Say($"  kept {System.IO.Path.GetFileName(dir)}: {Innermost(ex)}"); }
            }
            Say($"purgeall: {FreeGb():F1} GB free after");
        }
        catch (Exception ex) { Say("purgeall failed: " + Innermost(ex)); }
    }

    /// <summary>Free space on the volume holding the model store.</summary>
    double FreeGb()
    {
        try
        {
            var s = new Android.OS.StatFs(ModelDir());
            return s.AvailableBytes / 1024.0 / 1024.0 / 1024.0;
        }
        catch { return -1; }
    }

    // System.Environment, spelled out: Android.OS.Environment is also in scope
    // here and the two are not interchangeable.
    static string ModelDir() => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "CircleAI", "Models");

    /// <summary>
    /// Deletes a model bundle once it has been measured.
    /// </summary>
    /// <remarks>
    /// A LADDER OUTLIVES THE DISK. Walking from 0.5B to 35B means fetching well
    /// over 60 GB, and the phone has 33 GB free — without this the run does not
    /// find the model that is too big to RUN, it finds the one that is too big
    /// to STORE, and reports a download failure as if it were a verdict.
    ///
    /// Only under an explicit flag. A person's downloaded models are theirs, and
    /// re-fetching one over a metered connection because a tool tidied up is a
    /// real cost. This is for a bench walking a ladder, nothing else.
    /// </remarks>
    void Purge(string model)
    {
        try
        {
            var dir = System.IO.Path.Combine(ModelDir(), model);
            if (System.IO.Directory.Exists(dir))
            {
                var bytes = new System.IO.DirectoryInfo(dir)
                    .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                System.IO.Directory.Delete(dir, recursive: true);
                Say($"purged {model} ({bytes / 1e9:F2} GB) - {FreeGb():F1} GB free");
            }
            else
            {
                Say($"purge: nothing at {dir}");
            }
        }
        catch (Exception ex)
        {
            Say($"purge failed: {Innermost(ex)}");
        }
    }

    /// <summary>Free memory as the OS sees it — the "is the phone still usable" number.</summary>
    /// <remarks>
    /// MemAvailable rather than MemFree. MemFree counts only untouched pages and
    /// reads near zero on any Android device that has been on for an hour, which
    /// makes it useless for deciding whether another app can still start.
    /// </remarks>
    string MemLine()
    {
        try
        {
            var mi = new ActivityManager.MemoryInfo();
            var am = (ActivityManager?)GetSystemService(ActivityService);
            am?.GetMemoryInfo(mi);
            return $"avail {mi.AvailMem / 1048576} MB of {mi.TotalMem / 1048576} MB" +
                   (mi.LowMemory ? "  (LOW)" : "");
        }
        catch { return "mem unknown"; }
    }

    long SelfPssMb()
    {
        try
        {
            var am = (ActivityManager?)GetSystemService(ActivityService);
            var info = am?.GetProcessMemoryInfo(new[] { Android.OS.Process.MyPid() });
            return info is { Length: > 0 } ? info[0].TotalPss / 1024 : -1;
        }
        catch { return -1; }
    }

    static string Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is { } inner) e = inner;
        return e.Message.Trim();
    }
}
