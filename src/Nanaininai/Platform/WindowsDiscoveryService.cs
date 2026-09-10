using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;
using Nanaininai.Core.Network;

namespace Nanaininai.Platform;

public sealed class WindowsDiscoveryService : IDiscoveryService
{
    private readonly ILogService _log;
    public WindowsDiscoveryService(ILogService log) => _log = log;

    public async Task<List<LanDevice>> ScanAsync(string networkCidr, int timeoutMs, IProgress<LanDevice>? progress, CancellationToken ct = default)
    {
        var devices = new List<LanDevice>();
        var slash = networkCidr.IndexOf('/');
        if (slash < 0 || !IpMath.TryParseIp(networkCidr[..slash], out var network) || !IpMath.TryParsePrefix(networkCidr[slash..], out var prefix))
            throw new ArgumentException($"'{networkCidr}' is not valid CIDR notation (expected e.g. 192.168.10.0/24).");

        // Hard safety limit: never scan more than a /22 worth of hosts (1,022 addresses).
        if (prefix < 22)
        {
            prefix = 22;
            network = IpMath.NetworkOf(network, prefix);
        }

        var first = IpMath.FirstHost(network, prefix);
        var last = IpMath.LastHost(network, prefix);

        // Kick the ARP cache with a fast parallel ping sweep, then read ARP for MACs.
        var tasks = new List<Task>();
        using var gate = new SemaphoreSlim(128);
        for (var ip = first; ip <= last && !ct.IsCancellationRequested; ip = IpMath.Increment(ip))
        {
            var target = ip;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(IpMath.ToString(target), Math.Min(timeoutMs, 1000)).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        var dev = new LanDevice
                        {
                            Ip = IpMath.ToString(target),
                            Online = true,
                            LatencyMs = Math.Round((double)reply.RoundtripTime, 1)
                        };
                        lock (devices) { devices.Add(dev); }
                        progress?.Report(dev);
                    }
                }
                catch (PingException)
                {
                    // Host unreachable or protocol not configured — just skip the address.
                }
                catch (Exception)
                {
                    // Other transient failures (e.g. transient socket errors); skip.
                }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Enrich: hostname, MAC, SMB.
        var arp = await LoadArpTableAsync().ConfigureAwait(false);
        var enrichTasks = devices.Select(async dev =>
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(dev.Ip).ConfigureAwait(false);
                dev.Hostname = entry.HostName;
            }
            catch (Exception)
            {
                // DNS lookup can fail when the protocol is not configured or the name service is unavailable.
                dev.Hostname = null;
            }

            if (arp.TryGetValue(dev.Ip, out var mac)) dev.Mac = mac;

            dev.SmbAvailable = await IsPortOpenAsync(dev.Ip, 445).ConfigureAwait(false);
            dev.Notes = dev.SmbAvailable ? "SMB" : "";
            progress?.Report(dev);
        }).ToList();
        await Task.WhenAll(enrichTasks).ConfigureAwait(false);

        _log.Info("discovery.scan", networkCidr, $"{devices.Count} devices found");
        return devices.OrderBy(d => IpMath.TryParseIp(d.Ip, out var v) ? v : 0).ToList();
    }

    public async Task<PingResult> PingAsync(string host, int count, int timeoutMs, CancellationToken ct = default)
    {
        var result = new PingResult { Total = Math.Clamp(count, 1, 100) };
        var latencies = new List<double>();
        var target = host;

        for (var i = 0; i < result.Total && !ct.IsCancellationRequested; i++)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    result.Success++;
                    latencies.Add(reply.RoundtripTime);
                }
            }
            catch (PingException)
            {
                // Count as lost.
            }
            if (i < result.Total - 1) await Task.Delay(150, ct).ConfigureAwait(false);
        }

        if (latencies.Count > 0)
        {
            result.MinMs = latencies.Min();
            result.AvgMs = Math.Round(latencies.Average(), 1);
            result.MaxMs = latencies.Max();
        }
        return result;
    }

    public async Task<bool> IsSmbPortOpenAsync(string host, int timeoutMs = 500, CancellationToken ct = default)
        => await IsPortOpenAsync(host, 445, timeoutMs).ConfigureAwait(false);

    private static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return done == connect && client.Connected;
        }
        catch
        {
            // Port probe failure (connection refused, no route, protocol not configured, etc.).
            return false;
        }
    }

    private static async Task<Dictionary<string, string>> LoadArpTableAsync()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return table;
        var r = await ProcessRunner.RunArpAsync().ConfigureAwait(false);
        if (!r.Success) return table;

        var lineRx = new Regex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F-]{17})\s+(\S+)", RegexOptions.Multiline);
        foreach (Match m in lineRx.Matches(r.StdOut))
        {
            var mac = m.Groups[2].Value.ToUpperInvariant();
            if (mac != "FF-FF-FF-FF-FF-FF" && mac != "00-00-00-00-00-00")
                table[m.Groups[1].Value] = mac;
        }
        return table;
    }
}
