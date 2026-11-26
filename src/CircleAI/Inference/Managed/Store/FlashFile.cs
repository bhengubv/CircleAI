// FlashFile.cs
//
// THE FEW LINUX CALLS A STREAMING ENGINE NEEDS, so the flash does the engine's job and RAM only the
// turbo's. By default the OS keeps a copy in RAM of every page a process reads, so a model streamed
// through it would end up held in RAM anyway. Linux has two ways out, and both are plain calls:
//
//   O_DIRECT              the read goes from flash straight into the engine's own buffer and never
//                         enters the page cache. Buffer, offset and length are whole pages.
//   POSIX_FADV_DONTNEED   after a range is used the OS drops its pages at once. A range this process
//                         maps is let go first (MADV_DONTNEED): the OS keeps a page while a mapping
//                         holds it.
//
// WHICH WORKS IS THE DEVICE'S. On an older kernel with file-based encryption an O_DIRECT open can
// succeed and quietly read through the cache, so a direct file drops what it read as well. Off
// Linux - the desktop test legs - the same calls read through RandomAccess and drop nothing.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CircleAI.Inference.Managed.Store;

/// <summary>A file read in whole pages, past the OS's cache where the device allows it.</summary>
public sealed unsafe class FlashFile : IDisposable
{
    /// <summary>What direct reads, their offsets and their buffers are aligned to.</summary>
    public const int Page = 4096;

    private readonly int _fd = -1;
    private readonly SafeFileHandle? _handle;
    private int _disposed;

    private FlashFile(string path, long length, int fd, bool direct, SafeFileHandle? handle)
    {
        FilePath = path;
        Length = length;
        _fd = fd;
        Direct = direct;
        _handle = handle;
    }

    /// <summary>Whether this platform has the Linux calls (Linux and Android).</summary>
    public static bool Linux => OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Its length in bytes when it was opened.</summary>
    public long Length { get; }

    /// <summary>Whether the open took O_DIRECT. A device can still read through its cache; reads drop what they read either way.</summary>
    public bool Direct { get; }

    /// <summary>
    /// Opens <paramref name="path"/> to read: with O_DIRECT when <paramref name="direct"/> and the
    /// device takes it, else through the cache.
    /// </summary>
    public static FlashFile Open(string path, bool direct = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var length = new FileInfo(path).Length;
        if (Linux)
        {
            try
            {
                var name = Encoding.UTF8.GetBytes(path + "\0");
                fixed (byte* p = name)
                {
                    if (direct)
                    {
                        var fd = LinuxCalls.Open(p, LinuxCalls.ReadOnly | LinuxCalls.CloseOnExec | LinuxCalls.DirectFlag, 0);
                        if (fd >= 0 && ReadsDirect(fd, length)) return new FlashFile(path, length, fd, direct: true, null);
                        if (fd >= 0) LinuxCalls.Close(fd);
                    }
                    var buffered = LinuxCalls.Open(p, LinuxCalls.ReadOnly | LinuxCalls.CloseOnExec, 0);
                    if (buffered >= 0) return new FlashFile(path, length, buffered, direct: false, null);
                    throw new IOException($"{path} could not be opened (errno {Marshal.GetLastSystemError()}).");
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No libc to call: read through the runtime instead.
            }
        }
        return new FlashFile(path, length, -1, direct: false,
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess));
    }

    // A DIRECT OPEN CAN SUCCEED AND ITS READS STILL BE REFUSED (an encrypted file on an older kernel answers
    // EINVAL): one page is read to find out, and a refusal means the file is read through the cache instead.
    private static bool ReadsDirect(int fd, long length)
    {
        if (length < Page) return true;
        var page = Allocate(Page);
        try
        {
            return LinuxCalls.ReadAt(fd, page, Page, 0) == Page;
        }
        finally
        {
            Free(page);
        }
    }

