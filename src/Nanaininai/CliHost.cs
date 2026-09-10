using System.Text;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;
using Nanaininai.Core.Network;
using Nanaininai.Platform;
using Nanaininai.Services;

namespace Nanaininai;

/// <summary>Scriptable CLI: lanagent [verb] [--dry-run] [args]</summary>
public static class CliHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var ctx = new AgentContext();
        Console.OutputEncoding = Encoding.UTF8;
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Skip(1).ToArray();
        var dryRun = rest.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        rest = rest.Where(a => !a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)).ToArray();

        try
        {
            if (!Elevation.IsAdmin())
            {
                if (string.IsNullOrWhiteSpace(verb) || verb is "help" or "--help" or "-h" or "menu")
                    return AdminMenuAsync(ctx);
            }

            return verb switch
            {
                "" or "help" or "--help" or "-h" => Help(),
                "menu" => !Elevation.IsAdmin() ? AdminMenuAsync(ctx) : Help(),
                "scan" => await ScanAsync(ctx, rest).ConfigureAwait(false),
                "diagnose" => await DiagnoseAsync(ctx).ConfigureAwait(false),
                "health" => await HealthAsync(ctx).ConfigureAwait(false),
                "network" => await NetworkAsync(ctx, rest, dryRun).ConfigureAwait(false),
                "plan" => await PlanAsync(ctx, rest).ConfigureAwait(false),
                "prepare" => await PrepareAsync(ctx, dryRun).ConfigureAwait(false),
                "checklan" or "check-lan" => await CheckLanAsync(ctx, rest).ConfigureAwait(false),
                "firewall" => await FirewallAsync(ctx, rest, dryRun).ConfigureAwait(false),
                "smb" => await SmbAsync(ctx).ConfigureAwait(false),
                "shares" or "share" => await SharesAsync(ctx, rest).ConfigureAwait(false),
                "drive" or "drives" => await DrivesAsync(ctx).ConfigureAwait(false),
                "logs" => Logs(ctx),
                _ => Unknown(verb)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] {ex.Message}");
            return 1;
        }
    }

    private static int AdminMenuAsync(AgentContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("Nanaininai - Administrator Menu");
        Console.WriteLine("================================");
        Console.WriteLine("Running in LIMITED mode (not Administrator).");
        Console.WriteLine("Diagnostics work, but configuration changes require elevation.");
        Console.WriteLine();
        Console.WriteLine("Choose an action:");
        Console.WriteLine("  [Enter] Open administrator actions menu");
        Console.WriteLine("  [D]     Run diagnostics (no admin needed)");
        Console.WriteLine("  [Q]     Quit");
        Console.WriteLine();
        Console.Write("Press a key: ");

        var key = Console.ReadKey(intercept: true).Key;
        Console.WriteLine();            return key switch
        {
            ConsoleKey.Enter => AdminActionMenuAsync(ctx).GetAwaiter().GetResult(),
            ConsoleKey.D1 or ConsoleKey.D or ConsoleKey.D2 => RunDiagnoseAndContinue(ctx).GetAwaiter().GetResult(),
            ConsoleKey.Q => 0,
            _ => AdminMenuAsync(ctx)
        };
    }

    private static async Task<int> AdminActionMenuAsync(AgentContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("Administrator Actions");
        Console.WriteLine("--------------------");
        Console.WriteLine("These operations require Administrator privileges.");
        Console.WriteLine();
        Console.WriteLine("  [1] Diagnostics        (read-only)");
        Console.WriteLine("  [2] Network show       (read-only)");
        Console.WriteLine("  [3] Firewall status    (read-only)");
        Console.WriteLine("  [4] SMB status         (read-only)");
        Console.WriteLine("  [5] Share test         (read-only)");
        Console.WriteLine("  [A] Apply all fixes    (admin required)");
        Console.WriteLine("  [R] Restart as Admin   (then re-run this menu)");
        Console.WriteLine("  [Q] Quit");
        Console.WriteLine();
        Console.Write("Choice: ");

        var key = Console.ReadKey(intercept: true).Key;
        Console.WriteLine();            return key switch
        {
            ConsoleKey.D1 => DiagnoseAsync(ctx).GetAwaiter().GetResult(),
            ConsoleKey.D2 => NetworkAsync(ctx, new[] { "show" }, dryRun: false).GetAwaiter().GetResult(),
            ConsoleKey.D3 => FirewallAsync(ctx, new[] { "status" }, dryRun: false).GetAwaiter().GetResult(),
            ConsoleKey.D4 => SmbAsync(ctx).GetAwaiter().GetResult(),
            ConsoleKey.D5 => AskShareTestAsync(ctx).GetAwaiter().GetResult(),
            ConsoleKey.A => ApplyAllFixesAsync(ctx).GetAwaiter().GetResult(),
            ConsoleKey.R => RestartAsAdminAndResumeAsync("menu").GetAwaiter().GetResult(),
            ConsoleKey.Q => 0,
            _ => AdminActionMenuAsync(ctx).GetAwaiter().GetResult()
        };
    }

    private static async Task<int> ApplyAllFixesAsync(AgentContext ctx)
    {
        if (!Elevation.IsAdmin())
        {
            Console.WriteLine("Administrator privileges are required to apply fixes.");
            Console.WriteLine("Restarting as Administrator...");
            var started = Elevation.RestartAsAdmin("prepare");
            return started == Elevation.StartResult.Started ? 0 : 1;
        }

        Console.WriteLine("Applying recommended fixes (firewall rules, SMB, network profile)...");
        var fixer = new Services.FixService(ctx);
        foreach (var fixId in new[] { "firewall.enableRequiredRules", "smb.enableFileSharing", "network.setPrivate" })
        {
            var plan = await fixer.BuildPlanAsync(fixId).ConfigureAwait(false);
            if (plan.Changes.Count == 0)
            {
                Console.WriteLine($"  - No changes needed for {fixId}.");
                continue;
            }

            Console.WriteLine($"Running fix: {fixId}");
            var r = await fixer.ApplyAsync(fixId, new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
            Console.WriteLine($"  {(r.Success ? "[OK]" : "[FAIL]")} {r.Message}");
            if (!string.IsNullOrEmpty(r.Details)) Console.WriteLine($"  Details: {r.Details}");
        }

        Console.WriteLine();
        Console.WriteLine("Done. Run 'lanagent diagnose' to verify.");
        Console.WriteLine();
        Console.Write("Press Enter to return to admin menu, or Q to quit: ");
        var k = Console.ReadKey(intercept: true).Key;
        Console.WriteLine();
        return k == ConsoleKey.Q ? 0 : await AdminActionMenuAsync(ctx);
    }

    private static async Task<int> AskShareTestAsync(AgentContext ctx)
    {
        Console.Write("UNC path to test (e.g. \\\\server\\share): ");
        var target = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.WriteLine("Cancelled.");
            return await AdminActionMenuAsync(ctx);
        }

        var result = await ctx.Shares.TestShareAsync(target, testWrite: false).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine(result.Render());
        Console.WriteLine();
        Console.Write("Press Enter to return to admin menu, or Q to quit: ");
        var k = Console.ReadKey(intercept: true).Key;
        Console.WriteLine();
        return k == ConsoleKey.Q ? 0 : await AdminActionMenuAsync(ctx);
    }

    private static async Task<int> RunDiagnoseAndContinue(AgentContext ctx)
    {
        await DiagnoseAsync(ctx).ConfigureAwait(false);
        Console.WriteLine();
        Console.Write("Press Enter for admin menu, or Q to quit: ");
        var k = Console.ReadKey(intercept: true).Key;
        Console.WriteLine();
        return k == ConsoleKey.Q ? 0 : AdminMenuAsync(ctx);
    }

    private static async Task<int> RestartAsAdminAndResumeAsync(string args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Elevation is only supported on Windows.");
            return 1;
        }

        Console.WriteLine("Restarting as Administrator...");
        var started = Elevation.RestartAsAdmin(args);

        if (started == Elevation.StartResult.Started)
            return 0; // parent exits; child will show the menu again

        Console.WriteLine(started == Elevation.StartResult.DeclinedByUser
            ? "Elevation was declined."
            : "Could not restart as Administrator.");
        return 1;
    }

    private static int Help()
    {
        Console.WriteLine("""
            Nanaininai - Windows LAN Networking Automation Agent

            Usage: lanagent <command> [options]

            Commands:
              (no command)              Launch the interactive TUI
              scan [--dry-run]          Discover devices on the local subnet
              diagnose                  Run the diagnostic test battery
              health                    Show the LAN health score
              prepare [--dry-run]       Run the "Prepare this PC" workflow
              checklan [cidr]           Generate a full LAN report
              network show              Show IP configuration
              network validate <ip>/<p> <gw> <dns>  Validate a static config
              network apply <ip>/<p> <gw> <dns> [--dry-run]
              network rollback          Show last saved configuration backup
              plan                      Show the SCHOOL-18-PC allocation plan
              firewall status           Show firewall profiles + required rules
              firewall on|off           Turn ALL firewall profiles on/off (admin)
              smb status                Show SMB client/server status
              shares list               List SMB shares
              share test <\\host\share> [--write]   Test a remote share
              drive list                List mapped drives
              logs                      Show recent log entries
              help                      Show this help

            Options:
              --dry-run                 Preview changes without applying them

            Configuration-changing commands require Administrator privileges.
            """);
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"[FAIL] Unknown command '{verb}'. Run 'lanagent help'.");
        return 1;
    }

    private static async Task<int> ScanAsync(AgentContext ctx, string[] rest)
    {
        var cidr = rest.FirstOrDefault(a => a.Contains('/')) ?? await DetectSubnetAsync(ctx).ConfigureAwait(false);
        Console.WriteLine($"Scanning {cidr} (only the selected local subnet)...");
        var devices = await ctx.Discovery.ScanAsync(cidr, 1000, new Progress<LanDevice>(d => { })).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("LAN DEVICES");
        Console.WriteLine("IP               HOSTNAME            STATUS   LATENCY  SMB");
        foreach (var d in devices)
            Console.WriteLine($"{d.Ip,-16} {(d.Hostname ?? "-"),-19} {(d.Online ? "ONLINE" : "OFFLINE"),-8} {(d.LatencyMs is null ? "-" : Math.Round(d.LatencyMs.Value) + "ms"),-8} {(d.SmbAvailable ? "yes" : "no")}");
        Console.WriteLine($"\n{devices.Count} device(s) found.");
        return 0;
    }

    private static async Task<string> DetectSubnetAsync(AgentContext ctx)
    {
        var adapters = await ctx.NetworkConfig.GetAdaptersAsync().ConfigureAwait(false);
        var a = adapters.FirstOrDefault(x => x.IsPrimary) ?? throw new InvalidOperationException("No active adapter with an IPv4 address. Pass an explicit CIDR, e.g. 'lanagent scan 192.168.10.0/24'.");
        var ip = IpMath.TryParseIp(a.Ipv4!, out var v) ? v : throw new InvalidOperationException("Adapter has no valid IPv4 address.");
        var prefix = a.PrefixLength ?? 24;
        return $"{IpMath.ToString(IpMath.NetworkOf(ip, prefix))}/{prefix}";
    }

    private static async Task<int> DiagnoseAsync(AgentContext ctx)
    {
        Console.WriteLine("NETWORK DIAGNOSTICS");
        Console.WriteLine();
        var results = await ctx.Diagnostics.RunAllAsync(new Progress<DiagnosticResult>()).ConfigureAwait(false);
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Symbol} {r.Title,-24} {r.Message}");
            if (!string.IsNullOrEmpty(r.Detail)) Console.WriteLine($"    {r.Detail}");
            if (!string.IsNullOrEmpty(r.RecommendedAction)) Console.WriteLine($"    -> {r.RecommendedAction}");
        }
        var health = ctx.Diagnostics.ComputeHealth(results);
        Console.WriteLine();
        Console.WriteLine(health.Render());
        return results.Any(r => r.Status == DiagnosticStatus.Fail) ? 2 : 0;
    }

    private static async Task<int> HealthAsync(AgentContext ctx)
    {
        var results = await ctx.Diagnostics.RunAllAsync().ConfigureAwait(false);
        Console.WriteLine(ctx.Diagnostics.ComputeHealth(results).Render());
        return 0;
    }

    private static async Task<int> NetworkAsync(AgentContext ctx, string[] rest, bool dryRun)
    {
        var sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "show";
        if (sub == "show")
        {
            var adapters = await ctx.NetworkConfig.GetAdaptersAsync().ConfigureAwait(false);
            foreach (var a in adapters)
            {
                Console.WriteLine($"NETWORK ADAPTER");
                Console.WriteLine($"Name          : {a.Name}");
                Console.WriteLine($"Status        : {a.Status}");
                Console.WriteLine($"MAC           : {a.Mac}");
                Console.WriteLine($"IPv4          : {a.Ipv4 ?? "-"}");
                Console.WriteLine($"Prefix        : /{a.PrefixLength?.ToString() ?? "-"}");
                Console.WriteLine($"IPv6          : {a.Ipv6 ?? "-"}");
                Console.WriteLine($"DHCP          : {(a.DhcpEnabled ? "Enabled" : "Disabled")}");
                Console.WriteLine($"Gateway       : {a.Gateway ?? "-"}");
                Console.WriteLine($"DNS           : {string.Join(", ", a.DnsServers.DefaultIfEmpty("-"))}");
                Console.WriteLine($"Profile       : {a.NetworkCategory ?? "-"}");
                Console.WriteLine();
            }
            return 0;
        }

        if (sub == "validate" || sub == "apply")
        {
            // Expected: <ip>/<prefix|mask> <gateway> <dns> [alt-dns]
            if (rest.Length < 2)
            {
                Console.Error.WriteLine("Usage: lanagent network validate <ip>/<prefix-or-mask> <gateway> [dns] [alt-dns]");
                return 1;
            }
            var spec = rest[1];
            var cfg = new IpConfig { Mode = IpConfigMode.Static };
            var slash = spec.IndexOf('/');
            if (slash < 0 || !IpMath.TryParseIp(spec[..slash], out _))
            {
                Console.Error.WriteLine($"[FAIL] '{spec}' is not <ip>/<prefix-or-mask>.");
                return 1;
            }
            cfg.Address = spec[..slash];
            if (!IpMath.TryParsePrefix(spec[slash..], out var prefix))
            {
                Console.Error.WriteLine($"[FAIL] '{spec[slash..]}' is not a valid prefix or mask.");
                return 1;
            }
            cfg.PrefixLength = prefix;
            cfg.Gateway = rest.ElementAtOrDefault(2);
            cfg.PreferredDns = rest.ElementAtOrDefault(3);
            cfg.AlternateDns = rest.ElementAtOrDefault(4);

            var adapters = await ctx.NetworkConfig.GetAdaptersAsync().ConfigureAwait(false);
            var adapter = adapters.FirstOrDefault(x => x.IsPrimary);
            var validation = IpValidator.ValidateStatic(cfg, adapters.Where(x => x.Ipv4 is not null).Select(x => x.Ipv4!));
            Console.WriteLine(validation.Render());

            if (sub == "validate") return validation.IsValid ? 0 : 1;

            var plan = await ctx.NetworkConfig.BuildApplyPlanAsync(adapter?.Name ?? "Ethernet", cfg).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine(plan.Render(dryRun));
            if (validation.IsValid && !dryRun)
            {
                if (!Confirm("Apply changes? [Y/N]: ")) { Console.WriteLine("Cancelled."); return 1; }
                var result = await ctx.NetworkConfig.ApplyIpConfigAsync(adapter?.Name ?? "Ethernet", cfg,
                    new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
                Console.WriteLine($"{(result.Success ? "[OK]" : "[FAIL]")} {result.Message}");
                if (!string.IsNullOrEmpty(result.Details)) Console.WriteLine(result.Details);
                return result.Success ? 0 : 1;
            }
            return validation.IsValid ? 0 : 1;
        }

        if (sub == "rollback")
        {
            var adapters = await ctx.NetworkConfig.GetAdaptersAsync().ConfigureAwait(false);
            var adapter = adapters.FirstOrDefault(x => x.IsPrimary);
            if (adapter is null) { Console.Error.WriteLine("[FAIL] No primary adapter."); return 1; }
            var snap = await ctx.NetworkConfig.CaptureRollbackAsync(adapter.Name).ConfigureAwait(false);
            Console.WriteLine(snap?.Render() ?? "No backup available yet.");
            return 0;
        }

        Console.Error.WriteLine($"Unknown network subcommand '{sub}'.");
        return 1;
    }

    private static async Task<int> PlanAsync(AgentContext ctx, string[] rest)
    {
        var profile = ctx.Profiles.Profiles.FirstOrDefault();
        if (profile is null) { Console.Error.WriteLine("[FAIL] No profiles configured."); return 1; }
        var plan = IpPlanner.Plan(profile.NetworkCidr, profile.PcCount, profile.Gateway, profile.ReservedIps, profile.StartIp, profile.NamePrefix, profile.NamePadding);
        Console.WriteLine(plan.Render());
        return plan.IsValid ? 0 : 1;
    }

    private static async Task<int> PrepareAsync(AgentContext ctx, bool dryRun)
    {
        Console.WriteLine("PREPARE THIS PC");
        Console.WriteLine();
        var result = await ctx.Prepare.RunAsync().ConfigureAwait(false);
        foreach (var c in result.Checks)
        {
            Console.WriteLine($"{c.Symbol} {c.Title,-24} {c.Message}");
        }
        Console.WriteLine();
        Console.WriteLine("STATUS: " + (result.Ready ? "READY" : result.HasWarnings ? "READY WITH WARNINGS" : "NEEDS FIXES"));
        if (result.Fixes.Changes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(result.Fixes.Render(dryRun));
            if (dryRun) return 0;
            if (!Elevation.IsAdmin())
            {
                Console.WriteLine("Administrator privileges are required to apply fixes.");
                Console.WriteLine("Current mode: LIMITED");
                Console.WriteLine("[R] Restart as Administrator   [C] Continue in Read-Only Mode   [Q] Cancel");
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.R)
                {
                    var started = Elevation.RestartAsAdmin("prepare");
                    return started == Elevation.StartResult.Started ? 0 : 1;
                }
                return 0;
            }
            if (!Confirm("Apply recommended fixes? [Y/N]: ")) { Console.WriteLine("Cancelled."); return 0; }
            var fixer = new Services.FixService(ctx);
            foreach (var fixId in new[] { "firewall.enableRequiredRules", "smb.enableFileSharing", "network.setPrivate" })
            {
                var plan = await fixer.BuildPlanAsync(fixId).ConfigureAwait(false);
                if (plan.Changes.Count == 0) continue;
                var r = await fixer.ApplyAsync(fixId, new OperationContext { UserConfirmed = true }).ConfigureAwait(false);
                Console.WriteLine($"{(r.Success ? "[OK]" : "[FAIL]")} {r.Message}");
                if (!string.IsNullOrEmpty(r.Details)) Console.WriteLine(r.Details);
            }
        }
        return 0;
    }

    private static async Task<int> CheckLanAsync(AgentContext ctx, string[] rest)
    {
        var cidr = rest.FirstOrDefault(a => a.Contains('/'));
        var report = await ctx.LanReport.CheckAsync(cidr, new Progress<string>(s => Console.WriteLine(s))).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine(report.Render());
        return 0;
    }

    private static async Task<int> FirewallAsync(AgentContext ctx, string[] rest, bool dryRun)
    {
        var sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "status";
        if (sub == "status")
        {
            var fw = await ctx.Firewall.GetStatusAsync().ConfigureAwait(false);
            Console.WriteLine("WINDOWS FIREWALL");
            Console.WriteLine($"Domain Profile   {(fw.DomainEnabled ? "ENABLED" : "DISABLED")}");
            Console.WriteLine($"Private Profile  {(fw.PrivateEnabled ? "ENABLED" : "DISABLED")}");
            Console.WriteLine($"Public Profile   {(fw.PublicEnabled ? "ENABLED" : "DISABLED")}");
            Console.WriteLine();
            Console.WriteLine("Required Rules:");
            var required = await ctx.Firewall.GetRequiredRulesAsync().ConfigureAwait(false);
            foreach (var (display, rule) in required)
            {
                var status = rule is null ? "NOT FOUND" : rule.Enabled ? "ENABLED" : "DISABLED";
                Console.WriteLine($"{display,-30} {status}");
                if (rule is not null && !rule.Enabled)
                    Console.WriteLine($"    Fix: lanagent firewall enable \"{rule.Name}\"{(dryRun ? " --dry-run" : "")}");
            }
            return 0;
        }
        if (sub == "enable" && rest.Length > 1)
        {
            var ctxOp = new OperationContext { DryRun = dryRun, UserConfirmed = !dryRun && Confirm("Enable this rule? [Y/N]: ") };
            var r = await ctx.Firewall.EnableRuleAsync(string.Join(' ', rest.Skip(1)), ctxOp).ConfigureAwait(false);
            Console.WriteLine($"{(r.Success ? "[OK]" : "[FAIL]")} {r.Message}");
            if (!string.IsNullOrEmpty(r.Details)) Console.WriteLine(r.Details);
            return r.Success ? 0 : 1;
        }
        if (sub is "on" or "off")
        {
            var turnOn = sub == "on";
            var prompt = turnOn
                ? "Turn ON the firewall for all profiles (Domain, Private, Public)? [Y/N]: "
                : "Turn OFF the firewall for ALL profiles? The Windows Firewall will be completely disabled! Continue? [Y/N]: ";
            var op = new OperationContext { DryRun = dryRun, UserConfirmed = !dryRun && Confirm(prompt) };
            var r = await ctx.Firewall.SetAllProfilesAsync(turnOn, op).ConfigureAwait(false);
            Console.WriteLine($"{(r.Success ? "[OK]" : "[FAIL]")} {r.Message}");
            if (!string.IsNullOrEmpty(r.Details)) Console.WriteLine(r.Details);
            return r.Success ? 0 : 1;
        }
        Console.Error.WriteLine("Usage: lanagent firewall status | firewall on | firewall off | firewall enable \"<rule name>\" [--dry-run]");
        return 1;
    }

    private static async Task<int> SmbAsync(AgentContext ctx)
    {
        var s = await ctx.Smb.GetStatusAsync().ConfigureAwait(false);
        Console.WriteLine("SMB STATUS");
        Console.WriteLine($"SMB Client        {(s.ClientRunning ? "ENABLED" : "DISABLED")}");
        Console.WriteLine($"SMB Server        {(s.ServerRunning ? "ENABLED" : "DISABLED")}");
        Console.WriteLine($"File Sharing      {(s.FileSharingEnabled == true ? "ENABLED" : s.FileSharingEnabled == false ? "DISABLED" : "UNKNOWN")}");
        Console.WriteLine($"Network Discovery {(s.NetworkDiscoveryEnabled == true ? "ENABLED" : "DISABLED")}");
        Console.WriteLine($"SMB1 (obsolete)   {(s.Smb1Supported ? "ENABLED - consider disabling for security" : "DISABLED (good)")}");
        if (s.Detail.Length > 0) Console.WriteLine($"Note: {s.Detail}");
        return 0;
    }

    private static async Task<int> SharesAsync(AgentContext ctx, string[] rest)
    {
        var sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            var shares = await ctx.Shares.ListSharesAsync().ConfigureAwait(false);
            if (shares.Count == 0) { Console.WriteLine("No user shares found."); return 0; }
            foreach (var s in shares)
            {
                Console.WriteLine($"SHARE: {s.Name}");
                Console.WriteLine($"LOCAL PATH: {s.LocalPath}");
                Console.WriteLine("SHARE PERMISSIONS:");
                foreach (var p in s.SharePermissions) Console.WriteLine($"  {p.Identity,-30} {p.Rights} ({p.Type})");
                Console.WriteLine("NTFS:");
                foreach (var p in s.NtfsPermissions) Console.WriteLine($"  {p.Identity,-30} {p.Rights} ({p.Type})");
                Console.WriteLine();
            }
            return 0;
        }
        if (sub == "test" && rest.Length > 1)
        {
            var target = rest[1];
            var result = await ctx.Shares.TestShareAsync(target, testWrite: rest.Contains("--write")).ConfigureAwait(false);
            Console.WriteLine(result.Render());
            return result.Success ? 0 : 1;
        }
        Console.Error.WriteLine("Usage: lanagent shares list | share test <\\\\host\\share> [--write]");
        return 1;
    }

    private static async Task<int> DrivesAsync(AgentContext ctx)
    {
        var drives = await ctx.Drives.ListMappedDrivesAsync().ConfigureAwait(false);
        if (drives.Count == 0) { Console.WriteLine("No mapped network drives."); return 0; }
        foreach (var d in drives) Console.WriteLine($"{d.Letter,-4} → {d.RemotePath,-40} Persistent: {(d.Persistent ? "YES" : "no")}");
        return 0;
    }

    private static int Logs(AgentContext ctx)
    {
        var entries = ctx.Log.GetEntries();
        if (entries.Count == 0) Console.WriteLine("No log entries yet.");
        foreach (var e in entries.TakeLast(100)) Console.WriteLine(e.Format());
        Console.WriteLine($"\nLog file: {ctx.Log.LogFilePath}");
        return 0;
    }

    private static bool Confirm(string prompt)
    {
        if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
        {
            Console.Write(prompt);
            var key = Console.ReadKey(intercept: true).Key;
            Console.WriteLine(key == ConsoleKey.Y ? "Y" : "N");
            return key == ConsoleKey.Y;
        }
        return false; // never auto-confirm when non-interactive
    }
}
