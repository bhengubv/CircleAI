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

    /// <summary>
    /// The version a translation travels as: version 1's fields, then the target language (a
    /// request) or what was heard (a reply).
    /// </summary>
    /// <remarks>
    /// ONLY A TRANSLATION IS WRITTEN THIS WAY. Transcribe and Speak stay version 1, byte for
    /// byte, so an app and a service that never translate are unaffected; an older service
    /// meeting a translation refuses it at the version, with the sentence that says which app
    /// needs updating, rather than reading the target as the start of the next request.
    /// </remarks>
    public const int TranslateWireVersion = 2;

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

        var translating = request.Verb == LinkAudioVerb.Translate;
        w.WriteInt(translating ? TranslateWireVersion : WireVersion);
        w.WriteInt((int)request.Verb);
        w.WriteString(request.Text ?? string.Empty);
        w.WriteString(request.Language);
        w.WriteBytes(audio);
        if (translating) w.WriteString(request.Target);
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
        if (version != WireVersion && version != TranslateWireVersion) return null;

        var verb = (LinkAudioVerb)r.ReadInt();
        if (!Enum.IsDefined(typeof(LinkAudioVerb), verb)) return null;

        // A TRANSLATION IS VERSION 2 AND NOTHING ELSE IS: either mismatch is a layout this
        // reader would misread, so it is refused.
        if ((verb == LinkAudioVerb.Translate) != (version == TranslateWireVersion)) return null;

        var text = r.ReadString() ?? string.Empty;
        var language = r.ReadString();
        var audio = r.ReadBytes();
        var target = version == TranslateWireVersion ? r.ReadString() : null;

        // The service checks the size TOO, rather than trusting the client to have.
        // A service that trusts its callers on sizes is a service any app can crash.
        if (!LinkAudio.Fits(audio.Length, out _)) return null;

        // A translation into nowhere is not a request.
        if (verb == LinkAudioVerb.Translate && string.IsNullOrWhiteSpace(target)) return null;

        return new LinkAudioRequest(verb, audio, text, language, target);
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

        // WHAT WAS HEARD RIDES ONLY ON A TRANSLATION'S REPLY, as version 2 - a reply to
        // Transcribe or Speak is version 1 exactly as before.
        var translated = reply.Heard is not null;
        w.WriteInt(translated ? TranslateWireVersion : WireVersion);
        w.WriteInt(reply.Ok ? 1 : 0);
        w.WriteString(reply.Text ?? string.Empty);
        w.WriteString(reply.Error);
        w.WriteBytes(audio);
        if (translated) w.WriteString(reply.Heard);
    }

    /// <summary>Read a reply.</summary>
    public static LinkAudioReply ReadReply(IReader r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var version = r.ReadInt();
        if (version != WireVersion && version != TranslateWireVersion)
            return LinkAudioReply.Failure(
                $"CircleAI speaks audio-link v{version}; this app speaks v{TranslateWireVersion}. "
                + "One of the two needs updating.");

        var ok = r.ReadInt() == 1;
        var text = r.ReadString() ?? string.Empty;
        var error = r.ReadString();
        var audio = r.ReadBytes();
        var heard = version == TranslateWireVersion ? r.ReadString() ?? string.Empty : null;

        return ok ? new LinkAudioReply(true, text, audio, null, heard)
                  : LinkAudioReply.Failure(error ?? "CircleAI could not handle the audio.");
    }
}
