using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class CleanupAndStartupTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "lsb-clean-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public CleanupAndStartupTests()
    {
        _root = Path.Combine(_top, "a", "cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_top, true); } catch (IOException) { } }

    private CleanupCategory Cat(string id = "x") => new(id, "t", "d", new[] { _root });

    [Fact]
    public void Clean_deletes_files_and_reports_freed_bytes()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.tmp"), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.tmp"), new byte[500]);

        var svc = new CleanupService();
        Assert.Equal(1500, svc.Scan(Cat()).Bytes);
        Assert.Equal(1500, svc.Clean(Cat()));
        Assert.Empty(Directory.GetFileSystemEntries(_root)); // root kept, subfolder removed
    }

    [Fact]
    public void File_in_use_is_skipped()
    {
        var f = Path.Combine(_root, "locked.tmp");
        using (File.Open(f, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.Equal(0, new CleanupService().Clean(Cat()));
        }
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Junction_is_not_followed_out_of_the_folder()
    {
        var outside = Path.Combine(_top, "a", "outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "keep.txt");
        File.WriteAllText(precious, "x");
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside); }
        catch (Exception) { return; } // symlink creation needs privilege on some machines

        new CleanupService().Clean(Cat());
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Thumbnail_category_only_deletes_cache_files()
    {
        File.WriteAllText(Path.Combine(_root, "thumbcache_256.db"), "x");
        File.WriteAllText(Path.Combine(_root, "other.dat"), "x");
        new CleanupService().Clean(Cat("thumbs"));
        Assert.False(File.Exists(Path.Combine(_root, "thumbcache_256.db")));
        Assert.True(File.Exists(Path.Combine(_root, "other.dat")));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData("")]
    public void Unsafe_roots_are_refused(string root)
    {
        Assert.False(CleanupService.IsSafeRoot(root));
        Assert.Empty(CleanupService.Enumerate(root));
    }

    [Fact]
    public void Startup_toggle_round_trips_without_removing_the_entry()
    {
        var reg = new FakeRegistry();
        const string run = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
        reg.Seed(run, "Discord", "String", @"C:\d.exe");
        var mgr = new StartupManager(reg);

        var item = mgr.List().Single();
        Assert.True(item.Enabled);

        mgr.SetEnabled(item, false);
        Assert.False(mgr.List().Single().Enabled);
        Assert.True(reg.Read(run, "Discord").Exists);

        mgr.SetEnabled(item, true);
        Assert.True(mgr.List().Single().Enabled);
    }
}
