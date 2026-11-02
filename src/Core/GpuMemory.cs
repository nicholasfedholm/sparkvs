using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace SparkVS.Core;

public struct GpuReading
{
    public long DedicatedBytes;
    public long SharedBytes;
    public long BudgetBytes;                // DXGI only, 0 otherwise
    public string Source;                   // "dxgi" or "pdh"
    public string Adapter;
    public long? PdhDedicatedBytes;         // read in the same tick for the side by side check, null if PDH is unavailable
    public long? PdhSharedBytes;
    public long? PdhLocalBytes;             // PDH "Local Usage": resident VRAM, the quantity DXGI CurrentUsage reports
    public long? PdhNonLocalBytes;          // PDH "Non Local Usage": resident shared system memory
}

/// <summary>
/// VRAM of this process. Primary source is DXGI IDXGIAdapter3.QueryVideoMemoryInfo, which reports the calling
/// process usage per adapter and segment group (local = dedicated VRAM, non-local = shared system memory).
/// PDH "GPU Process Memory" is read in the same tick, used as the fallback and as the cross-check.
/// Which counter pairs with which: DXGI CurrentUsage is resident memory, and PDH "Local Usage" is the same quantity through a different API
/// (both read dxgkrnl per-process bookkeeping). PDH "Dedicated Usage" is committed memory: it includes allocations the driver demoted out of VRAM
/// and equals Task Manager's Dedicated GPU memory, so it stays flat when idle textures are demoted while DXGI and PDH Local fall.
/// Create and use on one thread. COM here is plain P/Invoke and ComImport, no extra packages.
/// </summary>
public sealed class GpuMemoryReader : IDisposable
{
    const int DxgiLocal = 0, DxgiNonLocal = 1;
    const uint SoftwareAdapterFlag = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct VideoMemoryInfo { public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters();
        [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IntPtr adapter);
    }

    // Vtable order must match dxgi1_4.h exactly; slots we never call are placeholders.
    [ComImport, Guid("645967a4-1392-4310-a798-8053ce3e93fd"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter3
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs();
        [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
        [PreserveSig] int GetDesc2();
        [PreserveSig] int RegisterHardwareContentProtectionTeardownStatusEvent();
        [PreserveSig] int UnregisterHardwareContentProtectionTeardownStatus();
        [PreserveSig] int QueryVideoMemoryInfo(uint nodeIndex, int segmentGroup, out VideoMemoryInfo info);
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    readonly List<(IDXGIAdapter3 Adapter, string Name)> adapters = new();
    readonly Pdh? pdh;

    public string UnavailableReason { get; private set; } = "";
    public bool DxgiAvailable => adapters.Count > 0 && DxgiReadsEnabled;
    public bool PdhAvailable => pdh != null;
    public int AdapterCount => adapters.Count;

    public List<string> AdapterNames()
    {
        var names = new List<string>();
        foreach (var a in adapters) names.Add(a.Name);
        return names;
    }

    /// <summary>False when DXGI reads are switched off (GpuMemoryDxgi=false). Adapters are still listed for the hardware inventory but never queried for usage.</summary>
    public bool DxgiReadsEnabled { get; }

    public GpuMemoryReader(int pid, bool dxgiReads = true)
    {
        DxgiReadsEnabled = dxgiReads;
        var reasons = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("not windows");
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int hr = CreateDXGIFactory1(ref iid, out IntPtr factoryPtr);
            if (hr < 0) throw new InvalidOperationException("CreateDXGIFactory1 0x" + hr.ToString("X8"));
            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            var iid3 = new Guid("645967a4-1392-4310-a798-8053ce3e93fd");
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out IntPtr adapterPtr) < 0) break;
                try
                {
                    if (Marshal.QueryInterface(adapterPtr, in iid3, out IntPtr p3) < 0) continue;
                    var adapter = (IDXGIAdapter3)Marshal.GetObjectForIUnknown(p3);
                    Marshal.Release(p3);
                    if (adapter.GetDesc1(out var desc) < 0) continue;
                    if ((desc.Flags & SoftwareAdapterFlag) != 0) continue;   // Microsoft Basic Render Driver
                    adapters.Add((adapter, desc.Description));
                }
                finally { Marshal.Release(adapterPtr); }
            }
            if (adapters.Count == 0) reasons.Add("DXGI found no hardware adapter with IDXGIAdapter3");
        }
        catch (Exception ex) { reasons.Add("DXGI: " + ex.GetType().Name + ": " + ex.Message); }

