// LinkAudioCodec.cs
//
// The audio transaction's wire format, in one file so both sides cannot drift.
//
// FIELD ORDER IS THE CONTRACT. There is no self-describing envelope here — a Parcel
// is a byte stream and both ends agree on what comes next. That is cheap and it is
// brittle in exactly one way: change the order on one side only and the reader gets
// garbage rather than an error, because every field is structurally valid nonsense.
// So the order lives here, written once, and both the client and the service call
// these methods rather than writing fields themselves.
//
// THE SIZE CHECK HAPPENS IN THE WRITER, not at the call site. A caller that forgets
// it gets TransactionTooLargeException — which names neither the size nor the
// culprit, and can land on an unrelated transaction that happened to be in flight.
// Refusing here turns that into a sentence a person can read.

using System;

namespace CircleAI.Linking;

/// <summary>Reads and writes the audio transaction's payload.</summary>
/// <remarks>
/// Deliberately free of Android types so it compiles and is testable off-device; the
/// caller supplies the primitive write/read operations the Parcel provides.
/// </remarks>
public static class LinkAudioCodec
{
    /// <summary>Wire version, so a newer client meeting an older service can say so.</summary>
    /// <remarks>
    /// A VERSION RATHER THAN HOPE. Two apps on one phone update independently: a
    /// client can be newer than the service it binds, and the reverse. Without a
    /// leading version the first field of a changed layout is read as the first field
    /// of the old one, which is the failure mode this file exists to avoid.
    /// </remarks>
    public const int WireVersion = 1;

    /// <summary>What a writer must be able to do — the Parcel operations, abstracted.</summary>
    public interface IWriter
    {
        /// <summary>Write a 32-bit integer.</summary>
        void WriteInt(int value);
        /// <summary>Write a string, which may be null.</summary>
        void WriteString(string? value);
        /// <summary>Write a byte array, which may be empty.</summary>
        void WriteBytes(byte[] value);
    }

    /// <summary>What a reader must be able to do.</summary>
    public interface IReader
    {
        /// <summary>Read a 32-bit integer.</summary>
        int ReadInt();
        /// <summary>Read a string, which may be null.</summary>
        string? ReadString();
        /// <summary>Read a byte array; empty when none was written.</summary>
        byte[] ReadBytes();
    }

    /// <summary>Write a request. Returns false and says why when the audio is too big.</summary>
    public static bool TryWriteRequest(IWriter w, LinkAudioRequest request, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(request);

        var audio = request.Audio ?? Array.Empty<byte>();
        if (!LinkAudio.Fits(audio.Length, out refusal)) return false;

        w.WriteInt(WireVersion);
        w.WriteInt((int)request.Verb);
        w.WriteString(request.Text ?? string.Empty);
        w.WriteString(request.Language);
        w.WriteBytes(audio);
        return true;
    }

    /// <summary>Read a request. Returns null when the wire version is not understood.</summary>
    /// <remarks>
    /// A VERSION MISMATCH IS A REFUSAL, NOT AN ATTEMPT. Reading a newer layout with
    /// older field expectations produces plausible rubbish — a verb that happens to be
    /// in range, a length that happens to parse — and then the service acts on it.
    /// Better to decline and let the caller say which app needs updating.
    /// </remarks>
    public static LinkAudioRequest? TryReadRequest(IReader r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var version = r.ReadInt();
        if (version != WireVersion) return null;

        var verb = (LinkAudioVerb)r.ReadInt();
        if (!Enum.IsDefined(typeof(LinkAudioVerb), verb)) return null;

        var text = r.ReadString() ?? string.Empty;
        var language = r.ReadString();
        var audio = r.ReadBytes();

        // The service checks the size TOO, rather than trusting the client to have.
        // A service that trusts its callers on sizes is a service any app can crash.
        if (!LinkAudio.Fits(audio.Length, out _)) return null;

        return new LinkAudioRequest(verb, audio, text, language);
    }

    /// <summary>Write a reply. Oversized audio becomes a failure the caller can read.</summary>
    public static void WriteReply(IWriter w, LinkAudioReply reply)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(reply);

        var audio = reply.Audio ?? Array.Empty<byte>();

        // SYNTHESIS CAN OVERRUN THE BUDGET TOO, and in the direction nobody checks: a
        // short sentence makes several seconds of speech, and a paragraph makes more
        // than the link can carry. Refusing here beats throwing inside the service's
        // own reply, where the client sees a dead binder and no reason.
        if (!LinkAudio.Fits(audio.Length, out var refusal))
        {
            w.WriteInt(WireVersion);
            w.WriteInt(0);
            w.WriteString(string.Empty);
            w.WriteString(refusal);
            w.WriteBytes(Array.Empty<byte>());
            return;
        }

        w.WriteInt(WireVersion);
        w.WriteInt(reply.Ok ? 1 : 0);
        w.WriteString(reply.Text ?? string.Empty);
        w.WriteString(reply.Error);
        w.WriteBytes(audio);
    }

    /// <summary>Read a reply.</summary>
    public static LinkAudioReply ReadReply(IReader r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var version = r.ReadInt();
        if (version != WireVersion)
            return LinkAudioReply.Failure(
                $"CircleAI speaks audio-link v{version}; this app speaks v{WireVersion}. "
                + "One of the two needs updating.");

        var ok = r.ReadInt() == 1;
        var text = r.ReadString() ?? string.Empty;
        var error = r.ReadString();
        var audio = r.ReadBytes();

        return ok ? new LinkAudioReply(true, text, audio, null)
                  : LinkAudioReply.Failure(error ?? "CircleAI could not handle the audio.");
    }
}
