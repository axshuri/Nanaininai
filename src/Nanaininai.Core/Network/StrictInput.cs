using System.Text.RegularExpressions;

namespace Nanaininai.Core.Network;

/// <summary>Strict validators for values that will reach external processes.</summary>
public static class StrictInput
{
    private static readonly System.Text.RegularExpressions.Regex IpRx = new(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ShareNameRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ComputerNameRx = new(@"^[A-Za-z0-9][A-Za-z0-9-]{0,14}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NtPathRx = new(@"^[A-Za-z]:\\[^<>:""/\\|?*\x00-\x1f]{0,300}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex UncPathRx = new(@"^\\\\[A-Za-z0-9.\-]{1,253}\\[^\\/:*?""<>|\x00-\x1f]{1,80}(\\[^\\/:*?""<>|\x00-\x1f]{1,80}){0,7}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AdapterNameRx = new(@"^[^""\r\n]{1,256}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex IdentityRx = new(@"^[^""\r\n;|&$]{1,120}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidIp(string? s) => s is not null && IpRx.IsMatch(s) && IpMath.TryParseIp(s, out _);
    public static bool IsValidShareName(string? s) => s is not null && ShareNameRx.IsMatch(s);
    public static bool IsValidComputerName(string? s) => s is not null && ComputerNameRx.IsMatch(s);
    public static bool IsValidLocalPath(string? s) => s is not null && NtPathRx.IsMatch(s);
    public static bool IsValidUncPath(string? s) => s is not null && UncPathRx.IsMatch(s);
    public static bool IsValidAdapterName(string? s) => s is not null && AdapterNameRx.IsMatch(s);
    public static bool IsValidIdentity(string? s) => s is not null && IdentityRx.IsMatch(s);

    /// <summary>Escapes a value for embedding in a single-quoted PowerShell string.</summary>
    public static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    public static string UncFor(string hostOrIp, string shareName) => $"\\\\{hostOrIp}\\{shareName}";
}
