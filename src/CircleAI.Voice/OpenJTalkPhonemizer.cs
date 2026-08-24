using System.Runtime.InteropServices;

namespace CircleAI.Voice;

/// <summary>
/// Japanese readings from Open JTalk, in this process.
/// </summary>
/// <remarks>
/// <para>
/// Japanese cannot be phonemised from a table: 聞き取れて is read by segmenting
/// the sentence, identifying 聞く as a verb and applying its conjugation. The
/// previous Japanese voice used a 51-token table from a Chinese-dominant model
/// and silently dropped whatever it could not map — 50/86 hiragana and 25/90
/// katakana covered as standalone characters, measured at CER 0.42 against
/// human speech. This replaces that with the analyser the Japanese TTS
/// ecosystem is actually built on.
/// </para>
/// <para>
/// IN-PROCESS, UNLIKE ESPEAK. Open JTalk is modified BSD, so it links straight
/// into the app. espeak-ng is GPL-3.0 and has to live in a second package to
/// keep the licence clean — which is a second thing to install, an IPC hop per
/// utterance, and a process the OEM low-memory killer can take. None of that
/// applies here.
/// </para>
/// <para>
/// The native side holds Mecab parse state, so a handle is not safe to share
/// across concurrent calls. Callers already serialise synthesis, so this type
/// stays a thin binding and does not add a second lock.
/// </para>
/// </remarks>
public sealed class OpenJTalkPhonemizer : IDisposable
{
    private const string Lib = "openjtalk_g2p";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr openjtalk_g2p_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dicDir);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void openjtalk_g2p_close(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int openjtalk_labels(
        IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, byte[] outBuf, int outLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int openjtalk_g2p(
        IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, byte[] outBuf, int outLen);

    private IntPtr _handle;
    private readonly byte[] _buffer = new byte[1 << 18];

    /// <summary>
    /// Where the compiled dictionary was unpacked, if the host knows. Set by the
    /// Android head; null elsewhere. The dictionary is ~104 MB (sys.dic alone is
    /// 100 MB), so it ships as a downloadable bundle rather than inside the APK.
    /// </summary>
    public static string? DictionaryFolder { get; set; }

    private OpenJTalkPhonemizer(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Open the phonemiser, or return null when the native library or the
    /// dictionary is absent. Null is a normal answer — it means this build or
    /// this device has no Japanese support yet — so callers fall back rather
    /// than fail.
    /// </summary>
    public static OpenJTalkPhonemizer? TryCreate(string? dictionaryFolder = null)
    {
        foreach (var candidate in Candidates(dictionaryFolder))
        {
            if (!Directory.Exists(candidate)) continue;

            // sys.dic is the one that must be there; the others are small and
            // arrive with it. Checking it avoids handing the native side a
            // directory that merely exists.
            if (!File.Exists(Path.Combine(candidate, "sys.dic"))) continue;

            try
            {
                var h = openjtalk_g2p_open(candidate);
                if (h != IntPtr.Zero)
                {
                    VoiceTrace.Write($"g2p: Open JTalk ready — {candidate}");
                    return new OpenJTalkPhonemizer(h);
                }
                VoiceTrace.Write($"g2p: Open JTalk rejected {candidate}");
            }
            catch (DllNotFoundException)
            {
                VoiceTrace.Write("g2p: libopenjtalk_g2p not in this build");
                return null;
            }
            catch (Exception ex)
            {
                VoiceTrace.Write($"g2p: Open JTalk failed — {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        VoiceTrace.Write("g2p: no Open JTalk dictionary found");
        return null;
    }

    /// <summary>
    /// Where downloaded model bundles are unpacked, if the host knows. The
    /// catalogued dictionary (<c>OpenJTalk-Dic-ja</c>) lands here rather than in
    /// the sideload folder.
    /// </summary>
    public static string? ModelStoreFolder { get; set; }

    /// <summary>
    /// Every place a dictionary might be, cheapest first. THE ENTRY EXISTS IN
    /// THE CATALOGUE, SO IT CAN ARRIVE TWO WAYS — pushed over a cable into the
    /// sideload folder, or downloaded into the model store under
    /// <c>OpenJTalk-Dic-ja/</c>. Looking in only one of those is how a registry
    /// entry becomes decorative: it downloads, and nothing finds it.
    ///
    /// THE SUBFOLDER IS NOT FIXED, AND MUST NOT BE HARDCODED. A downloaded
    /// bundle unpacks into whatever directory its BundleFiles names carry, and
    /// that prefix changes with the store: it was <c>open-jtalk-dic/</c> on the
    /// bucket and became <c>voices-v1/</c> the day the files moved to a GitHub
    /// release, because release assets are flat and the tag is the only
    /// directory there is. Nothing failed loudly when that happened — 103 MB
    /// downloaded correctly and Japanese silently had no phonemiser. So the
    /// last resort searches one level down for the file that actually matters
    /// rather than for a folder name someone has to remember to update.
    /// </summary>
    private static IEnumerable<string> Candidates(string? explicitFolder)
    {
        if (!string.IsNullOrWhiteSpace(explicitFolder)) yield return explicitFolder!;

        foreach (var root in new[] { DictionaryFolder, ModelStoreFolder })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            yield return root!;
            // The layouts we know by name, tried before touching the disk.
            yield return Path.Combine(root!, "OpenJTalk-Dic-ja", "open-jtalk-dic");
            yield return Path.Combine(root!, "open-jtalk-dic");
            // The upstream tarball unpacks into a version-named folder; accept
            // that too, so a hand-extracted copy works without renaming.
            yield return Path.Combine(root!, "open_jtalk_dic_utf_8-1.11");

            // Whatever the bundle actually called itself.
            foreach (var found in ContainingSysDic(Path.Combine(root!, "OpenJTalk-Dic-ja")))
                yield return found;
        }
    }

    /// <summary>
    /// Directories directly under <paramref name="parent"/> that hold a
    /// <c>sys.dic</c>. Enumerated lazily and defensively: this runs on a phone
    /// where the model store may not exist yet, and a missing folder is the
    /// normal case before the first download, not an error.
    /// </summary>
    private static IEnumerable<string> ContainingSysDic(string parent)
    {
        string[] subdirs;
        try
        {
            if (!Directory.Exists(parent)) yield break;
            subdirs = Directory.GetDirectories(parent);
        }
        catch (Exception ex)
        {
            VoiceTrace.Write($"g2p: cannot list {parent} — {ex.GetType().Name}");
            yield break;
        }

        foreach (var dir in subdirs)
            if (File.Exists(Path.Combine(dir, "sys.dic")))
                yield return dir;
    }

    /// <summary>Full-context labels, one per line — what the prosody tokeniser needs.</summary>
    public string Labels(string text)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(OpenJTalkPhonemizer));
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var n = openjtalk_labels(_handle, text, _buffer, _buffer.Length);
        if (n <= 0) return string.Empty;

        var len = Array.IndexOf<byte>(_buffer, 0);
        if (len < 0) len = _buffer.Length;
        return System.Text.Encoding.UTF8.GetString(_buffer, 0, len);
    }

    /// <summary>Space-separated phonemes, without prosody — for diagnostics.</summary>
    public string Phonemes(string text)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(OpenJTalkPhonemizer));
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var n = openjtalk_g2p(_handle, text, _buffer, _buffer.Length);
        if (n <= 0) return string.Empty;

        var len = Array.IndexOf<byte>(_buffer, 0);
        if (len < 0) len = _buffer.Length;
        return System.Text.Encoding.UTF8.GetString(_buffer, 0, len);
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) openjtalk_g2p_close(h);
    }
}
