using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DemoApp;

public sealed class SystemMetricsSampler : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<int, ProcessCpuSample> _processCpuSamples = [];
    private Task? _worker;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _diskCounter;
    private PerformanceCounter? _diskBytesCounter;
    private GpuPdhSampler? _gpuSampler;
    private NetworkInterface[] _networkAdapters = [];
    private NetworkSample? _previousNetworkSample;
    private int _processTick;
    private int _temperatureTick;
    private int _networkRefreshTick;
    private int _driveTick;
    private List<ProcessRow> _lastProcesses = [];
    private int _lastProcessCount;
    private double? _temperature;
    private DriveSnapshot _lastDrive;
    private string _gpuName = string.Empty;
    private bool _gpuAvailable;
    private bool _disposed;

    public event Action<SystemSnapshot>? SnapshotReady;
    public event Action<string>? SamplingFailed;

    public bool Paused { get; set; }
    public bool CompactMode { get; set; }

    public void RequestProcessRefresh() => Interlocked.Exchange(ref _processTick, 15);

    public static string OperatingSystemLabel
    {
        get
        {
            var version = Environment.OSVersion.Version;
            var product = version.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{product} · {RuntimeInformation.OSArchitecture} · Build {version.Build}";
        }
    }

    public void Start()
    {
        if (_worker is not null || _disposed) return;
        _worker = Task.Run(() => RunAsync(_cancellation.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            InitializeCounters();
            _gpuName = ReadGpuName();
            PrimeCounters();
            PrimeProcessSamples();
            await Task.Delay(700, token).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (!token.IsCancellationRequested)
            {
                if (!Paused)
                {
                    try
                    {
                        SnapshotReady?.Invoke(Sample());
                    }
                    catch (Exception exception)
                    {
                        SamplingFailed?.Invoke(exception.GetBaseException().Message);
                    }
                }

                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SamplingFailed?.Invoke(exception.GetBaseException().Message);
        }
    }

    private void InitializeCounters()
    {
        _cpuCounter = TryCreateCounter("Processor", "% Processor Time", "_Total");
        _diskCounter = TryCreateCounter("PhysicalDisk", "% Disk Time", "_Total");
        _diskBytesCounter = TryCreateCounter("PhysicalDisk", "Disk Bytes/sec", "_Total");
        _networkAdapters = GetActiveNetworkAdapters();
        _lastDrive = GetSystemDrive();

        _gpuSampler = GpuPdhSampler.TryCreate();
        _gpuAvailable = _gpuSampler is not null;

    }

    private static PerformanceCounter? TryCreateCounter(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, true); }
        catch { return null; }
    }

    private void PrimeCounters()
    {
        TryNextValue(_cpuCounter);
        TryNextValue(_diskCounter);
        TryNextValue(_diskBytesCounter);
    }

    private SystemSnapshot Sample()
    {
        var stopwatch = Stopwatch.StartNew();
        var compactMode = CompactMode;
        var memory = GetMemoryStatus();
        var drive = compactMode ? default : GetCachedSystemDrive();
        var (download, upload) = compactMode ? (0d, 0d) : GetNetworkRates();
        var cpu = ClampPercent(TryNextValue(_cpuCounter));
        var diskActivity = compactMode ? 0 : ClampPercent(TryNextValue(_diskCounter));
        var diskBytes = compactMode ? 0 : Math.Max(0, TryNextValue(_diskBytesCounter));
        var gpu = _gpuSampler?.NextValue() ?? 0;

        var processesUpdated = false;
        // Enumerating every process is intentionally decoupled from the live
        // gauges. An immediate first result followed by a 30-second cadence
        // keeps the ranking useful without
        // forcing hundreds of process queries and a ListView rebuild every
        // telemetry tick.
        if (!compactMode && (_lastProcesses.Count == 0 || Interlocked.Increment(ref _processTick) >= 15))
        {
            SampleProcesses();
            Interlocked.Exchange(ref _processTick, 0);
            processesUpdated = true;
        }

        if (_temperatureTick++ % 15 == 0)
        {
            _temperature = ReadTemperature();
        }

        stopwatch.Stop();
        return new SystemSnapshot
        {
            Timestamp = DateTime.Now,
            Cpu = cpu,
            MemoryPercent = memory.Load,
            MemoryUsedGb = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824.0,
            MemoryTotalGb = memory.TotalPhysical / 1073741824.0,
            DiskUsedPercent = drive.UsedPercent,
            DiskFreeGb = drive.FreeBytes / 1073741824.0,
            DiskActivity = diskActivity,
            DiskBytesPerSecond = diskBytes,
            Gpu = gpu,
            GpuAvailable = _gpuAvailable,
            GpuName = _gpuName,
            TemperatureCelsius = _temperature,
            DownloadBytesPerSecond = download,
            UploadBytesPerSecond = upload,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Processes = processesUpdated ? _lastProcesses : null,
            ProcessCount = _lastProcessCount,
            SampleDuration = stopwatch.Elapsed
        };
    }

    private static double TryNextValue(PerformanceCounter? counter)
    {
        if (counter is null) return 0;
        try { return counter.NextValue(); }
        catch { return 0; }
    }

    private static double ClampPercent(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);

    private (double Download, double Upload) GetNetworkRates()
    {
        try
        {
            if (_networkAdapters.Length == 0 || ++_networkRefreshTick >= 15)
            {
                _networkRefreshTick = 0;
                _networkAdapters = GetActiveNetworkAdapters();
            }

            long received = 0;
            long sent = 0;
            foreach (var adapter in _networkAdapters)
            {
                try
                {
                    var statistics = adapter.GetIPv4Statistics();
                    received += Math.Max(0, statistics.BytesReceived);
                    sent += Math.Max(0, statistics.BytesSent);
                }
                catch { }
            }

            var current = new NetworkSample(received, sent, Stopwatch.GetTimestamp());
            var previous = _previousNetworkSample;
            _previousNetworkSample = current;
            if (previous is null) return (0, 0);

            var elapsed = (current.Timestamp - previous.Value.Timestamp) / (double)Stopwatch.Frequency;
            if (elapsed <= 0) return (0, 0);
            return (
                Math.Max(0, current.ReceivedBytes - previous.Value.ReceivedBytes) / elapsed,
                Math.Max(0, current.SentBytes - previous.Value.SentBytes) / elapsed);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static NetworkInterface[] GetActiveNetworkAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                                  adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void SampleProcesses()
    {
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var next = new Dictionary<int, ProcessCpuSample>();
            var rows = new List<(int Id, string Name, long Memory, double Cpu)>();
            foreach (var process in QueryProcesses())
            {
                var current = new ProcessCpuSample(process.CpuTime, timestamp);
                next[process.Id] = current;
                var usage = 0.0;
                if (_processCpuSamples.TryGetValue(process.Id, out var previous))
                {
                    var elapsed = (current.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
                    if (elapsed > 0)
                    {
                        usage = (current.CpuTime - previous.CpuTime).TotalSeconds /
                            elapsed / Environment.ProcessorCount * 100.0;
                    }
                }

                rows.Add((process.Id, process.Name, process.WorkingSet, ClampPercent(usage)));
            }

            _processCpuSamples.Clear();
            foreach (var pair in next) _processCpuSamples[pair.Key] = pair.Value;
            _lastProcessCount = next.Count;
            _lastProcesses = rows
                .OrderByDescending(item => item.Cpu)
                .ThenByDescending(item => item.Memory)
                .Take(7)
                .Select(item => ProcessRow.Create(item.Id, item.Name, item.Memory, item.Cpu))
                .ToList();
        }
        catch
        {
        }
    }

    private void PrimeProcessSamples()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var next = new Dictionary<int, ProcessCpuSample>();
        try
        {
            foreach (var process in QueryProcesses())
            {
                next[process.Id] = new ProcessCpuSample(process.CpuTime, timestamp);
            }
        }
        catch { }

        _processCpuSamples.Clear();
        foreach (var pair in next) _processCpuSamples[pair.Key] = pair.Value;
        _lastProcessCount = next.Count;
    }

    private static IReadOnlyList<NativeProcessSnapshot> QueryProcesses()
    {
        const int systemProcessInformation = 5;
        const int statusInfoLengthMismatch = unchecked((int)0xC0000004);
        var size = 1024 * 1024;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(systemProcessInformation, buffer, size, out var required);
                if (status == statusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, required + 64 * 1024);
                    continue;
                }
                if (status < 0) return [];

                var results = new List<NativeProcessSnapshot>();
                var offset = 0;
                while (offset >= 0 && offset + 152 <= size)
                {
                    var entry = IntPtr.Add(buffer, offset);
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var processIdValue = Marshal.ReadInt64(entry, 80);
                    if (processIdValue is > 0 and <= int.MaxValue)
                    {
                        var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
                        var namePointer = Marshal.ReadIntPtr(entry, 64);
                        var name = nameLength > 0 && namePointer != IntPtr.Zero
                            ? Marshal.PtrToStringUni(namePointer, nameLength / 2)
                            : null;
                        var processId = (int)processIdValue;
                        name = string.IsNullOrWhiteSpace(name)
                            ? processId == 4 ? "System" : $"PID {processId}"
                            : name;

                        var userTicks = Math.Max(0, Marshal.ReadInt64(entry, 40));
                        var kernelTicks = Math.Max(0, Marshal.ReadInt64(entry, 48));
                        var workingSet = Math.Max(0, Marshal.ReadInt64(entry, 144));
                        results.Add(new NativeProcessSnapshot(
                            processId,
                            name,
                            workingSet,
                            TimeSpan.FromTicks(userTicks + kernelTicks)));
                    }

                    if (nextOffset <= 0) break;
                    offset = checked(offset + nextOffset);
                }
                return results;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    private static MemoryStatus GetMemoryStatus()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? status
            : new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
    }

    private static DriveSnapshot GetSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var used = drive.TotalSize > 0
                ? (drive.TotalSize - drive.AvailableFreeSpace) * 100.0 / drive.TotalSize
                : 0;
            return new DriveSnapshot(used, drive.AvailableFreeSpace);
        }
        catch
        {
            return new DriveSnapshot(0, 0);
        }
    }

    private DriveSnapshot GetCachedSystemDrive()
    {
        // Capacity changes slowly; the activity and throughput counters still
        // update every telemetry tick, while the filesystem query runs at 10 s.
        if (_driveTick++ % 5 == 0) _lastDrive = GetSystemDrive();
        return _lastDrive;
    }

    private static string ReadGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var collection = searcher.Get();
            return collection.Cast<ManagementObject>()
                .Select(item => item["Name"]?.ToString())
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static double? ReadTemperature()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            var query = new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var collection = searcher.Get();
            var values = collection.Cast<ManagementObject>()
                .Select(item => item["CurrentTemperature"])
                .Where(value => value is not null)
                .Select(value => Convert.ToDouble(value) / 10.0 - 273.15)
                .Where(value => value is > 5 and < 115)
                .ToArray();
            return values.Length == 0 ? null : values.Max();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cpuCounter?.Dispose();
        _diskCounter?.Dispose();
        _diskBytesCounter?.Dispose();
        _gpuSampler?.Dispose();
        _gpuSampler = null;
        _cancellation.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int informationClass,
        IntPtr information,
        int informationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private readonly record struct DriveSnapshot(double UsedPercent, long FreeBytes);
    private readonly record struct NetworkSample(long ReceivedBytes, long SentBytes, long Timestamp);
    private readonly record struct ProcessCpuSample(TimeSpan CpuTime, long Timestamp);
    private readonly record struct NativeProcessSnapshot(int Id, string Name, long WorkingSet, TimeSpan CpuTime);
}

