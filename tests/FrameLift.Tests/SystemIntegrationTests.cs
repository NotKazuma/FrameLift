using System.Diagnostics;
using FrameLift.Core;

namespace FrameLift.Tests;

/// <summary>
/// Real Windows calls, aimed at scratch objects so the PC's own settings are never touched: a duplicate power plan
/// that is deleted afterwards, and nvidia-smi set to the value the GPU already has. Each test returns early (with the
/// reason as a comment) on a machine that cannot run it, such as one without administrator rights or an NVIDIA GPU.
/// </summary>
public sealed class SystemIntegrationTests : IDisposable
{
    private readonly PowercfgPower _power = new();
    private Guid? _scratch;

    public void Dispose()
    {
        if (_scratch is Guid g) RunPowercfg("/delete", g.ToString());
    }

    private static string RunPowercfg(params string[] args)
    {
        var psi = new ProcessStartInfo("powercfg.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return o;
    }

    private Guid? MakeScratchScheme()
    {
        var active = _power.GetActiveScheme();
        var m = System.Text.RegularExpressions.Regex.Match(RunPowercfg("/duplicatescheme", active.ToString()),
            @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        if (!m.Success) return null; // not elevated: powercfg refuses to duplicate a plan
        _scratch = Guid.Parse(m.Value);
        return _scratch;
    }

    [Fact]
    public void Cpu_max_state_round_trips_on_a_real_scratch_plan()
    {
        if (MakeScratchScheme() is not Guid s) return;

        _power.SetCpuMaxPercent(s, true, 37);
        _power.SetCpuMaxPercent(s, false, 41);

        Assert.Equal(37, _power.GetCpuMaxPercent(s, true));
        Assert.Equal(41, _power.GetCpuMaxPercent(s, false));
        Assert.NotEqual(s, _power.GetActiveScheme()); // the PC's own plan was never switched
    }

    [Fact]
    public void Power_cap_journal_entry_restores_the_real_scratch_plan_through_the_reverter()
    {
        if (MakeScratchScheme() is not Guid s) return;
        var dir = Path.Combine(Path.GetTempPath(), "fl-realpower-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new Journal(Path.Combine(dir, "j.json"));
            int ac = _power.GetCpuMaxPercent(s, true), dc = _power.GetCpuMaxPercent(s, false);
            journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerCap, Name = s.ToString(), Data = $"{ac}|{dc}" });
            _power.SetCpuMaxPercent(s, true, 20);
            _power.SetCpuMaxPercent(s, false, 20);

            var r = new Reverter(journal, new SystemRegistry(), new SystemServiceRunner(), _power, new SystemProcessOps());
            r.RevertOwner("boost");

            Assert.Empty(r.Failures);
            Assert.Equal(ac, _power.GetCpuMaxPercent(s, true));
            Assert.Equal(dc, _power.GetCpuMaxPercent(s, false));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Cool_Boost_settings_apply_and_restore_exactly_on_a_real_scratch_plan()
    {
        if (MakeScratchScheme() is not Guid s) return;
        var dir = Path.Combine(Path.GetTempPath(), "fl-realcool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sub = PowerSettings.SubProcessor;
            var watched = EfficiencyTuner.BoostTargets.Select(t => t.Setting).Append(PowerSettings.MaxState).ToList();
            var before = watched.ToDictionary(g => g,
                g => (Ac: _power.GetSetting(s, sub, g, true), Dc: _power.GetSetting(s, sub, g, false)));
            var journal = new Journal(Path.Combine(dir, "j.json"));
            var failures = new List<string>();

            PowerSettingWriter.Apply(_power, journal, "boost", s, EfficiencyTuner.BoostTargets, failures);

            Assert.Empty(failures);
            foreach (var t in EfficiencyTuner.BoostTargets)
                Assert.Equal(PowerSettings.Resolve(t.Setting, before[t.Setting].Ac, t.Value), _power.GetSetting(s, sub, t.Setting, true));
            Assert.NotEqual(s, _power.GetActiveScheme()); // the PC's own plan was never switched

            // The real ceiling (higher of min and max) never drops: games keep the speed they had.
            Assert.True(_power.GetSetting(s, sub, PowerSettings.MaxState, true) >= Math.Max(before[PowerSettings.MinState].Ac, before[PowerSettings.MaxState].Ac));

            var r = new Reverter(journal, new SystemRegistry(), new SystemServiceRunner(), _power, new SystemProcessOps());
            r.RevertOwner("boost");

            Assert.Empty(r.Failures);
            foreach (var g in watched)
                Assert.Equal(before[g], (_power.GetSetting(s, sub, g, true), _power.GetSetting(s, sub, g, false)));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void An_unknown_plan_reports_a_clear_error_instead_of_crashing()
    {
        var ex = Assert.Throws<System.ComponentModel.Win32Exception>(() => _power.GetCpuMaxPercent(Guid.NewGuid(), true));
        Assert.Contains("CPU max", ex.Message);
    }

    [Fact]
    public void Nvidia_probe_never_throws_and_only_claims_support_with_a_known_default()
    {
        var smi = NvidiaSmi.Find();
        if (smi is null) return; // no NVIDIA driver on this machine

        var gpu = new NvidiaGpuPower(smi); // probes with a no-op set to the GPU's own default

        if (gpu.Supported) Assert.True(gpu.DefaultWatts > 0);
        else gpu.SetLimitPercent(70); // unsupported: must be a harmless no-op
        Assert.Null(Record.Exception(() => gpu.SetLimitPercent(100)));
    }

    [Fact]
    public void A_missing_nvidia_smi_gives_no_result_instead_of_an_exception()
    {
        Assert.Null(NvidiaSmi.Run(Path.Combine(Path.GetTempPath(), "no-such-smi.exe"), "-pl 1"));
    }
}
