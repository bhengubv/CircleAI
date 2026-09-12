// ModelStore.cs
//
// Where the models live: the app's own private storage.
//
// MODELS ARE APP DATA. Without them the app does nothing - no voice, no ears, no
// answers - so they are not media and they are not optional. That distinction
// decides where they belong.
//
// An earlier version of this file put them in shared storage so they would
// survive an uninstall, on the reasoning that they were "large, expensive, and
// worth the same to whichever app opens them". Both halves of that were wrong:
//
//   They are ESSENTIAL, not optional. Putting the thing the app cannot run
//   without into a world-visible folder means a file manager, a cleaner app, or
//   an owner freeing space can break the app, and the folder looks like 250 MB of
//   junk to anybody who does not know what it is.
//
//   And it ASSUMED shared storage exists. Android.OS.Environment
//   .ExternalStorageDirectory is not a guarantee - plenty of devices have no
//   removable card, and scoped storage can refuse the write on the ones that do.
//   The fallback that hid this was papering over an assumption rather than
//   removing it.
//
// Surviving a reinstall is handled where Android provides for it:
// android:hasFragileUserData="true" in the manifest, which makes the uninstall
// dialog offer to KEEP this app's data. That is the mechanism designed for data
// an app needs and cannot cheaply refetch, and it keeps it private.
//
// This class earns its place anyway: six services used to build this path
// themselves, and a model store spelled slightly differently in one of them is a
// download that lands where nothing looks for it.

namespace CircleAI.Assistant.Device;

/// <summary>The one place the model directory is decided.</summary>
public static class ModelStore
{
    private static string? _resolved;

    /// <summary>
    /// Held while the store is resolved for the first time.
    /// </summary>
    /// <remarks>
    /// THE ORIGINAL RACE WAS HARMLESS AND THIS ONE IS NOT. Two threads arriving
    /// together used to mean CreateDirectory ran twice, which does nothing. Adopt
    /// MOVES a person's downloads, and two threads moving the same directory is
    /// one succeeding, one throwing, and a gigabyte in an indeterminate state.
    /// </remarks>
    private static readonly object Gate = new();

    private const string Tag = "CircleAI.Models";

    /// <summary>Where models are, created if it does not exist yet.</summary>
    public static string Path
    {
        get
        {
            if (_resolved is not null) return _resolved;
            lock (Gate) return _resolved ??= Resolve();
        }
    }

    private static string Resolve()
    {
        // MODELPATHS.DEFAULT, WHICH IS WHAT THE SESSION READS.
        //
        // This used to be Context.FilesDir + "CircleAI/Models" on the reasoning
        // that MAUI's AppDataDirectory returns FilesDir and ModelPaths.Default
        // returns the same thing. MEASURED ON A P30, 2026-09-12, AND IT DOES NOT:
        //
        //     Context.FilesDir                 ->  files
        //     SpecialFolder.Personal           ->  files/Documents      <- session
        //     SpecialFolder.ApplicationData    ->  files/.config        <- the head
        //
        // Personal is NOT the files directory on .NET for Android; it is
        // "Documents" underneath it. ModelPaths' own header says otherwise and is
        // wrong about this, which is how one phone ended up with THREE live model
        // directories: the session downloading into Documents, nineteen call
        // sites in the native head downloading into .config, and this class
        // pointing at a third folder that was empty on every device.
        //
        // The census that found it is still below, and it prints on every launch.
        // Which folder wins matters far less than the head and the session
        // naming the same one, so this defers to the session's.
        var dir = CircleAI.Core.ModelPaths.Default;
        System.IO.Directory.CreateDirectory(dir);

        Adopt(dir);
        return dir;
    }

