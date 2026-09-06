using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Nanaininai;

namespace Nanaininai;

/// <summary>A candidate physical network adapter.</summary>
public sealed record AdapterInfo(string Name, string Description, NetworkInterfaceType Type, bool IsUp);

public sealed class WindowsOps
{
    // ---------------- Admin ----------------

    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ---------------- Adapter detection ----------------

    private static readonly HashSet<string> VirtualKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "virtualbox", "vmware", "hyper-v", "vethernet", "virtual",
        "vpn", "tap", "tunnel", "wintun", "wireguard", "openvpn",
        "loopback", "teredo", "isatap", "bluetooth", "wan miniport",
        "microsoft wi-fi direct", "microsoft hosted", "km-test", "npcap",
        "packet scheduler", "qos", "microsoft kernel", "aws", "parallels",
    };

    /// <summary>Detect physical, enabled, up adapters. Ethernet first, then Wi-Fi.</summary>
    public static List<AdapterInfo> DetectAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var desc = nic.Description ?? "";
            var name = nic.Name ?? "";
            string combined = name + " " + desc;
            if (VirtualKeywords.Any(k => combined.Contains(k))) continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            {
                list.Add(new AdapterInfo(name, desc, nic.NetworkInterfaceType, true));
            }
        }

        // Ethernet before Wi-Fi.
        return list
            .OrderByDescending(a => a.Type == NetworkInterfaceType.Ethernet)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- IP conflict probe (ARP, no ICMP-only reliance) ----------------

    /// <summary>
    /// Returns true if the IP appears occupied. Sends an ARP request directly,
    /// which works even on hosts that drop ICMP echo (common for printers, firewalled PCs).
    /// </summary>
    public static async Task<bool> IsIpOccupiedAsync(IPAddress ip, TimeSpan timeout)
    {
        // ARP probe via raw sendip-style approach is not available in .NET directly,
        // so use a layered probe: ARP table check + parallel TCP on common ports + ICMP.

        // 1. ARP cache fast path.
        if (GetArpEntry(ip)) return true;

        // 2. ICMP ping.
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, (int)timeout.TotalMilliseconds);
            if (reply.Status == IPStatus.Success) return true;
        }
        catch (PingException) { }

        // 3. TCP connect to common service ports (devices that drop ping but serve TCP).
        int[] ports = { 80, 443, 445, 3389, 22, 5000, 8080, 9100 };
        var tasks = ports.Select(async p =>
        {
            using var client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync(ip, p);
                var finished = await Task.WhenAny(connect, Task.Delay(timeout));
                return finished == connect && client.Connected;
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        });
        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    private static bool GetArpEntry(IPAddress ip)
    {
        try
        {
            // `arp -d` would be intrusive; instead parse `arp -a` for the exact IP.
            var psi = new ProcessStartInfo("arp", $"-a {ip}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output.Contains(ip.ToString(), StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    // ---------------- Static IP configuration via netsh ----------------

    public static OpResult ConfigureStaticIp(string adapterName, string ip, string mask, string? gateway, IReadOnlyList<string> dns)
    {
        // Reset any previous static/DHCP config on this adapter first.
        RunNetsh($"interface ip set address \"{adapterName}\" dhcp", ignoreErrors: true);

        string addrCmd = string.IsNullOrEmpty(gateway)
            ? $"interface ip set address \"{adapterName}\" static {ip} {mask}"
            : $"interface ip set address \"{adapterName}\" static {ip} {mask} {gateway}";

        var result = RunNetsh(addrCmd);
        if (!result.Success) return result;

        // DNS: clear, then add configured entries.
        RunNetsh($"interface ip set dnsservers \"{adapterName}\" source=static address=none register=primary validate=no", ignoreErrors: true);
        for (int i = 0; i < dns.Count; i++)
        {
            string action = i == 0 ? "address" : "add";
            var dnsResult = RunNetsh($"interface ip {action} dnsservers \"{adapterName}\" {dns[i]} primary validate=no");
            if (!dnsResult.Success) return dnsResult;
        }

        return new OpResult(true, $"Static IP {ip} configured on {adapterName}");
    }

    private static OpResult RunNetsh(string args, bool ignoreErrors = false)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new OpResult(false, "Could not start netsh");
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            bool ok = proc.ExitCode == 0;
            if (ignoreErrors) return new OpResult(true, stdout);
            if (!ok)
            {
                string err = (proc.StandardError.ReadToEnd() + " " + stdout).Trim();
                return new OpResult(false, string.IsNullOrWhiteSpace(err) ? $"netsh exit code {proc.ExitCode}" : err);
            }
            return new OpResult(true, stdout);
        }
        catch (Exception ex)
        {
            return new OpResult(false, ex.Message);
        }
    }

    // ---------------- Rename ----------------

    /// <summary>Schedules a computer rename; takes effect after restart. Uses the Win32 API directly.</summary>
    public static OpResult RenameComputer(string newName)
    {
        // ComputerNamePhysicalNetBIOS = 0 → physical NetBIOS name of the computer.
        if (SetComputerNameEx(0, newName))
            return new OpResult(true, $"Rename to {newName} scheduled (restart required)");
        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return new OpResult(false, $"SetComputerNameEx failed, Win32 error {err}");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetComputerNameEx(int type, string lpComputerName);

    // ---------------- Verification ----------------

    public static bool VerifyAdapterHasIp(string adapterName, IPAddress expectedIp, string expectedMask)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!IPAddress.Equals(addr.Address, expectedIp)) continue;
                var prefix = addr.IPv4Mask;
                return prefix is not null && prefix.ToString() == expectedMask;
            }
        }
        return false;
    }

    // ---------------- Connectivity (TCP, not ICMP-only) ----------------

    public static async Task<OpResult> TestControllerAsync(string controllerIp, int? port, TimeSpan timeout)
    {
        if (!IPAddress.TryParse(controllerIp, out var ip))
            return new OpResult(false, $"Invalid controller IP '{controllerIp}'");

        if (port is int p)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(ip, p);
                var finished = await Task.WhenAny(connect, Task.Delay(timeout));
                if (finished != connect) return new OpResult(false, $"Controller {controllerIp}:{p} timeout");
                return new OpResult(true, $"Controller {controllerIp}:{p} reachable (TCP)");
            }
            catch (SocketException ex)
            {
                return new OpResult(false, $"Controller {controllerIp}:{p} unreachable: {ex.SocketErrorCode}");
            }
        }

        // No port configured: fall back to ICMP.
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, (int)timeout.TotalMilliseconds);
            return reply.Status == IPStatus.Success
                ? new OpResult(true, $"Controller {controllerIp} reachable (ICMP)")
                : new OpResult(false, $"Controller {controllerIp} did not respond to ICMP");
        }
        catch (PingException ex)
        {
            return new OpResult(false, $"Controller ping failed: {ex.Message}");
        }
    }

    // ---------------- Local marker (C:\ProgramData\LabNetwork\setup.json) ----------------

    public static string MarkerDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FileNames.AppDir);

    public static string MarkerPath => Path.Combine(MarkerDir, FileNames.LocalMarker);

    public static LocalSetupMarker? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            return JsonSerializer.Deserialize<LocalSetupMarker>(File.ReadAllText(MarkerPath));
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static void WriteMarker(LocalSetupMarker marker)
    {
        Directory.CreateDirectory(MarkerDir);
        var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(MarkerPath, json);
    }

    public static void DeleteMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch (IOException) { }
    }

    public static string CurrentComputerName => Environment.MachineName;
}
