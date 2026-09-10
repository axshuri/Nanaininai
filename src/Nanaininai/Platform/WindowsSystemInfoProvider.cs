using System.Net.NetworkInformation;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Platform;

public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    // Short-TTL runtime cache: adapter enumeration + WMI workgroup/profile
    // queries are re-run by several services within a single screen render.
    // InvalidateRuntimeCache() is called whenever configuration changes.
    private static readonly object CacheLock = new();
    private static List<AdapterInfo>? _cachedAdapters;
    private static DateTimeOffset _adaptersAt;
    private static WorkgroupInfo? _cachedWorkgroup;
    private static DateTimeOffset _workgroupAt;
    private static readonly TimeSpan RuntimeCacheTtl = TimeSpan.FromSeconds(3);

    internal static void InvalidateRuntimeCache()
    {
        lock (CacheLock)
        {
            _cachedAdapters = null;
            _cachedWorkgroup = null;
        }
    }

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var info = new SystemInfo
            {
                ComputerName = Environment.MachineName,
                CurrentUser = Environment.UserName,
                OsPlatform = OperatingSystem.IsWindows() ? "Windows" : Environment.OSVersion.Platform.ToString(),
                IsElevated = Elevation.IsAdmin()
            };

            if (OperatingSystem.IsWindows())
            {
                var os = Environment.OSVersion;
                info.Build = os.Version.ToString();
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    info.Edition = key?.GetValue("ProductName")?.ToString() ?? "";
                    var displayVersion = key?.GetValue("DisplayVersion")?.ToString();
                    info.WindowsVersion = string.IsNullOrEmpty(displayVersion) ? os.VersionString : $"Windows {displayVersion}";
                    if (info.Edition.Length == 0) info.Edition = os.VersionString;
                }
                catch (Exception ex) when (
                    ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
                {
                    // Registry access can surface protocol errors on some configurations; fall back to OS version.
                    info.Edition = os.VersionString;
                }
                catch { info.Edition = os.VersionString; }
            }



            var wg = GetCachedWorkgroup();
            info.Workgroup = wg.Workgroup;
            info.IsDomainJoined = wg.IsDomainJoined;
            info.Adapters = GetCachedAdapters();
            return info;
        }, ct);
    }

    public Task<WorkgroupInfo> GetWorkgroupInfoAsync(CancellationToken ct = default)
        => Task.Run(GetWorkgroupInfo, ct);

    public Task<string?> GetActiveNetworkCategoryAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return null;
            var adapters = GetCachedAdapters();
            return adapters.FirstOrDefault(a => a.IsPrimary && a.Status == "UP")?.NetworkCategory;
        }, ct);
    }

    internal static WorkgroupInfo GetWorkgroupInfo()
    {
        var info = new WorkgroupInfo();
        if (!OperatingSystem.IsWindows()) { info.Detail = "Workgroup information is only available on Windows."; return info; }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Domain, PartOfDomain FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                info.Workgroup = o["Domain"]?.ToString() ?? "";
                info.IsDomainJoined = o["PartOfDomain"] is bool b && b;
                break;
            }
        }
        catch (Exception ex) when (
            ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
        {
            info.Detail = "Could not query workgroup: network protocol not configured.";
        }
        catch (Exception ex)
        {
            info.Detail = $"Could not query workgroup: {ex.Message}";
        }
        return info;
    }

    internal static List<AdapterInfo> GetCachedAdapters()
    {
        lock (CacheLock)
        {
            if (_cachedAdapters is not null && DateTimeOffset.UtcNow - _adaptersAt < RuntimeCacheTtl)
                return _cachedAdapters;
        }
        var list = CollectAdapters();
        lock (CacheLock)
        {
            _cachedAdapters = list;
            _adaptersAt = DateTimeOffset.UtcNow;
        }
        return list;
    }

    private static WorkgroupInfo GetCachedWorkgroup()
    {
        lock (CacheLock)
        {
            if (_cachedWorkgroup is not null && DateTimeOffset.UtcNow - _workgroupAt < RuntimeCacheTtl)
                return _cachedWorkgroup;
        }
        var wg = GetWorkgroupInfo();
        lock (CacheLock)
        {
            _cachedWorkgroup = wg;
            _workgroupAt = DateTimeOffset.UtcNow;
        }
        return wg;
    }

    internal static List<AdapterInfo> CollectAdapters()
    {
        var list = new List<AdapterInfo>();
        if (!OperatingSystem.IsWindows()) return list;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Virtual/WFP filter adapters, tunnels and loopback are not real LAN interfaces.
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (nic.Description.Contains("WFP", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Native WiFi Filter", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var ipProps = nic.GetIPProperties();
                var v4Props = ipProps.GetIPv4Properties();
                var v4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                // Skip filter/dummy interfaces that expose no addresses.
                if (v4 is null && ipProps.UnicastAddresses.All(a => a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                    continue;

                var adapter = new AdapterInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    Status = nic.OperationalStatus == OperationalStatus.Up ? "UP" : "DOWN",
                    Mac = nic.GetPhysicalAddress().GetAddressBytes().Length > 0
                        ? string.Join('-', nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")))
                        : "00-00-00-00-00-00",
                    DhcpEnabled = v4Props?.IsDhcpEnabled ?? false,
                    IsPrimary = false
                };

                if (v4 is not null)
                {
                    adapter.Ipv4 = v4.Address.ToString();
                    adapter.PrefixLength = v4.PrefixLength > 0 ? v4.PrefixLength : null;
                }
                adapter.Ipv6 = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                    ?.Address.ToString();

                var gw = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                adapter.Gateway = gw?.Address.ToString();

                adapter.DnsServers = ipProps.DnsAddresses
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToList();

                if (adapter.Ipv4 is not null && adapter.Status == "UP")
                {
                    var cat = GetConnectionCategory(nic.Id);
                    if (cat is not null) adapter.NetworkCategory = cat;
                }

                list.Add(adapter);
            }
            catch (Exception)
            {
                // Some virtual/NIC-filters throw on property access in certain environments.
                // Silently skip them so the real adapters are still enumerated.
            }
        }

        // Prefer the first UP adapter with an IPv4 as primary.
        var primary = list.FirstOrDefault(a => a.Status == "UP" && a.Ipv4 is not null);
        if (primary is not null) primary.IsPrimary = true;
        return list;
    }

    private static string? GetConnectionCategory(string adapterId)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, InterfaceIndex FROM MSFT_NetConnectionProfile");
            searcher.Scope = new System.Management.ManagementScope(@"root\StandardCimv2");
            foreach (var o in searcher.Get())
                return MapCategory(o["NetworkCategory"]?.ToString());
        }
        catch (Exception ex) when (
            ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
        {
            // WMI/CIM queries can also surface the protocol-not-configured error on some environments.
            // Fall back to null (category unknown) rather than failing.
        }
        catch { }
        return null;
    }

    internal static string? MapCategory(object? raw) => raw?.ToString() switch
    {
        "0" => "Public",
        "1" => "Private",
        "2" => "DomainAuthenticated",
        _ => null
    };
}
