#if IT_VOICE_ANDROID
#nullable enable

// Earcon.cs
//
// The two sounds that let someone across the room use this thing.
//
// WITHOUT THESE, HANDS-FREE IS UNUSABLE AT ANY DISTANCE. You say "Hey B", and
// then — measured on the P30 — nothing happens for between thirty and ninety
// seconds while the brain works. Every piece of feedback in that window is on
// the screen: the circle changes state, the caption changes. All of it is
// invisible to the person who just spoke from the kitchen doorway, which is
// exactly the person the wake word exists for.
//
// From there the failure is silent too. If the voice cannot be created the turn
// finishes by putting text on a screen nobody is looking at, so a broken
// assistant and a thinking one sound identical: like nothing.
//
// So: one sound the instant the phrase lands, one sound if it cannot answer
// aloud. Not decoration — they are the only two things a distant listener gets.
//
// DELIBERATELY TONES, NOT SPEECH. A spoken "one moment" would have to be
// synthesised, and synthesis needs the very model whose absence we might be
// reporting; on a cold engine it costs seconds, which defeats a sound whose
// entire job is to be immediate. ToneGenerator is in the platform, needs no
// model, and starts in milliseconds.

using System;
using Android.Media;
using Android.Util;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Short non-speech sounds for the moments words cannot cover.</summary>
public static class Earcon
{
    const string Tag = "CircleAI.Earcon";

    /// <summary>
    /// "I heard you." Played the moment the wake phrase lands.
    /// </summary>
    /// <remarks>
    /// Rising pair, quiet and quick. Rising because it is an opening — the
    /// question is still to come — and short enough that it is finished well
    /// before the person stops speaking, so it never competes with the microphone
    /// it is about to hand over to.
    /// </remarks>
    public static void Woke() => Play(
        (ToneAudio.Ack1, 90),
        (ToneAudio.Ack2, 110));

    /// <summary>
    /// "I cannot say this out loud." Played when the answer exists but the voice does not.
    /// </summary>
    /// <remarks>
    /// Falling pair, the mirror of <see cref="Woke"/>, so the two are told apart
    /// without being learned. This is the sound that stops a silent failure from
    /// being indistinguishable from a long think: it says stop waiting and come
    /// and look, which is a thing a person can act on.
    /// </remarks>
    public static void CannotSpeak() => Play(
        (ToneAudio.Fail1, 140),
        (ToneAudio.Fail2, 200));

    static void Play(params (Tone Tone, int Ms)[] notes)
    {
        // Fire and forget on a pool thread: this is called from the wake handler
        // and from the tail of a turn, and neither should wait on audio.
        System.Threading.Tasks.Task.Run(() =>
        {
            ToneGenerator? gen = null;
            try
            {
                // Notification stream, not media: it is a status sound, and it
                // should still be heard by someone who turned the media volume
                // down after playing something else.
                gen = new ToneGenerator(Android.Media.Stream.Notification, 70);
                foreach (var (tone, ms) in notes)
                {
                    gen.StartTone(tone, ms);
                    System.Threading.Thread.Sleep(ms + 25);   // let it ring out
                }
            }
            catch (Exception ex)
            {
                // A phone that will not make a beep must not take the turn down
                // with it — the answer still matters more than the announcement.
                Log.Warn(Tag, "earcon failed: " + ex.Message);
            }
            finally
            {
                try { gen?.Release(); } catch { }
            }
        });
    }

    /// <summary>The specific DTMF tones used, named for what they mean.</summary>
    static class ToneAudio
    {
        public const Tone Ack1  = Tone.PropBeep;
        public const Tone Ack2  = Tone.PropAck;
        public const Tone Fail1 = Tone.PropNack;
        public const Tone Fail2 = Tone.SupError;
    }
}
#endif
