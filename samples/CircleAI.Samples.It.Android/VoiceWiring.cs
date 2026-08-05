#if IT_VOICE_ANDROID
#nullable enable

// VoiceWiring.cs
//
// The process-wide voice setup, in one place, so it cannot be half-done.
//
// IT WAS WIRED IN ONE ACTIVITY AND NEEDED IN TWO. ItSpeaker.MobilePhonemizerFactory
// is a STATIC — set it once and the whole process has a voice; never set it and
// English synthesis fails with "on device phonemizer not wired". It was being
// assigned in MainActivity.OnCreate, and MainActivity is not the launcher.
//
// So a normal run went: HomeActivity starts -> "Hey B" -> answer generated ->
// nothing spoken, because the factory was still null. The chat screen worked
// perfectly, which made it look like a voice problem rather than a startup-order
// problem: the one path that set the static was the one path anybody tested.
//
// The greeting on the home screen hid it further. Those are MMS voices, which are
// character-driven and never ask for phonemes, so the phone demonstrably spoke
// eleven languages on a screen where the English speaker could not say a word.
//
// A static that must be set before use, from whichever entry point happens to run
// first, is a rule no one can keep by remembering. Both activities now call this,
// it is idempotent, and it is the only place the assignment lives.

using Android.Content;
using Android.Util;

namespace CircleAI.Samples.It.Mobile;

/// <summary>One-time, order-independent wiring for on-device speech.</summary>
public static class VoiceWiring
{
    const string Tag = "CircleAI.VoiceWiring";

    static readonly object Gate = new();
    static bool _installed;

    /// <summary>
    /// Makes sure the process can turn text into phonemes. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// Phonemes come from the SEPARATE espeak G2P app (com.bhengubv.espeakng) across
    /// a process boundary — espeak-ng is GPL and is never linked into CircleAI. If
    /// that app is absent the phonemizer throws a clear reason when it is used,
    /// which SpokenReply now surfaces on screen rather than swallowing.
    /// <para>
    /// Called from every activity that can reach the speaker, because which one
    /// runs first depends on how the app was opened: the launcher, a notification,
    /// or the wake word.
    /// </para>
    /// </remarks>
    public static void Install(Context context)
    {
        lock (Gate)
        {
            if (_installed) return;

            // Application context, not the activity: this outlives whichever screen
            // happened to install it, and holding an activity in a static is how a
            // process-wide hook leaks a window.
            var app = context.ApplicationContext ?? context;

            CircleAI.Samples.It.Voice.ItSpeaker.MobilePhonemizerFactory =
                voice => new OutOfProcessEspeakPhonemizer(app, voice);

            _installed = true;
            Log.Info(Tag, "phonemizer factory installed");
        }
    }
}
#endif
