using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Nanaininai;

/// <summary>
/// Linux system operations layer.
/// Requires root (sudo). Uses NetworkManager (nmcli) primarily;
/// falls back to configuring via /etc/netplan YAML only if nmcli is absent.
/// </summary>
public sealed class LinuxOps
{
    // ---------- Shared types re-exposed (truly OS-independent) ----------

    // ---------- Privilege ----------

    public static bool IsRoot()
    {
        var id = TryRun("id", "-u");
        if (string.IsNullOrEmpty(id)) return false;
        return id.Trim() == "0";
    }

    private static string? TryReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllLines(path).FirstOrDefault()?.Trim();
        }
        catch (IOException) { return null; }
    }

    // ---------- Adapter detection ----------

    // Virtual/interfaces we explicitly ignore.
    private static readonly HashSet<string> IgnoreKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "docker", "veth", "virbr", "veth", "lxc", "br-", "lago", "tun", "tap",
        "vmnet", "vbox", "vmware", "veth", "bridge", "bond", "team", "bonding",
        "wmaster", "bluetooth", "carrier", "ppp", "sit", "gre", "ipip", "ip6tnl",
        "dummy", "macvlan", "ipvlan", "geneve", "vxlan", "wireguard", "awg",
        "ifb", "san", "zoet", "dummy", "fcoe", "fcoe-nop", "ib", "ib_",
    };

    /// <summary>
    /// Detect physical network interfaces that are UP.
    /// Prefer Ethernet (eth*), then Wi-Fi (wlan*).
    /// We read /sys/class/net and /sys/class/net/*/type for physicality.
    /// </summary>
    public static List<LinuxAdapterInfo> DetectAdapters()
    {
        var result = new List<LinuxAdapterInfo>();

        // Try /sys/class/net first (accurate physical indication), fallback to NetworkInterface.
        var sysNet = Path.Combine("/sys", "class", "net");
        if (Directory.Exists(sysNet))
        {
            foreach (var dir in Directory.EnumerateDirectories(sysNet))
            {
                var name = Path.GetFileName(dir);
                if (name == null || name.StartsWith('.')) continue;
                if (!IsUp(name, sysNet)) continue;
                var typeName = TryReadFirstLine(Path.Combine(dir, "type"));
                var real = !Enum.TryParse(typeName, out int type) || type != 772 /* ARPHRD_LOOPBACK */;
                var isVirtual = IgnoreKeywords.Any(k => name.Contains(k));
                if (real && !isVirtual)
                {
                    var phy = IsPhysicalPort(name, sysNet);
                    // Ethernet-like (type 1), otherwise treat as wireless-ish
                    var @enum = Enum.TryParse(typeName, out int t) && t == 1 ? "eth" : "other";
                    result.Add(new LinuxAdapterInfo(
                        name, name,
                        @enum == "eth" ? NetworkInterfaceType.Ethernet : NetworkInterfaceType.Wireless80211,
                        true, phy));
                }
            }
        }
        else
        {
            // Fallback: System.Net.NetworkInformation
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var desc = nic.Description ?? "";
                var name = nic.Name ?? "";
                string combined = name + " " + desc;
                if (IgnoreKeywords.Any(k => combined.Contains(k))) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    result.Add(new LinuxAdapterInfo(name, desc,
                        nic.NetworkInterfaceType, true));
                }
            }
        }

        // Sort: physical first, then true Ethernet before wireless.
        return result
            .OrderByDescending(a => a.IsPhysical)
            .ThenByDescending(a => a.Type == NetworkInterfaceType.Ethernet)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsUp(string name, string sysNet)
    {
        return TryReadFirstLine(Path.Combine(sysNet, name, "operstate"))?.Trim() == "up";
    }

    private static bool IsPhysicalPort(string name, string sysNet)
    {
        // Physical ports usually have a "device" symlink pointing to /sys/devices/pci...
        var deviceLink = Path.Combine(sysNet, name, "device");
        return Directory.Exists(deviceLink) ||
               (File.Exists(deviceLink) && !string.IsNullOrEmpty(TryReadFirstLine(deviceLink)));
    }

    // ---------- DHCP/static via nmcli ----------

    private static readonly string NmcliBase = "nmcli";

    public static string? ActiveConnectionProfile() =>
        TryRun(NmcliBase, "connection show --active --fields name,device")?.Split('\n')
            .FirstOrDefault(line => line.Contains("DEVICE "))?.Split(' ')
                .LastOrDefault();

    /// <summary>Sets a static IPv4 address on the target profile/device via nmcli.</summary>
    public static OpResult ConfigureStaticIp(string device, string ip, string mask,
        string? gateway, IReadOnlyList<string> dns)
    {
        // Use nmcli to configure the existing profile on this device.
        // If there is no existing profile, nmcli con add ... is fiddly; instead we
        // modify the device-level connection temporarily via "nmcli dev set" when possible.
        string profile = FindOrCreateProfileForDevice(device);
        if (profile is null)
            return new OpResult(false, $"No network connection found for device '{device}'. " +
                "Create a connection profile via nmcli or NetworkManager GUI first.");

        var commands = new List<string[]>();

        // Method: manual (static)
        commands.Add(new[] { NmcliBase, "connection", "modify", profile,
            "ipv4.method", "manual" });

        var ipWithPrefix = $"{ip}/{CidrFromMask(mask)}";
        commands.Add(new[] { NmcliBase, "connection", "modify", profile,
            "ipv4.addresses", ipWithPrefix });
        commands.Add(new[] { NmcliBase, "connection", "modify", profile,
            "ipv4.gateway", gateway ?? "" });
        commands.Add(new[] { NmcliBase, "connection", "modify", profile,
            "ipv4.dns", string.Join(",", dns) });

        foreach (var cmd in commands)
        {
            var r = Run(cmd);
            if (!r.Success)
                return new OpResult(false, $"nmcli failed: {string.Join(" ", cmd)} → {r.Message}");
        }

        // Apply
        var apply = Run(new[] { NmcliBase, "connection", "up", profile });
        if (!apply.Success)
            return new OpResult(false, $"Failed to bring connection up: {apply.Message}");

        // Wait a moment for DHCP lease removal / address stabilization.
        Thread.Sleep(1500);

        // Clear DNS servers from any other profile bound to same device only if requested empty.
        if (dns.Count == 0)
        {
            var dnsClear = Run(new[] { NmcliBase, "connection", "modify", profile,
                "ipv4.dns", "" });
            if (!dnsClear.Success)
                return dnsClear;
            Run(new[] { NmcliBase, "connection", "up", profile });
        }

        return new OpResult(true, $"Static IP {ip} configured on {device} (profile {profile})");
    }

    private static string? FindOrCreateProfileForDevice(string device)
    {
        var lines = TryRun(NmcliBase, $"connection show --active --fields name,device")
            ?.Split('\n');
        if (lines is null) return null;

        foreach (var line in lines)
        {
            if (string.Equals(line.TrimEnd(), device, StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ');
                // "NAME       UUID   TYPE  DEVICE"
                return parts.FirstOrDefault(p => !string.IsNullOrEmpty(p))?.Split(' ').FirstOrDefault();
            }
        }

        // Try to find by device column.
        foreach (var line in lines)
        {
            var cols = line.Split(' ');
            // device may be last column; skip header-ish lines
            if (cols.Length >= 2 && string.Equals(cols.Last().Trim(), device, StringComparison.OrdinalIgnoreCase))
            {
                return cols[0];
            }
        }

        // If nmcli "con show" has no active profile matching device, try nmtui-style device show.
        if (TryRun(NmcliBase, "dev status") is not string devStatus || !devStatus.Contains(device, StringComparison.Ordinal))
            return null;

        // Create a minimal profile for the device.
        var newProfile = $"LabNetwork-{device.Replace(":", "").Replace("/", "")}-{Environment.ProcessId}";
        var create = Run(new[] { NmcliBase, "con", "add", "type", "ethernet", "ifname", device,
            "con-name", newProfile, "autoconnect", "yes" });
        if (create.Success)
            return newProfile;

        return null;
    }

    // ---------- hostname via hostnamectl ----------

    public static OpResult RenameHostname(string newName)
    {
        // hostnamectl works on systemd; fallback to local hostname write if absent.
        var r = Run(new[] { "hostnamectl", "set-hostname", newName });
        if (r.Success)
            return new OpResult(true, $"Hostname set to {newName} (may require login-session restart)");

        // Fallback: write to /etc/hostname and set syscall hostname.
        try
        {
            File.WriteAllText("/etc/hostname", newName + "\n");
            SetHostname(newName);
            return new OpResult(true, $"Hostname set to {newName} (requires restart or re-login to fully propagate)");
        }
        catch (Exception ex)
        {
            return new OpResult(false, ex.Message);
        }
    }

    // ---------- Verification ----------

    public static bool VerifyAdapterIp(string device, IPAddress expectedIp, string expectedMask)
    {
        // Use `ip -4 addr show dev <device>` for precise parsing.
        var out_ = TryRun("ip", $"-4 addr show dev {device}");
        if (out_ is null) return false;

        // Look for inet line: inet 192.168.50.105/24 brd ... scope ...
        foreach (var line in out_.Split('\n'))
        {
            if (!line.Contains("inet ", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // find the first token that contains '/'
            var addrToken = parts.FirstOrDefault(p => p.Contains('/'));
            if (addrToken is null) continue;
            var idx = addrToken.IndexOf('/');
            var addr = addrToken[..idx];
            var prefixStr = addrToken[(idx + 1)..];
            if (!int.TryParse(prefixStr, out int prefix)) continue;
            if (string.Equals(addr, expectedIp.ToString(), StringComparison.Ordinal))
            {
                // Verify mask consistency.
                var maskFromPrefix = PrefixToMask(prefix);
                return string.Equals(maskFromPrefix, expectedMask, StringComparison.Ordinal);
            }
        }
        return false;
    }

    // ---------- ARP/TCP/IP conflict probe (same layered approach as Windows) ----------

    public static async Task<bool> IsIpOccupiedAsync(IPAddress ip, TimeSpan timeout)
    {
        if (GetArpEntryUnix(ip)) return true;

        // ICMP
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, (int)timeout.TotalMilliseconds);
            if (reply.Status == IPStatus.Success) return true;
        }
        catch (PingException) { }

        // TCP ports
        int[] ports = { 80, 443, 445, 3389, 22, 5000, 8080, 9100 };
        var tasks = ports.Select(async p =>
        {
            using var c = new TcpClient();
            try
            {
                var comp = c.ConnectAsync(ip, p);
                var done = await Task.WhenAny(comp, Task.Delay(timeout));
                return done == comp && c.Connected;
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        });
        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    private static bool GetArpEntryUnix(IPAddress ip)
    {
        // `ip neigh show 192.168.50.x` returns a line if the entry exists.
        var out_ = TryRun("ip", $"neigh show {ip}");
        return !string.IsNullOrEmpty(out_);
    }

    private static string? PrefixToMask(int prefix) => prefix switch
    {
        32 => "255.255.255.255",
        24 => "255.255.255.0",
        23 => "255.255.254.0",
        22 => "255.255.252.0",
        21 => "255.255.248.0",
        20 => "255.255.240.0",
        19 => "255.255.224.0",
        18 => "255.255.192.0",
        17 => "255.255.128.0",
        16 => "255.255.0.0",
        8 => "255.0.0.0",
        _ => null,
    };

    private static string CidrFromMask(string mask)
    {
        if (!IPAddress.TryParse(mask, out var m)) return "24";
        var bytes = m.GetAddressBytes();
        int bits = 0;
        foreach (var b in bytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0) bits++;
                else bits = -1; // non-contiguous
            }
            if (bits < 0) return "24";
        }
        return bits.ToString();
    }

    // ---------- Connectivity ----------

    public static async Task<OpResult> TestControllerAsync(string controllerIp, int? port, TimeSpan timeout)
    {
        if (!IPAddress.TryParse(controllerIp, out var ip))
            return new OpResult(false, $"Invalid controller IP '{controllerIp}'");

        if (port is int p)
        {
            try
            {
                using var c = new TcpClient();
                var comp = c.ConnectAsync(ip, p);
                var done = await Task.WhenAny(comp, Task.Delay(timeout));
                if (done != comp) return new OpResult(false, $"Controller {controllerIp}:{p} timeout");
                return new OpResult(true, $"Controller {controllerIp}:{p} reachable (TCP)");
            }
            catch (SocketException ex)
            {
                return new OpResult(false, $"Controller {controllerIp}:{p} unreachable: {ex.SocketErrorCode}");
            }
        }

        // ICMP fallback
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

    // ---------- Local marker (Linux: /var/lib/labnetwork/setup.json or writable fallback) ----------

    public static string MarkerPath
    {
        get
        {
            // Prefer /var/lib/labnetwork; fall back to /etc/labnetwork if writable;
            // final fallback to a temp dir only if nothing is writable (rare on admin boxes).
            var candidates = new[]
            {
                "/var/lib/labnetwork/setup.json",
                "/etc/labnetwork/setup.json",
            };
            foreach (var p in candidates)
            {
                var dir = Path.GetDirectoryName(p)!;
                try
                {
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    // touch test for write permission
                    File.WriteAllText(p, "");
                    File.Delete(p);
                    return p;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            // Last resort: XDG or /tmp, but mark as non-standard.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "labnetwork", "setup.json");
        }
    }

    public static void WriteMarker(LocalSetupMarker marker)
    {
        var path = MarkerPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

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

    public static void DeleteMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch (IOException) { }
    }

    public static string CurrentHostname =>
        TryRun("hostname")?.Trim() ?? Environment.MachineName;

    // ---------- Helpers ----------

    private static string? TryRun(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(args[0], string.Join(" ", args.Skip(1)))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(4000);
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                return null; // treat as unsupported / missing tool
            return stdout + stderr;
        }
        catch (Exception) { return null; }
    }

    private static OpResult Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(args[0], string.Join(" ", args.Skip(1)))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new OpResult(false, "Could not start command");
            string out_ = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(8000);
            if (proc.ExitCode != 0)
                return new OpResult(false, string.IsNullOrWhiteSpace(err)
                    ? $"{string.Join(" ", args)} (exit {proc.ExitCode})" : err);
            return new OpResult(true, string.IsNullOrWhiteSpace(out_) ? string.Join(" ", args) : out_.Trim());
        }
        catch (Exception ex)
        {
            return new OpResult(false, ex.Message);
        }
    }


    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int sethostname(byte[] name, int len);

    public static void SetHostname(string hostname)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(hostname);
        // Allow null terminator but ensure within 64 bytes
        if (bytes.Length > 63) bytes = bytes[..63];
        sethostname(bytes, bytes.Length);
    }

    // ---------- Linux adapter info ----------

    public sealed record LinuxAdapterInfo(
        string Name,
        string Description,
        NetworkInterfaceType Type,
        bool IsUp,
        bool IsPhysical = true);

    /// <summary>Convert LinuxAdapterInfo to shared AdapterInfo for the UI selector.</summary>
    public static AdapterInfo ToAdapterInfo(LinuxAdapterInfo a) =>
        new(a.Name, a.Description, a.Type, a.IsUp);
}
