using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Platform;

public sealed class WindowsFirewallService : IFirewallService
{
    private readonly ILogService _log;
    public WindowsFirewallService(ILogService log) => _log = log;

    public static readonly (string DisplayName, Func<FirewallRuleInfo, bool> Match)[] RequiredRules =
    {
        ("ICMPv4 Echo Request (ping)", r => r.Name.Contains("Echo Request", StringComparison.OrdinalIgnoreCase) && r.Name.Contains("ICMPv4", StringComparison.OrdinalIgnoreCase)),
        ("File and Printer Sharing (SMB-In)", r => r.Name.Contains("SMB-In", StringComparison.OrdinalIgnoreCase)),
        ("Network Discovery", r => r.Group.Contains("Network Discovery", StringComparison.OrdinalIgnoreCase) && r.Direction == "In")
    };

    public async Task<FirewallStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var status = new FirewallStatus();
        if (!OperatingSystem.IsWindows())
        {
            status.Detail = "Firewall information is only available on Windows.";
            return status;
        }            var r = await ProcessRunner.RunPowerShellAsync(
                "Get-NetFirewallProfile 2>$null | Select-Object Name,Enabled | ConvertTo-Json -Compress").ConfigureAwait(false);
        if (r.Success)
        {
            try
            {
                var json = r.StdOut.Trim();
                if (json.StartsWith('['))
                {
                    var arr = System.Text.Json.JsonSerializer.Deserialize<ProfileDto[]>(json) ?? Array.Empty<ProfileDto>();
                    foreach (var p in arr)
                        switch (p.Name)
                        {
                            case "Domain": status.DomainEnabled = p.Enabled; break;
                            case "Private": status.PrivateEnabled = p.Enabled; break;
                            case "Public": status.PublicEnabled = p.Enabled; break;
                        }
                }
                else if (json.Length > 0)
                {
                    var p = System.Text.Json.JsonSerializer.Deserialize<ProfileDto>(json);
                    if (p is not null) Apply(status, p);
                }
            }
            catch (Exception ex) { status.Detail = $"Could not parse firewall profile state: {ex.Message}"; }
        }
        else
        {
            status.Detail = $"Could not query firewall profiles: {r.Combined.Trim()}";
        }

