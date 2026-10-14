using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameLift.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntryKind
{
    Registry, ServiceRunning, PowerPlan, Process, File,
    /// <summary>A plan's "maximum processor state" was lowered: Name = plan GUID, Data = "acPercent|dcPercent" before.</summary>
    PowerCap,
    /// <summary>The GPU power limit was lowered: Data = the default limit in watts to go back to.</summary>
    GpuPowerLimit,
    /// <summary>A process was frozen: Pid + StartTicks identify it. Undo resumes it.</summary>
    ProcessSuspend,
    /// <summary>A plan setting was changed: Name = "plan|subgroup|setting" GUIDs, Data = "ac|dc" values before.</summary>
    PowerSetting,
    /// <summary>The GPU core clock was capped. Undo hands clocks back to the driver.</summary>
    GpuClockLock,
}

/// <summary>One thing we changed, with what it was before. Written to disk before the change is made.</summary>
public sealed class JournalEntry
{
    /// <summary>Which tweak or session owns this change ("boost" for Game Boost).</summary>
    public string Owner { get; set; } = "";
    public EntryKind Kind { get; set; }

    // Registry: Path/Name/Existed/ValueKind/Data. Service: Name. Power: Data = previous scheme GUID.
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string ValueKind { get; set; } = "";
    public string Data { get; set; } = "";

    // Process: Pid + StartTicks guard against pid reuse; Data = original ProcessPriorityClass name.
    public int Pid { get; set; }
    public long StartTicks { get; set; }
}

/// <summary>
/// On-disk list of every change still in effect. Whatever is in it can be undone, whether the app
/// exited cleanly, crashed or was killed (the watchdog replays it).
/// </summary>
public sealed class Journal
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<JournalEntry> _entries = new();

    public Journal(string path)
    {
        _path = path;
        _entries = LoadFromDisk();
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FrameLift", "journal.json");

    public IReadOnlyList<JournalEntry> Entries { get { lock (_lock) return _entries.ToList(); } }

    public bool HasOwner(string owner) { lock (_lock) return _entries.Any(e => e.Owner == owner); }

    public IReadOnlyList<string> Owners() { lock (_lock) return _entries.Select(e => e.Owner).Distinct().ToList(); }

    public void Add(JournalEntry e)
    {
        lock (_lock) { _entries.Add(e); Save(); }
    }

    public void Remove(JournalEntry e)
    {
        lock (_lock) { _entries.Remove(e); Save(); }
    }

    /// <summary>Re-read the file (used by the watchdog, which has its own instance).</summary>
    public void Reload() { lock (_lock) _entries = LoadFromDisk(); }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        if (_entries.Count == 0) { try { File.Delete(_path); } catch (IOException) { } return; }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
        File.Move(tmp, _path, overwrite: true);
    }

    private List<JournalEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }
}

/// <summary>Undoes journal entries. Newest first, so layered changes unwind correctly.</summary>
public sealed class Reverter
{
    private readonly Journal _journal;
    private readonly IRegistry _reg;
    private readonly IServiceRunner _svc;
    private readonly IPower _power;
    private readonly IProcessOps _proc;
    private readonly IGpuPower? _gpu;

    public Reverter(Journal journal, IRegistry reg, IServiceRunner svc, IPower power, IProcessOps proc, IGpuPower? gpu = null)
    {
        _journal = journal; _reg = reg; _svc = svc; _power = power; _proc = proc; _gpu = gpu;
    }

    /// <summary>What could not be undone by the most recent revert, with the reason. Empty when everything went back.</summary>
    public IReadOnlyList<string> Failures { get; private set; } = Array.Empty<string>();

    /// <summary>Reverts every entry for the owner. Returns how many entries were undone.</summary>
    public int RevertOwner(string owner) => Revert(_journal.Entries.Where(e => e.Owner == owner));

    public int RevertAll() => Revert(_journal.Entries);

