using System.Net;
using System.Net.Sockets;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Diagnostics;
using Nanaininai.Core.Models;
using Nanaininai.Core.Network;

namespace Nanaininai.Services;

/// <summary>
/// Runs the SCAN → DIAGNOSE test battery. Every test explains what it found,
/// why it matters, and which automated fix (FixId) could address it.
/// </summary>
public sealed class DiagnosticService
{
    private readonly AgentContext _ctx;
    public DiagnosticService(AgentContext ctx) => _ctx = ctx;

    /// <summary>Combined result used by the TUI so diagnostics are fetched once and cached together.</summary>
    public async Task<(List<DiagnosticResult> Results, HealthReport Health)> RunAllWithHealthAsync(CancellationToken ct = default)
    {
        var results = await RunAllAsync(null, ct).ConfigureAwait(false);
        return (results, ComputeHealth(results));
    }

    public async Task<List<DiagnosticResult>> RunAllAsync(IProgress<DiagnosticResult>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DiagnosticResult>();

        void Add(DiagnosticResult r)
        {
            results.Add(r);
            progress?.Report(r);
        }

        // 1. Network adapter
        SystemInfo? sys = null;
        try
        {
            sys = await _ctx.SystemInfo.GetSystemInfoAsync(ct).ConfigureAwait(false);
            var adapter = sys.PrimaryAdapter;
            if (adapter is null)
                Add(new DiagnosticResult { Id = "adapter", Title = "Network Adapter", Status = DiagnosticStatus.Fail, Message = "No active Ethernet or Wi-Fi adapter with an IPv4 address was found.", RecommendedAction = "Connect a network cable or enable a network adapter." });
            else if (adapter.Status != "UP")
                Add(new DiagnosticResult { Id = "adapter", Title = "Network Adapter", Status = DiagnosticStatus.Fail, Message = $"Adapter '{adapter.Name}' reports {adapter.Status}.", RecommendedAction = "Check the cable, switch port and driver." });
            else
                Add(new DiagnosticResult { Id = "adapter", Title = "Network Adapter", Status = DiagnosticStatus.Pass, Message = $"{adapter.Name} is connected.", Detail = $"MAC {adapter.Mac}" });
        }
        catch (Exception ex)
        {
            var isProtocolError = ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase)
                || ex is System.Net.NetworkInformation.NetworkInformationException;

            Add(new DiagnosticResult
            {
                Id = "adapter",
                Title = "Network Adapter",
                Status = isProtocolError ? DiagnosticStatus.Warn : DiagnosticStatus.Skipped,
                Message = isProtocolError
                    ? "Adapter enumeration failed: network protocol not configured or unavailable."
                    : "Could not read adapter information.",
                Detail = isProtocolError
                    ? "Windows_networking reported the requested protocol is not configured. This usually indicates the active network stack is in an incomplete state (for example a virtualised adapter without a supported protocol stack, a NIC filter driver exposing no addresses, or an adapter that is UP but has no IP configuration stack bound). The agent falls back to the next available adapter."
                    : ex.Message
            });
        }

        if (sys is null) return results;
        var adapter1 = sys.PrimaryAdapter;