        return status;
    }

    private static void Apply(FirewallStatus status, ProfileDto p)
    {
        switch (p.Name)
        {
            case "Domain": status.DomainEnabled = p.Enabled; break;
            case "Private": status.PrivateEnabled = p.Enabled; break;
            case "Public": status.PublicEnabled = p.Enabled; break;
        }
    }

    private sealed class ProfileDto
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
    }

    public async Task<List<(string DisplayName, FirewallRuleInfo? Rule)>> GetRequiredRulesAsync(CancellationToken ct = default)
    {
        var results = new List<(string, FirewallRuleInfo?)>();
        if (!OperatingSystem.IsWindows()) return results;

        // Query the well-known rule groups and match the required rules against them.
        var script = "Get-NetFirewallRule -DisplayGroup 'File and Printer Sharing','Network Discovery' -ErrorAction SilentlyContinue 2>$null | " +
                     "Select-Object DisplayName,DisplayGroup,Enabled,Direction,Action,Profile | ConvertTo-Json -Compress -Depth 2";
        var r = await ProcessRunner.RunPowerShellAsync(script).ConfigureAwait(false);

        var rules = new List<FirewallRuleInfo>();
        if (r.Success && r.StdOut.Trim().Length > 0)
        {
            try
            {
                var json = r.StdOut.Trim();
                if (json.StartsWith('['))
                {
                    var arr = System.Text.Json.JsonSerializer.Deserialize<RuleDto[]>(json) ?? Array.Empty<RuleDto>();
                    rules.AddRange(arr.Select(ToInfo));
                }
                else
                {
                    var dto = System.Text.Json.JsonSerializer.Deserialize<RuleDto>(json);
                    if (dto is not null) rules.Add(ToInfo(dto));
                }
            }
            catch { }
        }

        foreach (var (display, match) in RequiredRules)
        {
            // Prefer any matching rule; report the disabled ones first so the UI can offer a fix.
            var candidate = rules.Where(match).OrderBy(x => x.Enabled).FirstOrDefault();
            results.Add((display, candidate));
        }
        return results;
    }

    private static FirewallRuleInfo ToInfo(RuleDto d) => new()
    {
        Name = d.DisplayName ?? "",
        Group = d.DisplayGroup ?? "",
        Enabled = Convert.ToInt64(d.Enabled) == 1 || string.Equals(d.Enabled?.ToString(), "True", StringComparison.OrdinalIgnoreCase),
        Direction = d.Direction ?? "",
        Action = d.Action ?? "",
        Profiles = d.Profile?.ToString() ?? "Any"
    };

    private sealed class RuleDto
    {
        public string? DisplayName { get; set; }
        public string? DisplayGroup { get; set; }
        public object? Enabled { get; set; }
        public string? Direction { get; set; }
        public string? Action { get; set; }
        public object? Profile { get; set; }
    }

    public async Task<OperationResult> EnableRuleAsync(string ruleName, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");

        // Only rules we enumerated ourselves may be enabled - never arbitrary names.
        var required = await GetRequiredRulesAsync(ct).ConfigureAwait(false);
        var known = required.Where(x => x.Rule is not null).Select(x => x.Rule!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!known.Contains(ruleName))
            return OperationResult.Fail($"Rule '{ruleName}' is not one of the rules Nanaininai manages.", "Only the required LAN-sharing rules (ICMP Echo, SMB-In, Network Discovery) can be enabled.");

        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.", $"1. Enable inbound firewall rule '{ruleName}'.");

        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to change firewall rules.");

        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        var args = new List<string> { "advfirewall", "firewall", "set", "rule", $"name={ruleName}", "new", "enable=yes" };
        var r = await ProcessRunner.RunNetshAsync(args).ConfigureAwait(false);            if (!r.Success)
            {
                _log.Error("firewall.enableRule", ruleName, r.Combined);
                var isProtocolError = r.Combined.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                    || r.Combined.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
                return isProtocolError
                    ? OperationResult.Fail(
                        $"Could not enable rule '{ruleName}': the network protocol stack appears to be incomplete or not configured.",
                        $"Possible causes:\n- Nanaininai is not running as Administrator\n- Antivirus policy overrides firewall rules\n- The adapter has no IP protocol stack bound\n- A third-party network driver/filter is blocking netsh\n\nTechnical details:\n{r.Combined}",
                        r.ExitCode)
                    : OperationResult.Fail(
                        $"Could not enable rule '{ruleName}'.",
                        $"Possible causes:\n- Nanaininai is not running as Administrator\n- Antivirus policy overrides firewall rules\n\nTechnical details:\n{r.Combined}",
                        r.ExitCode);
            }
        _log.Success("firewall.enableRule", ruleName, $"enabled, confirmed={ctx.UserConfirmed}");
        return OperationResult.Ok($"Rule '{ruleName}' enabled.");
    }

    public async Task<OperationResult> SetAllProfilesAsync(bool enabled, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");

        var stateWord = enabled ? "ON" : "OFF";

        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.",
                $"1. Set all firewall profiles (Domain, Private, Public) state {(enabled ? "on" : "off")}.");

        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to change the firewall state.");

        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        var r = await ProcessRunner.RunNetshAsync(new[] { "advfirewall", "set", "allprofiles", "state", enabled ? "on" : "off" }).ConfigureAwait(false);
        if (!r.Success)
        {
            _log.Error("firewall.setAllProfiles", stateWord, r.Combined);
            return OperationResult.Fail(
                $"Could not turn all firewall profiles {stateWord}.",
                $"Possible causes:\n- Nanaininai is not running as Administrator\n- Firewall policy is managed by antivirus software or group policy\n\nTechnical details:\n{r.Combined}",
                r.ExitCode);
        }

        _log.Success("firewall.setAllProfiles", stateWord, $"all profiles {(enabled ? "enabled" : "disabled")}, confirmed={ctx.UserConfirmed}");
        return enabled
            ? OperationResult.Ok("All firewall profiles (Domain, Private, Public) are now ENABLED.")
            : OperationResult.Ok("All firewall profiles (Domain, Private, Public) are now DISABLED.\n\nWARNING: the Windows Firewall is now completely off. Turn it back on when you are finished.");
    }
}
