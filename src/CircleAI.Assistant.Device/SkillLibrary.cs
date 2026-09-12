// SkillLibrary.cs
//
// The skill database, out of the APK and onto the phone.
//
// SHIPPED AS AN ASSET RATHER THAN BUILT ON DEVICE. Building it means walking
// 1,405 SKILL.md files and writing an FTS5 index, which is seconds of work and
// 7.5 MB of source that would have to ship anyway. The database IS the shipped
// form: 5.1 MB compressed in the APK, 20 MB unpacked once.
//
// ZIPPED, EVEN THOUGH IT IS ONE FILE. Android compresses most assets inside the
// APK and Assets.Open decompresses transparently, so a bare .db would also have
// been about 5 MB in the package - but it would then be copied out at full size
// through a stream with no integrity signal at all. A zip entry carries its own
// length and CRC, so a truncated copy fails here rather than becoming a database
// that opens and returns nothing.
//
// The unpack follows VoiceWiring.UnpackEspeakData: a marker that is a REAL file
// from the payload rather than a flag, a zip-slip guard, and a catch that
// returns null so a failure costs the skill library and not the app.

using CircleAI.Skills;
using Android.Content;
using Android.Util;
// ExtractToFile is an EXTENSION METHOD on ZipArchiveEntry, in
// System.IO.Compression.ZipFileExtensions - so fully qualifying the type below
// is not enough to bring it into scope, and the error reads as a missing
// assembly reference rather than a missing using.
using System.IO.Compression;

namespace CircleAI.Assistant.Device;

/// <summary>Unpacks and opens the shipped skill database.</summary>
public static class SkillLibrary
{
    private const string Tag   = "CircleAI.Skills";
    private const string Asset = "skills.db.zip";
    private const string DbName = "skills.db";

    /// <summary>
    /// Schema this build understands.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM SqliteSkillStore'S user_version, AND BOTH ARE NEEDED. That
    /// one catches a database whose TABLES moved; this one catches a database
    /// whose CONTENT was regenerated - a new skill library shipped in a new APK
    /// over a phone that already unpacked the old one. Without it the marker file
    /// exists, the unpack is skipped, and the phone keeps last release's skills
    /// forever while the APK carries the new ones.
    /// </remarks>
    private const int Version = 1;

    /// <summary>Open the library, unpacking it first if it is not there yet.</summary>
    /// <returns>A store, or null when there is no library on this build.</returns>
    public static ISkillStore? Open(Context app)
    {
        try
        {
            var root = app.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(root)) return null;

            var dir = System.IO.Path.Combine(root, "skills", $"v{Version}");
            var db = System.IO.Path.Combine(dir, DbName);

            if (!System.IO.File.Exists(db) && !Unpack(app, dir)) return null;

            var store = new SqliteSkillStore(db);
            Log.Info(Tag, store.FullTextAvailable
                ? $"skill library open at {db} (FTS5)"
                : $"skill library open at {db} (LIKE fallback - no FTS5 in this build)");
            return store;
        }
        catch (Exception ex)
        {
            // A missing library costs "how do I do X" answers. The capability
            // manifest still answers "what can you do", and the app still runs.
            Log.Warn(Tag, $"skill library unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool Unpack(Context app, string dir)
    {
        try
        {
            // START CLEAN. A copy killed half way leaves a database that opens
            // and returns nothing, which is worse than one that is absent -
            // absent falls back to the manifest and says so in the log.
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            System.IO.Directory.CreateDirectory(dir);

            // OLD VERSIONS GO. Leaving them costs 20 MB each on a phone with
            // 1.4 GB of usable memory and a 500 MB model to hold.
            var parent = System.IO.Directory.GetParent(dir)!.FullName;
            foreach (var old in System.IO.Directory.GetDirectories(parent))
                if (!string.Equals(old, dir, StringComparison.Ordinal))
                    try { System.IO.Directory.Delete(old, true); } catch { /* best effort */ }

            using var asset = app.Assets!.Open(Asset);
            using var archive = new System.IO.Compression.ZipArchive(
                asset, System.IO.Compression.ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, entry.Name));
                if (!dest.StartsWith(dir, StringComparison.Ordinal)) continue;   // zip-slip
                entry.ExtractToFile(dest, overwrite: true);
            }

            var db = System.IO.Path.Combine(dir, DbName);
            if (!System.IO.File.Exists(db))
            {
                Log.Warn(Tag, $"{Asset} did not contain {DbName}");
                return false;
            }

            Log.Info(Tag, $"skill library unpacked to {db} "
                        + $"({new System.IO.FileInfo(db).Length / 1_000_000} MB)");
            return true;
        }
        catch (Java.IO.FileNotFoundException)
        {
            // No asset in this build. Not an error: a head may ship without one.
            Log.Info(Tag, $"no {Asset} in this build - the capability manifest still answers");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"skill library unpack failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
