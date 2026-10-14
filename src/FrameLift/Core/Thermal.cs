namespace FrameLift.Core;

public enum ThermalLevel { Ok = 0, Warm = 1, Hot = 2, Critical = 3 }

/// <summary>Temperature thresholds (Celsius) and how far to ease off at each level. Defaults are the ones validated in ThermalGuard.</summary>
public sealed class ThermalLimits
{
    public int GpuWarm { get; set; } = 78;
    public int GpuHot { get; set; } = 83;
    public int GpuCritical { get; set; } = 87;
    public int CpuWarm { get; set; } = 85;
    public int CpuHot { get; set; } = 92;
    public int CpuCritical { get; set; } = 96;

    /// <summary>CPU "maximum processor state" in percent at Warm/Hot/Critical. Never raises the plan's own value.</summary>
    public int CapWarm { get; set; } = 80;
    public int CapHot { get; set; } = 50;
    public int CapCritical { get; set; } = 30;

    /// <summary>GPU power limit as a percent of the GPU's default limit.</summary>
    public int GpuPowerWarm { get; set; } = 100;
    public int GpuPowerHot { get; set; } = 85;
    public int GpuPowerCritical { get; set; } = 70;

    /// <summary>Degrees below a level's threshold before stepping back down, so the level does not flap.</summary>
    public int Cooldown { get; set; } = 5;
    public int SuspendMs { get; set; } = 1500;

    /// <summary>
    /// Below Critical, hold the temperature near a target with small steps instead of the Warm/Hot jumps: the highest
    /// speed the cooling can sustain, rather than swinging between full speed and a hard cap.
    /// </summary>
    public bool SmoothControl { get; set; } = true;
    public int CpuTarget { get; set; } = 88;
    public int GpuTarget { get; set; } = 80;
    /// <summary>At most one step per this many seconds: writing power settings on every poll stalled the power API.</summary>
    public int StepSeconds { get; set; } = 6;
    /// <summary>Step down at target + this, step back up at target - <see cref="StepUpMargin"/>; in between, hold.</summary>
    public int StepDownMargin { get; set; } = 2;
    public int StepUpMargin { get; set; } = 4;
    /// <summary>This far under both targets, the climb back runs at twice the step rate.</summary>
    public int FastClimbMargin { get; set; } = 12;
    /// <summary>After this long without overheating, the learned speed ceiling is raised one step.</summary>
    public int CeilingRelaxSeconds { get; set; } = 90;
    public int CapFloor { get; set; } = 40;
    public int GpuPowerStep { get; set; } = 5;
    public int GpuPowerFloor { get; set; } = 60;
    /// <summary>GPU power limit kept for the whole Boost session when Cool Boost is on (percent of default).</summary>
    public int GpuPowerBaseline { get; set; } = 90;

    public int CapFor(ThermalLevel l) => l switch
    {
        ThermalLevel.Warm => CapWarm, ThermalLevel.Hot => CapHot, ThermalLevel.Critical => CapCritical, _ => 100,
    };

    public int GpuPowerFor(ThermalLevel l) => l switch
    {
        ThermalLevel.Warm => GpuPowerWarm, ThermalLevel.Hot => GpuPowerHot, ThermalLevel.Critical => GpuPowerCritical, _ => 100,
    };
}

public sealed record TempReading(int? Cpu, int? Gpu, DateTime At)
{
    public bool Blind => Cpu is null && Gpu is null;
}

public interface ITempSource : IDisposable
{
    /// <summary>Refreshes the readings; call once per poll. May take a moment, so never call it on the UI thread.</summary>
    void Update();
    int? CpuTemp { get; }
    int? GpuTemp { get; }
}

public static class TempRange
{
    /// <summary>Rejects sensor garbage (0, 255, NaN) so a bad read never drives a decision.</summary>
    public static int? Valid(float? v) =>
        v is float f && !float.IsNaN(f) && f >= 20 && f <= 120 ? (int)Math.Round(f) : null;
}

/// <summary>Temperatures in, level out. A level only steps down once the temperature is Cooldown degrees under its threshold.</summary>
public sealed class ThermalPolicy
{
    private readonly ThermalLimits _l;
    public ThermalLevel Current { get; private set; } = ThermalLevel.Ok;

    public ThermalPolicy(ThermalLimits limits) => _l = limits;

    public ThermalLevel LevelFor(int? gpu, int? cpu, int margin)
    {
        var level = ThermalLevel.Ok;
        if (gpu is int g) level = Max(level, Classify(g, _l.GpuWarm - margin, _l.GpuHot - margin, _l.GpuCritical - margin));
        if (cpu is int c) level = Max(level, Classify(c, _l.CpuWarm - margin, _l.CpuHot - margin, _l.CpuCritical - margin));
        return level;
    }

    public ThermalLevel Update(int? gpu, int? cpu)
    {
        var next = LevelFor(gpu, cpu, 0);
        if (next < Current)
        {
            var held = LevelFor(gpu, cpu, _l.Cooldown);
            next = Max(next, (ThermalLevel)Math.Min((int)Current, (int)held));
        }
        Current = next;
        return Current;
    }

    public void Reset() => Current = ThermalLevel.Ok;

    private static ThermalLevel Classify(int t, int warm, int hot, int critical) =>
        t >= critical ? ThermalLevel.Critical : t >= hot ? ThermalLevel.Hot : t >= warm ? ThermalLevel.Warm : ThermalLevel.Ok;

    private static ThermalLevel Max(ThermalLevel a, ThermalLevel b) => a > b ? a : b;
}
