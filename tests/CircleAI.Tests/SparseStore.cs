// SparseStore.cs
//
// A model store on disk, without spending the disk.
//
// WHY THIS EXISTS. The loader, the fit gate and DeadWeight all measure the FILE.
// That is deliberate and was a fix: DeadWeight used to resolve sizes through the
// embedded registry and so reported zero bytes for a feed-delivered model, which
// is the opposite of its job. So a model is "here" when its bundle files exist at
// their declared lengths, and a test standing in for an installed model has to
// put those lengths on disk.
//
// It does NOT have to put the bytes there, and four tests did.
// `new FileStream(path, FileMode.Create)` followed by `SetLength(n)` physically
// extends the file on NTFS - it is not a hole, it is n bytes of zeros. The
// catalogue's chat rows include a 22.8 GB bundle and a 17.7 GB one, and xUnit runs
// test classes as separate collections, so CrashVerdictTests, ModelChoiceTests,
// ModelOfferTests and SetupStatusTests materialised tens of gigabytes each, at the
// same time, against whatever the dev box had free.
//
// What that looked like: "There is not enough space on the disk" from
// RandomAccess.SetFileLength, unit tests running for 1m09s / 1m57s / 2m25s, and a
// suite that failed once, passed in isolation, then failed four times - because the
// variable was free space, not the code. Nothing in any assert mentioned a disk.
//
// SetupStatusTests already had a private helper called `Sparse` that was not
// sparse. The intent was right and the mechanism was never written; this is the
// mechanism, in one place, so there is nothing to get wrong twice.
//
// HOW. FSCTL_SET_SPARSE before SetLength: the length is then metadata and costs
// nothing. On Linux and macOS SetLength already leaves a hole, so only NTFS needs
// telling. If the ioctl fails we throw rather than fall back - a quiet fallback to
// real allocation is exactly the bug this file removes, and a test that fills a
// disk should say so instead of taking 2 minutes to find out.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CircleAI.Tests;

internal static class SparseStore
{
    /// <summary>
    /// Creates <paramref name="path"/> reporting <paramref name="length"/> bytes
    /// without allocating them. Parent directories are created.
    /// </summary>
    public static void Write(string path, long length)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // ReadWrite, not Write: DeviceIoControl needs write access to the handle.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        MarkSparse(fs);
        fs.SetLength(length);
    }

    private const uint FsctlSetSparse = 0x000900C4;

    private static void MarkSparse(FileStream fs)
    {
        // SetLength already produces a hole on ext4/APFS/btrfs.
        if (!OperatingSystem.IsWindows()) return;

        if (DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse,
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return;

        var err = Marshal.GetLastWin32Error();
        throw new IOException(
            $"could not mark '{fs.Name}' sparse (Win32 {err}). Refusing to allocate it for " +
            "real: the catalogue's largest bundle is 22.8 GB and several tests install the " +
            "whole chat set in parallel. Is TEMP on a non-NTFS volume?");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