    /// <summary>Every folder this app has ever downloaded a model into.</summary>
    /// <remarks>
    /// THE NATIVE HEAD SPELLED THIS THREE WAYS AND THE SESSION SPELLED IT A
    /// FOURTH. Nineteen call sites built the path from
    /// <c>SpecialFolder.ApplicationData</c>, four of them with a SPACE in
    /// "Circle AI", while CircleAISession used <c>ModelPaths.Default</c> and this
    /// class used the files directory. Whether those resolve to the same folder
    /// depends on how .NET maps SpecialFolder on Android, and this file is not
    /// going to assert an answer to that: the last two times a path was reasoned
    /// about from source rather than measured, it was wrong.
    /// <para>
    /// So it asks the disk instead. Each candidate is checked for actual model
    /// directories, and whatever is found is MOVED into the canonical one - a
    /// rename on the same filesystem, so it costs nothing and leaves exactly one
    /// directory afterwards. If every spelling already resolves to the same
    /// folder this does nothing at all, which is the right behaviour for that
    /// case too.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Legacy()
    {
        foreach (var root in new[]
        {
            // What this class pointed at until 2026-09-12. A phone that ran that
            // build has its whole library here and nothing anywhere else.
            global::Android.App.Application.Context.FilesDir?.AbsolutePath ?? "",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return System.IO.Path.Combine(root, "CircleAI", "Models");
            yield return System.IO.Path.Combine(root, "Circle AI", "Models");
        }
    }

    /// <summary>Take over anything an earlier spelling left behind.</summary>
    /// <remarks>
    /// MEASURED ON A P30, 2026-09-12. Pointing the native head at this class
    /// turned every ability on the abilities screen from installed to "Turn on",
    /// and the home screen from "Tap and talk" to "Let's set it up" - on a phone
    /// that had not lost a byte. Roughly 2 GB of downloads were simply somewhere
    /// else, and without this the app would have asked its owner to fetch them
    /// all again over a metered link.
    /// </remarks>
    private static void Adopt(string canonical)
    {
        // THE CENSUS, PRINTED, BECAUSE THE ALTERNATIVE IS ARGUING ABOUT IT. Four
        // spellings of "the model directory" and no agreement in the codebase
        // about whether they resolve to the same folder on Android. One launch
        // now answers it:
        //
        //     adb logcat -s CircleAI.Models
        Log($"canonical = {canonical} ({Count(canonical)})");
        foreach (var old in Legacy()) Log($"legacy    = {old} ({Count(old)})");

        foreach (var old in Legacy())
        {
            try
            {
                if (string.Equals(
                        System.IO.Path.GetFullPath(old),
                        System.IO.Path.GetFullPath(canonical),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!System.IO.Directory.Exists(old)) continue;

                foreach (var model in System.IO.Directory.GetDirectories(old))
                {
                    var name = System.IO.Path.GetFileName(model);
                    var moved = System.IO.Path.Combine(canonical, name);

                    // ALREADY THERE WINS. A model present in both is the case
                    // where the two folders each got a copy - leave the canonical
                    // one alone rather than overwriting it mid-read.
                    if (System.IO.Directory.Exists(moved))
                    {
                        Reclaim(model, moved, name);
                        continue;
                    }

                    System.IO.Directory.Move(model, moved);
                    Log($"adopted {name} from {old}");
                }

                // Loose files too - a non-bundle model is a single .gguf.
                foreach (var file in System.IO.Directory.GetFiles(old))
                {
                    var moved = System.IO.Path.Combine(canonical, System.IO.Path.GetFileName(file));
                    if (System.IO.File.Exists(moved)) continue;
                    System.IO.File.Move(file, moved);
                }

                // EMPTY, NOT RECURSIVE. Everything worth keeping has been moved
                // or proven redundant by now, so what is left is an empty folder
                // - and Delete without recursion REFUSES to remove anything else,
                // which is the guard rather than a comment promising care.
                if (System.IO.Directory.GetFileSystemEntries(old).Length == 0)
                {
                    System.IO.Directory.Delete(old);
                    Log($"removed empty {old}");
                }
            }
            catch (Exception ex)
            {
                // A failed adoption costs a re-download, which is bad. A failed
                // adoption that takes the app down with it is worse.
                Log($"could not adopt {old}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Give back the space a second copy of one model is holding.</summary>
    /// <remarks>
    /// MEASURED ON A P30, 2026-09-12: 800 MB of Qwen3.5-0.8B and SmolVLM-256M
    /// sitting in .config while identical copies sat in the canonical store,
    /// because two owners of "the model directory" each downloaded their own. The
    /// adoption above consolidates; without this it leaves the duplicate behind
    /// forever on every phone that ever ran one of those builds.
    /// <para>
    /// DELETES ONLY WHAT IT CAN PROVE IS REDUNDANT. The keeper has to be at least
    /// as large as the copy being dropped, byte for byte across the whole
    /// directory. That is deliberately a cheap check rather than a registry
    /// lookup - this runs before anything else is wired up - and it errs the only
    /// safe way: an interrupted or truncated download in the canonical store is
    /// SMALLER than the good copy beside it, so the good copy survives and gets
    /// logged instead. A wasted 400 MB is a nuisance; deleting somebody's only
    /// working model is not.
    /// </para>
    /// </remarks>
    private static void Reclaim(string duplicate, string keeper, string name)
    {
        try
        {
            var dup = Bytes(duplicate);
            var keep = Bytes(keeper);

            if (dup <= 0 || keep < dup)
            {
                Log($"kept both copies of {name}: {keeper} is {keep} B, {duplicate} is {dup} B");
                return;
            }

            System.IO.Directory.Delete(duplicate, recursive: true);
            Log($"reclaimed {dup / 1_000_000} MB - duplicate {name} at {duplicate}");
        }
        catch (Exception ex)
        {
            Log($"could not reclaim {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Everything under a directory, in bytes.</summary>
    private static long Bytes(string dir)
    {
        try
        {
            long total = 0;
            foreach (var f in System.IO.Directory.EnumerateFiles(
                         dir, "*", System.IO.SearchOption.AllDirectories))
                total += new System.IO.FileInfo(f).Length;
            return total;
        }
        catch { return -1; }
    }

    /// <summary>What is in a candidate directory, for the census line.</summary>
    private static string Count(string dir)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir)) return "absent";
            var models = System.IO.Directory.GetDirectories(dir).Length;
            var files  = System.IO.Directory.GetFiles(dir).Length;
            return models == 0 && files == 0
                ? "empty"
                : $"{models} model dir(s), {files} file(s)";
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static void Log(string message)
    {
        try { global::Android.Util.Log.Info(Tag, message); } catch { }
    }
}
