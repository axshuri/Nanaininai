using System.Net;

namespace Nanaininai;

/// <summary>Pure calculation + validation helpers. Unit-testable, no OS calls.</summary>
public static class Core
{
    public static string ComputeComputerName(AppConfig config, int number) =>
        $"{config.ComputerNamePrefix}-{number.ToString().PadLeft(config.ComputerNamePadding, '0')}";

    /// <summary>Client IP = clientStartIp + (number - 1), applied to the last octet.</summary>
    public static IPAddress? ComputeClientIp(string clientStartIp, int number)
    {
        if (!IPAddress.TryParse(clientStartIp, out var start)) return null;
        var bytes = start.GetAddressBytes();
        if (bytes.Length != 4) return null;
        if (bytes[3] + (number - 1) > 255) return null;
        bytes[3] += (byte)(number - 1);
        return new IPAddress(bytes);
    }

    /// <summary>Validates against Windows computer-name (NetBIOS) rules — also reasonable for Linux hostnames.</summary>
    public static bool IsValidComputerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 15) return false;
        if (name.All(char.IsDigit)) return false;
        // Allow letters, digits, hyphens only; must not start/end with hyphen or dot.
        if (name[0] is '-' or '.') return false;
        if (name[^1] is '-' or '.') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.');
    }

    public static bool IsValidSubnetMask(string? mask)
    {
        if (!IPAddress.TryParse(mask, out var m)) return false;
        var bytes = m.GetAddressBytes();
        if (bytes.Length != 4) return false;
        bool zerosStarted = false;
        foreach (var b in bytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool one = (b & (1 << bit)) != 0;
                if (zerosStarted && one) return false;
                if (!one) zerosStarted = true;
            }
        }
        return bytes.Any(b => b != 0);
    }
}