    /// <summary>Reverts just these entries (newest first), for a feature that undoes its own changes mid-session.</summary>
    public int RevertEntries(IEnumerable<JournalEntry> entries) => Revert(entries);

    private int Revert(IEnumerable<JournalEntry> entries)
    {
        int n = 0;
        var failures = new List<string>();
        foreach (var e in entries.Reverse().ToList())
        {
            try { Undo(e); n++; }
            catch (Exception ex)
            {
                // Gone or access denied: the entry is dropped either way, but the reason is kept for the caller.
                failures.Add($"{e.Kind} ({e.Owner}): {ex.Message}");
            }
            _journal.Remove(e);
        }
        Failures = failures;
        return n;
    }

    private void Undo(JournalEntry e)
    {
        switch (e.Kind)
        {
            case EntryKind.Registry:
                if (e.Existed) _reg.Write(e.Path, e.Name, e.ValueKind, e.Data);
                else _reg.DeleteValue(e.Path, e.Name);
                break;
            case EntryKind.ServiceRunning:
                _svc.Start(e.Name);
                break;
            case EntryKind.PowerPlan:
                if (Guid.TryParse(e.Data, out var g)) _power.SetActiveScheme(g);
                break;
            case EntryKind.File:
                // Path = the file, Data = our backup copy. Existed=false means we created the file.
                if (e.Existed) { if (File.Exists(e.Data)) File.Copy(e.Data, e.Path, overwrite: true); }
                else if (File.Exists(e.Path)) File.Delete(e.Path);
                break;
            case EntryKind.Process:
                if (_proc.GetStartTicks(e.Pid) != e.StartTicks) break; // exited, or pid reused by another process
                if (Enum.TryParse<ProcessPriorityClass>(e.Data, out var pc)) _proc.SetPriority(e.Pid, pc);
                _proc.SetEcoQos(e.Pid, false);
                break;
            case EntryKind.PowerCap:
            {
                var parts = e.Data.Split('|');
                if (!Guid.TryParse(e.Name, out var scheme) || parts.Length != 2
                    || !int.TryParse(parts[0], out var ac) || !int.TryParse(parts[1], out var dc))
                    throw new FormatException($"Bad power cap entry: plan '{e.Name}', data '{e.Data}'");
                _power.SetCpuMaxPercent(scheme, true, ac);
                _power.SetCpuMaxPercent(scheme, false, dc);
                if (_power.GetActiveScheme() == scheme) _power.Reapply(scheme);
                break;
            }
            case EntryKind.GpuPowerLimit:
                if (!int.TryParse(e.Data, out var watts)) throw new FormatException($"Bad GPU power entry: data '{e.Data}'");
                if (_gpu is null) throw new InvalidOperationException("no GPU power control available to restore the limit");
                _gpu.SetLimitWatts(watts);
                break;
            case EntryKind.ProcessSuspend:
                if (_proc.GetStartTicks(e.Pid) == e.StartTicks) _proc.Resume(e.Pid); // same process, not a reused pid
                break;
            case EntryKind.PowerSetting:
            {
                var ids = e.Name.Split('|');
                var vals = e.Data.Split('|');
                if (ids.Length != 3 || !Guid.TryParse(ids[0], out var scheme) || !Guid.TryParse(ids[1], out var sub)
                    || !Guid.TryParse(ids[2], out var setting) || vals.Length != 2
                    || !int.TryParse(vals[0], out var ac) || !int.TryParse(vals[1], out var dc))
                    throw new FormatException($"Bad power setting entry: name '{e.Name}', data '{e.Data}'");
                _power.SetSetting(scheme, sub, setting, true, ac);
                _power.SetSetting(scheme, sub, setting, false, dc);
                if (_power.GetActiveScheme() == scheme) _power.Reapply(scheme);
                break;
            }
            case EntryKind.GpuClockLock:
                if (_gpu is null) throw new InvalidOperationException("no GPU control available to reset the clocks");
                _gpu.ResetClocks();
                break;
        }
    }
}
