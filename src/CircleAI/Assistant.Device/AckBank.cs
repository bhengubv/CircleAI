// AckBank.cs
//
// The small spoken things a listener says while the other person waits.
//
// THIS IS WHAT MAKES THE WAIT FEEL LIKE A MIND AT WORK. Jarvis says "Yes?" the
// instant he hears his name and "One moment" before he goes to look. Her
// breathes. Ours played a two-note tone, which tells you a machine registered a
// sound, and then - measured on 2026-09-09 - left four to twenty-two seconds of
// nothing while the model thought. A tone says "beep"; a voice says "I'm here".
//
// RENDERED ONCE, PLAYED FROM DISK. Synthesising "Yes?" at wake time would cost
// the same seconds it exists to cover. Each line is rendered to a cached wav at
// warm-up, in the language the phone speaks, and played from the file - which
// costs nothing at the moment it matters. If the language has no line here, or
// the voice could not render it, the tone is still there.
//
// SHORT ON PURPOSE. The wake acknowledgement plays inside the settle before the
// turn's microphone opens, so it must be over well within that window or it is
// recorded as the question. "Yes?" is about four hundred milliseconds.

using CircleAI.Assistant.Voice;

namespace CircleAI.Assistant.Device;

/// <remarks>
/// PUBLIC BECAUSE THE RESIDENT ASSISTANT SPEAKS THROUGH IT. A head running the
/// wake word off-screen needs the same acknowledgements the in-app turn uses,
/// or the two answer in different voices.
/// </remarks>
public static class AckBank
{
    /// <summary>What it says when it hears its name.</summary>
    public const string Woke = "woke";

    /// <summary>What it says once it has your question and is going to think.</summary>
    public const string Working = "working";

    // ENGLISH ONLY UNTIL SOMEBODY WHO SPEAKS THE OTHER LANGUAGES WRITES THEIRS.
    // An invented phrase mispronounced at a native speaker is worse than a
    // tone; the greeting table in SampleLanguages was built on exactly that
    // rule and this follows it.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Lines =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = new Dictionary<string, string>
            {
                [Woke]    = "Yes?",
                [Working] = "One moment.",
            },
        };

    private static string Root(string? tag)
        => (tag ?? "").Split('-', '_')[0].ToLowerInvariant();

    private static string PathFor(string tag, string key)
        => System.IO.Path.Combine(AppPaths.Cache, $"ack-{Root(tag)}-{key}.wav");

    /// <summary>
    /// Renders every line for the language once, at warm-up. Returns how many
    /// are ready to play.
    /// </summary>
    public static async Task<int> PrepareAsync(string storageDir, string tag, CancellationToken ct)
    {
        if (!Lines.TryGetValue(Root(tag), out var lines)) return 0;

        var ready = 0;
        foreach (var (key, text) in lines)
        {
            var path = PathFor(tag, key);
            if (System.IO.File.Exists(path)) { ready++; continue; }

            try
            {
                await Task.Run(() => CircleAITtsProbe.RunCataloguedAsync(
                    storageDir, tag, text, path, log: null, ct: ct), ct).ConfigureAwait(false);
                if (System.IO.File.Exists(path)) ready++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("CircleAI.Warm", $"ack '{key}' could not be rendered: {ex.Message}");
            }
        }
        return ready;
    }

    /// <summary>
    /// Plays one line if it is ready. Returns false when there is nothing to play,
    /// so the caller can fall back to the tone.
    /// </summary>
    public static async Task<bool> PlayAsync(string tag, string key, CancellationToken ct = default)
    {
        var path = PathFor(tag, key);
        if (!System.IO.File.Exists(path)) return false;

        try
        {
            await PlatformAudio.PlayAsync(path, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Turn", $"ack '{key}' failed to play: {ex.Message}");
            return false;
        }
    }
}
