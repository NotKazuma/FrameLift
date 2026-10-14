using FrameLift.Native;

namespace FrameLift.Core;

public sealed record CleanupCategory(string Id, string Title, string Description, IReadOnlyList<string> Roots, bool RecycleBin = false);

public sealed record ScanResult(CleanupCategory Category, long Bytes, int Files);

public static class CleanupCatalog
{
    public static IReadOnlyList<CleanupCategory> Build()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new CleanupCategory[]
        {
            new("temp-user", "User temp files", "Leftovers from apps and installers in your temp folder.",
                new[] { Path.GetTempPath() }),
            new("temp-win", "Windows temp files", "System temp files that are safe to delete.",
                new[] { Path.Combine(win, "Temp") }),
            new("thumbs", "Thumbnail cache", "Explorer rebuilds it as needed.",
                new[] { Path.Combine(local, @"Microsoft\Windows\Explorer") }),
            new("shaders", "Shader caches", "Rebuilt by games and drivers; the first launch after may stutter once.",
                new[] { Path.Combine(local, "D3DSCache"), Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"AMD\DxCache") }),
            new("wu-cache", "Windows Update downloads", "Already-installed update files.",
                new[] { Path.Combine(win, @"SoftwareDistribution\Download") }),
            new("crash", "Crash dumps and error reports", "Memory dumps from crashed apps.",
                new[] { Path.Combine(local, "CrashDumps"), Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"),
                        Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue") }),
            new("recycle", "Recycle Bin", "Everything you already deleted.", Array.Empty<string>(), RecycleBin: true),
        };
    }
}

public sealed class CleanupService
{
    // Only files matching these are deleted from the thumbnail folder (it also holds other Explorer data).
    private static bool Eligible(CleanupCategory c, string file) =>
        c.Id != "thumbs" || Path.GetFileName(file).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(file).StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase);

    public ScanResult Scan(CleanupCategory c)
    {
        long bytes = 0; int files = 0;
        if (c.RecycleBin)
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            if (NativeMethods.SHQueryRecycleBin(null, ref info) == 0) { bytes = info.i64Size; files = (int)info.i64NumItems; }
            return new ScanResult(c, bytes, files);
        }
        foreach (var root in c.Roots)
            foreach (var f in Enumerate(root))
            {
                if (!Eligible(c, f)) continue;
                try { bytes += new FileInfo(f).Length; files++; } catch (Exception) { }
            }
        return new ScanResult(c, bytes, files);
    }

    /// <summary>Deletes what it can; files in use are skipped. Returns bytes freed.</summary>
    public long Clean(CleanupCategory c)
    {
        if (c.RecycleBin)
        {
            var before = Scan(c).Bytes;
            NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
            return before;
        }
        long freed = 0;
        foreach (var root in c.Roots)
        {
            foreach (var f in Enumerate(root))
            {
                if (!Eligible(c, f)) continue;
                try
                {
                    long len = new FileInfo(f).Length;
                    File.Delete(f);
                    freed += len;
                }
                catch (Exception) { /* in use or access denied */ }
            }
            DeleteEmptyDirs(root, isRoot: true);
        }
        return freed;
    }

    /// <summary>Every file under <paramref name="root"/>, never following junctions or symlinks out of it.</summary>
    public static IEnumerable<string> Enumerate(string root)
    {
        if (!IsSafeRoot(root) || !Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (Exception) { continue; }
            foreach (var f in files)
            {
                if (IsReparse(f)) continue;
                yield return f;
            }
            foreach (var d in dirs)
                if (!IsReparse(d)) stack.Push(d);
        }
    }

    private static void DeleteEmptyDirs(string dir, bool isRoot)
    {
        string[] subs;
        try { subs = Directory.GetDirectories(dir); } catch (Exception) { return; }
        foreach (var d in subs)
            if (!IsReparse(d)) DeleteEmptyDirs(d, false);
        if (isRoot) return;
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception) { }
    }

    private static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return true; }
    }

    /// <summary>Refuses drive roots and anything too shallow to be a cache folder.</summary>
    public static bool IsSafeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var full = Path.GetFullPath(root).TrimEnd('\\');
        if (Path.GetPathRoot(full)!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
        return full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }
}
