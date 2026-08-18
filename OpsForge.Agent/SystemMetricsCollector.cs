using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpsForge.Contracts;

namespace OpsForge.Agent;

internal sealed class SystemMetricsCollector : IDisposable
{
    private readonly IReadOnlyList<string> _monitoredProcesses;
    private readonly IReadOnlyList<string> _monitoredServices;
    private readonly IReadOnlyList<HealthCheckOptions> _healthChecks;
    private readonly HttpClient _probeHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _cpuPrimed;

    public SystemMetricsCollector(
        IReadOnlyList<string> monitoredProcesses,
        IReadOnlyList<string> monitoredServices,
        IReadOnlyList<HealthCheckOptions> healthChecks)
    {
        _monitoredProcesses = monitoredProcesses;
        _monitoredServices = monitoredServices;
        _healthChecks = healthChecks;
    }

    public void PrimeCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            ReadCpuTimes(out _previousIdle, out _previousKernel, out _previousUser);
            _cpuPrimed = true;
        }
    }

    public async Task<AgentHeartbeatRequest> CollectAsync(string agentId, string version, string displayName, string site, string environmentName)
    {
        return new AgentHeartbeatRequest
        {
            AgentId = agentId,
            MachineName = Environment.MachineName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName,
            Site = site,
            EnvironmentName = environmentName,
            OperatingSystem = RuntimeInformation.OSDescription,
            AgentVersion = version,
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuPercent = Math.Round(GetCpuPercent(), 1),
            MemoryUsedPercent = Math.Round(GetMemoryUsedPercent(), 1),
            UptimeSeconds = Environment.TickCount64 / 1000,
            Drives = GetDrives(),
            NetworkAdapters = GetNetworkAdapters(),
            MonitoredProcesses = GetProcesses(),
            MonitoredServices = GetServices(),
            Probes = await GetProbesAsync()
        };
    }

    public void Dispose() => _probeHttpClient.Dispose();

    private double GetCpuPercent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0.0;
        }

        ReadCpuTimes(out var idle, out var kernel, out var user);
        if (!_cpuPrimed)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _cpuPrimed = true;
            return 0.0;
        }

        var idleDelta = idle - _previousIdle;
        var kernelDelta = kernel - _previousKernel;
        var userDelta = user - _previousUser;

        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return 0.0;
        }

        var busy = total > idleDelta ? total - idleDelta : 0;
        return Math.Clamp((double)busy / total * 100.0, 0.0, 100.0);
    }

    private static double GetMemoryUsedPercent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0.0;
        }

        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return 0.0;
        }

        return status.MemoryLoad;
    }

    private static List<DriveMetric> GetDrives()
    {
        var metrics = new List<DriveMetric>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                metrics.Add(new DriveMetric
                {
                    Name = drive.Name,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    UsedPercent = Math.Round((1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100.0, 1)
                });
            }
            catch
            {
                // Removable and disconnected media can throw while enumerating.
            }
        }
        return metrics;
    }

    private static List<NetworkAdapterMetric> GetNetworkAdapters()
    {
        var metrics = new List<NetworkAdapterMetric>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            string? ipv4 = null;
            try
            {
                ipv4 = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString();
            }
            catch
            {
            }

            metrics.Add(new NetworkAdapterMetric
            {
                Name = nic.Name,
                Description = nic.Description,
                Ipv4Address = ipv4,
                SpeedBitsPerSecond = nic.Speed,
                Status = nic.OperationalStatus.ToString()
            });
        }
        return metrics;
    }

    private List<ProcessMetric> GetProcesses()
    {
        var metrics = new List<ProcessMetric>();
        foreach (var name in _monitoredProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            metrics.Add(new ProcessMetric
            {
                Name = name,
                Running = processes.Length > 0,
                ProcessId = processes.FirstOrDefault()?.Id
            });

            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        return metrics;
    }

    private List<ServiceMetric> GetServices()
    {
        var metrics = new List<ServiceMetric>();
        foreach (var name in _monitoredServices)
        {
            if (!OperatingSystem.IsWindows())
            {
                metrics.Add(new ServiceMetric
                {
                    Name = name,
                    DisplayName = name,
                    Exists = false,
                    Status = "Unsupported on this OS"
                });
                continue;
            }

            try
            {
                using var service = new ServiceController(name);
                var status = service.Status;
                metrics.Add(new ServiceMetric
                {
                    Name = service.ServiceName,
                    DisplayName = service.DisplayName,
                    Exists = true,
                    Status = status.ToString()
                });
            }
            catch (InvalidOperationException)
            {
                metrics.Add(new ServiceMetric
                {
                    Name = name,
                    DisplayName = name,
                    Exists = false,
                    Status = "Not found"
                });
            }
            catch (Exception ex)
            {
                metrics.Add(new ServiceMetric
                {
                    Name = name,
                    DisplayName = name,
                    Exists = false,
                    Status = $"Error: {ex.GetType().Name}"
                });
            }
        }
        return metrics;
    }

    private async Task<List<ProbeMetric>> GetProbesAsync()
    {
        var results = new List<ProbeMetric>();
        foreach (var check in _healthChecks)
        {
            results.Add(await RunProbeAsync(check));
        }
        return results;
    }

    private async Task<ProbeMetric> RunProbeAsync(HealthCheckOptions check)
    {
        var started = Stopwatch.StartNew();
        var checkedUtc = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(check.TimeoutMs, 250, 30000));

        try
        {
            var type = check.Type.Trim().ToUpperInvariant();
            string detail;

            if (type == "HTTP")
            {
                using var cts = new CancellationTokenSource(timeout);
                using var response = await _probeHttpClient.GetAsync(check.Target, cts.Token);
                detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return Probe(check, response.IsSuccessStatusCode, started.ElapsedMilliseconds, detail, checkedUtc);
            }

            if (type == "TCP")
            {
                if (!check.Port.HasValue || check.Port.Value is < 1 or > 65535)
                {
                    return Probe(check, false, started.ElapsedMilliseconds, "TCP probe requires a valid port.", checkedUtc);
                }

                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(timeout);
                await client.ConnectAsync(check.Target, check.Port.Value, cts.Token);
                detail = $"Connected to {check.Target}:{check.Port.Value}";
                return Probe(check, true, started.ElapsedMilliseconds, detail, checkedUtc);
            }

            if (type == "DNS")
            {
                var addresses = await Dns.GetHostAddressesAsync(check.Target).WaitAsync(timeout);
                detail = addresses.Length == 0
                    ? "Resolver returned no addresses."
                    : $"Resolved {string.Join(", ", addresses.Take(3).Select(a => a.ToString()))}";
                return Probe(check, addresses.Length > 0, started.ElapsedMilliseconds, detail, checkedUtc);
            }

            return Probe(check, false, started.ElapsedMilliseconds, $"Unsupported probe type: {check.Type}", checkedUtc);
        }
        catch (OperationCanceledException)
        {
            return Probe(check, false, started.ElapsedMilliseconds, $"Timed out after {check.TimeoutMs} ms.", checkedUtc);
        }
        catch (System.TimeoutException)
        {
            return Probe(check, false, started.ElapsedMilliseconds, $"Timed out after {check.TimeoutMs} ms.", checkedUtc);
        }
        catch (Exception ex)
        {
            return Probe(check, false, started.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}", checkedUtc);
        }
    }

    private static ProbeMetric Probe(
        HealthCheckOptions check,
        bool success,
        long latencyMs,
        string detail,
        DateTimeOffset checkedUtc) => new()
    {
        Id = string.IsNullOrWhiteSpace(check.Id) ? $"{check.Type}-{check.Target}" : check.Id,
        Type = check.Type.ToUpperInvariant(),
        Target = check.Port.HasValue ? $"{check.Target}:{check.Port.Value}" : check.Target,
        Success = success,
        LatencyMs = latencyMs,
        Detail = detail,
        CheckedUtc = checkedUtc
    };

    private static void ReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            idle = kernel = user = 0;
            return;
        }

        idle = ToUInt64(idleTime);
        kernel = ToUInt64(kernelTime);
        user = ToUInt64(userTime);
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            MemoryLoad = 0;
            TotalPhysical = 0;
            AvailablePhysical = 0;
            TotalPageFile = 0;
            AvailablePageFile = 0;
            TotalVirtual = 0;
            AvailableVirtual = 0;
            AvailableExtendedVirtual = 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