        // 2/3. Link + IPv4 configuration
        var ipCfg = new IpConfig();
        if (adapter1 is null)
        {
            Add(new DiagnosticResult { Id = "link", Title = "Link Status", Status = DiagnosticStatus.Fail, Message = "No link: the adapter has no network connection." });
            Add(new DiagnosticResult { Id = "ipv4", Title = "IPv4 Configuration", Status = DiagnosticStatus.Fail, Message = "No IPv4 address is configured." });
        }
        else
        {
            Add(new DiagnosticResult { Id = "link", Title = "Link Status", Status = DiagnosticStatus.Pass, Message = $"{adapter1.Name} link is {adapter1.Status}." });

            ipCfg = await _ctx.NetworkConfig.GetIpConfigAsync(adapter1.Name, ct).ConfigureAwait(false) ?? ipCfg;
            if (string.IsNullOrEmpty(ipCfg.Address))
                Add(new DiagnosticResult
                {
                    Id = "ipv4",
                    Title = "IPv4 Configuration",
                    Status = DiagnosticStatus.Fail,
                    Message = "This PC has no IPv4 address (APIPA/self-assigned).",
                    Detail = "Without a valid IPv4 address the PC cannot communicate on the LAN.",
                    RecommendedAction = "Configure DHCP or a static IPv4 address in the Network section."
                });
            else
            {
                var consistency = IpValidator.ValidateConsistency(ipCfg);
                var known = await CollectKnownLanIpsAsync(ct).ConfigureAwait(false);
                var validation = ipCfg.Mode == IpConfigMode.Static ? IpValidator.ValidateStatic(ipCfg, known) : new ValidationResult();
                var allErrors = consistency.Errors.Concat(validation.Errors).Distinct().ToList();
                if (allErrors.Count > 0)
                    Add(new DiagnosticResult { Id = "ipv4", Title = "IPv4 Configuration", Status = DiagnosticStatus.Fail, Message = allErrors[0], Detail = string.Join(Environment.NewLine, allErrors), RecommendedAction = "Fix the configuration in the Network section." });
                else if (ipCfg.Mode == IpConfigMode.Static)
                    Add(new DiagnosticResult { Id = "ipv4", Title = "IPv4 Configuration", Status = DiagnosticStatus.Pass, Message = $"Static {ipCfg.Address}/{ipCfg.PrefixLength}", Detail = string.Join("; ", validation.Warnings) });
                else
                    Add(new DiagnosticResult { Id = "ipv4", Title = "IPv4 Configuration", Status = DiagnosticStatus.Pass, Message = $"DHCP: {ipCfg.Address}/{ipCfg.PrefixLength}" });
            }

            // 4. Subnet
            if (IpMath.TryParseIp(ipCfg.Address ?? "", out var ip) && ipCfg.PrefixLength is >= 0 and <= 32)
            {
                var net = IpMath.ToString(IpMath.NetworkOf(ip, ipCfg.PrefixLength));
                var isPrivate = IpMath.IsPrivateRange(ip);
                Add(new DiagnosticResult
                {
                    Id = "subnet",
                    Title = "Subnet Configuration",
                    Status = isPrivate ? DiagnosticStatus.Pass : DiagnosticStatus.Warn,
                    Message = $"{net}/{ipCfg.PrefixLength}",
                    Detail = isPrivate ? "Private LAN address range." : "The address is not in a private range (10/8, 172.16/12, 192.168/16) which is unusual for a workgroup LAN.",
                    RecommendedAction = isPrivate ? null : "Use a private address range such as 192.168.10.0/24 for a school LAN."
                });
            }
            else
            {
                Add(new DiagnosticResult { Id = "subnet", Title = "Subnet Configuration", Status = DiagnosticStatus.Skipped, Message = "No usable IPv4/prefix to derive the subnet." });
            }
        }

        // 5/7. Gateway presence + ping
        if (!string.IsNullOrEmpty(ipCfg.Gateway) && !string.IsNullOrEmpty(ipCfg.Address))
        {
            var gwPing = await _ctx.Discovery.PingAsync(ipCfg.Gateway, 2, 1000, ct).ConfigureAwait(false);
            if (gwPing.AnySuccess)
                Add(new DiagnosticResult { Id = "gateway", Title = "Gateway", Status = DiagnosticStatus.Pass, Message = $"Gateway {ipCfg.Gateway} responds ({gwPing.MinMs:0} ms).", Detail = $"Gateway ping: {gwPing.Success}/{gwPing.Total} replies" });
            else
                Add(new DiagnosticResult
                {
                    Id = "gateway",
                    Title = "Gateway",
                    Status = DiagnosticStatus.Warn,
                    Message = $"Gateway {ipCfg.Gateway} does not answer ping.",
                    Detail = "The gateway may simply block ICMP while still routing traffic.",
                    RecommendedAction = "If internet/other subnets fail, check that the gateway address is correct and the router is online."
                });
        }
        else if (!string.IsNullOrEmpty(ipCfg.Address))
        {
            Add(new DiagnosticResult { Id = "gateway", Title = "Gateway", Status = DiagnosticStatus.Warn, Message = "No default gateway is configured.", Detail = "Other subnets and the internet will be unreachable.", RecommendedAction = "Set the gateway in the Network section (usually x.x.x.1)." });
        }