    /// <summary>
    /// Reads <paramref name="count"/> bytes at <paramref name="offset"/> into <paramref name="buffer"/>
    /// - all of them unless the file ends first - and returns how many. A direct read wants all three
    /// aligned to <see cref="Page"/>; the file's last page may be short.
    /// </summary>
    public int Read(byte* buffer, int count, long offset)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var done = 0;
        while (done < count)
        {
            int n;
            if (_handle is not null)
            {
                n = RandomAccess.Read(_handle, new Span<byte>(buffer + done, count - done), offset + done);
            }
            else
            {
                var got = LinuxCalls.ReadAt(_fd, buffer + done, (nuint)(count - done), offset + done);
                if (got < 0)
                {
                    var errno = Marshal.GetLastSystemError();
                    if (errno == LinuxCalls.Interrupted) continue;
                    throw new IOException($"Reading {count} bytes of {FilePath} at {offset} failed (errno {errno}).");
                }
                n = (int)got;
            }
            if (n == 0) break; // the end of the file
            done += n;
        }
        return done;
    }

    /// <summary>
    /// Reads [<paramref name="offset"/>, +<paramref name="count"/>) into <paramref name="buffer"/> as the device's recipe
    /// says: reads of <paramref name="readBytes"/> on <paramref name="channels"/> readers at once, each dropping from the
    /// cache what it brought in. Returns the bytes read - fewer only where the file ends. For a direct read the offset,
    /// the count and the buffer are whole pages.
    /// </summary>
    /// <remarks>
    /// THE FLASH ON THE ENGINE'S TERMS. A tensor read through the model's mapping comes in a page fault at a time, and
    /// on a phone whose RAM is full the pages leave as fast as they arrive: the P30 split the 27B's feed-forward at
    /// 8-15 MB/s that way, where its flash gives 245 MB/s to 1 MB reads on four readers.
    /// </remarks>
    public long ReadChannels(byte* buffer, long count, long offset, int channels, int readBytes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (count <= 0) return 0;
        var size = (int)Math.Max(Page, Pages(readBytes));
        var reads = (count + size - 1) / size;
        long got = 0;
        var into = (nint)buffer;
        Parallel.For(0L, reads, new ParallelOptions { MaxDegreeOfParallelism = (int)Math.Clamp(channels, 1, reads) }, i =>
        {
            var from = i * size;
            var length = (int)Math.Min(size, count - from);
            var n = Read((byte*)into + from, length, offset + from);
            Drop(offset + from, length);
            Interlocked.Add(ref got, n);
        });
        return got;
    }

    /// <summary>
    /// Writes what <paramref name="stream"/> holds of [<paramref name="offset"/>, +<paramref name="length"/>) to flash and
    /// tells the OS to drop those pages, so a file bigger than the phone's RAM can be written without filling it with
    /// pages waiting to be written back. Only flushes off Linux.
    /// </summary>
    public static void Settle(FileStream stream, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Flush(flushToDisk: true);
        if (!Linux || length <= 0) return;
        try
        {
            LinuxCalls.Advise((int)stream.SafeFileHandle.DangerousGetHandle(), offset, length, LinuxCalls.DontNeed);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc to call: the cache lets go in its own time.
        }
    }

    /// <summary>Tells the OS to drop its cached pages of [<paramref name="offset"/>, +<paramref name="length"/>); nothing off Linux.</summary>
    public void Drop(long offset, long length)
    {
        if (_fd < 0 || length <= 0) return;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        LinuxCalls.Advise(_fd, offset, length, LinuxCalls.DontNeed);
    }

    /// <summary>
    /// Lets the OS take back the pages of a range this process maps: <paramref name="mapped"/> points at
    /// byte <paramref name="fileOffset"/> of <paramref name="path"/>, <paramref name="length"/> long.
    /// The mapping lets go of them (MADV_DONTNEED), then the file's cache drops them. A later read of the
    /// range faults them back in from flash. False where there are no Linux calls.
    /// </summary>
    public static bool Release(byte* mapped, long length, string path, long fileOffset)
    {
        if (!Linux || mapped == null || length <= 0) return false;
        try
        {
            var start = (nint)mapped & ~(nint)(Page - 1);
            var end = (nint)mapped + (nint)length;
            LinuxCalls.AdviseMemory((void*)start, (nuint)(end - start), LinuxCalls.MemoryDontNeed);
            using var file = Open(path, direct: false);
            file.Drop(fileOffset, length);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            return false;
        }
    }

    /// <summary>A huge page, and what a huge-page-backed buffer is aligned to.</summary>
    public const int HugePage = 2 << 20; // 2 MB

    /// <summary>
    /// Whether the weight buffers are backed by huge pages (transparent, MADV_HUGEPAGE): 512x fewer page-table entries
    /// and TLB misses than 4 KB pages for the same memory, so the compute walks 1.3 GB of weights a token without the
    /// OS's per-4-KB-page work in the loop. Read from debug.circleai.hugepages so a run can turn it on without a rebuild.
    /// </summary>
    public static bool HugePages { get; set; } = Linux && Property("debug.circleai.hugepages") == "1";

    /// <summary>Memory aligned for direct reads, <paramref name="bytes"/> long; free it with <see cref="Free"/>.</summary>
    public static byte* Allocate(long bytes)
    {
        var size = (nuint)Math.Max(Page, bytes);
        if (HugePages && Linux && bytes >= HugePage)
        {
            var big = NativeMemory.AlignedAlloc(size, HugePage); // 2 MB-aligned so transparent huge pages can back it
            if (big != null)
            {
                try { LinuxCalls.AdviseMemory(big, size, LinuxCalls.HugePageAdvice); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
                return (byte*)big;
            }
        }
        return (byte*)NativeMemory.AlignedAlloc(size, Page);
    }

    /// <summary>Frees what <see cref="Allocate"/> gave; both huge-page and plain buffers come from AlignedAlloc.</summary>
    public static void Free(byte* memory)
    {
        if (memory != null) NativeMemory.AlignedFree(memory);
    }

    // One Android system property, null when there is none or no libc to ask. A byte* name dodges the string
    // marshalling that threw on the CoreCLR worker (MarshalDirectiveException, 2026-11-19).
    private static string? Property(string name)
    {
        if (!Linux) return null;
        try
        {
            var ask = Encoding.UTF8.GetBytes(name + "\0");
            var value = stackalloc byte[92];
            fixed (byte* n = ask)
            {
                var length = LinuxCalls.PropertyGet(n, value);
                return length > 0 ? Encoding.ASCII.GetString(value, length) : null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary><paramref name="value"/> rounded up to whole pages.</summary>
    public static long Pages(long value) => (value + Page - 1) / Page * Page;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_fd >= 0) LinuxCalls.Close(_fd);
        _handle?.Dispose();
    }
}

// The libc calls, blittable as the assembly's disabled runtime marshalling wants them. The flag values
// are Linux's, and O_DIRECT is the one that differs by architecture.
internal static unsafe class LinuxCalls
{
    public const int ReadOnly = 0;
    public const int CloseOnExec = 0x80000;
    public const int DontNeed = 4;       // POSIX_FADV_DONTNEED
    public const int MemoryDontNeed = 4; // MADV_DONTNEED
    public const int HugePageAdvice = 14; // MADV_HUGEPAGE
    public const int Interrupted = 4;    // EINTR

    public static int DirectFlag => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
        ? 0x10000  // 0200000 on ARM
        : 0x4000;  // 040000 on x86

    [DllImport("libc", EntryPoint = "open")]
    public static extern int Open(byte* path, int flags, int mode);

    [DllImport("libc", EntryPoint = "close")]
    public static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "pread64")]
    public static extern nint ReadAt(int fd, void* buffer, nuint count, long offset);

    [DllImport("libc", EntryPoint = "posix_fadvise64")]
    public static extern int Advise(int fd, long offset, long length, int advice);

    [DllImport("libc", EntryPoint = "madvise")]
    public static extern int AdviseMemory(void* address, nuint length, int advice);

    [DllImport("libc", EntryPoint = "__system_property_get")]
    public static extern int PropertyGet(byte* name, byte* value);
}
