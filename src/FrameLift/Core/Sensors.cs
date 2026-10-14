using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace FrameLift.Core;

/// <summary>
/// LibreHardwareMonitor: CPU package (or hottest core) and GPU core temperature. Only one <see cref="Computer"/> may be
/// open per process; opening a second corrupted its state in ThermalGuard, so <see cref="TempReader"/> owns the only one.
/// </summary>
public sealed class LhmTempSource : ITempSource
{
    private readonly Computer _pc = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    public int? CpuTemp { get; private set; }
    public int? GpuTemp { get; private set; }

    public LhmTempSource() => _pc.Open();

    public void Update()
    {
        float? pkg = null, coreMax = null, gpu = null;
        foreach (var hw in _pc.Hardware)
        {
            hw.Update();
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value is not float v) continue;
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) pkg = v;
                    else coreMax = Math.Max(coreMax ?? v, v);
                }
                else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || gpu is null) gpu = Math.Max(gpu ?? v, v);
                }
            }
        }
        CpuTemp = TempRange.Valid(pkg ?? coreMax);
        GpuTemp = TempRange.Valid(gpu);
    }

    public void Dispose() => _pc.Close();
}

/// <summary>GPU-only fallback for when LibreHardwareMonitor cannot open (no driver access).</summary>
public sealed class NvidiaSmiTempSource : ITempSource
{
    private readonly string _smi;
    public int? CpuTemp => null;
    public int? GpuTemp { get; private set; }

    public NvidiaSmiTempSource(string smiPath) => _smi = smiPath;

    public void Update()
    {
        GpuTemp = null;
        var r = NvidiaSmi.Run(_smi, "--query-gpu=temperature.gpu --format=csv,noheader,nounits", 3000);
        if (r is not null && int.TryParse(r.Value.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            GpuTemp = TempRange.Valid(t);
    }

    public void Dispose() { }
}

/// <summary>Uses LibreHardwareMonitor when it opens, and fills a missing GPU reading from nvidia-smi.</summary>
public sealed class CompositeTempSource : ITempSource
{
    private readonly LhmTempSource? _lhm;
    private readonly NvidiaSmiTempSource? _smi;
    public string LhmError { get; } = "";
    public int? CpuTemp { get; private set; }
    public int? GpuTemp { get; private set; }

    public CompositeTempSource()
    {
        try { _lhm = new LhmTempSource(); }
        catch (Exception e) { LhmError = e.Message; }
        var smi = NvidiaSmi.Find();
        if (smi is not null) _smi = new NvidiaSmiTempSource(smi);
        if (_lhm is null && _smi is null)
            throw new InvalidOperationException("no temperature source could be opened" + (LhmError.Length > 0 ? ": " + LhmError : ""));
    }

    public void Update()
    {
        _lhm?.Update();
        CpuTemp = _lhm?.CpuTemp;
        GpuTemp = _lhm?.GpuTemp;
        if (GpuTemp is null && _smi is not null) { _smi.Update(); GpuTemp = _smi.GpuTemp; }
    }

    public void Dispose() { _lhm?.Dispose(); _smi?.Dispose(); }
}

/// <summary>
/// Owns the temperature source and keeps the latest reading. <see cref="Poll"/> is thread-safe and never throws:
/// a failure is kept in <see cref="Error"/>, and after repeated failures the reader stops trying.
/// </summary>
public sealed class TempReader : IDisposable
{
    public const int MaxConsecutiveFailures = 5;

    private readonly Func<ITempSource> _factory;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private ITempSource? _src;
    private int _failures;
    private bool _dead;

    /// <param name="timeout">How long opening or reading the sensors may take before the reader gives up for good.</param>
    public TempReader(Func<ITempSource>? factory = null, Func<DateTime>? now = null, TimeSpan? timeout = null)
    {
        _factory = factory ?? (() => new CompositeTempSource());
        _now = now ?? (() => DateTime.UtcNow);
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        Latest = new TempReading(null, null, DateTime.MinValue);
    }

    public TempReading Latest { get; private set; }

    /// <summary>Why there is no reading (null while sensors work), in plain words.</summary>
    public string? Error { get; private set; }

    public TempReading Poll()
    {
        lock (_gate)
        {
            if (_dead) return Latest;
            try
            {
                // The sensor driver can hang; a stuck read must not freeze the caller or hold the lock forever.
                var work = Task.Run(() =>
                {
                    _src ??= _factory();
                    _src.Update();
                });
                if (!work.Wait(_timeout))
                {
                    _dead = true; // the stuck call may still be using the source, so it is never touched again
                    Latest = new TempReading(null, null, _now());
                    Error = $"Temperature sensors did not respond within {_timeout.TotalSeconds:0} seconds.";
                    return Latest;
                }
                Latest = new TempReading(_src!.CpuTemp, _src.GpuTemp, _now());
                _failures = 0;
                Error = Latest.Blind ? "The temperature sensors returned no readings." : null;
            }
            catch (Exception ex)
            {
                var e = ex is AggregateException ae && ae.InnerException is not null ? ae.InnerException : ex;
                Latest = new TempReading(null, null, _now());
                Error = "Temperature sensors unavailable: " + e.Message;
                if (++_failures >= MaxConsecutiveFailures || _src is null) _dead = true; // never retry an open that failed
            }
            return Latest;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _dead = true;
            try { _src?.Dispose(); } catch (Exception e) { Error = "Closing the temperature sensors failed: " + e.Message; }
            _src = null;
        }
    }
}
