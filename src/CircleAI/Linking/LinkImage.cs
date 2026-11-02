// LinkImage.cs
//
// A PICTURE ACROSS THE LINK, AND THE MEGABYTE IT CANNOT BE (Step 4).
//
// A Binder transaction carries about one megabyte, shared by everything in flight in the process;
// exceed it and the kernel throws TransactionTooLargeException on whichever call is unlucky. The
// audio transaction keeps under it by refusing anything bigger (LinkAudio). A picture cannot be
// refused that way - a phone photo is several megabytes before it is shrunk - so it travels two ways:
//
//   inline       up to 800 kB (the audio's limit, for the same reason), in the parcel itself;
//   a handle     anything bigger, up to 16 MB: the bytes go in a file and the transaction carries
//                only its descriptor, which is Android's own answer to "too large for a transaction".
//
// SHRUNK FIRST. ImageBudget decides, from the header alone, whether a picture is bigger than a vision
// model can use; the client shrinks it before sending and the service shrinks it again if the client
// did not. Most pictures then go inline; the handle is for what still does not fit.
//
// THE PORTABLE HALF IS HERE, tested off a phone: which way a picture goes, the layout, the limits,
// and where a received picture waits for its ask. The Parcel and the descriptor are the Android head's.

using System;
using System.IO;
using System.Linq;

namespace CircleAI.Linking;

/// <summary>A question about a picture.</summary>
/// <param name="Question">What the person asks about it.</param>
/// <param name="SessionId">The app's conversation id.</param>
/// <param name="ClientRequestId">The app's idempotency key: the same id is the same ask.</param>
/// <param name="Image">The encoded picture (JPEG, PNG, WebP...).</param>
public sealed record LinkImageRequest(string Question, string SessionId, string ClientRequestId, byte[] Image);

/// <summary>Limits for pictures on the link.</summary>
public static class LinkImage
{
    /// <summary>The most picture one transaction carries in the parcel itself: 800 kB.</summary>
    public const int MaxInlineBytes = LinkAudio.MaxAudioBytes;

    /// <summary>The most picture the link takes at all, by handle: 16 MB.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>Whether a picture of this size goes by handle rather than in the parcel.</summary>
    public static bool ByHandle(int byteCount) => byteCount > MaxInlineBytes;

    /// <summary>Whether this much picture can cross at all, and why not when it cannot.</summary>
    public static bool Fits(long byteCount, out string? refusal)
    {
        if (byteCount <= 0)
        {
            refusal = "There was no picture to send.";
            return false;
        }
        if (byteCount > MaxBytes)
        {
            refusal = $"That picture is {byteCount / 1_000_000d:0.#} MB; the link takes at most {MaxBytes / 1_000_000d:0} MB. Send a smaller one.";
            return false;
        }
        refusal = null;
        return true;
    }
}

/// <summary>The layout of a picture request, both directions.</summary>
public static class LinkImageCodec
{
    /// <summary>Wire version, so a newer app meeting an older service can say so.</summary>
    public const int WireVersion = 1;

    private const int Inline = 0;
    private const int Handle = 1;

    /// <summary>Puts a picture's bytes behind a descriptor in the parcel (the Android head's half).</summary>
    public interface IHandleWriter
    {
        /// <summary>Write <paramref name="bytes"/> where a descriptor can reach them, and the descriptor.</summary>
        void WriteHandle(byte[] bytes);
    }

    /// <summary>Reads a picture's bytes back from the descriptor in the parcel.</summary>
    public interface IHandleReader
    {
        /// <summary>Read exactly <paramref name="length"/> bytes from the descriptor; null when it cannot.</summary>
        byte[]? ReadHandle(int length);
    }

    /// <summary>Write a request: inline when it fits the parcel, by handle when not. False and why when it cannot go.</summary>
    public static bool TryWriteRequest(LinkAudioCodec.IWriter w, IHandleWriter handles, LinkImageRequest request, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(request);

        var image = request.Image ?? Array.Empty<byte>();
        if (!LinkImage.Fits(image.Length, out refusal)) return false;

        w.WriteInt(WireVersion);
        w.WriteString(request.Question ?? string.Empty);
        w.WriteString(request.SessionId ?? string.Empty);
        w.WriteString(request.ClientRequestId ?? string.Empty);
        w.WriteInt(image.Length);
        if (LinkImage.ByHandle(image.Length))
        {
            w.WriteInt(Handle);
            handles.WriteHandle(image);
        }
        else
        {
            w.WriteInt(Inline);
            w.WriteBytes(image);
        }
        return true;
    }

