using Nanaininai.Core.Models;
using Nanaininai.Core.Network;

namespace Nanaininai.Services;

/// <summary>Implements the one-action "CHECK LAN" report.</summary>
public sealed class LanReportService
{
    private readonly AgentContext _ctx;
    public LanReportService(AgentContext ctx) => _ctx = ctx;

    public sealed class LanReport
    {
        public string NetworkCidr { get; set; } = "";
        public string? Gateway { get; set; }
        public List<LanDevice> Devices { get; set; } = new();
        public List<string> Issues { get; set; } = new();
        public List<string> Conflicts { get; set; } = new();
        public HealthReport? Health { get; set; }

        public string Render()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("LAN REPORT");
            sb.AppendLine($"Network: {NetworkCidr}");
            sb.AppendLine($"Gateway: {Gateway ?? "-"}");
            var online = Devices.Count(d => d.Online);
            sb.AppendLine();
            sb.AppendLine($"Devices: {online} online, {Devices.Count - online} offline");
            sb.AppendLine();
            sb.AppendLine("IP          HOSTNAME          STATUS   LATENCY  SMB");
            foreach (var d in Devices)
                sb.AppendLine($"{d.Ip,-12} {(d.Hostname ?? "-"),-18} {(d.Online ? "ONLINE" : "OFFLINE"),-8} {(d.LatencyMs is null ? "-" : Math.Round(d.LatencyMs.Value) + "ms"),-8} {(d.SmbAvailable ? "yes" : "no")}");
            if (Issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Issues:");
                foreach (var i in Issues) sb.AppendLine($"  [!] {i}");
            }
            if (Conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Possible IP conflicts:");
                foreach (var c in Conflicts) sb.AppendLine($"  [!!] {c}");
            }
            if (Health is not null)
            {
                sb.AppendLine();
                sb.AppendLine(Health.Render());
            }
            return sb.ToString();
        }
    }

    public async Task<LanReport> CheckAsync(string? networkCidrOverride = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var report = new LanReport();
        var sys = await _ctx.SystemInfo.GetSystemInfoAsync(ct).ConfigureAwait(false);
        var adapter = sys.PrimaryAdapter;
        var ipCfg = adapter is null ? null : await _ctx.NetworkConfig.GetIpConfigAsync(adapter.Name, ct).ConfigureAwait(false);

        // 1-2. Detect local network / subnet.
        string cidr;
        if (!string.IsNullOrEmpty(networkCidrOverride))
        {
            cidr = networkCidrOverride;
        }
        else if (!string.IsNullOrEmpty(ipCfg?.Address) && ipCfg.PrefixLength is > 0 and <= 32 &&
                 IpMath.TryParseIp(ipCfg.Address, out var own))
        {
            var net = IpMath.NetworkOf(own, ipCfg.PrefixLength);
            cidr = $"{IpMath.ToString(net)}/{ipCfg.PrefixLength}";
        }
        else
        {
            report.Issues.Add("No local IPv4 configuration - cannot determine subnet. Configure the network first or pass an explicit subnet.");
            return report;
        }
        report.NetworkCidr = cidr;

        progress?.Report($"Scanning {cidr} ...");
        report.Devices = await _ctx.Discovery.ScanAsync(cidr, 1000, null, ct).ConfigureAwait(false);
        report.Gateway = ipCfg?.Gateway;

        progress?.Report("Testing SMB on found devices ...");
        foreach (var dev in report.Devices.Where(d => d.Online))
        {
            dev.SmbAvailable = await _ctx.Discovery.IsSmbPortOpenAsync(dev.Ip).ConfigureAwait(false);

            // Firewall-related finding: TCP open but ICMP was lost during scan.
            if (dev.LatencyMs is null && dev.SmbAvailable)
                report.Issues.Add($"{(dev.Hostname ?? dev.Ip)}: reachable over TCP but did not answer ICMP (echo requests likely blocked by its firewall).");
            if (dev.Online && !dev.SmbAvailable && dev.Hostname != Environment.MachineName)
                report.Issues.Add($"{(dev.Hostname ?? dev.Ip)}: SMB unavailable (sharing disabled or firewalled).");
        }

        // Detect obvious IP conflicts: same IP appearing with different MACs is impossible from a single
        // ARP view, so instead flag duplicates of this PC's own address and duplicate MACs with different IPs.
        var byMac = report.Devices.Where(d => !string.IsNullOrEmpty(d.Mac)).GroupBy(d => d.Mac).Where(g => g.Count() > 1).ToList();
        foreach (var g in byMac)
            report.Conflicts.Add($"MAC {g.Key} responds on multiple addresses: {string.Join(", ", g.Select(x => x.Ip))}");

        progress?.Report("Computing health score ...");
        var diagnostics = await _ctx.Diagnostics.RunAllAsync(null, ct).ConfigureAwait(false);
        report.Health = _ctx.Diagnostics.ComputeHealth(diagnostics);
        report.Health.Score = Math.Clamp(report.Health.Score - 2 * report.Issues.Count, 0, 100);

        _ctx.Log.Info("lan.report", cidr, $"{report.Devices.Count(d => d.Online)} online; {report.Issues.Count} issues");
        return report;
    }
}