        pdh = Pdh.TryCreate(pid, out string pdhReason);
        if (pdh == null) reasons.Add("PDH: " + pdhReason);
        UnavailableReason = string.Join("; ", reasons);
    }

    /// <summary>Reads once. False when neither source produced a number.</summary>
    public bool TryRead(out GpuReading reading)
    {
        reading = new GpuReading { Source = "none", Adapter = "" };
        long? pdhDedicated = null, pdhShared = null;
        if (pdh != null && pdh.TryRead(out long pd, out long ps, out long? pl, out long? pn)) { pdhDedicated = pd; pdhShared = ps; reading.PdhLocalBytes = pl; reading.PdhNonLocalBytes = pn; }
        reading.PdhDedicatedBytes = pdhDedicated;
        reading.PdhSharedBytes = pdhShared;

        // The adapter this process is actually using is the one with the most local usage.
        long bestLocal = -1;
        if (DxgiReadsEnabled)
        foreach (var (adapter, name) in adapters)
        {
            if (adapter.QueryVideoMemoryInfo(0, DxgiLocal, out var local) < 0) continue;
            if (adapter.QueryVideoMemoryInfo(0, DxgiNonLocal, out var shared) < 0) continue;
            if ((long)local.CurrentUsage > bestLocal)
            {
                bestLocal = (long)local.CurrentUsage;
                reading.DedicatedBytes = (long)local.CurrentUsage;
                reading.SharedBytes = (long)shared.CurrentUsage;
                reading.BudgetBytes = (long)local.Budget;
                reading.Source = "dxgi";
                reading.Adapter = name;
            }
        }

        // DXGI answers zero when the driver does not attribute usage to this process; PDH is the fallback then.
        if ((reading.Source != "dxgi" || reading.DedicatedBytes == 0) && pdhDedicated is long fallback && fallback > 0)
        {
            reading.DedicatedBytes = fallback;
            reading.SharedBytes = pdhShared ?? 0;
            reading.BudgetBytes = 0;
            reading.Source = "pdh";
            reading.Adapter = "all adapters (PDH process total)";
        }
        return reading.Source != "none";
    }

    public void Dispose()
    {
        foreach (var (adapter, _) in adapters) Marshal.ReleaseComObject(adapter);
        adapters.Clear();
        pdh?.Dispose();
    }

    /// <summary>PDH "GPU Process Memory" dedicated, shared, local and non-local usage for one process, summed over adapters.</summary>
    sealed class Pdh : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FmtItem { public IntPtr Name; public FmtValue Value; }

        [StructLayout(LayoutKind.Explicit)]
        struct FmtValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double DoubleValue; }

        const uint FmtDouble = 0x200, MoreData = 0x800007D2, NoData = 0x800007D5;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

        IntPtr query, dedicated, shared, local, nonLocal, buffer;
        uint capacity;

        public static Pdh? TryCreate(int pid, out string reason)
        {
            reason = "";
            var p = new Pdh();
            try
            {
                if (PdhOpenQueryW(null, UIntPtr.Zero, out p.query) != 0) { reason = "PdhOpenQuery failed"; return null; }
                PdhAddEnglishCounterW(p.query, $"\\GPU Process Memory(pid_{pid}_*)\\Dedicated Usage", UIntPtr.Zero, out p.dedicated);
                PdhAddEnglishCounterW(p.query, $"\\GPU Process Memory(pid_{pid}_*)\\Shared Usage", UIntPtr.Zero, out p.shared);
                PdhAddEnglishCounterW(p.query, $"\\GPU Process Memory(pid_{pid}_*)\\Local Usage", UIntPtr.Zero, out p.local);
                PdhAddEnglishCounterW(p.query, $"\\GPU Process Memory(pid_{pid}_*)\\Non Local Usage", UIntPtr.Zero, out p.nonLocal);
                if (p.dedicated == IntPtr.Zero) { reason = "counter GPU Process Memory unavailable"; p.Dispose(); return null; }
                uint r = PdhCollectQueryData(p.query);
                if (r != 0 && r != NoData) { reason = "PdhCollectQueryData 0x" + r.ToString("X"); p.Dispose(); return null; }
                return p;
            }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; p.Dispose(); return null; }
        }

        public bool TryRead(out long dedicatedBytes, out long sharedBytes, out long? localBytes, out long? nonLocalBytes)
        {
            dedicatedBytes = 0; sharedBytes = 0; localBytes = null; nonLocalBytes = null;
            if (query == IntPtr.Zero) return false;
            if (PdhCollectQueryData(query) != 0) return false;
            if (!Sum(dedicated, out double d)) return false;
            dedicatedBytes = (long)d;
            if (Sum(shared, out double s)) sharedBytes = (long)s;
            if (Sum(local, out double l)) localBytes = (long)l;
            if (Sum(nonLocal, out double n)) nonLocalBytes = (long)n;
            return true;
        }

        bool Sum(IntPtr counter, out double sum)
        {
            sum = 0;
            if (counter == IntPtr.Zero) return false;
            uint size = capacity;
            uint r = PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, out uint n, buffer);
            if (r == MoreData)
            {
                size = 0;
                r = PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, out n, IntPtr.Zero);
                if (r != MoreData || size == 0 || size > int.MaxValue) return false;
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
                capacity = size;
                r = PdhGetFormattedCounterArrayW(counter, FmtDouble, ref size, out n, buffer);
            }
            if (r != 0 || n == 0) return false;
            int itemSize = Marshal.SizeOf<FmtItem>();
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                var item = Marshal.PtrToStructure<FmtItem>(IntPtr.Add(buffer, i * itemSize));
                if ((item.Value.Status == 0 || item.Value.Status == 1) && double.IsFinite(item.Value.DoubleValue))
                {
                    sum += Math.Max(0, item.Value.DoubleValue);
                    valid++;
                }
            }
            return valid > 0;
        }

        public void Dispose()
        {
            try { if (query != IntPtr.Zero) PdhCloseQuery(query); } catch { }
            query = IntPtr.Zero;
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
        }
    }
}

