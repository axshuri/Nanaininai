using Nanaininai.Core.Models;

namespace Nanaininai.Core.Abstractions;

/// <summary>Secure credential set handed over by the UI layer. Never persisted or logged.</summary>
public sealed class CredentialSet
{
    public string UserName { get; init; } = "";
    public char[] Password { get; init; } = Array.Empty<char>();

    public void Wipe() => Array.Clear(Password);
}

public sealed class OperationContext
{
    /// <summary>When true, no change is applied; the plan is produced instead.</summary>
    public bool DryRun { get; init; }
    /// <summary>Whether the user explicitly confirmed the change.</summary>
    public bool UserConfirmed { get; init; }
    public string? ConfirmedBy { get; init; }
}

public interface ISystemInfoProvider
{
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
    Task<WorkgroupInfo> GetWorkgroupInfoAsync(CancellationToken ct = default);
    Task<string?> GetActiveNetworkCategoryAsync(CancellationToken ct = default);
}

public interface INetworkConfigService
{
    Task<List<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default);
    Task<IpConfig?> GetIpConfigAsync(string adapterName, CancellationToken ct = default);
    Task<RollbackSnapshot?> CaptureRollbackAsync(string adapterName, CancellationToken ct = default);
    Task<OperationResult> ApplyIpConfigAsync(string adapterName, IpConfig target, OperationContext ctx, CancellationToken ct = default);
    Task<OperationPlan> BuildApplyPlanAsync(string adapterName, IpConfig target, CancellationToken ct = default);
    Task<OperationResult> RenameComputerAsync(string newName, OperationContext ctx, CancellationToken ct = default);
    Task<OperationResult> SetNetworkCategoryAsync(string category, OperationContext ctx, CancellationToken ct = default);
    Task<bool> IsElevatedAsync(CancellationToken ct = default);
}

public interface IDiscoveryService
{
    Task<List<LanDevice>> ScanAsync(string networkCidr, int timeoutMs, IProgress<LanDevice>? progress, CancellationToken ct = default);
    Task<PingResult> PingAsync(string host, int count, int timeoutMs, CancellationToken ct = default);
    Task<bool> IsSmbPortOpenAsync(string host, int timeoutMs = 500, CancellationToken ct = default);
}

public interface IFirewallService
{
    Task<FirewallStatus> GetStatusAsync(CancellationToken ct = default);
    Task<OperationResult> EnableRuleAsync(string ruleName, OperationContext ctx, CancellationToken ct = default);
    /// <summary>Status of the rules the agent considers required for LAN file sharing + ICMP.</summary>
    Task<List<(string DisplayName, FirewallRuleInfo? Rule)>> GetRequiredRulesAsync(CancellationToken ct = default);
    /// <summary>Master switch: turns all firewall profiles (Domain, Private, Public) on or off. Requires elevation and explicit user confirmation.</summary>
    Task<OperationResult> SetAllProfilesAsync(bool enabled, OperationContext ctx, CancellationToken ct = default);
}

public interface ISmbService
{
    Task<SmbStatus> GetStatusAsync(CancellationToken ct = default);
    Task<OperationResult> EnableFileSharingAsync(OperationContext ctx, CancellationToken ct = default);
}

public interface IShareService
{
    Task<List<ShareInfo>> ListSharesAsync(CancellationToken ct = default);
    Task<ShareInfo?> GetShareAsync(string name, CancellationToken ct = default);
    Task<OperationResult> CreateShareAsync(CreateShareRequest request, OperationContext ctx, CancellationToken ct = default);
    Task<OperationResult> RemoveShareAsync(string shareName, OperationContext ctx, CancellationToken ct = default);
    Task<ShareTestResult> TestShareAsync(string target, bool testWrite, CancellationToken ct = default);
}

public interface IDriveMappingService
{
    Task<List<DriveMapping>> ListMappedDrivesAsync(CancellationToken ct = default);
    Task<OperationResult> MapDriveAsync(string letter, string remotePath, bool persistent, CredentialSet? credentials, OperationContext ctx, CancellationToken ct = default);
    Task<OperationResult> UnmapDriveAsync(string letter, OperationContext ctx, CancellationToken ct = default);
}

public interface ILogService
{
    void Log(LogEntry entry);
    void Info(string operation, string? target = null, string? result = null) => Log(new LogEntry { Level = LogEventLevel.Info, Operation = operation, Target = target, Result = result });
    void Success(string operation, string? target = null, string? result = null) => Log(new LogEntry { Level = LogEventLevel.Success, Operation = operation, Target = target, Result = result });
    void Warn(string operation, string? target = null, string? result = null) => Log(new LogEntry { Level = LogEventLevel.Warn, Operation = operation, Target = target, Result = result });
    void Error(string operation, string? target = null, string? error = null) => Log(new LogEntry { Level = LogEventLevel.Error, Operation = operation, Target = target, ErrorMessage = error });
    IReadOnlyList<LogEntry> GetEntries(LogEventLevel? minLevel = null);
    string LogFilePath { get; }
}
