// MnnTokenRouter.cs
//
// Shared token-callback + <think>...</think> reasoning router used by every
// MNN-backed generator (QwenTextGenerator, KimiVlGenerator). Token IDs come
// off the wire from the native bridge, get decoded via mnn_llm_token_to_text,
// drained into complete UTF-8 codepoints, and routed into either the
// Content or Reasoning channel based on a small state machine over the
// emitted text.
//
// The callback must be a static [UnmanagedCallersOnly] function because the
// assembly sets [DisableRuntimeMarshalling] — only function pointers may
// cross the managed/native boundary, no managed delegates.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace CircleAI.Inference;

/// <summary>
/// Per-call state handed to <see cref="MnnTokenRouter.OnTokenNative"/> via a
/// <see cref="GCHandle"/> (the native <c>user_data</c> pointer).
/// </summary>
internal sealed class MnnTokenSink
{
    public required MnnModelHandle Model;
    public required List<byte> Pending;                   // UTF-8 bytes awaiting full codepoint
    public required StringBuilder Emitted;                // all decoded text so far
    public required string[] StopSequences;
    public required ChannelWriter<ChatFragment> Writer;   // unified content+reasoning sink
    public required CancellationToken Ct;
    public bool IncludeReasoning;
    public bool InThink;
    public int FlushedChars;
    public bool Stopped;
}

/// <summary>
/// Static token-callback machinery for the MNN bridge. Centralised so every
/// generator gets identical UTF-8 draining, &lt;think&gt; routing, and
/// stop-sequence handling.
/// </summary>
internal static class MnnTokenRouter
{
    // Reasoning-trace tags emitted by Qwen3 / DeepSeek / o1-style models.
    internal const string ThinkOpen  = "<think>";
    internal const string ThinkClose = "</think>";

    /// <summary>
    /// Holdback size — we never flush the last
    /// <c>ThinkClose.Length - 1</c> characters of decoded text until a new
    /// fragment arrives, so a tag that straddles a token boundary is never
    /// mis-classified as content or reasoning.
    /// </summary>
    internal const int ThinkHoldback = 7;  // = ThinkClose.Length - 1

    /// <summary>
    /// Native per-token callback. Signature must match
    /// <c>int (*)(int token_id, void* user_data)</c> from mnnbridge.h.
    /// Returns 0 to keep generating, non-zero to stop. Must never let a
    /// managed exception unwind into native code.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int OnTokenNative(int tokenId, IntPtr userData)
    {
        try
        {
            if (userData == IntPtr.Zero) return 1;
            if (GCHandle.FromIntPtr(userData).Target is not MnnTokenSink sink) return 1;
            if (sink.Stopped) return 1;
            if (sink.Ct.IsCancellationRequested) return 1;

            const int BufLen = 512;
            byte* buf = stackalloc byte[BufLen];
            int n = MnnInterop.mnn_llm_token_to_text(sink.Model, tokenId, buf, BufLen);
            for (int i = 0; i < n && i < BufLen; i++)
                sink.Pending.Add(buf[i]);

            if (!TryDrainUtf8(sink.Pending, out var fragment) || fragment.Length == 0)
                return 0; // need more bytes before a full codepoint

            sink.Emitted.Append(fragment);

            // Stop-sequence safety check. MNN stops on <|im_end|> natively but
            // we guard here so caller-supplied stops also fire.
            if (TryFindStopSequence(sink.Emitted, sink.StopSequences, out int stopAt))
            {
                RouteUntil(sink, stopAt);
                sink.Stopped = true;
                return 1;
            }

            var safeUpTo = Math.Max(sink.FlushedChars, sink.Emitted.Length - ThinkHoldback);
            RouteUntil(sink, safeUpTo);
            return 0;
        }
        catch
        {
            return 1; // never unwind into native
        }
    }

    /// <summary>
    /// Native text callback — the streaming one. Signature must match
    /// <c>int (*)(const char* text, int len, void* user_data)</c> from
    /// mnnbridge.h. Returns 0 to keep generating, non-zero to stop.
    /// </summary>
    /// <remarks>
    /// SAME MACHINERY, DIFFERENT DOOR. This is <see cref="OnTokenNative"/> with
    /// the one step that needed a token id removed: instead of asking MNN to
    /// turn an id back into bytes, the bytes arrive already decoded. Everything
    /// after that — the UTF-8 reassembly, the stop-sequence check, the
    /// &lt;think&gt; routing and the holdback — is shared, so the two paths
    /// cannot drift in how they treat an answer.
    ///
    /// The bytes still go through <c>Pending</c> rather than being decoded
    /// directly: MNN writes whenever it has something, and there is no promise
    /// that a write ends on a codepoint boundary. A split multi-byte character
    /// would otherwise surface as a replacement glyph mid-word — which in these
    /// languages means mid-diacritic.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int OnTextNative(byte* text, int len, IntPtr userData)
    {
        try
        {
            if (userData == IntPtr.Zero || text is null || len <= 0) return 0;
            if (GCHandle.FromIntPtr(userData).Target is not MnnTokenSink sink) return 1;
            if (sink.Stopped) return 1;
            if (sink.Ct.IsCancellationRequested) return 1;

            for (int i = 0; i < len; i++) sink.Pending.Add(text[i]);

            if (!TryDrainUtf8(sink.Pending, out var fragment) || fragment.Length == 0)
                return 0;

            sink.Emitted.Append(fragment);

            if (TryFindStopSequence(sink.Emitted, sink.StopSequences, out int stopAt))
            {
                RouteUntil(sink, stopAt);
                sink.Stopped = true;
                return 1;
            }

            var safeUpTo = Math.Max(sink.FlushedChars, sink.Emitted.Length - ThinkHoldback);
            RouteUntil(sink, safeUpTo);
            return 0;
        }
        catch
        {
            return 1; // never unwind into native
        }
    }

