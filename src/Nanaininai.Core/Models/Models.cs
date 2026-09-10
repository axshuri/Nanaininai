namespace Nanaininai.Core.Models;

public enum IpConfigMode { Dhcp, Static }

public enum DiagnosticStatus { Pass, Warn, Fail, Skipped, Info }

public enum LogEventLevel { Info, Success, Warn, Error }

/// <summary>IPv4 configuration for a single adapter.</summary>
public sealed class IpConfig
{
    public IpConfigMode Mode { get; set; } = IpConfigMode.Dhcp;
    public string? Address { get; set; }
    public int PrefixLength { get; set; } = 24;
    public string? Gateway { get; set; }
    public string? PreferredDns { get; set; }
    public string? AlternateDns { get; set; }

    public string Summary()
    {
        if (Mode == IpConfigMode.Dhcp) return "DHCP";
        return $"Static {Address}/{PrefixLength} GW:{Gateway ?? "-"} DNS:{PreferredDns ?? "-"}";
    }
}

public sealed class AdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "DOWN";
    public string Mac { get; set; } = "";
    public string? Ipv4 { get; set; }
    public int? PrefixLength { get; set; }
    public string? Ipv6 { get; set; }
    public bool DhcpEnabled { get; set; }
    public string? Gateway { get; set; }
    public List<string> DnsServers { get; set; } = new();
    public string? NetworkCategory { get; set; }
    public bool IsPrimary { get; set; }

    public override string ToString() => $"{Name} ({Status}) {Ipv4 ?? "no IPv4"}";
}

public sealed class SystemInfo
{
    public string ComputerName { get; set; } = "";
    public string WindowsVersion { get; set; } = "";
    public string Edition { get; set; } = "";
    public string Build { get; set; } = "";
    public string CurrentUser { get; set; } = "";
    public string Workgroup { get; set; } = "";
    public bool IsDomainJoined { get; set; }
    public bool IsElevated { get; set; }
    public string OsPlatform { get; set; } = "";
    public List<AdapterInfo> Adapters { get; set; } = new();

    public AdapterInfo? PrimaryAdapter => Adapters.FirstOrDefault(a => a.IsPrimary) ?? Adapters.FirstOrDefault(a => a.Ipv4 is not null);
}

