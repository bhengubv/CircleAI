// SpeechGate.cs
//
// DECIDING WHETHER A BLOCK OF AUDIO IS WORTH RUNNING A MODEL ON.
//
// WHY THE WAKE WORD SUDDENLY NEEDS ONE. The keyword spotter it replaces is 3.3
// MILLION parameters and can afford to run on every frame for ever. The
// streaming recogniser that replaces it is 20M - six times the model - and
// running that continuously on a Kirin 710 is a battery question, not a
// correctness one.
//
// But a room is silent almost all of the time, and a cheap arithmetic test can
// tell a voice from a room. Measured on the P30 on 2026-10-12: the quietest
// second of a room read 0,00093 RMS and the owner's speech read 0,045 - a
// factor of fifty.
//
// AND THEN THE FIRST VERSION OF THIS CLASS HARD-CODED THAT 0,00093.
//
// It opened at a fixed 0,003 - "three times the quiet room" - and on the P30 at
// 04:37 that same night it passed 77 TO 87 PER CENT of blocks and bought
// nothing. The room was not the room that had been measured. A threshold taken
// from one room is a constant that only works in that room, which is the same
// defect as hard-coding the wake phrase: the one number nobody else can be.
//
// So it LEARNS the room. It tracks the floor while it is shut - speech can
// never raise it - and opens at a small multiple above whatever the room
// actually is. The measured figures above survive as the BOUNDS either side of
// that multiple, which is the honest job for a number taken in one room.
//
// THE HANGOVER IS THE PART THAT IS EASY TO GET WRONG. A gate that closes the
// instant the energy drops cuts the last syllable off every phrase, and a wake
// phrase that loses its final token is exactly the failure this product spent a
// day on: "Hey B" reached two of its three tokens 1,796 times and completed
// zero. So it stays open for a while after the sound stops.
//
// AND IT OPENS BEFORE IT IS SURE, NOT AFTER. A gate that waits for confirmation
// misses the attack of the first word. It is far cheaper to decode a little
// silence than to lose the start of the phrase.
//
// IT ALSO HAS TO SHUT EVENTUALLY. A recogniser behind it holds state across
// everything it is fed, so a gate held open by a noisy room hands it one
// unbounded utterance - and measured at 04:37 that produced a transcript which
// grew for ever and read "YOU SEE THERE YOU ARE TO YOU WHOM MOVED YOU THE DAYS
// FOLLOWED UNDER A SPARECT". Nobody said that. An endpoint is not a nicety: a
// recogniser with no endpoint invents words until one of them is the wake
// phrase, and a wake word that fires on noise cannot be worked around.
//
// PURE ARITHMETIC OVER A BLOCK, so it is asserted on both framework legs
// instead of being discovered on a phone - which is where every other rule in
// this feature was hiding while it was wrong.

using System;

namespace CircleAI.Voice;