    /// <summary>
    /// Read a request; null when the version is not understood, the sizes disagree, or the picture is
    /// past the limit - the service checks what the client claims rather than trusting it.
    /// </summary>
    public static LinkImageRequest? TryReadRequest(LinkAudioCodec.IReader r, IHandleReader handles)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(handles);

        if (r.ReadInt() != WireVersion) return null;
        var question = r.ReadString() ?? string.Empty;
        var session = r.ReadString() ?? string.Empty;
        var rid = r.ReadString() ?? string.Empty;
        var length = r.ReadInt();
        if (!LinkImage.Fits(length, out _)) return null;

        byte[]? image;
        switch (r.ReadInt())
        {
            case Inline:
                if (length > LinkImage.MaxInlineBytes) return null;
                image = r.ReadBytes();
                break;
            case Handle:
                image = handles.ReadHandle(length);
                break;
            default:
                return null;
        }

        return image is { Length: > 0 } && image.Length == length
            ? new LinkImageRequest(question, session, rid, image)
            : null;
    }
}

/// <summary>Where a received picture waits for the ask about it, by the request that asked it.</summary>
/// <remarks>
/// ON DISK, BESIDE THE ASKS, because the ask is: a question re-queued after a crash must still find
/// its picture. Swept by age, so a picture outlives its answer by a day at most.
///
/// KEYED BY THE REQUEST, NOT BY THE ASK'S ID. The id exists only once the queue has accepted the ask,
/// and the queue may start answering in that same instant - a picture filed under the id afterwards
/// could land after the brain had been handed the question without it. The caller and its request id
/// exist before the ask does, and the ask carries both, so the picture is written first and the ask
/// finds it itself.
/// </remarks>
public sealed class LinkImageStore
{
    private readonly string _directory;

    /// <summary>A store in <paramref name="directory"/>.</summary>
    public LinkImageStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>
    /// Keep a picture for the ask <paramref name="callerKey"/> is about to submit under
    /// <paramref name="requestId"/>; returns where it went, to drop it if the ask is refused. A
    /// resend of the same request keeps the picture already there.
    /// </summary>
    public string Hold(byte[] image, string callerKey, string requestId)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        Directory.CreateDirectory(_directory);
        var path = PathFor(callerKey, requestId);
        if (File.Exists(path)) return path;

        // WRITTEN WHOLE OR NOT AT ALL: a reader never sees half a picture.
        var writing = path + ".writing";
        File.WriteAllBytes(writing, image);
        try { File.Move(writing, path, overwrite: false); }
        catch (IOException) when (File.Exists(path)) { TryDelete(writing); }
        return path;
    }

    /// <summary>The ask was refused: the held picture goes.</summary>
    public void Drop(string held) => TryDelete(held);

    /// <summary>The picture the ask <paramref name="callerKey"/> made under <paramref name="requestId"/> is about, or null.</summary>
    public byte[]? For(string callerKey, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(callerKey) || string.IsNullOrWhiteSpace(requestId)) return null;
        try
        {
            var path = PathFor(callerKey, requestId);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes pictures older than <paramref name="age"/>; returns how many went.</summary>
    public int Sweep(TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(_directory)) return 0;
            var cut = DateTime.UtcNow - age;
            var gone = 0;
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.img*").Where(f => f.LastWriteTimeUtc < cut))
            {
                try { file.Delete(); gone++; }
                catch (IOException) { }
            }
            return gone;
        }
        catch
        {
            return 0;
        }
    }

    // ONE FILE PER CALLER AND REQUEST, under a hash: neither a caller key nor an app's request id -
    // text the app chose - ever becomes part of a path.
    private string PathFor(string callerKey, string requestId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(callerKey + "\n" + requestId));
        return Path.Combine(_directory, "request-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".img");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* swept later */ }
        catch (UnauthorizedAccessException) { /* swept later */ }
    }
}
