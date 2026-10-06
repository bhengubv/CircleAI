#nullable enable

// NativeEspeakPhonemizer.cs
//
// Text -> IPA via libespeak-ng IN-PROCESS (P/Invoke), so mobile TTS works.
//
// EspeakPhonemizer shells out to the espeak-ng *executable*. That is fine on a
// desktop and impossible on Android/iOS, where there is no binary to launch —
// which is why on-device TTS was blocked. This binds the library directly:
//
//   Android : libespeak-ng.so bundled in the APK (lib/<abi>/), data unpacked to
//             the app's files dir and pointed at via espeak_Initialize.
//   Desktop : libespeak-ng.dll / .so / .dylib on the loader path.
//
// espeak-ng needs its DATA directory (phoneme tables, dictionaries). On Android
// that ships as an asset and must be copied somewhere readable before init —
// see SetDataPath.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleAI.Voice;

/// <summary>
/// <see cref="IPhonemizer"/> that calls libespeak-ng in-process. Works wherever
/// the native library + data can be loaded, including Android and iOS.
/// </summary>
public sealed class NativeEspeakPhonemizer : IPhonemizer, IDisposable
{
    private const string Lib = "espeak-ng";

    // espeak_Initialize output modes
    private const int AUDIO_OUTPUT_SYNCHRONOUS = 0x02;
    // espeak_TextToPhonemes phonememode: bit1 = IPA
    private const int PHONEME_MODE_IPA = 0x02;
    private const int ESPEAKNG_OK = 0;

    private readonly object _gate = new();
    private bool _initialised;
    private bool _disposed;

    [DllImport(Lib, EntryPoint = "espeak_Initialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int espeak_Initialize(int output, int bufLength, [MarshalAs(UnmanagedType.LPStr)] string? path, int options);

    [DllImport(Lib, EntryPoint = "espeak_SetVoiceByName", CallingConvention = CallingConvention.Cdecl)]
    private static extern int espeak_SetVoiceByName([MarshalAs(UnmanagedType.LPStr)] string name);

    // const void **textptr — espeak advances the pointer as it consumes text.
    [DllImport(Lib, EntryPoint = "espeak_TextToPhonemes", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr espeak_TextToPhonemes(ref IntPtr textptr, int textmode, int phonememode);

    [DllImport(Lib, EntryPoint = "espeak_Terminate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int espeak_Terminate();

    /// <summary>
    /// Absolute path to the directory CONTAINING <c>espeak-ng-data</c>. On
    /// Android set this to where the data asset was unpacked (e.g. the app's
    /// files dir) BEFORE the first Phonemize call. <c>null</c> lets espeak use
    /// its compiled-in default, which is what desktop installs want.
    /// </summary>
    public static string? DataPath { get; set; }

    private readonly string _voice;

    public NativeEspeakPhonemizer(string voice = "en-us") => _voice = voice;

    /// <inheritdoc />
    public IReadOnlyList<string> Phonemize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            EnsureInitialised();

            // Marshal to UTF-8; espeak advances the pointer, so keep the
            // original to free it.
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var cursor = buffer;

                var sb = new StringBuilder();
                // espeak returns one clause per call; loop until it consumes all.
                while (cursor != IntPtr.Zero)
                {
                    var res = espeak_TextToPhonemes(ref cursor, /* textmode: UTF-8 */ 1, PHONEME_MODE_IPA);
                    if (res == IntPtr.Zero) break;
                    var clause = Marshal.PtrToStringUTF8(res);
                    if (!string.IsNullOrEmpty(clause)) sb.Append(clause).Append(' ');
                    if (cursor == IntPtr.Zero) break;
                }

                // "(en)hello(af)" - espeak annotates a language switch whenever the
                // text is not in the voice's own language, which real user text does
                // constantly (an Afrikaans sentence containing "WhatsApp"). They are
                // annotations, not phonemes; left in, the letters inside the brackets
                // get mapped and spoken aloud.
                var text2 = Regex.Replace(sb.ToString().Trim(), @"\([^)]*\)", "").Trim();
                return PiperVoiceConfig.SplitPhonemeString(text2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private void EnsureInitialised()
    {
        if (_initialised) return;

        int rate;
        try
        {
            rate = espeak_Initialize(AUDIO_OUTPUT_SYNCHRONOUS, 0, DataPath, 0);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "libespeak-ng not found. Desktop: install espeak-ng or use EspeakPhonemizer. " +
                "Android: bundle libespeak-ng.so for the target ABI and unpack espeak-ng-data, " +
                "then set NativeEspeakPhonemizer.DataPath to its parent directory.", ex);
        }

        if (rate < 0)
            throw new InvalidOperationException(
                $"espeak_Initialize failed ({rate}). Usually a missing/incorrect data path — " +
                $"DataPath='{DataPath ?? "(default)"}' must CONTAIN an 'espeak-ng-data' folder.");

        if (espeak_SetVoiceByName(_voice) != ESPEAKNG_OK)
            throw new InvalidOperationException($"espeak could not select voice '{_voice}'.");

        _initialised = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            if (_initialised)
            {
                try { espeak_Terminate(); } catch { /* shutting down */ }
                _initialised = false;
            }
        }
    }
}
