// TtsEngines.cs
//
// HOLDING THE VOICE OPEN BETWEEN SENTENCES.
//
// THE MEASUREMENT THAT PUT THIS HERE, from a P30's own log on 2026-10-11/12.
// Nine of twelve syntheses in one session reported "(INCLUDING model open)":
//
//     21 chars -> 1393 ms audio in  2911 ms                 <- session already open
//      3 chars ->  487 ms audio in  6672 ms (INCLUDING model open)
//     29 chars -> 1695 ms audio in 10706 ms (INCLUDING model open)
//     39 chars -> 1950 ms audio in 12182 ms
//
// Three characters cost as much as thirty-nine. That is the signature of a
// fixed cost, not of synthesis: the load dominates and the words barely
// register. The speaking path built a fresh OnnxTtsEngine for every sentence
// and dropped it, so each reply paid three to nine seconds to open a model it
// had opened a moment earlier.
//
// The owner's words were "translate and transcribe are real-time but voice
// lags so bad", and that is the whole difference: those paths keep a model
// open, and this one did not. His next guess was "something is not cached" -
// which was exactly right.
//
// WHY A CACHE AND NOT A FIELD. The engine is created deep inside a static
// resolution path that runs per utterance, with no object living long enough
// to hold one. Threading an instance out to every caller is a larger change
// than this deserves and a riskier one at the point it is needed.
//
// WHAT THIS DOES NOT FIX, AND MUST NOT CLAIM TO. The P30 runs at 95-100% swap.
// The operating system can evict a model whatever this dictionary believes,
// and the next sentence will pay the load again and say so in the log. This
// removes the cost we were CHOOSING to pay; it cannot remove the cost the
// phone imposes.

using System;
using System.Collections.Generic;

namespace CircleAI.Voice;

/// <summary>Keeps a loaded voice between sentences instead of reloading it.</summary>
public static class TtsEngines
{
    /// <summary>What makes two requests the same voice.</summary>
    /// <remarks>
    /// EVERYTHING THAT CHANGES HOW IT SOUNDS. Caching on the model path alone
    /// would hand back an engine set to the wrong speaker or language the
    /// moment somebody switches, and a wrong-language voice is worse than a
    /// slow one - it is the "English words spoken with Zulu phonetics" failure
    /// this codebase already records.
    /// </remarks>
    private readonly record struct Key(string Model, string Phonemizer, long Speaker, long Language);

    private static readonly Dictionary<Key, OnnxTtsEngine> Open = new();
    private static readonly object Gate = new();

    /// <summary>How many voices may stay open at once.</summary>
    /// <remarks>
    /// Four. A device ships a handful of voices and a person uses one or two in
    /// a sitting; holding more on a 3,87 GB phone trades the problem this
    /// solves for a worse one. When the limit is reached the cache is cleared
    /// rather than evicting cleverly - a wrong guess about which to keep costs
    /// one reload, and the arithmetic to avoid it costs more than it saves.
    /// </remarks>
    public const int MostOpen = 4;

    /// <summary>How many voices are currently held open.</summary>
    public static int Count { get { lock (Gate) return Open.Count; } }

    /// <summary>An engine for this voice, loaded once and kept.</summary>
    /// <param name="modelPath">The .onnx voice.</param>
    /// <param name="phonemizer">Its phonemizer, or null for a grapheme voice.</param>
    /// <param name="speakerId">Speaker for a multi-speaker voice.</param>
    /// <param name="languageId">Language for a multi-language voice.</param>
    public static OnnxTtsEngine For(
        string modelPath, IPhonemizer? phonemizer, long speakerId = 0, long languageId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        // The phonemizer's TYPE, not the instance: a fresh espeak wrapper for
        // the same language is the same voice, and keying on the object would
        // defeat the cache completely while looking like it worked.
        var key = new Key(modelPath, phonemizer?.GetType().FullName ?? "none", speakerId, languageId);

        lock (Gate)
        {
            if (Open.TryGetValue(key, out var held)) return held;

            if (Open.Count >= MostOpen)
            {
                foreach (var e in Open.Values)
                {
                    try { e.Dispose(); } catch { /* a voice that will not close must not stop the next one */ }
                }
                Open.Clear();
                VoiceTrace.Write($"tts: cleared the voice cache at {MostOpen} open");
            }

            var engine = new OnnxTtsEngine(modelPath, phonemizer)
            {
                SpeakerId = speakerId,
                LanguageId = languageId,
            };

            Open[key] = engine;
            return engine;
        }
    }

    /// <summary>Closes every held voice.</summary>
    /// <remarks>
    /// FOR MEMORY PRESSURE, which this phone has constantly. A caller told by
    /// the system to give memory back can call this; the next sentence pays one
    /// load, which is the right trade against being killed.
    /// </remarks>
    public static void CloseAll()
    {
        lock (Gate)
        {
            foreach (var e in Open.Values)
            {
                try { e.Dispose(); } catch { }
            }
            var n = Open.Count;
            Open.Clear();
            if (n > 0) VoiceTrace.Write($"tts: closed {n} held voice(s)");
        }
    }
}