/// <summary>Whether the expensive model should run on this block of audio.</summary>
public sealed class SpeechGate
{
    /// <summary>How long the gate stays open after the sound stops.</summary>
    /// <remarks>
    /// A WHOLE SECOND, AND IT IS NOT GENEROSITY. A transducer needs trailing
    /// silence to commit its last token, and the pause inside "Hey ... B" is
    /// itself a few hundred milliseconds. Closing early truncates the phrase,
    /// which is the precise way this feature failed before.
    /// </remarks>
    public TimeSpan Hangover { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether blocks heard while the gate is open teach it the room.</summary>
    /// <remarks>
    /// TRUE FOR THE WAKE LOOP, FALSE FOR A QUESTION - and the two are different
    /// because of how long each one listens.
    /// <para>
    /// The wake loop listens for hours. Its window is overwhelmingly room, so a
    /// few seconds of somebody talking never moves the median, and it MUST learn
    /// while open: a room loud enough to hold the gate open would otherwise
    /// never be able to raise the bar, which is the trap measured on the P30 at
    /// 04:37, where 87 per cent of blocks reached the model for ever.
    /// </para>
    /// <para>
    /// A question is the opposite case. The turn opens a fresh gate seconds
    /// after saying "Hi." and the person may start at once, so its window has no
    /// room in it to speak of. Learning from what it hears there fills the
    /// window with the question, takes the question as the room, lifts the bar
    /// above the speaker, and shuts mid-sentence. Set false, it learns only from
    /// blocks that leave it shut - the room before they spoke - and fifteen
    /// seconds is far too short for that room to have changed.
    /// </para>
    /// </remarks>
    public bool LearnsWhileOpen { get; init; } = true;

    /// <summary>The longest one open stretch may run before it is cut.</summary>
    /// <remarks>
    /// EIGHT SECONDS, BECAUSE A WAKE PHRASE IS TWO. Anything still open after
    /// that is a room, not a phrase, and the cost of letting it run is a
    /// recogniser that hallucinates - see this file's header for what it wrote.
    /// </remarks>
    public TimeSpan MostOpen { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Where in the recent levels the room's own level is read off.</summary>
    /// <remarks>
    /// THE MEDIAN, because a wake word is listened for far more than it is
    /// spoken. Over any window the overwhelming majority of blocks are room
    /// rather than voice, so the middle of them IS the room - and a percentile
    /// ignores the loud minority completely, which is exactly the property an
    /// average does not have.
    /// </remarks>
    public double RoomQuantile { get; init; } = 0.5;

    /// <summary>How much recent audio the room estimate is read from.</summary>
    /// <remarks>
    /// LONG ENOUGH THAT TALKING CANNOT FILL IT. A person asking for something
    /// occupies two or three seconds; a window of half a minute keeps that a
    /// small minority of its contents, so the median stays the room even while
    /// somebody is mid-sentence. Shorter windows adapt faster and start letting
    /// the speaker become the room.
    /// </remarks>
    public TimeSpan Remembers { get; init; } = TimeSpan.FromSeconds(30);

    private TimeSpan _since = TimeSpan.MaxValue;
    private double _floor = -1;

    // The recent levels, as a ring. Sized on the first block from the real
    // block length, because this class is told the rate rather than choosing it.
    private double[] _levels = Array.Empty<double>();
    private int _at;
    private int _held;
    private int _untilSort;
    private double _busiest;
    private double[] _scratch = Array.Empty<double>();

    /// <summary>True while the gate is letting audio through.</summary>
    public bool Open { get; private set; }

    /// <summary>True on the block that ENDED an open stretch.</summary>
    /// <remarks>
    /// THE OTHER EDGE, AND IT IS THE ONE THAT CARRIES THE ANSWER. A transducer
    /// does not emit its last token until something tells it the utterance is
    /// over; while audio keeps arriving it is always waiting for more context.
    /// So the moment the gate shuts is the moment the recogniser has to be
    /// asked what it heard - and if nobody asks, the final token of every
    /// phrase stays uncommitted for ever.
    /// <para>
    /// That is not a theory about this product, it is this product's history:
    /// the keyword spotter it replaced reached two of "Hey B"'s three tokens
    /// 1,796 times and completed zero.
    /// </para>
    /// </remarks>
    public bool Closed { get; private set; }

    /// <summary>True on the block that began this open stretch.</summary>
    /// <remarks>
    /// THE SEAM A RECOGNISER NEEDS. Anything stateful behind this gate has to
    /// forget what it heard before the cut, because the silence the gate
    /// swallowed never reached it and its state is now describing audio that
    /// does not exist. Reading <see cref="Open"/> twice to spot that edge is
    /// the kind of thing every caller gets wrong differently.
    /// </remarks>
    public bool Opened { get; private set; }

    /// <summary>The room's typical level, as learnt from every block.</summary>
    public double Floor => _floor < 0 ? 0 : _floor;

    /// <summary>The loudest block in the window the room is measured over.</summary>
    /// <remarks>
    /// THE NUMBER WHOSE ABSENCE COST A DAY. The gate reported the room and the
    /// bar it was demanding, and both were correct, and neither could answer
    /// the only question that mattered: DID A VOICE ARRIVE AT ALL. A room of
    /// 0,0010 under a bar of 0,0040 looks identical whether somebody is
    /// speaking into it or the phone is alone on a table.
    /// <para>
    /// Measured 2026-10-12: the owner's voice through this capture path reaches
    /// 0,0023 at its ninetieth percentile - BELOW the lowest bar the gate will
    /// go - while the same voice through the phone's own recorder app reaches
    /// 0,0611. Printing the loudest block beside the bar makes that visible in
    /// one line instead of in four hours of elimination.
    /// </para>
    /// </remarks>
    public double Loudest
    {
        get
        {
            var top = 0.0;
            for (var i = 0; i < _held; i++)
                if (_levels[i] > top) top = _levels[i];
            return top;
        }
    }

    /// <summary>The level a block has to beat right now.</summary>
    /// <remarks>
    /// A MULTIPLE OF THE ROOM, BOUNDED - and the bounds are the part that was
    /// wrong, not the shape.
    /// <para>
    /// They used to be 0,004 and 0,030, taken from P30 figures of 0,00093 for a
    /// quiet room and 0,045 for the owner's speech. Those were measured through
    /// the PHONE'S OWN RECORDER APP. Through the capture path this gate is
    /// actually attached to, the same voice in the same room reads, per-second
    /// RMS:
    /// </para>
    /// <para>
    ///     recorder app      room 0,00093   speech median 0,0446   p90 0,0611
    ///     our capture path   room 0,0011    speech p90     0,0023  loudest 0,0156
    /// </para>
    /// <para>
    /// So the LOWEST bar the gate would go to - 0,004 - sat ABOVE the speech it
    /// exists to pass. The numbers were right about the phone and wrong about
    /// the instrument, which is the whole reason this feature went a day
    /// without a single wake.
    /// </para>
    /// <para>
    /// A RATIO-ONLY VERSION WAS TRIED AND ABANDONED, and it is worth saying why
    /// rather than leaving it to be re-attempted: drawing the bar between the
    /// window's median and its ninety-fifth percentile is genuinely
    /// instrument-independent, and it also reads a CONTINUOUS loud sound as the
    /// room - correct for a television, and it means a long steady sentence
    /// stops opening the gate. It traded one calibration problem for a harder
    /// one, and it is not what stands between this product and a wake: at the
    /// 2x signal-to-noise this capture path delivers, NO gate setting both
    /// refuses the room and passes the voice. The signal is the defect; this is
    /// a bar.
    /// </para>
    /// </remarks>
    public double Opening => Math.Clamp(
        _floor < 0 ? QuietestOpening : _floor * NoiseMultiple,
        QuietestOpening,
        LoudestOpening);

    /// <summary>How far above the room's own level a block has to be.</summary>
    /// <remarks>
    /// LOWERED FROM THREE, because three does not fit this capture path. Our
    /// own recordings put the room at 0,0011 and speech at 0,0023 to 0,0053, so
    /// three times the room lands at 0,0033 - above most of the voice. At 1,8
    /// the bar sits at 0,0020, under the voice and over the room.
    /// <para>
    /// It is also an honest admission of how little headroom there is: a
    /// multiple this close to 1 only works because the room is steady. The
    /// right fix is the 12 to 29 dB this path loses, not this number.
    /// </para>
    /// </remarks>
    public double NoiseMultiple { get; init; } = 1.8;

    /// <summary>The lowest the opening level is ever allowed to fall.</summary>
    /// <remarks>
    /// UNDER THE QUIETEST SPEECH THIS PATH HAS EVER DELIVERED (0,0023) and over
    /// the room it delivers it against (0,0011). The old 0,004 was under the
    /// RECORDER APP's speech and over ours, which is exactly as useful as no
    /// gate at all.
    /// </remarks>
    public double QuietestOpening { get; init; } = 0.0015;

    /// <summary>The highest the opening level is ever allowed to climb.</summary>
    /// <remarks>
    /// BELOW MEASURED SPEECH ON THE LOUDER INSTRUMENT (0,045), so a loud room
    /// can never push the bar past a voice on either path. It does not bind on
    /// our capture path, which has never produced a second over 0,0521.
    /// </remarks>
    public double LoudestOpening { get; init; } = 0.030;

    /// <summary>The loud end of the window - its ninety-fifth percentile.</summary>
    /// <remarks>
    /// A PERCENTILE RATHER THAN THE MAXIMUM, because one door slam should not
    /// set the bar for the next half minute. <see cref="Loudest"/> is the true
    /// maximum and is for the log, where a single transient is worth seeing.
    /// </remarks>
    public double Busiest => _busiest;

    /// <summary>How much audio has been passed since the last close.</summary>
    public TimeSpan Passed { get; private set; }

    /// <summary>Blocks passed to the model, for reporting what the gate saved.</summary>
    public long Decoded { get; private set; }

    /// <summary>Blocks the gate kept out.</summary>
    public long Skipped { get; private set; }

    /// <summary>Open stretches that hit <see cref="MostOpen"/> and were cut.</summary>
    /// <remarks>
    /// COUNTED RATHER THAN LOGGED, because this is the number that says the
    /// room is beating the gate. One cut is a long sentence; a hundred is a
    /// threshold that is not working, and the difference has to be visible
    /// without reading a transcript to infer it.
    /// </remarks>
    public long Cut { get; private set; }

    /// <summary>Asks whether this block should be decoded.</summary>
    /// <param name="pcm">Float samples in [-1, 1].</param>
    /// <param name="rate">Sample rate, for the hangover clock.</param>
    /// <returns>True if the caller should feed this block to the model.</returns>
    /// <remarks>
    /// THE BLOCK THAT OPENS THE GATE IS ITSELF PASSED. Returning false on the
    /// first loud block and true thereafter would drop the attack of the first
    /// word, which is the part a recogniser most needs.
    /// </remarks>
    public bool Wants(ReadOnlySpan<float> pcm, int rate = 16_000)
    {
        Opened = false;
        Closed = false;
        if (pcm.Length == 0) return Open;

        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        var rms = Math.Sqrt(sum / pcm.Length);

        var length = TimeSpan.FromSeconds(pcm.Length / (double)rate);

        // THE BAR COMES FROM THE WINDOW BEFORE THIS BLOCK, NOT INCLUDING IT.
        //
        // The order of these two lines is the whole of a bug. The bar is drawn
        // between the window's median and its ninety-fifth percentile, so a
        // block recorded BEFORE it is judged contributes to both ends and lifts
        // the bar it then has to clear. With a short history the first loud
        // block of all is its own median and its own loud end, so the bar lands
        // half again above it and the gate can never open at all - seven of
        // this class's own tests went red on exactly that.
        //
        // Reading the bar first means a voice is judged against the room as the
        // room was a moment ago, which is the question actually being asked.
        var bar = Opening;

        // THE ROOM IS A PERCENTILE OF ACTUAL BLOCKS, not a walk towards one.
        //
        // Three versions of this were wrong and each was wrong differently,
        // which is worth keeping because each looked right.
        //
        // A HARD-CODED CONSTANT from one room: it opened at 0,003, "three times
        // the quiet room measured on the P30", and on the P30 at 04:37 that
        // same night it passed 77 to 87 per cent of blocks.
        //
        // THE MINIMUM instead of the typical level: learning only from blocks
        // the gate already called quiet converges on the quietest moment, so at
        // 04:52 it learnt "room 0,0008" - correctly - and still passed 62 to 68
        // per cent, because a room whose quietest moment is 0,0008 spends most
        // of its time well above that. The number was right and the statistic
        // was wrong.
        //
        // A WALK towards the median: stepping the estimate up on a louder block
        // and down on a quieter one settles on the median only if the steps are
        // equal, and a real room is BIMODAL - quiet dips, a busy baseline,
        // almost nothing between - so a biased walk stops wherever it entered
        // that gap. Equal steps fixed the bias and left the speed: ninety
        // seconds of that room moved the estimate from 0,0009 to 0,0019 when it
        // needed 0,0050, so the gate would have run the model on everything for
        // the first four minutes after the phone was picked up.
        //
        // A walk was the wrong instrument for a quantile. Keeping the last few
        // seconds of levels and reading the percentiles off them is exact,
        // converges in one window rather than asymptotically, has no rate to
        // tune, and costs a few hundred doubles and one sort a second.
        // WHETHER THIS BLOCK MAY TEACH THE ROOM. Always, by default - see
        // LearnsWhileOpen for the one caller that must not let it.
        if (LearnsWhileOpen || (!Open && rms < bar)) Remember(rms, length);

        if (rms >= bar)
        {
            if (!Open)
            {
                Open = true;
                Opened = true;
                Passed = TimeSpan.Zero;
            }
            _since = TimeSpan.Zero;
        }
        else if (Open)
        {
            _since += length;
            if (_since >= Hangover) { Close(); Closed = true; }
        }

        if (!Open)
        {
            Skipped++;
            return false;
        }

        Passed += length;
        Decoded++;

        // CUT AFTER IT IS COUNTED, so the block that reaches the limit is still
        // delivered and the stretch ends on a whole block rather than mid-word.
        //
        // The cut is about the RECOGNISER, not the room: whatever is making the
        // noise, the thing behind this gate must not be handed one utterance
        // that never ends. The floor needs no help from here, because it is
        // learning from these blocks as they pass.
        if (Passed >= MostOpen)
        {
            Cut++;
            Close();
            Closed = true;
        }

        return true;
    }

    /// <summary>How long the stretch that just ended ran for.</summary>
    /// <remarks>
    /// KEPT BECAUSE <see cref="Passed"/> IS ZEROED BY THE CLOSE, and a caller
    /// reporting an utterance at the moment it ends would otherwise print "0,0
    /// s" for every one of them - a number that looks like data and is an
    /// artefact of the order two lines run in.
    /// </remarks>
    public TimeSpan LastStretch { get; private set; }

    private void Close()
    {
        if (Open) LastStretch = Passed;
        Open = false;
        _since = TimeSpan.MaxValue;
        Passed = TimeSpan.Zero;
    }

    /// <summary>Files this block's level and keeps the room estimate current.</summary>
    /// <remarks>
    /// THE SORT IS ONCE A SECOND, NOT ONCE A BLOCK. A room does not change
    /// between two consecutive tenths of a second, and this runs on a Kirin 710
    /// behind a 20M recogniser - the arithmetic that decides whether to spend
    /// CPU should not be the thing spending it. Ten blocks of staleness is a
    /// tenth of a second of staleness.
    /// </remarks>
    private void Remember(double rms, TimeSpan length)
    {
        if (_levels.Length == 0)
        {
            var want = length > TimeSpan.Zero
                ? (int)Math.Round(Remembers / length)
                : 300;
            _levels = new double[Math.Clamp(want, 16, 4096)];
            _scratch = new double[_levels.Length];
        }

        _levels[_at] = rms;
        _at = (_at + 1) % _levels.Length;
        if (_held < _levels.Length) _held++;

        if (--_untilSort > 0 && _floor > 0) return;
        _untilSort = 10;

        Array.Copy(_levels, _scratch, _held);
        Array.Sort(_scratch, 0, _held);

        // BOTH NUMBERS OFF ONE SORT. Opening is read on every block and asks
        // for the loud end as well as the room; sorting the window twice a
        // block on a Kirin 710 to answer a question about a room that changes
        // over seconds would be the gate spending the CPU it exists to save.
        var i = (int)(RoomQuantile * (_held - 1));
        _floor = _scratch[Math.Clamp(i, 0, _held - 1)];
        _busiest = _scratch[Math.Clamp((int)(0.95 * (_held - 1)), 0, _held - 1)];
    }

    /// <summary>What share of blocks reached the model.</summary>
    /// <remarks>
    /// THE NUMBER THAT SAYS WHETHER THIS WAS WORTH DOING. A gate letting
    /// everything through has bought nothing and must be reported, not assumed
    /// to be working. On the P30 at 04:37 on 2026-10-12 this read 77 to 87 per
    /// cent, which is how the hard-coded threshold was caught.
    /// </remarks>
    public double DutyCycle =>
        Decoded + Skipped == 0 ? 0 : Decoded / (double)(Decoded + Skipped);

    /// <summary>Closes the gate and forgets the counts and the room.</summary>
    public void Reset()
    {
        Close();
        Opened = false;
        Closed = false;
        LastStretch = TimeSpan.Zero;
        _floor = -1;
        _at = 0;
        _held = 0;
        _untilSort = 0;
        _busiest = 0;
        Decoded = 0;
        Skipped = 0;
        Cut = 0;
    }
}
