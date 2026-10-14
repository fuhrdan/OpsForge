using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpsForge.Contracts;
using System.Runtime.Versioning;

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
    private long _previousLinuxIdle;
    private long _previousLinuxTotal;
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
            ReadWindowsCpuTimes(out _previousIdle, out _previousKernel, out _previousUser);
            _cpuPrimed = true;
            return;
        }

        if (OperatingSystem.IsLinux() && TryReadLinuxCpuTimes(out _previousLinuxIdle, out _previousLinuxTotal))
        {
            _cpuPrimed = true;
        }
    }

    public async Task<AgentHeartbeatRequest> CollectAsync(
        string agentId,
        string version,
        string displayName,
        string site,
        string environmentName)
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
            UptimeSeconds = GetUptimeSeconds(),
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
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsCpuPercent();
        }

        if (OperatingSystem.IsLinux())
        {
            return GetLinuxCpuPercent();
        }

        return 0.0;
    }

    private double GetWindowsCpuPercent()
    {
        ReadWindowsCpuTimes(out var idle, out var kernel, out var user);
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

    private double GetLinuxCpuPercent()
    {
        if (!TryReadLinuxCpuTimes(out var idle, out var total))
        {
            return 0.0;
        }

        if (!_cpuPrimed)
        {
            _previousLinuxIdle = idle;
            _previousLinuxTotal = total;
            _cpuPrimed = true;
            return 0.0;
        }

        var idleDelta = idle - _previousLinuxIdle;
        var totalDelta = total - _previousLinuxTotal;

        _previousLinuxIdle = idle;
        _previousLinuxTotal = total;

        if (totalDelta <= 0)
        {
            return 0.0;
        }

        var busyDelta = Math.Max(0, totalDelta - idleDelta);
        return Math.Clamp((double)busyDelta / totalDelta * 100.0, 0.0, 100.0);
    }

    private static bool TryReadLinuxCpuTimes(out long idle, out long total)
    {
        idle = 0;
        total = 0;

        try
        {
            var line = File.ReadLines("/proc/stat")
                .FirstOrDefault(value => value.StartsWith("cpu ", StringComparison.Ordinal));

            if (line is null)
            {
                return false;
            }

            var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(value => long.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();

            if (values.Length < 4)
            {
                return false;
            }

            // Linux /proc/stat:
            // user nice system idle iowait irq softirq steal guest guest_nice
            var idleTicks = values[3];
            var ioWaitTicks = values.Length > 4 ? values[4] : 0;

            idle = idleTicks + ioWaitTicks;
            total = values.Sum();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double GetMemoryUsedPercent()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(ref status))
            {
                return 0.0;
            }

            return status.MemoryLoad;
        }

        if (OperatingSystem.IsLinux())
        {
            return GetLinuxMemoryUsedPercent();
        }

        return 0.0;
    }

    private static double GetLinuxMemoryUsedPercent()
    {
        try
        {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator];
                var number = line[(separator + 1)..]
                    .Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                {
                    values[key] = kb;
                }
            }

            if (!values.TryGetValue("MemTotal", out var totalKb) || totalKb <= 0)
            {
                return 0.0;
            }

            // MemAvailable is the kernel's best estimate of memory that can be
            // made available without swapping. It is more meaningful than
            // MemFree for operational monitoring.
            if (!values.TryGetValue("MemAvailable", out var availableKb))
            {
                values.TryGetValue("MemFree", out availableKb);
                availableKb += values.GetValueOrDefault("Buffers");
                availableKb += values.GetValueOrDefault("Cached");
            }

            return Math.Clamp((1.0 - (double)availableKb / totalKb) * 100.0, 0.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private static long GetUptimeSeconds()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var first = File.ReadAllText("/proc/uptime")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    return Math.Max(0, (long)seconds);
                }
            }
            catch
            {
            }
        }

        return Math.Max(0, Environment.TickCount64 / 1000);
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
                    UsedPercent = Math.Round(
                        (1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100.0,
                        1)
                });
            }
            catch
            {
                // Removable, pseudo and disconnected media can throw while enumerating.
            }
        }

        return metrics;
    }

    private static List<NetworkAdapterMetric> GetNetworkAdapters()
    {
        var metrics = new List<NetworkAdapterMetric>();
        NetworkInterface[] adapters;

        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return metrics;
        }
        catch (UnauthorizedAccessException)
        {
            return metrics;
        }

        foreach (var nic in adapters)
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
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                processes = Array.Empty<Process>();
            }

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
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxServices();
        }

        if (OperatingSystem.IsWindows())
        {
            return GetWindowsServices();
        }

        return _monitoredServices.Select(name => new ServiceMetric
        {
            Name = name,
            DisplayName = name,
            Exists = false,
            Status = "Unsupported on this OS"
        }).ToList();
    }

    [SupportedOSPlatform("windows")]
    private List<ServiceMetric> GetWindowsServices()
    {
        var metrics = new List<ServiceMetric>();

        foreach (var name in _monitoredServices)
        {
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

    private List<ServiceMetric> GetLinuxServices()
    {
        var metrics = new List<ServiceMetric>();

        foreach (var configuredName in _monitoredServices)
        {
            var name = NormalizeSystemdUnit(configuredName);

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.StartInfo.ArgumentList.Add("show");
                process.StartInfo.ArgumentList.Add(name);
                process.StartInfo.ArgumentList.Add("--property=LoadState");
                process.StartInfo.ArgumentList.Add("--property=ActiveState");
                process.StartInfo.ArgumentList.Add("--property=SubState");
                process.StartInfo.ArgumentList.Add("--no-pager");

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(2500))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    metrics.Add(new ServiceMetric
                    {
                        Name = name,
                        DisplayName = name,
                        Exists = false,
                        Status = "systemctl timeout"
                    });
                    continue;
                }

                var state = ParseSystemdProperties(output);
                var loadState = state.GetValueOrDefault("LoadState", "unknown");
                var activeState = state.GetValueOrDefault("ActiveState", "unknown");
                var subState = state.GetValueOrDefault("SubState", "unknown");
                var exists = !string.Equals(loadState, "not-found", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(loadState, "unknown", StringComparison.OrdinalIgnoreCase);

                var status = exists
                    ? MapLinuxServiceStatus(activeState, subState)
                    : "Not found";

                if (process.ExitCode != 0 && !exists && !string.IsNullOrWhiteSpace(error))
                {
                    status = "Not found";
                }

                metrics.Add(new ServiceMetric
                {
                    Name = name,
                    DisplayName = name,
                    Exists = exists,
                    Status = status
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                metrics.Add(new ServiceMetric
                {
                    Name = name,
                    DisplayName = name,
                    Exists = false,
                    Status = "systemctl unavailable"
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

    private static string NormalizeSystemdUnit(string name)
    {
        var value = name.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Contains('.', StringComparison.Ordinal)
            ? value
            : $"{value}.service";
    }

    private static Dictionary<string, string> ParseSystemdProperties(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string MapLinuxServiceStatus(string activeState, string subState)
    {
        if (string.Equals(activeState, "active", StringComparison.OrdinalIgnoreCase))
        {
            return "Running";
        }

        if (string.Equals(activeState, "activating", StringComparison.OrdinalIgnoreCase))
        {
            return "StartPending";
        }

        if (string.Equals(activeState, "deactivating", StringComparison.OrdinalIgnoreCase))
        {
            return "StopPending";
        }

        if (string.Equals(activeState, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Failed";
        }

        if (string.Equals(activeState, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(subState, "dead", StringComparison.OrdinalIgnoreCase)
                ? "Stopped"
                : $"Inactive ({subState})";
        }

        return string.IsNullOrWhiteSpace(subState)
            ? activeState
            : $"{activeState} ({subState})";
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

    private static void ReadWindowsCpuTimes(out ulong idle, out ulong kernel, out ulong user)
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
