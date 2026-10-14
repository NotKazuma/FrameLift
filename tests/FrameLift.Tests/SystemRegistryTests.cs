using FrameLift.Core;
using Microsoft.Win32;

namespace FrameLift.Tests;

/// <summary>Runs the real registry wrapper against a scratch key under HKCU, which is removed afterwards.</summary>
public sealed class SystemRegistryTests : IDisposable
{
    private const string Key = @"HKCU\Software\FrameLiftTest";
    private readonly SystemRegistry _reg = new();

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\FrameLiftTest", throwOnMissingSubKey: false);

    [Fact]
    public void Round_trips_every_value_kind()
    {
        var bin = Convert.ToBase64String(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 });
        _reg.Write(Key, "d", "DWord", "-1");
        _reg.Write(Key, "s", "String", "hello");
        _reg.Write(Key, "b", "Binary", bin);
        _reg.Write(Key, "q", "QWord", "123456789012");

        Assert.Equal(new RegSnapshot(true, "DWord", "-1"), _reg.Read(Key, "d"));
        Assert.Equal(new RegSnapshot(true, "String", "hello"), _reg.Read(Key, "s"));
        Assert.Equal(new RegSnapshot(true, "Binary", bin), _reg.Read(Key, "b"));
        Assert.Equal(new RegSnapshot(true, "QWord", "123456789012"), _reg.Read(Key, "q"));
        Assert.Equal(4, _reg.ValueNames(Key).Count);
    }

    [Fact]
    public void Missing_values_and_keys_read_as_not_existing_and_delete_is_harmless()
    {
        Assert.False(_reg.Read(Key + @"\nope", "x").Exists);
        Assert.False(_reg.KeyExists(Key + @"\nope"));
        _reg.DeleteValue(Key + @"\nope", "x");
        Assert.Empty(_reg.ValueNames(Key + @"\nope"));
    }

    [Fact]
    public void Tweak_engine_works_end_to_end_on_the_real_registry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lsb-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new Journal(Path.Combine(dir, "j.json"));
            var power = new FakePower();
            var svc = new FakeServices();
            var reverter = new Reverter(journal, _reg, svc, power, new FakeProcs());
            var engine = new TweakEngine(_reg, svc, power, journal, reverter);
            var t = new TweakDef("t", "c", "t", "d", Risk.Low, false,
                new RegChange(Key, "A", "DWord", "0"), new RegChange(Key, "B", "String", "new"));
            _reg.Write(Key, "A", "DWord", "1");

            engine.Apply(t);
            Assert.Equal("0", _reg.Read(Key, "A").Data);
            Assert.Equal("new", _reg.Read(Key, "B").Data);

            engine.Revert("t");
            Assert.Equal("1", _reg.Read(Key, "A").Data);
            Assert.False(_reg.Read(Key, "B").Exists);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