public sealed class WorkgroupInfo
{
    public string Workgroup { get; set; } = "";
    public bool IsDomainJoined { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class FirewallRuleInfo
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public bool Enabled { get; set; }
    public string Direction { get; set; } = "";
    public string Action { get; set; } = "";
    public string Profiles { get; set; } = "";
}

public sealed class FirewallStatus
{
    public bool DomainEnabled { get; set; }
    public bool PrivateEnabled { get; set; }
    public bool PublicEnabled { get; set; }
    public List<FirewallRuleInfo> Rules { get; set; } = new();
    public string Detail { get; set; } = "";
}

public sealed class SmbStatus
{
    public bool ClientRunning { get; set; }
    public bool ServerRunning { get; set; }
    public bool? FileSharingEnabled { get; set; }
    public bool? NetworkDiscoveryEnabled { get; set; }
    public bool Smb1Supported { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class PermissionEntry
{
    public string Identity { get; set; } = "";
    public string Rights { get; set; } = "";
    public string Type { get; set; } = "Allow";
}

public sealed class ShareInfo
{
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PermissionEntry> SharePermissions { get; set; } = new();
    public List<PermissionEntry> NtfsPermissions { get; set; } = new();
    public string NetworkPath => $"\\\\{Environment.MachineName}\\{Name}";
}

public sealed class CreateShareRequest
{
    public string LocalPath { get; set; } = "";
    public string ShareName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Read, Change or Full.</summary>
    public string AccessLevel { get; set; } = "Read";
    public List<string> AllowedIdentities { get; set; } = new() { "Authenticated Users" };
}

public sealed class DriveMapping
{
    public string Letter { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public bool Persistent { get; set; }
    public string Status { get; set; } = "";
}

public sealed class LanDevice
{
    public string Ip { get; set; } = "";
    public string? Hostname { get; set; }
    public string? Mac { get; set; }
    public bool Online { get; set; }
    public double? LatencyMs { get; set; }
    public bool SmbAvailable { get; set; }
    public string Notes { get; set; } = "";

    public override string ToString() => $"{Ip,-16} {Hostname ?? "?",-18} {(Online ? "ONLINE" : "OFFLINE"),-8} {(LatencyMs is null ? "-" : Math.Round(LatencyMs.Value) + "ms")}";
}

public sealed class PingResult
{
    public int Total { get; set; }
    public int Success { get; set; }
    public int Lost => Total - Success;
    public double MinMs { get; set; }
    public double AvgMs { get; set; }
    public double MaxMs { get; set; }
    public bool AnySuccess => Success > 0;
}

public sealed class DiagnosticResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DiagnosticStatus Status { get; set; } = DiagnosticStatus.Skipped;
    public string Message { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? RecommendedAction { get; set; }
    /// <summary>Optional identifier describing an automated fix that can be applied for this finding.</summary>
    public string? FixId { get; set; }

    public string Symbol => Status switch
    {
        DiagnosticStatus.Pass => "[✓]",
        DiagnosticStatus.Warn => "[!]",
        DiagnosticStatus.Fail => "[✗]",
        DiagnosticStatus.Info => "[i]",
        _ => "[-]"
    };
}

public sealed class HealthItem
{
    public string Name { get; set; } = "";
    public DiagnosticStatus Status { get; set; }
    public int Deduction { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class HealthReport
{
    public int Score { get; set; }
    public const int MaxScore = 100;
    public List<HealthItem> Items { get; set; } = new();

    public IEnumerable<HealthItem> Deductions => Items.Where(i => i.Deduction > 0);

    public string Render()
    {
        var lines = new List<string> { $"LAN HEALTH: {Score} / {MaxScore}" };
        foreach (var item in Items)
            lines.Add($"  {item.Name,-24} {(item.Status == DiagnosticStatus.Pass ? "✓" : item.Status == DiagnosticStatus.Warn ? "!" : "✗")}  {item.Reason}{(item.Deduction > 0 ? $"  (-{item.Deduction})" : "")}");
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public LogEventLevel Level { get; set; } = LogEventLevel.Info;
    public string Operation { get; set; } = "";
    public string? Target { get; set; }
    public string? PreviousState { get; set; }
    public string? NewState { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public bool ConfirmedByUser { get; set; }
    public bool DryRun { get; set; }

    public string Format()
    {
        var parts = new List<string> { $"[{Level.ToString().ToUpperInvariant()}]", Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), Operation };
        if (!string.IsNullOrEmpty(Target)) parts.Add($"target={Target}");
        if (!string.IsNullOrEmpty(PreviousState)) parts.Add($"old={PreviousState}");
        if (!string.IsNullOrEmpty(NewState)) parts.Add($"new={NewState}");
        if (!string.IsNullOrEmpty(Result)) parts.Add($"result={Result}");
        if (DryRun) parts.Add("(dry-run)");
        if (!string.IsNullOrEmpty(ErrorMessage)) parts.Add($"error={ErrorMessage}");
        return string.Join(' ', parts);
    }
}

/// <summary>A single change that an operation would perform (used for preview / dry-run).</summary>
public sealed class PlannedChange
{
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";

    public override string ToString() => $"{Index}. {Description}";
}

public sealed class OperationPlan
{
    public string Title { get; set; } = "";
    public string Warning { get; set; } = "Applying this configuration may temporarily interrupt network connectivity.";
    public bool RequiresAdmin { get; set; } = true;
    public List<PlannedChange> Changes { get; set; } = new();

    public string Render(bool dryRun)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(dryRun ? "DRY RUN - the following changes WOULD be made:" : "OPERATION PLAN");
        sb.AppendLine(Title);
        sb.AppendLine();
        foreach (var c in Changes) sb.AppendLine($"  {c.Index}. [{c.Category}] {c.Description}");
        if (Changes.Count == 0) sb.AppendLine("  (no changes)");
        if (!dryRun && !string.IsNullOrEmpty(Warning)) { sb.AppendLine(); sb.AppendLine("WARNING: " + Warning); }
        if (dryRun) { sb.AppendLine(); sb.AppendLine("No changes were made."); }
        return sb.ToString();
    }
}

/// <summary>Result of an operation executed against the system.</summary>
public sealed class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    public int? ExitCode { get; set; }
    public bool DryRun { get; set; }

    public static OperationResult Ok(string message, string? details = null) => new() { Success = true, Message = message, Details = details };
    public static OperationResult Fail(string message, string? details = null, int? exitCode = null) => new() { Success = false, Message = message, Details = details, ExitCode = exitCode };
}

public sealed class RollbackSnapshot
{
    public DateTimeOffset TakenAt { get; set; } = DateTimeOffset.Now;
    public string Adapter { get; set; } = "";
    public IpConfig Previous { get; set; } = new();

    public string Render() =>
        $"BACKUP{Environment.NewLine}Adapter: {Adapter}{Environment.NewLine}Previous IPv4: {(Previous.Mode == IpConfigMode.Dhcp ? "DHCP" : "Static")}" +
        $"{Environment.NewLine}Previous IP: {Previous.Address ?? "-"}" +
        $"{Environment.NewLine}Previous Gateway: {Previous.Gateway ?? "-"}" +
        $"{Environment.NewLine}Previous DNS: {Previous.PreferredDns ?? "-"}";
}

public sealed class ShareTestStep
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";

    public override string ToString() => $"{(Passed ? "✓" : "✗")} {Name}{(Detail.Length > 0 ? $" - {Detail}" : "")}";
}

public sealed class ShareTestResult
{
    public string Target { get; set; } = "";
    public List<ShareTestStep> Steps { get; set; } = new();
    public bool Success { get; set; }
    public string Reason { get; set; } = "";

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SMB TEST RESULT for {Target}");
        foreach (var s in Steps) sb.AppendLine($"  {(s.Passed ? "✓" : "✗")} {s.Name}{(s.Detail.Length > 0 ? $" ({s.Detail})" : "")}");
        if (!Success) sb.AppendLine($"Reason: {Reason}");
        return sb.ToString();
    }
}

/// <summary>Editable LAN profile (e.g. the 18-PC school LAN example).</summary>
public sealed class LanProfile
{
    public string Name { get; set; } = "SCHOOL-18-PC";
    public string NetworkCidr { get; set; } = "192.168.10.0/24";
    public string Gateway { get; set; } = "192.168.10.1";
    public string StartIp { get; set; } = "192.168.10.10";
    public int PcCount { get; set; } = 18;
    public string NamePrefix { get; set; } = "SCHOOL-PC";
    public int NamePadding { get; set; } = 2;
    public List<string> ReservedIps { get; set; } = new();
    public string? PreferredDns { get; set; }
    public string? AlternateDns { get; set; }
}