    /// <summary>
    /// After the native loop returns, flush any holdback remainder. Idempotent
    /// when called twice.
    /// <para>
    /// When <see cref="MnnTokenSink.Stopped"/> is set the stop-sequence flush
    /// already wrote everything up to (but not including) the stop marker — any
    /// remaining text in <see cref="MnnTokenSink.Emitted"/> IS the stop marker
    /// (plus any trailing junk the bridge produced after the callback returned
    /// non-zero), so we must NOT route it. Otherwise the trailing
    /// <c>&lt;|im_end|&gt;</c> would leak into the content channel.
    /// </para>
    /// </summary>
    public static void DrainRemainder(MnnTokenSink sink)
    {
        if (sink.Stopped) return;
        RouteUntil(sink, sink.Emitted.Length);
    }

    /// <summary>
    /// Push <c>Emitted[FlushedChars..upTo]</c> through the <c>&lt;think&gt;</c>
    /// state machine and write tagged fragments to the channel. Always
    /// advances <see cref="MnnTokenSink.FlushedChars"/>.
    /// </summary>
    private static void RouteUntil(MnnTokenSink sink, int upTo)
    {
        if (upTo <= sink.FlushedChars) return;

        var src = sink.Emitted;
        while (sink.FlushedChars < upTo)
        {
            if (sink.InThink)
            {
                // Hunt for </think> in [FlushedChars, upTo + ThinkClose.Length].
                // The extra ThinkClose.Length characters past `upTo` may still be
                // checked for tag completion, but we never WRITE past upTo here.
                var searchEnd = Math.Min(src.Length, upTo + ThinkClose.Length);
                var closeIdx = IndexOfIn(src, ThinkClose, sink.FlushedChars, searchEnd);
                if (closeIdx >= 0 && closeIdx + ThinkClose.Length <= src.Length)
                {
                    if (closeIdx > sink.FlushedChars && sink.IncludeReasoning)
                    {
                        var text = src.ToString(sink.FlushedChars, closeIdx - sink.FlushedChars);
                        sink.Writer.TryWrite(new ChatFragment(ChatFragmentKind.Reasoning, text));
                    }
                    sink.FlushedChars = closeIdx + ThinkClose.Length;
                    sink.InThink = false;
                    continue;
                }
                if (upTo > sink.FlushedChars && sink.IncludeReasoning)
                {
                    var text = src.ToString(sink.FlushedChars, upTo - sink.FlushedChars);
                    sink.Writer.TryWrite(new ChatFragment(ChatFragmentKind.Reasoning, text));
                }
                sink.FlushedChars = upTo;
                return;
            }
            else
            {
                var searchEnd = Math.Min(src.Length, upTo + ThinkOpen.Length);
                var openIdx = IndexOfIn(src, ThinkOpen, sink.FlushedChars, searchEnd);
                if (openIdx >= 0 && openIdx + ThinkOpen.Length <= src.Length)
                {
                    if (openIdx > sink.FlushedChars)
                    {
                        var text = src.ToString(sink.FlushedChars, openIdx - sink.FlushedChars);
                        sink.Writer.TryWrite(new ChatFragment(ChatFragmentKind.Content, text));
                    }
                    sink.FlushedChars = openIdx + ThinkOpen.Length;
                    sink.InThink = true;
                    continue;
                }
                if (upTo > sink.FlushedChars)
                {
                    var text = src.ToString(sink.FlushedChars, upTo - sink.FlushedChars);
                    sink.Writer.TryWrite(new ChatFragment(ChatFragmentKind.Content, text));
                }
                sink.FlushedChars = upTo;
                return;
            }
        }
    }

    private static int IndexOfIn(StringBuilder src, string needle, int start, int end)
    {
        if (end <= start) return -1;
        var span = src.ToString(start, end - start).AsSpan();
        var found = span.IndexOf(needle.AsSpan(), StringComparison.Ordinal);
        return found < 0 ? -1 : start + found;
    }

    /// <summary>
    /// Drains as many complete UTF-8 codepoints as possible from
    /// <paramref name="pending"/> and returns them. Trailing partial-codepoint
    /// bytes stay in the buffer for the next call.
    /// </summary>
    internal static bool TryDrainUtf8(List<byte> pending, out string decoded)
    {
        if (pending.Count == 0)
        {
            decoded = string.Empty;
            return false;
        }

        int safeLen = pending.Count;
        for (int i = pending.Count - 1; i >= 0 && i >= pending.Count - 4; i--)
        {
            byte b = pending[i];
            if ((b & 0x80) == 0) break;
            if ((b & 0xC0) == 0xC0)
            {
                int needed = (b & 0xE0) == 0xC0 ? 2
                           : (b & 0xF0) == 0xE0 ? 3
                           : (b & 0xF8) == 0xF0 ? 4
                           : 1;
                int have = pending.Count - i;
                if (have < needed) safeLen = i;
                break;
            }
        }

        if (safeLen == 0) { decoded = string.Empty; return false; }

        var arr = new byte[safeLen];
        pending.CopyTo(0, arr, 0, safeLen);
        pending.RemoveRange(0, safeLen);
        decoded = Encoding.UTF8.GetString(arr);
        return decoded.Length > 0;
    }

    internal static bool TryFindStopSequence(StringBuilder sb, string[] stops, out int index)
    {
        var s = sb.ToString();
        foreach (var stop in stops)
        {
            if (string.IsNullOrEmpty(stop)) continue;
            int idx = s.IndexOf(stop, StringComparison.Ordinal);
            if (idx >= 0) { index = idx; return true; }
        }
        index = -1;
        return false;
    }
}