        // 6. DNS
        if (!string.IsNullOrEmpty(ipCfg.PreferredDns))
            Add(new DiagnosticResult { Id = "dns", Title = "DNS", Status = DiagnosticStatus.Pass, Message = $"DNS servers: {ipCfg.PreferredDns}{(ipCfg.AlternateDns is null ? "" : ", " + ipCfg.AlternateDns)}" });
        else
            Add(new DiagnosticResult { Id = "dns", Title = "DNS", Status = DiagnosticStatus.Warn, Message = "No DNS server configured.", Detail = "Hostnames cannot be resolved.", RecommendedAction = "Set the DNS server in the Network section (usually the router)." });

        // 8. LAN peer ping (any other online device on the subnet)
        if (IpMath.TryParseIp(ipCfg.Address ?? "", out var ownIp) && ipCfg.PrefixLength is > 0 and <= 32)
        {
            var net = IpMath.NetworkOf(ownIp, ipCfg.PrefixLength);
            var candidates = IpMath.Range(IpMath.FirstHost(net, ipCfg.PrefixLength), IpMath.LastHost(net, ipCfg.PrefixLength))
                .Where(v => v != ownIp)
                .Take(25)
                .Select(IpMath.ToString)
                .ToList();
            LanDevice? peer = null;
            Exception? peerError = null;
            foreach (var c in candidates)
            {
                try
                {
                    var pr = await _ctx.Discovery.PingAsync(c, 1, 500, ct).ConfigureAwait(false);
                    if (pr.AnySuccess) { peer = new LanDevice { Ip = c, LatencyMs = pr.MinMs }; break; }
                }
                catch (Exception ex)
                {
                    var isProtocolError = ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
                    if (isProtocolError)
                    {
                        peerError = ex;
                        break;
                    }
                    // Other ping failures (host unreachable, timeout) are normal when scanning — keep trying.
                }
            }
            if (peerError is not null)
                Add(new DiagnosticResult
                {
                    Id = "peer",
                    Title = "LAN Peer Ping",
                    Status = DiagnosticStatus.Warn,
                    Message = "LAN peer ping skipped: network protocol not configured.",
                    Detail = "The ping helper reported the requested protocol has not been configured. This is typically transient (for example when the active adapter's IP stack is still initialising). The diagnostic will fall back to other checks.",
                    RecommendedAction = "If this persists, verify the adapter has an IPv4 address and the ICMP Echo Request firewall rule is enabled."
                });
            else if (peer is not null)
                Add(new DiagnosticResult { Id = "peer", Title = "LAN Peer Ping", Status = DiagnosticStatus.Pass, Message = $"Reached peer {peer.Ip} ({peer.LatencyMs:0} ms).", Detail = "LAN connectivity confirmed with another device." });
            else
                Add(new DiagnosticResult { Id = "peer", Title = "LAN Peer Ping", Status = DiagnosticStatus.Warn, Message = "No other device answered ping in the first 25 addresses.", Detail = "The LAN may simply be quiet, or ICMP may be blocked between devices.", RecommendedAction = "Run a full Discovery scan for a definitive picture." });
        }

        // 9. Firewall rules + 11. File sharing — queried in parallel: two
        // sequential PowerShell startups dominated the diagnostics time.
        var firewallTask = _ctx.Firewall.GetStatusAsync(ct);
        var rulesTask = _ctx.Firewall.GetRequiredRulesAsync(ct);
        var smbTask = _ctx.Smb.GetStatusAsync(ct);
        var categoryTask = _ctx.SystemInfo.GetActiveNetworkCategoryAsync(ct);
        await Task.WhenAll(firewallTask, rulesTask, smbTask, categoryTask).ConfigureAwait(false);

        var fw = firewallTask.Result;
        var required = rulesTask.Result;
        var smb = smbTask.Result;
        var missingRules = required.Where(x => x.Rule is null || !x.Rule.Enabled).ToList();

        if (missingRules.Count == 0)
            Add(new DiagnosticResult { Id = "firewall", Title = "Windows Firewall", Status = DiagnosticStatus.Pass, Message = "All required LAN rules (ICMP Echo, SMB-In, Network Discovery) are enabled." });
        else
            Add(new DiagnosticResult
            {
                Id = "firewall",
                Title = "Windows Firewall",
                Status = DiagnosticStatus.Warn,
                Message = $"{missingRules.Count} required rule(s) disabled: {string.Join(", ", missingRules.Select(m => m.DisplayName))}.",
                Detail = "Disabled rules block ping and/or file sharing between LAN PCs.",
                RecommendedAction = "Enable the specific rules in the Firewall section (never disable whole profiles).",
                FixId = "firewall.enableRequiredRules"
            });

