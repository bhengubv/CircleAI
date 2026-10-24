// TranscribingWake.cs
//
// WAKING ON A PHRASE FOUND IN A TRANSCRIPT, INSTEAD OF A KEYWORD SPOTTER.
//
// WHY THE PRODUCT NEEDS THIS AND NOT A BETTER WAKE-WORD MODEL. The phrase must
// be settable to ANY text, in ANY language, at runtime and with no training.
// Africa has roughly two thousand languages and South Africa twelve official
// ones, so one trained model per phrase does not scale and never will - which
// rules out openWakeWord, microWakeWord and the whole fixed-phrase family
// however good they are at the four phrases they ship.
//
// sherpa-onnx's keyword spotter does take arbitrary text, and it is what the
// product has used. But its entire published shelf is 3 to 3.3 MILLION
// parameters. Measured on the owner's own voice on 2026-10-12, the English one
// found his phrase FOUR times out of roughly twenty spoken close to the
// microphone, and NEVER at normal holding distance. There is no bigger keyword
// model to upgrade to; that is the shelf.
//
// The same project publishes a 20M streaming recogniser under Apache-2.0 (the
// model card says so; the KWS weights we ship have no stated licence at all).
// On that same recording it woke on "hey circle" three times, "hey alexa" once
// and "hey b" once - three different phrases, one model, nothing trained - and
// refused "open the garage door", which was never said.
//
// WHAT THIS FILE IS, AND IS NOT. Everything that can be decided without a
// microphone already lives somewhere both test legs can reach it:
// StreamingTranscriber does the decoding, SpeechGate decides what is worth
// decoding, PhraseInSpeech decides what counts as the phrase. All three are
// tested. What is left here is the Android: where the model lives on disk, and
// the loop that joins the three together. That division is deliberate - every
// rule in this feature that turned out to be wrong was one that lived in a
// place no test could see.

using System;
using System.IO;
using System.Linq;
using Android.Util;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Where the streaming recogniser lives, if this phone has one.</summary>
public static class TranscribingWake
{
    /// <summary>Catalogue id of the bundle this path needs.</summary>
    public const string ModelId = "ASR-Streaming-Zipformer-20M";

    /// <summary>
    /// The directory holding the recogniser's graphs, or null if it is absent.
    /// </summary>
    /// <remarks>
    /// THE ENCODER IS SEARCHED FOR RATHER THAN A PATH GUESSED, for the reason
    /// ResidentListeningControl records about the keyword bundle: these
    /// archives unpack a level down as often as not, and assuming otherwise
    /// fails late and obscurely - the recogniser throws "no *encoder*.onnx in
    /// ..." from inside its constructor, which surfaces as the wake loop dying
    /// rather than as a model that is not where it was expected.
    /// <para>
    /// ABSENCE IS THE NORMAL ANSWER, NOT A FAULT. A phone that has not
    /// downloaded this keeps the keyword spotter and behaves exactly as before.
    /// </para>
    /// </remarks>
    public static string? BundleDir()
    {
        try
        {
            // THE MODEL STORE FIRST, where a downloaded bundle lands.
            //
            // AND THEN THE EXTERNAL DIRECTORY, which is not a shortcut but the
            // only way to get a model onto a RELEASE build for a measurement:
            // the store is internal storage and a non-debuggable package
            // refuses run-as, so there is no adb path to it. The owner can also
            // copy a bundle there from a file manager, see it, and delete it -
            // none of which is true of internal storage.
            foreach (var root in new[]
                     {
                         Path.Combine(ModelStore.Path, ModelId),
                         Path.Combine(
                             global::Android.App.Application.Context
                                 .GetExternalFilesDir(null)?.AbsolutePath ?? string.Empty,
                             ModelId),
                     })
            {
                if (root.Length == 0 || !Directory.Exists(root)) continue;
                if (HasEncoder(root)) return IsUsable(root) ? root : null;

                foreach (var child in Directory.EnumerateDirectories(root))
                    if (HasEncoder(child)) return IsUsable(child) ? child : null;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("CircleAI.ScreenWake", "could not look for the recogniser: " + ex.Message);
            return null;
        }
    }

    private static bool HasEncoder(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*encoder*.onnx").Any(); }
        catch { return false; }
    }

    /// <summary>Whether everything StreamingTranscriber opens is there.</summary>
    /// <remarks>
    /// AN ENCODER IS NOT A RECOGNISER, and the way this bundle arrives makes that the
    /// usual case rather than a corner. There is no catalogue entry for it - it is
    /// copied in with a file manager or pushed to the external files directory for a
    /// measurement - so it appears FILE BY FILE, and a listener started while the copy
    /// was halfway took the transcribing path, threw inside the transducer's
    /// constructor, logged "wake loop failed", and reported Listening.
    /// <para>
    /// The keyword bundle's answer to the same question comes from the catalogue
    /// (BundleModelLoader.ModelPresent, every file by size). This one has no catalogue
    /// to ask, so it asks the only other authority there is: the files the recogniser
    /// opens.
    /// </para>
    /// </remarks>
    private static bool IsUsable(string dir)
    {
        try
        {
            bool Has(string pattern) => Directory.EnumerateFiles(dir, pattern).Any();

            var usable = Has("*encoder*.onnx") && Has("*decoder*.onnx") && Has("*joiner*.onnx")
                      && File.Exists(Path.Combine(dir, "tokens.txt"));

            if (!usable)
                Log.Warn("CircleAI.ScreenWake",
                    "the streaming recogniser at " + dir + " is incomplete - keeping the keyword spotter");

            return usable;
        }
        catch { return false; }
    }
}
