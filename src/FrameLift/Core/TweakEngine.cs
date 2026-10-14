namespace FrameLift.Core;

/// <summary>Applies and reverts catalog tweaks, journaling each change before making it.</summary>
public sealed class TweakEngine
{
    private readonly IRegistry _reg;
    private readonly IServiceRunner _svc;
    private readonly IPower _power;
    private readonly Journal _journal;
    private readonly Reverter _reverter;

    public TweakEngine(IRegistry reg, IServiceRunner svc, IPower power, Journal journal, Reverter reverter)
    {
        _reg = reg; _svc = svc; _power = power; _journal = journal; _reverter = reverter;
    }

    public bool IsApplied(string id) => _journal.HasOwner(Owner(id));

    private static string Owner(string id) => "tweak:" + id;

    /// <summary>False when applying would do nothing on this PC (every service it targets is protected or not installed).</summary>
    public bool IsApplicable(TweakDef t) => t.Changes.Any(c => c switch
    {
        ServiceChange s => !TweakCatalog.ProtectedServices.Contains(s.Service) && _reg.Read(TweakCatalog.ServicePath(s.Service), "Start").Exists,
        _ => true,
    });

    /// <summary>Returns the number of changes made (changes that were skipped, such as a missing service, are not counted).</summary>
    public int Apply(TweakDef t)
    {
        if (IsApplied(t.Id)) return 0;
        var owner = Owner(t.Id);
        int n = 0;
        foreach (var c in t.Changes)
        {
            switch (c)
            {
                case RegChange r:
                    JournalRegistry(owner, r.Path, r.Name);
                    _reg.Write(r.Path, r.Name, r.Kind, r.Data);
                    n++;
                    break;

                case ServiceChange s:
                    if (TweakCatalog.ProtectedServices.Contains(s.Service)) break;
                    var key = TweakCatalog.ServicePath(s.Service);
                    if (!_reg.Read(key, "Start").Exists) break; // not installed on this PC
                    JournalRegistry(owner, key, "Start");
                    _reg.Write(key, "Start", "DWord", s.StartType.ToString());
                    if (_svc.IsRunning(s.Service))
                    {
                        _journal.Add(new JournalEntry { Owner = owner, Kind = EntryKind.ServiceRunning, Name = s.Service });
                        _svc.Stop(s.Service);
                    }
                    n++;
                    break;

                case PowerChange p:
                    var current = _power.GetActiveScheme();
                    var target = _power.IsPerformancePlan(current) ? current : _power.EnsureScheme(p.Template);
                    if (current == target) break;
                    _journal.Add(new JournalEntry { Owner = owner, Kind = EntryKind.PowerPlan, Data = current.ToString() });
                    _power.SetActiveScheme(target);
                    n++;
                    break;
            }
        }
        return n;
    }

    public int Revert(string id) => _reverter.RevertOwner(Owner(id));

    private void JournalRegistry(string owner, string path, string name)
    {
        var cur = _reg.Read(path, name);
        _journal.Add(new JournalEntry
        {
            Owner = owner, Kind = EntryKind.Registry, Path = path, Name = name,
            Existed = cur.Exists, ValueKind = cur.Kind, Data = cur.Data,
        });
    }
}