        if (!fw.DomainEnabled && !fw.PrivateEnabled && !fw.PublicEnabled)
            Add(new DiagnosticResult { Id = "firewall.profiles", Title = "Windows Firewall", Status = DiagnosticStatus.Warn, Message = "All firewall profiles are DISABLED.", Detail = "This is a security risk. Nanaininai enables specific rules instead of disabling profiles.", FixId = null });

        if (smb.ServerRunning && smb.ClientRunning)
            Add(new DiagnosticResult { Id = "sharing", Title = "File Sharing", Status = DiagnosticStatus.Pass, Message = "SMB client and server services are running." });
        else
            Add(new DiagnosticResult
            {
                Id = "sharing",
                Title = "File Sharing",
                Status = DiagnosticStatus.Fail,
                Message = $"SMB {(smb.ServerRunning ? "" : "server ")}{(smb.ClientRunning ? "" : "client")} not running.",
                Detail = "File sharing and drive mapping need the LanmanServer/LanmanWorkstation services.",
                RecommendedAction = "Enable File and Printer Sharing in the Sharing section.",
                FixId = "smb.enableFileSharing"
            });

        // 10. SMB connectivity to own server
        if (smb.ServerRunning && !string.IsNullOrEmpty(ipCfg.Address))
        {
            var localSmb = await _ctx.Discovery.IsSmbPortOpenAsync(ipCfg.Address, 800, ct).ConfigureAwait(false);
            Add(localSmb
                ? new DiagnosticResult { Id = "smb", Title = "SMB Connectivity", Status = DiagnosticStatus.Pass, Message = $"SMB (port 445) reachable on {ipCfg.Address}." }
                : new DiagnosticResult { Id = "smb", Title = "SMB Connectivity", Status = DiagnosticStatus.Warn, Message = $"Port 445 is not answering on this PC.", Detail = "The SMB-In firewall rule may be disabled.", FixId = "firewall.enableRequiredRules" });
        }

        // 12. Network profile
        var category = adapter1?.NetworkCategory ?? categoryTask.Result;
        if (category == "Public")
            Add(new DiagnosticResult
            {
                Id = "profile",
                Title = "Network Profile",
                Status = DiagnosticStatus.Warn,
                Message = "Current network profile: Public.",
                Detail = "File sharing may be restricted on Public networks.",
                RecommendedAction = "Switch the network profile to Private (with confirmation).",
                FixId = "network.setPrivate"
            });
        else if (!string.IsNullOrEmpty(category))
            Add(new DiagnosticResult { Id = "profile", Title = "Network Profile", Status = DiagnosticStatus.Pass, Message = $"Current network profile: {category}." });

        return results;
    }

    private async Task<List<string>> CollectKnownLanIpsAsync(CancellationToken ct)
    {
        try
        {
            var sys = await _ctx.SystemInfo.GetSystemInfoAsync(ct).ConfigureAwait(false);
            return sys.Adapters.Where(a => a.Ipv4 is not null).Select(a => a.Ipv4!).ToList();
        }
        catch (Exception)
        {
            // Defensive: if adapter enumeration throws for any reason (including protocol-not-configured),
            // return an empty set rather than propagating to the validator.
            return new List<string>();
        }
    }

    public HealthReport ComputeHealth(List<DiagnosticResult> results)
    {
        var checks = results.Select(r => (r.Title, r.Status,
            Deduction: r.Status switch
            {
                DiagnosticStatus.Warn => r.Id switch
                {
                    "gateway" => 5,
                    "dns" => 1,
                    "firewall" => 5,
                    "profile" => 3,
                    "subnet" => 2,
                    "peer" => 2,
                    _ => 3
                },
                DiagnosticStatus.Fail => 15,
                _ => 0
            },
            Reason: r.Message)).ToList();

        // Guarantee canonical ordering/deductions for the score sheet.
        var order = new[] { "adapter", "link", "ipv4", "subnet", "gateway", "dns", "gateway", "peer", "firewall", "smb", "sharing", "profile" };
        checks = checks.OrderBy(c => Array.IndexOf(order, c.Item1 switch { _ => c.Item1 })).ToList();
        return HealthScorer.Compute(checks);
    }
}
