using Nanaininai.Core.Models;

namespace Nanaininai.Core.Network;

public sealed class AllocationEntry
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public bool IsGateway { get; set; }
    public bool IsReserved { get; set; }

    public override string ToString() => $"{Name,-16} {Ip}";
}

public sealed class AllocationPlan
{
    public string NetworkCidr { get; set; } = "";
    public List<AllocationEntry> Entries { get; set; } = new();
    public ValidationResult Validation { get; set; } = new();
    public bool IsValid => Validation.IsValid;

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("NETWORK PLANNER");
        sb.AppendLine($"Network: {NetworkCidr}");
        sb.AppendLine();
        foreach (var e in Entries)
        {
            var tag = e.IsGateway ? "  (gateway)" : e.IsReserved ? "  (reserved)" : "";
            sb.AppendLine($"{e.Name,-20} {e.Ip}{tag}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Plans non-overlapping IP allocations for N hosts inside a subnet.
/// The planner can never produce duplicate addresses: allocations are generated
/// from a single ascending cursor and every candidate is checked against the
/// gateway, user-reserved and already-allocated sets.
/// </summary>
public static class IpPlanner
{
    public static AllocationPlan Plan(string networkCidr, int pcCount, string? gateway = null,
        IEnumerable<string>? reservedExtra = null, string? startIp = null,
        string namePrefix = "PC", int namePadding = 2, int startNumber = 1)
    {
        var plan = new AllocationPlan { NetworkCidr = networkCidr };
        var r = plan.Validation;

        // Parse "a.b.c.d/p" or "a.b.c.d mask m.m.m.m" style input.
        var slash = networkCidr.IndexOf('/');
        if (slash < 0 || !IpMath.TryParseIp(networkCidr[..slash], out var network) ||
            !IpMath.TryParsePrefix(networkCidr[slash..], out var prefix))
        {
            r.AddError($"'{networkCidr}' is not valid CIDR notation (expected e.g. 192.168.10.0/24).");
            return plan;
        }

        var netAddr = IpMath.NetworkOf(network, prefix);
        if (network != netAddr)
            r.AddWarning($"Network address given as {IpMath.ToString(network)} but the actual network base of /{prefix} is {IpMath.ToString(netAddr)}; planning on {IpMath.ToString(netAddr)}/{prefix}.");

        var first = IpMath.FirstHost(netAddr, prefix);
        var last = IpMath.LastHost(netAddr, prefix);

        var blocked = new HashSet<uint>();
        var gatewayEntry = (AllocationEntry?)null;

        if (!string.IsNullOrEmpty(gateway))
        {
            if (!IpMath.TryParseIp(gateway, out var gw))
                r.AddError($"Gateway '{gateway}' is not a valid IPv4 address.");
            else if (!IpMath.InSameSubnet(netAddr, gw, prefix))
                r.AddError($"Gateway {gateway} is outside {IpMath.ToString(netAddr)}/{prefix}.");
            else
            {
                blocked.Add(gw);
                gatewayEntry = new AllocationEntry { Name = "Gateway", Ip = gateway, IsGateway = true };
            }
        }

        if (reservedExtra != null)
            foreach (var res in reservedExtra)
            {
                if (!IpMath.TryParseIp(res, out var rip))
                    r.AddError($"Reserved address '{res}' is not a valid IPv4 address.");
                else if (!IpMath.InSameSubnet(netAddr, rip, prefix))
                    r.AddError($"Reserved address {res} is outside {IpMath.ToString(netAddr)}/{prefix}.");
                else if (!blocked.Add(rip))
                    r.AddWarning($"Reserved address {res} is already reserved; ignoring duplicate.");
            }

        if (pcCount < 0) r.AddError("PC count must not be negative.");
        if (r.Errors.Count > 0) return plan;

        var cursor = first;
        if (!string.IsNullOrEmpty(startIp))
        {
            if (!IpMath.TryParseIp(startIp, out var start))
                r.AddError($"Start IP '{startIp}' is not a valid IPv4 address.");
            else if (start < first || start > last)
                r.AddError($"Start IP {startIp} is outside the host range {IpMath.ToString(first)} - {IpMath.ToString(last)}.");
            else
                cursor = start;
        }
        if (r.Errors.Count > 0) return plan;

        if (gatewayEntry is not null) plan.Entries.Add(gatewayEntry);

        long free = 0;
        for (var v = cursor; v <= last; v++)
            if (!blocked.Contains(v)) free++;
        if (free < pcCount)
        {
            r.AddError($"Only {free} free addresses between {IpMath.ToString(cursor)} and {IpMath.ToString(last)}; {pcCount} requested. Reduce the count, choose a larger subnet, or move the start IP.");
            return plan;
        }

        var allocated = new HashSet<uint>();
        for (var i = 0; i < pcCount; i++)
        {
            // Advance to the next unblocked address.
            while (blocked.Contains(cursor)) cursor = IpMath.Increment(cursor);

            if (allocated.Contains(cursor))
            {
                r.AddError("Internal error: planner attempted to allocate a duplicate address.");
                return plan;
            }
            allocated.Add(cursor);

            plan.Entries.Add(new AllocationEntry
            {
                Name = $"{namePrefix}-{(startNumber + i).ToString(new string('0', Math.Max(1, namePadding)))}",
                Ip = IpMath.ToString(cursor)
            });

            cursor = IpMath.Increment(cursor);
        }

        r.AddWarning($"Allocated addresses {IpMath.ToString(allocated.Min())} - {IpMath.ToString(allocated.Max())}; remember to configure the matching range on the switch/router DHCP exclusions if present.");
        return plan;
    }
}
