// BrainHold.cs
//
// The brain stands aside for a moment, across a process boundary, for the first word out loud.
//
// THE FIRST SOUND WAITED FOR TWO ENGINES TO SHARE FOUR CORES. The voice renders the opening words
// while the brain writes the next ones, each with four threads on the same four big cores, and each
// went at half speed. Measured on the P30 on 2026-11-06 the opening took 0,7 to 1 s to render beside
// the brain and a third of a second alone. The brain writes twice as fast as anybody speaks, so it
// has the time to give: what it loses is made up while the opening is being heard.
//
// A SHARED WORD IN A FILE, BECAUSE THE VOICE AND THE BRAIN ARE IN DIFFERENT PROCESSES and a call
// between them would have to cross six layers to say one thing. The word is the moment the hold runs
// out, so a voice that dies holding it costs the brain a second and no more.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace CircleAI.Inference.Managed.Runtime;

/// <summary>Asks the brain, wherever it runs, to stop writing for a moment.</summary>
public static class BrainHold
{
    private static readonly object Gate = new();
    private static MemoryMappedViewAccessor? _word;

    /// <summary>Joins the hold kept in <paramref name="file"/>. Every process that takes part calls this once.</summary>
    public static void Open(string file)
    {
        lock (Gate)
        {
            if (_word is not null) return;
            try
            {
                var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < sizeof(long)) stream.SetLength(sizeof(long));
                var map = MemoryMappedFile.CreateFromFile(stream, null, sizeof(long), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                _word = map.CreateViewAccessor(0, sizeof(long));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Without it the two engines share the cores, as they did. Nothing depends on it.
            }
        }
    }

    /// <summary>Holds the brain for at most <paramref name="most"/>.</summary>
    public static void For(TimeSpan most) => _word?.Write(0, (DateTime.UtcNow + most).Ticks);

    /// <summary>Lets the brain go on.</summary>
    public static void Release() => _word?.Write(0, 0L);

    /// <summary>True while somebody is holding the brain.</summary>
    public static bool Now => _word is { } word && word.ReadInt64(0) > DateTime.UtcNow.Ticks;
}
