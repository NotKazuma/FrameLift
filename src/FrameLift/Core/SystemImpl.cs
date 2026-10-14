using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using FrameLift.Native;
using Microsoft.Win32;

namespace FrameLift.Core;

public sealed class SystemRegistry : IRegistry
{
    private static (RegistryKey root, string sub) Split(string path)
    {
        var i = path.IndexOf('\\');
        var hive = i < 0 ? path : path[..i];
        var sub = i < 0 ? "" : path[(i + 1)..];
        var root = hive.ToUpperInvariant() switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            _ => throw new ArgumentException("Unsupported registry hive: " + hive),
        };
        return (root, sub);
    }

    public RegSnapshot Read(string path, string name)
    {
        var (root, sub) = Split(path);
        using var k = root.OpenSubKey(sub);
        if (k is null) return new(false, "", "");
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v is null) return new(false, "", "");
        var kind = k.GetValueKind(name);
        return new(true, kind.ToString(), kind switch
        {
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])v),
            RegistryValueKind.MultiString => string.Join("\0", (string[])v),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        });
    }

    public void Write(string path, string name, string kind, string data)
    {
        var (root, sub) = Split(path);
        using var k = root.CreateSubKey(sub);
        var rk = Enum.Parse<RegistryValueKind>(kind);
        object v = rk switch
        {
            // Read() stores the signed int text, but accept unsigned text (e.g. 4294967295) too.
            RegistryValueKind.DWord => int.TryParse(data, out var i) ? i : unchecked((int)uint.Parse(data)),
            RegistryValueKind.QWord => long.Parse(data),
            RegistryValueKind.Binary => Convert.FromBase64String(data),
            RegistryValueKind.MultiString => data.Split('\0'),
            _ => data,
        };
        k.SetValue(name, v, rk);
    }

    public void DeleteValue(string path, string name)
    {
        var (root, sub) = Split(path);
        using var k = root.OpenSubKey(sub, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public IReadOnlyList<string> ValueNames(string path)
    {
        var (root, sub) = Split(path);
        using var k = root.OpenSubKey(sub);
        return k?.GetValueNames() ?? Array.Empty<string>();
    }

    public bool KeyExists(string path)
    {
        var (root, sub) = Split(path);
        using var k = root.OpenSubKey(sub);
        return k is not null;
    }
}

public sealed class SystemServiceRunner : IServiceRunner
{
    public bool IsRunning(string name)
    {
        try { using var s = new ServiceController(name); return s.Status == ServiceControllerStatus.Running; }
        catch (Exception) { return false; }
    }

    public bool Stop(string name)
    {
        try
        {
            using var s = new ServiceController(name);
            if (s.Status == ServiceControllerStatus.Stopped) return true;
            if (!s.CanStop) return false;
            s.Stop();
            s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool Start(string name)
    {
        try
        {
            using var s = new ServiceController(name);
            if (s.Status == ServiceControllerStatus.Running) return true;
            s.Start();
            s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Power plan control through powercfg.exe, which ships with every Windows install.</summary>
public sealed class PowercfgPower : IPower
{
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");

    private static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("powercfg.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(true); } catch (Exception) { /* exited between the check and the kill */ }
            throw new System.TimeoutException("powercfg did not finish within 15 seconds: " + string.Join(' ', args));
        }
        return o.GetAwaiter().GetResult();
    }

    public Guid GetActiveScheme()
    {
        var m = GuidRx.Match(Run("/getactivescheme"));
        return m.Success ? Guid.Parse(m.Value) : Guid.Empty;
    }

    public void SetActiveScheme(Guid scheme) => Run("/setactive", scheme.ToString());

    public bool IsPerformancePlan(Guid scheme)
    {
        foreach (var line in Run("/list").Split('\n'))
            if (line.Contains(scheme.ToString(), StringComparison.OrdinalIgnoreCase))
                return line.Contains("performance", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static void Check(uint rc, string what)
    {
        if (rc != 0) throw new System.ComponentModel.Win32Exception((int)rc, $"Power setting {what} failed (error {rc})");
    }

    public int GetCpuMaxPercent(Guid scheme, bool ac) => Read(scheme, PowerSettings.SubProcessor, PowerSettings.MaxState, ac, "CPU max");

    public void SetCpuMaxPercent(Guid scheme, bool ac, int percent) =>
        Write(scheme, PowerSettings.SubProcessor, PowerSettings.MaxState, ac, Math.Clamp(percent, 1, 100), "CPU max");

    public int GetSetting(Guid scheme, Guid sub, Guid setting, bool ac) => Read(scheme, sub, setting, ac, setting.ToString());

    public void SetSetting(Guid scheme, Guid sub, Guid setting, bool ac, int value) => Write(scheme, sub, setting, ac, value, setting.ToString());

    private static int Read(Guid scheme, Guid sub, Guid set, bool ac, string what)
    {
        uint v;
        Check(ac ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v)
                 : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v),
              $"read of {what} ({(ac ? "AC" : "DC")}) on {scheme}");
        return (int)v;
    }

    private static void Write(Guid scheme, Guid sub, Guid set, bool ac, int value, string what)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), $"Power setting {what} cannot be negative ({value})");
        uint v = (uint)value;
        Check(ac ? NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, v)
                 : NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, v),
              $"write of {what} ({(ac ? "AC" : "DC")}) on {scheme}");
    }

    public void Reapply(Guid scheme) =>
        Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme), $"re-apply of {scheme}");

    public Guid EnsureScheme(Guid template)
    {
        if (Run("/list").Contains(template.ToString(), StringComparison.OrdinalIgnoreCase)) return template;
        // Modern-standby PCs hide the built-in High Performance plan; recreate it from its template.
        var m = GuidRx.Match(Run("/duplicatescheme", template.ToString()));
        return m.Success ? Guid.Parse(m.Value) : GetActiveScheme();
    }
}

public sealed class SystemProcessOps : IProcessOps
{
    public long GetStartTicks(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.Ticks; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    public ProcessPriorityClass GetPriority(int pid)
    {
        using var p = Process.GetProcessById(pid);
        return p.PriorityClass;
    }

    public void SetPriority(int pid, ProcessPriorityClass c)
    {
        using var p = Process.GetProcessById(pid);
        p.PriorityClass = c;
    }

    public void Suspend(int pid)
    {
        using var p = Process.GetProcessById(pid);
        NativeMethods.NtSuspendProcess(p.Handle);
    }

    public void Resume(int pid)
    {
        using var p = Process.GetProcessById(pid);
        NativeMethods.NtResumeProcess(p.Handle);
    }

    public void SetEcoQos(int pid, bool on)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            var s = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                // ControlMask 0 hands control back to the system, which is the original state.
                ControlMask = on ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                StateMask = on ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };
            NativeMethods.SetProcessInformation(h, NativeMethods.ProcessPowerThrottling, ref s,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
        }
        finally { NativeMethods.CloseHandle(h); }
    }
}