/// <summary>
/// The VRAM cross-check gate. DXGI CurrentUsage (resident) is paired with PDH "Local Usage" (resident, other API) over the settled window,
/// the last 60 rows. PDH "Dedicated Usage" is committed memory and is reported for information only, never as the gate.
/// </summary>
public static class VramGate
{
    public const int SettledRows = 60;
    public const double BarPercent = 10;

    public static JsonObject Compute(IReadOnlyList<GpuReading> rows)
    {
        var window = rows.Skip(Math.Max(0, rows.Count - SettledRows)).ToList();
        var node = new JsonObject
        {
            ["pair"] = "dxgi_local_current_usage vs pdh_local_usage",
            ["settled_rows"] = window.Count,
            ["bar_pct"] = BarPercent,
        };
        var paired = window.Where(r => r.Source == "dxgi" && r.PdhLocalBytes != null).ToList();
        node["paired_rows"] = paired.Count;
        string? reason = null;
        if (window.Count == 0) reason = "no rows";
        else if (!window.Any(r => r.Source == "dxgi")) reason = "DXGI was not the source in the settled window (GpuMemoryDxgi off or DXGI unavailable)";
        else if (paired.Count == 0) reason = "PDH Local Usage unavailable in the settled window";
        var withDed = window.Where(r => r.PdhDedicatedBytes != null).ToList();
        if (withDed.Count > 0)
        {
            double medDed = Median(withDed.Select(r => (double)r.PdhDedicatedBytes!.Value));
            node["median_pdh_dedicated_bytes"] = (long)medDed;
            var both = withDed.Where(r => r.PdhLocalBytes != null).ToList();
            if (both.Count > 0) node["demoted_or_nonresident_bytes"] = (long)Median(both.Select(r => (double)(r.PdhDedicatedBytes!.Value - r.PdhLocalBytes!.Value)));
            var dx = withDed.Where(r => r.Source == "dxgi").ToList();
            if (dx.Count > 0 && medDed > 0)
                node["dxgi_vs_pdh_dedicated_pct"] = Math.Round((Median(dx.Select(r => (double)r.DedicatedBytes)) - medDed) / medDed * 100.0, 2);
        }
        if (reason != null)
        {
            node["pass"] = null;
            node["reason"] = reason;
            return node;
        }
        double medDxgi = Median(paired.Select(r => (double)r.DedicatedBytes));
        double medLocal = Median(paired.Select(r => (double)r.PdhLocalBytes!.Value));
        node["median_dxgi_bytes"] = (long)medDxgi;
        node["median_pdh_local_bytes"] = (long)medLocal;
        if (medLocal <= 0)
        {
            node["pass"] = null;
            node["reason"] = "median PDH Local Usage is 0";
            return node;
        }
        double diff = (medDxgi - medLocal) / medLocal * 100.0;
        node["diff_pct"] = Math.Round(diff, 3);
        node["pass"] = Math.Abs(diff) <= BarPercent;
        return node;
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>One or two lines for summary.md and the headline label note.</summary>
    public static string Describe(JsonObject? gate)
    {
        if (gate == null) return "VRAM gate not evaluated.";
        string pair = gate["pair"]?.GetValue<string>() ?? "";
        string result = gate["pass"] == null
            ? "not evaluated (" + gate["reason"]?.GetValue<string>() + ")"
            : (gate["pass"]!.GetValue<bool>() ? "PASS" : "FAIL") + ", diff " + gate["diff_pct"]?.GetValue<double>().ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " percent against a " + gate["bar_pct"] + " percent bar over " + gate["paired_rows"] + " settled rows";
        return $"VRAM gate ({pair}): {result}. DXGI CurrentUsage and PDH Local Usage are resident memory; PDH Dedicated Usage (Task Manager) is committed memory and also counts allocations the driver demoted, so it is not the pair.";
    }
}
