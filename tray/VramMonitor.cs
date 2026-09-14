using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FreeVram;

public sealed record ProcInfo(int Pid, string Name, long Dedicated, long Shared, bool Protected, bool Fragile)
{
    public double DedicatedMB => Dedicated / 1048576.0;
    public double SharedMB => Shared / 1048576.0;
}

public sealed record VramTotals(long Used, long Total)
{
    public double UsedMB => Used / 1048576.0;
    public double TotalMB => Total / 1048576.0;
    public int Percent => Total > 0 ? (int)Math.Round(100.0 * Used / Total) : 0;
}

/// <summary>
/// Reads per-process and per-adapter VRAM from the WDDM performance counters.
/// Vendor-agnostic and needs no admin, unlike nvidia-smi which reports N/A per process on WDDM.
/// </summary>
public static partial class VramMonitor
{
    // Killing these takes the desktop down. Never offered.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "dwm", "csrss", "wininit", "winlogon", "System", "Idle", "smss", "services", "lsass", "svchost", "fontdrvhost", "FreeVram" };

    // Survivable but disruptive. Extra confirmation.
    static readonly HashSet<string> Fragile = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ShellHost" };

    static readonly PerformanceCounterCategory ProcCat = new("GPU Process Memory");
    static readonly PerformanceCounterCategory AdapterCat = new("GPU Adapter Memory");
    static long _cachedTotal = -1;

    [GeneratedRegex(@"pid_(\d+)")]
    private static partial Regex PidRegex();

    public static List<ProcInfo> GetProcesses()
    {
        var byPid = new Dictionary<int, (long d, long s)>();
        InstanceDataCollectionCollection data;
        try { data = ProcCat.ReadCategory(); }
        catch { return new(); }

        var ded = data["Dedicated Usage"];
        var sh = data["Shared Usage"];
        if (ded is null) return new();

        foreach (InstanceData inst in ded.Values)
        {
            var m = PidRegex().Match(inst.InstanceName);
            if (!m.Success) continue;
            int pid = int.Parse(m.Groups[1].Value);
            long s = sh is not null && sh.Contains(inst.InstanceName) ? sh[inst.InstanceName].RawValue : 0;
            byPid.TryGetValue(pid, out var cur);
            byPid[pid] = (cur.d + inst.RawValue, cur.s + s);
        }

        var result = new List<ProcInfo>(byPid.Count);
        foreach (var (pid, (d, s)) in byPid)
        {
            string name;
            try { name = Process.GetProcessById(pid).ProcessName; }
            catch { name = "<exited>"; }
            result.Add(new ProcInfo(pid, name, d, s, Protected.Contains(name), Fragile.Contains(name)));
        }
        result.Sort((a, b) => b.Dedicated.CompareTo(a.Dedicated));
        return result;
    }

    public static VramTotals GetTotals()
    {
        long used = 0;
        try
        {
            var ded = AdapterCat.ReadCategory()["Dedicated Usage"];
            if (ded is not null)
                foreach (InstanceData inst in ded.Values) used += inst.RawValue;
        }
        catch { }

        if (_cachedTotal < 0) _cachedTotal = ReadTotalVram();
        return new VramTotals(used, _cachedTotal);
    }

    /// <summary>
    /// Total on-card memory. Win32_VideoController.AdapterRAM is a uint32 and caps at 4 GB,
    /// so read the driver's HardwareInformation.qwMemorySize from the display class key instead.
    /// </summary>
    static long ReadTotalVram()
    {
        long best = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return 0;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is long q && q > best) best = q;
            }
        }
        catch { }
        return best;
    }
}
