using Nanaininai.Core.Models;

namespace Nanaininai.Core.Network;

/// <summary>Validated result with human-readable errors and warnings.</summary>
public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;

    public void AddError(string message) => Errors.Add(message);
    public void AddWarning(string message) => Warnings.Add(message);

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in Errors) sb.AppendLine($"[FAIL] {e}");
        foreach (var w in Warnings) sb.AppendLine($"[WARN] {w}");
        if (Errors.Count == 0 && Warnings.Count == 0) sb.AppendLine("[PASS] configuration is valid.");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Validates an IPv4 configuration before it is applied to an adapter.</summary>
public static class IpValidator
{
    /// <param name="knownIps">Optional list of IPs known to be in use on the LAN (duplicate detection).</param>
    public static ValidationResult ValidateStatic(IpConfig config, IEnumerable<string>? knownIps = null)
    {
        var r = new ValidationResult();

        if (!IpMath.TryParseIp(config.Address, out var ip))
        {
            r.AddError($"IP address '{config.Address ?? "(empty)"}' is not a valid IPv4 address (expected e.g. 192.168.10.10).");
            return r;
        }

        if (config.PrefixLength < 0 || config.PrefixLength > 32)
        {
            r.AddError($"Prefix length /{config.PrefixLength} is invalid; expected /1 to /32.");
            return r;
        }

        if (IpMath.IsNetworkOrBroadcast(ip, config.PrefixLength))
            r.AddError($"{IpMath.ToString(ip)}/{config.PrefixLength} is the network or broadcast address of its subnet and cannot be assigned to a host.");

        if (IpMath.IsReserved(ip))
            r.AddError($"{IpMath.ToString(ip)} is a reserved address (loopback / link-local / multicast / broadcast) and cannot be assigned.");

        if (config.PrefixLength < 8)
            r.AddWarning($"/{config.PrefixLength} is an unusually large subnet for a small LAN; verify this is intended.");

        // Gateway checks
        if (string.IsNullOrEmpty(config.Gateway))
        {
            r.AddWarning("No default gateway configured; this PC will not be able to reach other subnets or the internet.");
        }
        else if (IpMath.TryParseIp(config.Gateway, out var gw))
        {
            if (IpMath.IsReserved(gw))
                r.AddError($"Gateway {config.Gateway} is a reserved address.");
            else if (!IpMath.InSameSubnet(ip, gw, config.PrefixLength))
                r.AddError($"Gateway {config.Gateway} is outside the subnet of {config.Address}/{config.PrefixLength}.");
            else if (gw == ip)
                r.AddError("Gateway must not be the address of this PC.");
            else if (IpMath.IsNetworkOrBroadcast(gw, config.PrefixLength))
                r.AddError($"Gateway {config.Gateway} is the network/broadcast address of the subnet.");
        }
        else
        {
            r.AddError($"Gateway '{config.Gateway}' is not a valid IPv4 address.");
        }

        // DNS checks
        foreach (var (label, dns) in new[] { ("Preferred DNS", config.PreferredDns), ("Alternate DNS", config.AlternateDns) })
        {
            if (string.IsNullOrEmpty(dns)) continue;
            if (!IpMath.TryParseIp(dns, out var dnsIp))
                r.AddError($"{label} '{dns}' is not a valid IPv4 address.");
            else if (IpMath.IsReserved(dnsIp))
                r.AddError($"{label} {dns} is a reserved address.");
            else if (dnsIp == ip)
                r.AddWarning($"{label} points at this PC; make sure a DNS server is actually running here.");
        }

        if (string.IsNullOrEmpty(config.PreferredDns))
            r.AddWarning("No preferred DNS configured; hostname resolution may fail.");

        // Duplicate-IP detection
        if (knownIps != null)
        {
            foreach (var known in knownIps)
            {
                if (IpMath.TryParseIp(known, out var k) && k == ip)
                {
                    r.AddError($"IP conflict: {config.Address} already appears to be in use on this LAN.");
                    break;
                }
            }
        }

        return r;
    }

    /// <summary>Validates that a device with an existing config can actually talk to its gateway/subnet.</summary>
    public static ValidationResult ValidateConsistency(IpConfig cfg)
    {
        var r = new ValidationResult();
        if (cfg.Mode == IpConfigMode.Dhcp) return r;
        if (cfg.Address is null || cfg.Gateway is null) return r;
        if (IpMath.TryParseIp(cfg.Address, out var ip) && IpMath.TryParseIp(cfg.Gateway, out var gw) &&
            !IpMath.InSameSubnet(ip, gw, cfg.PrefixLength))
            r.AddError($"IP {cfg.Address}/{cfg.PrefixLength} and gateway {cfg.Gateway} are in different subnets.");
        return r;
    }
}
