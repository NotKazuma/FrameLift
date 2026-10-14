namespace FrameLift.Core;

public sealed record StartupItem(string Name, string Command, string Source, bool Enabled, string ApprovedPath, string ApprovedName);

/// <summary>
/// Lists startup apps and flips them on/off with the same StartupApproved flag Task Manager uses.
/// Nothing is deleted, so turning an app back on restores it exactly. When a journal is given, each
/// change is recorded so "Undo all changes" brings the apps back too.
/// </summary>
public sealed class StartupManager
{
    private readonly IRegistry _reg;
    private readonly Journal? _journal;
    private readonly Reverter? _reverter;

    public StartupManager(IRegistry reg, Journal? journal = null, Reverter? reverter = null)
    {
        _reg = reg; _journal = journal; _reverter = reverter;
    }

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static readonly (string hive, string label)[] Hives = { ("HKCU", "Current user"), ("HKLM", "All users") };

    private static string Owner(StartupItem i) => "startup:" + i.ApprovedPath + "|" + i.ApprovedName;

    public IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        foreach (var (hive, label) in Hives)
        {
            var runPath = hive + "\\" + Run;
            var approvedPath = hive + "\\" + Approved;
            foreach (var name in _reg.ValueNames(runPath))
            {
                var cmd = _reg.Read(runPath, name).Data;
                items.Add(new StartupItem(name, cmd, label, IsEnabled(approvedPath, name), approvedPath, name));
            }
        }
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Disabled when the first flag byte has bit 0 set (0x03, 0x07), which is what Task Manager writes.</summary>
    public bool IsEnabled(string approvedPath, string name)
    {
        var s = _reg.Read(approvedPath, name);
        if (!s.Exists || s.Kind != "Binary") return true;
        var bytes = Convert.FromBase64String(s.Data);
        return bytes.Length == 0 || (bytes[0] & 1) == 0;
    }

    public void SetEnabled(StartupItem item, bool enabled)
    {
        if (enabled && _journal is not null && _journal.HasOwner(Owner(item)))
        {
            _reverter!.RevertOwner(Owner(item)); // puts the flag back exactly as it was
            return;
        }
        if (!enabled && _journal is not null && !_journal.HasOwner(Owner(item)))
        {
            var cur = _reg.Read(item.ApprovedPath, item.ApprovedName);
            _journal.Add(new JournalEntry
            {
                Owner = Owner(item), Kind = EntryKind.Registry, Path = item.ApprovedPath, Name = item.ApprovedName,
                Existed = cur.Exists, ValueKind = cur.Kind, Data = cur.Data,
            });
        }
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        _reg.Write(item.ApprovedPath, item.ApprovedName, "Binary", Convert.ToBase64String(data));
    }
}
