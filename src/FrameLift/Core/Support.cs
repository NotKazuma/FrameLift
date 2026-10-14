using System.Diagnostics;
using FrameLift.Native;

namespace FrameLift.Core;

/// <summary>Live CPU, RAM and disk numbers for the dashboard.</summary>
public sealed class SystemStats
{
    private long _idle, _total;

    /// <summary>CPU use since the previous call, 0-100.</summary>
    public int CpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long total = kernel + user; // kernel time already includes idle time
        long dIdle = idle - _idle, dTotal = total - _total;
        _idle = idle; _total = total;
        return dTotal <= 0 ? 0 : (int)Math.Clamp(100 - dIdle * 100 / dTotal, 0, 100);
    }

    public (int percent, double usedGb, double totalGb) Ram()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.TotalPhys / 1073741824.0, avail = m.AvailPhys / 1073741824.0;
        return ((int)m.MemoryLoad, total - avail, total);
    }

    public (int percent, double freeGb, double totalGb) SystemDrive()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            double total = d.TotalSize / 1073741824.0, free = d.AvailableFreeSpace / 1073741824.0;
            return ((int)Math.Round((total - free) * 100 / total), free, total);
        }
        catch (Exception) { return (0, 0, 0); }
    }
}

public static class RestorePoint
{
    /// <summary>
    /// Creates a System Restore point. Returns null on success, otherwise a reason in plain words
    /// (System Protection off, or Windows' once-per-24-hours limit).
    /// </summary>
    public static string? Create(string description)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"$ErrorActionPreference='Stop'; Checkpoint-Computer -Description '{description.Replace("'", "")}' -RestorePointType MODIFY_SETTINGS");
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch (Exception) { } return "Timed out creating the restore point."; }
            if (p.ExitCode == 0) return null;
            return err.Contains("1440", StringComparison.Ordinal) || err.Contains("frequency", StringComparison.OrdinalIgnoreCase)
                ? "Windows already made a restore point in the last 24 hours, so a new one was skipped."
                : "Could not create a restore point. System Protection may be turned off for this drive.";
        }
        catch (Exception e) { return "Could not create a restore point: " + e.Message; }
    }
}
