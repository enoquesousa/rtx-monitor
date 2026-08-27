using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RtxMonitor.Managed;

namespace RtxMonitor.Service;

internal sealed class WindowsGpuReader : IWindowsGpuReader
{
    internal static readonly string[] EngineTypes =
        ["3D", "Copy", "VideoDecode", "VideoEncode", "OFA", "VR"];
    private readonly IWindowsAdapterSource adapters;
    private readonly IPdhGpuCounterSource counters;

    internal WindowsGpuReader(IWindowsAdapterSource adapters, IPdhGpuCounterSource counters)
    {
        this.adapters = adapters;
        this.counters = counters;
    }

    public WindowsTelemetrySnapshot Read(DiscoveredGpu gpu, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        BoardIdentity? board = gpu.Evidence.Board;
        if (board is null || !board.HasPciIdentity)
        {
            return Unavailable(gpu.Gpu, "identity_unavailable", "A identidade PCI NVML não está disponível.");
        }

        IReadOnlyList<WindowsAdapterIdentity> enumeratedAdapters;
        try
        {
            enumeratedAdapters = adapters.Enumerate();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Unavailable(gpu.Gpu, "identity_unavailable", $"Falha ao enumerar DXGI: {error.Message}");
        }

        WindowsAdapterIdentity? adapter = MatchAdapter(board, enumeratedAdapters);
        if (adapter is null)
        {
            return Unavailable(gpu.Gpu, "identity_mismatch", "Nenhum adaptador DXGI corresponde ao PCI NVML.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PdhGpuSample sample = counters.Read(adapter.Luid, cancellationToken);
            WindowsTelemetryMetric local = Metric(sample.LocalMemoryBytes, "bytes");
            WindowsTelemetryMetric nonLocal = Metric(sample.NonLocalMemoryBytes, "bytes");
            WindowsEngineTelemetry[] engines = EngineTypes.Select(type =>
            {
                sample.EngineUtilization.TryGetValue(type, out double? value);
                return new WindowsEngineTelemetry(type, Metric(value, "percent", inactiveWhenZero: true));
            }).ToArray();
            bool partial = local.State != "available" || nonLocal.State != "available" ||
                engines.Any(engine => engine.Utilization.State == "counter_unavailable");
            return new WindowsTelemetrySnapshot(
                1,
                DateTimeOffset.UtcNow,
                partial ? "partial" : "available",
                partial ? "Um ou mais counters não produziram valor nesta coleta." : null,
                gpu.Gpu,
                adapter,
                local,
                nonLocal,
                engines);
        }
        catch (PdhException error)
        {
            WindowsTelemetryMetric unavailable = new("counters_unavailable", null, "bytes", error.Message);
            return new WindowsTelemetrySnapshot(
                1, DateTimeOffset.UtcNow, "counters_unavailable", error.Message, gpu.Gpu, adapter,
                unavailable, unavailable with { Unit = "bytes" }, UnavailableEngines("counters_unavailable", error.Message));
        }
    }

    internal static WindowsAdapterIdentity? MatchAdapter(
        BoardIdentity board,
        IReadOnlyList<WindowsAdapterIdentity> adapters)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(adapters);
        WindowsAdapterIdentity[] matches = adapters.Where(adapter =>
            adapter.VendorId == (board.PciVendorId & 0xffffU) &&
            adapter.DeviceId == (board.PciDeviceId & 0xffffU) &&
            adapter.SubsystemVendorId == (board.PciSubsystemVendorId & 0xffffU) &&
            adapter.SubsystemDeviceId == (board.PciSubsystemDeviceId & 0xffffU)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static WindowsTelemetryMetric Metric(
        double? value, string unit, bool inactiveWhenZero = false) => value is null
        ? new("counter_unavailable", null, unit)
        : new(inactiveWhenZero && value == 0 ? "inactive" : "available", value, unit);

    private static WindowsTelemetrySnapshot Unavailable(GpuInfo gpu, string state, string error)
    {
        var bytes = new WindowsTelemetryMetric(state, null, "bytes", error);
        return new WindowsTelemetrySnapshot(
            1, DateTimeOffset.UtcNow, state, error, gpu, null, bytes, bytes,
            UnavailableEngines(state, error));
    }

    private static WindowsEngineTelemetry[] UnavailableEngines(string state, string error) =>
        EngineTypes.Select(type => new WindowsEngineTelemetry(
            type, new WindowsTelemetryMetric(state, null, "percent", error))).ToArray();
}

internal interface IWindowsAdapterSource
{
    IReadOnlyList<WindowsAdapterIdentity> Enumerate();
}

internal interface IPdhGpuCounterSource
{
    PdhGpuSample Read(long luid, CancellationToken cancellationToken);
}

internal sealed record PdhGpuSample(
    double? LocalMemoryBytes,
    double? NonLocalMemoryBytes,
    IReadOnlyDictionary<string, double?> EngineUtilization);

internal sealed record PdhEngineReading(string EngineType, string PhysicalId, double? Value);

internal sealed class PdhException : Exception
{
    internal PdhException(string message) : base(message) { }
}

internal sealed class PdhGpuCounterSource : IPdhGpuCounterSource
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtLarge = 0x00000400;

    public PdhGpuSample Read(long luid, CancellationToken cancellationToken)
    {
        string luidToken = FormattableString.Invariant(
            $"luid_0x{unchecked((uint)(luid >> 32)):x8}_0x{unchecked((uint)luid):x8}");
        string[] localInstances = EnumerateInstances("GPU Local Adapter Memory")
            .Where(name => name.Contains(luidToken, StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] nonLocalInstances = EnumerateInstances("GPU Non Local Adapter Memory")
            .Where(name => name.Contains(luidToken, StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] engineInstances = EnumerateInstances("GPU Engine")
            .Where(name => name.Contains(luidToken, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (localInstances.Length == 0 && nonLocalInstances.Length == 0 && engineInstances.Length == 0)
        {
            throw new PdhException($"Nenhuma instância PDH encontrada para {luidToken}.");
        }

        Check(PdhOpenQuery(null, 0, out nint query), "PdhOpenQuery");
        try
        {
            var local = AddCounters(query, "GPU Local Adapter Memory", localInstances, "Local Usage");
            var nonLocal = AddCounters(query, "GPU Non Local Adapter Memory", nonLocalInstances, "Non Local Usage");
            var engines = AddCounters(query, "GPU Engine", engineInstances, "Utilization Percentage");
            Check(PdhCollectQueryData(query), "primeira coleta PDH");
            if (engines.Count > 0)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
                cancellationToken.ThrowIfCancellationRequested();
                Check(PdhCollectQueryData(query), "segunda coleta PDH");
            }

            return new PdhGpuSample(
                SumLarge(local),
                SumLarge(nonLocal),
                AggregateEngineReadings(ReadEngineCounters(engines)));
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }

    private static Dictionary<string, nint> AddCounters(
        nint query, string objectName, IEnumerable<string> instances, string counterName)
    {
        var counters = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        foreach (string instance in instances)
        {
            string path = $"\\{objectName}({instance})\\{counterName}";
            uint status = PdhAddEnglishCounter(query, path, 0, out nint counter);
            if (status == ErrorSuccess)
            {
                counters[instance] = counter;
            }
        }
        return counters;
    }

    private static double? SumLarge(IReadOnlyDictionary<string, nint> counters)
    {
        double total = 0;
        bool available = false;
        foreach (nint counter in counters.Values)
        {
            uint status = PdhGetFormattedCounterValue(counter, PdhFmtLarge, out _, out PdhFmtValue value);
            if (status == ErrorSuccess && value.CStatus == ErrorSuccess)
            {
                total += value.LargeValue;
                available = true;
            }
        }
        return available ? total : null;
    }

    private static IReadOnlyList<PdhEngineReading> ReadEngineCounters(
        IReadOnlyDictionary<string, nint> counters)
    {
        var readings = new List<PdhEngineReading>();
        foreach ((string instance, nint counter) in counters)
        {
            uint status = PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out PdhFmtValue value);
            string type = Segment(instance, "engtype_") ?? "Unknown";
            string physicalId = Segment(instance, "phys_") ?? instance;
            readings.Add(new PdhEngineReading(
                type,
                physicalId,
                status == ErrorSuccess && value.CStatus == ErrorSuccess
                    ? Math.Max(0.0, value.DoubleValue)
                    : null));
        }
        return readings;
    }

    internal static IReadOnlyDictionary<string, double?> AggregateEngineReadings(
        IReadOnlyList<PdhEngineReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var physical = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (PdhEngineReading reading in readings)
        {
            string key = reading.EngineType + "\0" + reading.PhysicalId;
            if (reading.Value is not double value)
            {
                physical.TryAdd(key, null);
                continue;
            }
            physical[key] = Math.Min(100.0, (physical.GetValueOrDefault(key) ?? 0) + value);
        }

        return physical.GroupBy(item => item.Key.Split('\0')[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Any(item => item.Value is not null)
                    ? group.Where(item => item.Value is not null).Max(item => item.Value)
                    : null,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? Segment(string value, string prefix)
    {
        int start = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += prefix.Length;
        int end = value.IndexOf('_', start);
        return end < 0 ? value[start..] : value[start..end];
    }

    private static string[] EnumerateInstances(string objectName)
    {
        uint counterLength = 0;
        uint instanceLength = 0;
        uint status = PdhEnumObjectItems(null, null, objectName, null, ref counterLength, null,
            ref instanceLength, 100, 0);
        if (status != PdhMoreData && status != ErrorSuccess)
        {
            throw new PdhException($"PdhEnumObjectItems({objectName}) falhou: 0x{status:x8}.");
        }
        var counters = new StringBuilder(checked((int)Math.Max(counterLength, 1)));
        var instances = new StringBuilder(checked((int)Math.Max(instanceLength, 1)));
        Check(PdhEnumObjectItems(null, null, objectName, counters, ref counterLength, instances,
            ref instanceLength, 100, 0), $"PdhEnumObjectItems({objectName})");
        return instances.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void Check(uint status, string operation)
    {
        if (status != ErrorSuccess) throw new PdhException($"{operation} falhou: 0x{status:x8}.");
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtValue
    {
        [FieldOffset(0)] internal uint CStatus;
        [FieldOffset(8)] internal double DoubleValue;
        [FieldOffset(8)] internal long LargeValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtValue value);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhEnumObjectItems(string? dataSource, string? machineName, string objectName,
        StringBuilder? counterList, ref uint counterListLength, StringBuilder? instanceList,
        ref uint instanceListLength, uint detailLevel, uint flags);
}

internal sealed class DxgiAdapterSource : IWindowsAdapterSource
{
    public IReadOnlyList<WindowsAdapterIdentity> Enumerate()
    {
        Guid iid = typeof(IDxgiFactory1).GUID;
        int result = CreateDXGIFactory1(ref iid, out IDxgiFactory1 factory);
        Marshal.ThrowExceptionForHR(result);
        var adapters = new List<WindowsAdapterIdentity>();
        try
        {
            for (uint index = 0; ; index++)
            {
                result = factory.EnumAdapters1(index, out IDxgiAdapter1 adapter);
                if (result == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    adapter.GetDesc1(out DxgiAdapterDesc1 desc);
                    adapters.Add(new WindowsAdapterIdentity(
                        ((long)desc.AdapterLuid.HighPart << 32) | desc.AdapterLuid.LowPart,
                        desc.Description,
                        desc.VendorId,
                        desc.DeviceId,
                        desc.SubSysId & 0xffffU,
                        desc.SubSysId >> 16));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return adapters;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint VendorId, DeviceId, SubSysId, Revision;
        internal nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal Luid AdapterLuid;
        internal uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint LowPart; internal int HighPart; }

    [ComImport, Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiFactory1
    {
        [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters(uint adapter, out nint result); [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation(); [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint adapter, out IDxgiAdapter1 result);
        [PreserveSig] int IsCurrent();
    }
    [ComImport, Guid("29038F61-3839-4626-91FD-086879011A05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiAdapter1
    {
        [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs(); [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        void GetDesc1(out DxgiAdapterDesc1 desc);
    }
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IDxgiFactory1 factory);
}
