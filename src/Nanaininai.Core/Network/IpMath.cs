using System.Net;
using System.Text;

namespace Nanaininai.Core.Network;

/// <summary>Platform-agnostic IPv4 / CIDR mathematics used by validation, planning and diagnostics.</summary>
public static class IpMath
{
    /// <summary>Parses a strict dotted-quad IPv4 address (no shorthand, no leading-zero octets).</summary>
    public static bool TryParseIp(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        uint result = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            foreach (var c in part) if (!char.IsAsciiDigit(c)) return false;
            if (part.Length > 1 && part[0] == '0') return false; // no leading zeros
            if (!int.TryParse(part, out var octet) || octet > 255) return false;
            result = (result << 8) | (uint)octet;
        }
        value = result;
        return true;
    }

    public static bool IsValidIp(string? text) => TryParseIp(text, out _);

    public static string ToString(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    /// <summary>Parses CIDR "/24" notation into a prefix length.</summary>
    public static bool TryParseCidr(string? text, out int prefix)
    {
        prefix = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (!t.StartsWith('/')) return false;
        if (!int.TryParse(t[1..], out var p) || p < 0 || p > 32) return false;
        prefix = p;
        return true;
    }

    /// <summary>Parses a dotted subnet mask ("255.255.255.0") into a prefix length.</summary>
    public static bool TryParseMask(string? text, out int prefix)
    {
        prefix = 0;
        if (!TryParseIp(text, out var mask)) return false;
        // Mask must be contiguous 1-bits followed by 0-bits.
        var inv = ~mask;
        if ((inv & (inv + 1)) != 0) return false;
        prefix = popcount(mask);
        return true;
    }

    /// <summary>Accepts either "/nn" CIDR or a dotted subnet mask.</summary>
    public static bool TryParsePrefix(string? text, out int prefix)
    {
        if (text is null) { prefix = 0; return false; }
        if (text.Trim().StartsWith('/')) return TryParseCidr(text, out prefix);
        return TryParseMask(text, out prefix);
    }

    public static int popcount(uint v)
    {
        var count = 0;
        while (v != 0) { count += (int)(v & 1); v >>= 1; }
        return count;
    }

    public static uint PrefixToMask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    public static uint NetworkOf(uint ip, int prefix) => ip & PrefixToMask(prefix);

    public static uint BroadcastOf(uint ip, int prefix) => NetworkOf(ip, prefix) | ~PrefixToMask(prefix);

    /// <summary>First usable host address of the subnet (network address + 1; for /31//32 returns network itself).</summary>
    public static uint FirstHost(uint ip, int prefix) =>
        prefix >= 31 ? NetworkOf(ip, prefix) : NetworkOf(ip, prefix) + 1;

    /// <summary>Last usable host address of the subnet (broadcast - 1; for /31//32 returns broadcast).</summary>
    public static uint LastHost(uint ip, int prefix) =>
        prefix >= 31 ? BroadcastOf(ip, prefix) : BroadcastOf(ip, prefix) - 1;

    public static uint TotalAddresses(int prefix) => 1UL << (32 - prefix) == 0 ? uint.MaxValue : (uint)(1UL << (32 - prefix));

    public static long UsableHosts(int prefix) => prefix >= 31 ? (1L << (32 - prefix)) : (1L << (32 - prefix)) - 2;

    /// <summary>Returns true when <paramref name="ip"/> is the network or broadcast address of its own subnet.</summary>
    public static bool IsNetworkOrBroadcast(uint ip, int prefix) => ip == NetworkOf(ip, prefix) || ip == BroadcastOf(ip, prefix);

    public static bool InSameSubnet(uint a, uint b, int prefix) => NetworkOf(a, prefix) == NetworkOf(b, prefix);

    /// <summary>Addresses that must never be allocated to a host (0/8, loopback, link-local, multicast, broadcast).</summary>
    public static bool IsReserved(uint ip)
    {
        if (ip == 0 || ip == uint.MaxValue) return true;
        var first = ip >> 24;
        if (first == 0) return true;                       // 0.0.0.0/8
        if (first == 127) return true;                     // loopback
        if ((ip & 0xFFFF0000) == 0xA9FE0000) return true;  // 169.254.0.0/16 link-local (APIPA)
        if (first >= 224) return true;                     // multicast + reserved
        return false;
    }

    public static bool IsPrivateRange(uint ip)
    {
        var a = ip >> 24;
        var b = (ip >> 16) & 0xFF;
        return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
    }

    public static uint Increment(uint ip) => ip == uint.MaxValue ? ip : ip + 1;

    public static uint Decrement(uint ip) => ip == 0 ? ip : ip - 1;

    /// <summary>Enumerates host addresses from start to end inclusive (no wraparound).</summary>
    public static IEnumerable<uint> Range(uint start, uint end)
    {
        for (var v = start; ; v++)
        {
            yield return v;
            if (v >= end) yield break;
        }
    }
}