/// <summary>
/// Uses one native PDH wildcard query for all 3D GPU-engine instances. This is
/// substantially cheaper than asking PerformanceCounterCategory to materialize
/// every counter and every process-scoped instance on each telemetry tick.
/// </summary>
internal sealed class GpuPdhSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private IntPtr _query;
    private IntPtr _counter;
    private IntPtr _buffer;
    private uint _bufferSize;
    private bool _disposed;

    private GpuPdhSampler()
    {
        ThrowIfFailed(PdhOpenQuery(null, UIntPtr.Zero, out _query));
        try
        {
            ThrowIfFailed(PdhAddEnglishCounter(
                _query,
                @"\GPU Engine(*)\Utilization Percentage",
                UIntPtr.Zero,
                out _counter));
            ThrowIfFailed(PdhCollectQueryData(_query));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static GpuPdhSampler? TryCreate()
    {
        try { return new GpuPdhSampler(); }
        catch { return null; }
    }

    public double NextValue()
    {
        if (_disposed || _query == IntPtr.Zero || _counter == IntPtr.Zero) return 0;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return 0;

            uint itemCount = 0;
            if (_buffer == IntPtr.Zero)
            {
                uint required = 0;
                var probe = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref required, out itemCount, IntPtr.Zero);
                if (probe != PdhMoreData || required == 0) return 0;
                ResizeBuffer(required);
            }

            var size = _bufferSize;
            var status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out itemCount, _buffer);
            if (status == PdhMoreData && size > _bufferSize)
            {
                ResizeBuffer(size);
                size = _bufferSize;
                status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out itemCount, _buffer);
            }
            if (status != 0) return 0;

            var stride = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var total = 0.0;
            for (var index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(IntPtr.Add(_buffer, checked(index * stride)));
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Value)) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (name?.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) == true)
                {
                    total += Math.Max(0, item.Value.Value);
                }
            }
            return Math.Clamp(total, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private void ResizeBuffer(uint size)
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(checked((int)size));
        _bufferSize = size;
    }

    private static void ThrowIfFailed(uint status)
    {
        if (status != 0) throw new InvalidOperationException($"PDH query failed with 0x{status:X8}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

public sealed class SystemSnapshot
{
    public DateTime Timestamp { get; init; }
    public double Cpu { get; init; }
    public double MemoryPercent { get; init; }
    public double MemoryUsedGb { get; init; }
    public double MemoryTotalGb { get; init; }
    public double DiskUsedPercent { get; init; }
    public double DiskFreeGb { get; init; }
    public double DiskActivity { get; init; }
    public double DiskBytesPerSecond { get; init; }
    public double Gpu { get; init; }
    public bool GpuAvailable { get; init; }
    public string GpuName { get; init; } = string.Empty;
    public double? TemperatureCelsius { get; init; }
    public double DownloadBytesPerSecond { get; init; }
    public double UploadBytesPerSecond { get; init; }
    public TimeSpan Uptime { get; init; }
    public IReadOnlyList<ProcessRow>? Processes { get; init; }
    public int ProcessCount { get; init; }
    public TimeSpan SampleDuration { get; init; }
}

public sealed class ProcessRow
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(10, 132, 255),
        Color.FromRgb(94, 92, 230),
        Color.FromRgb(48, 205, 160),
        Color.FromRgb(255, 159, 10),
        Color.FromRgb(191, 90, 242),
        Color.FromRgb(100, 210, 255)
    ];

    public required string Name { get; init; }
    public required int ProcessId { get; init; }
    public required bool CanEnd { get; init; }
    public required string Initial { get; init; }
    public required string MemoryText { get; init; }
    public required string CpuText { get; init; }
    public required Brush AccentBrush { get; init; }

    public static ProcessRow Create(int processId, string name, long memory, double cpu)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name) & int.MaxValue;
        var brush = new SolidColorBrush(Palette[hash % Palette.Length]);
        brush.Freeze();
        return new ProcessRow
        {
            Name = name,
            ProcessId = processId,
            CanEnd = processId > 4 && processId != Environment.ProcessId,
            Initial = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant(),
            MemoryText = $"{memory / 1048576.0:0} MB",
            CpuText = $"{cpu:0.0}%",
            AccentBrush = brush
        };
    }
}
