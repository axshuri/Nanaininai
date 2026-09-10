using System.Net.NetworkInformation;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;
using Nanaininai.Core.Network;

namespace Nanaininai.Platform;

public sealed class WindowsNetworkConfigService : INetworkConfigService
{
    private readonly ILogService _log;

    public WindowsNetworkConfigService(ILogService log) => _log = log;

    public async Task<bool> IsElevatedAsync(CancellationToken ct = default) => Elevation.IsAdmin();

    public Task<List<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default)
        => Task.Run(() => WindowsSystemInfoProvider.GetCachedAdapters(), ct);

    public async Task<IpConfig?> GetIpConfigAsync(string adapterName, CancellationToken ct = default)
    {            var adapters = await GetAdaptersAsync(ct).ConfigureAwait(false);
            var a = adapters.FirstOrDefault(x => string.Equals(x.Name, adapterName, StringComparison.OrdinalIgnoreCase));
            if (a is null) return null;

            // Defensive: if the adapter object was populated but its IP properties are empty
            // (can happen when the protocol stack is not fully configured), return null so
            // callers treat it as "no configuration available" rather than attempting to apply
            // a half-empty config.
            if (string.IsNullOrEmpty(a.Ipv4) && a.DnsServers.Count == 0 && string.IsNullOrEmpty(a.Gateway))
                return null;

            return new IpConfig
            {
                Mode = a.DhcpEnabled ? IpConfigMode.Dhcp : IpConfigMode.Static,
                Address = a.Ipv4,
                PrefixLength = a.PrefixLength ?? 24,
                Gateway = a.Gateway,
                PreferredDns = a.DnsServers.FirstOrDefault(),
                AlternateDns = a.DnsServers.Skip(1).FirstOrDefault()
            };
    }

    public Task<RollbackSnapshot?> CaptureRollbackAsync(string adapterName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var cfg = GetIpConfigAsync(adapterName, ct).GetAwaiter().GetResult();
            if (cfg is null) return null;
            return new RollbackSnapshot { Adapter = adapterName, Previous = cfg };
        }, ct);

    public Task<OperationPlan> BuildApplyPlanAsync(string adapterName, IpConfig target, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var plan = new OperationPlan { Title = $"Configure IPv4 on '{adapterName}'" };
            var i = 0;
            if (target.Mode == IpConfigMode.Dhcp)
            {
                plan.Changes.Add(new PlannedChange { Index = ++i, Category = "Address", Description = $"Switch '{adapterName}' to DHCP (automatic addressing)" });
                plan.Changes.Add(new PlannedChange { Index = ++i, Category = "DNS", Description = $"Switch '{adapterName}' DNS servers to DHCP-assigned" });
            }
            else
            {
                plan.Changes.Add(new PlannedChange { Index = ++i, Category = "Address", Description = $"Set '{adapterName}' IPv4 to {target.Address}/{target.PrefixLength}" });
                if (!string.IsNullOrEmpty(target.Gateway))
                    plan.Changes.Add(new PlannedChange { Index = ++i, Category = "Gateway", Description = $"Set default gateway to {target.Gateway}" });
                if (!string.IsNullOrEmpty(target.PreferredDns))
                    plan.Changes.Add(new PlannedChange { Index = ++i, Category = "DNS", Description = $"Set preferred DNS to {target.PreferredDns}" });
                if (!string.IsNullOrEmpty(target.AlternateDns))
                    plan.Changes.Add(new PlannedChange { Index = ++i, Category = "DNS", Description = $"Set alternate DNS to {target.AlternateDns}" });
            }
            return plan;
        }, ct);
    }

    public async Task<OperationResult> ApplyIpConfigAsync(string adapterName, IpConfig target, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.", "Nanaininai configures Windows adapters through the netsh subsystem.");

        if (!StrictInput.IsValidAdapterName(adapterName))
            return OperationResult.Fail($"Adapter name '{adapterName}' contains characters that are not allowed.");

        var validation = target.Mode == IpConfigMode.Dhcp
            ? new ValidationResult()
            : IpValidator.ValidateStatic(target);
        if (!validation.IsValid)
        {
            _log.Error("network.apply", adapterName, string.Join("; ", validation.Errors));
            return OperationResult.Fail("Configuration is invalid and was not applied.", string.Join(Environment.NewLine, validation.Errors));
        }

        if (ctx.DryRun)
        {
            var dryPlan = await BuildApplyPlanAsync(adapterName, target, ct).ConfigureAwait(false);
            return OperationResult.Ok("DRY RUN - no changes were made.", dryPlan.Render(dryRun: true));
        }

        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to change network configuration.", "Current mode: LIMITED. Restart the agent as Administrator.");

        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        // Read the live adapter state (ignore the short cache) so the rollback
        // snapshot reflects reality just before the change is applied.
        WindowsSystemInfoProvider.InvalidateRuntimeCache();
        var snapshot = await CaptureRollbackAsync(adapterName, ct).ConfigureAwait(false);
        if (snapshot is not null)
            _log.Info("network.backup", adapterName, $"saved previous {snapshot.Previous.Summary()}");

        if (target.Mode == IpConfigMode.Dhcp)
        {
            var r1 = await ProcessRunner.RunNetshAsync(new[] { "interface", "ipv4", "set", "address", $"name={adapterName}", "source=dhcp" }).ConfigureAwait(false);
            if (!r1.Success)
            {
                var isProtocolError = r1.Combined.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                    || r1.Combined.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
                _log.Error("network.apply", adapterName, r1.Combined);
                return isProtocolError
                    ? OperationResult.Fail(
                        $"Could not switch '{adapterName}' to DHCP: the network protocol stack appears to be incomplete or not configured.",
                        $"Possible causes:\n- The adapter has no IP protocol stack bound (re-add IPv4 in the adapter properties)\n- A third-party network driver/filter is blocking netsh\n- The system is in a transient network state\n\nTechnical details:\n{r1.Combined}")
                    : ToHuman("Could not switch the adapter to DHCP.", r1);
            }
            await ProcessRunner.RunNetshAsync(new[] { "interface", "ipv4", "set", "dnsservers", $"name={adapterName}", "source=dhcp" }).ConfigureAwait(false);
            _log.Success("network.apply", adapterName, "switched to DHCP");
            return OperationResult.Ok($"'{adapterName}' switched to DHCP. If the PC does not receive an address within ~30 seconds, roll back the last configuration.");
        }

        // Static
        var mask = Core.Network.IpMath.ToString(Core.Network.IpMath.PrefixToMask(target.PrefixLength));
        var addrArgs = new List<string> { "interface", "ipv4", "set", "address", $"name={adapterName}", "source=static", $"addr={target.Address}", $"mask={mask}" };
        if (!string.IsNullOrEmpty(target.Gateway)) { addrArgs.Add($"gateway={target.Gateway}"); addrArgs.Add("gwmetric=1"); }
        var ra = await ProcessRunner.RunNetshAsync(addrArgs).ConfigureAwait(false);
        if (!ra.Success)
        {
            var isProtocolError = ra.Combined.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                || ra.Combined.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
            _log.Error("network.apply", adapterName, ra.Combined);
            if (isProtocolError)
                return OperationResult.Fail(
                    $"Could not apply the static IP configuration to '{adapterName}': the network protocol stack appears to be incomplete or not configured.",
                    $"Possible causes:\n- The adapter has no IP protocol stack bound (re-add IPv4 in the adapter properties)\n- A third-party network driver/filter is blocking netsh\n- The system is in a transient network state\n\nTechnical details:\n{ra.Combined}");
            return ToHuman("Could not apply the static IP configuration.", ra);
        }

        if (!string.IsNullOrEmpty(target.PreferredDns))
        {
            var rd = await ProcessRunner.RunNetshAsync(new[] { "interface", "ipv4", "set", "dnsservers", $"name={adapterName}", "static", $"addr={target.PreferredDns}", "primary" }).ConfigureAwait(false);
            if (!rd.Success)
            {
                var isProtocolError = rd.Combined.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                    || rd.Combined.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
                if (isProtocolError)
                    _log.Warn("network.apply.dns", adapterName, $"DNS update failed — protocol not configured: {rd.Combined}");
                else
                    _log.Warn("network.apply.dns", adapterName, rd.Combined);
            }
        }
        if (!string.IsNullOrEmpty(target.AlternateDns))
        {
            var ra2 = await ProcessRunner.RunNetshAsync(new[] { "interface", "ipv4", "add", "dnsservers", $"name={adapterName}", $"addr={target.AlternateDns}", "index=2" }).ConfigureAwait(false);
            if (!ra2.Success)
            {
                var isProtocolError = ra2.Combined.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                    || ra2.Combined.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
                if (isProtocolError)
                    _log.Warn("network.apply.dns", adapterName, $"Alternate DNS update failed — protocol not configured: {ra2.Combined}");
                else
                    _log.Warn("network.apply.dns", adapterName, ra2.Combined);
            }
        }

        WindowsSystemInfoProvider.InvalidateRuntimeCache();
        _log.Success("network.apply", adapterName, $"previous={snapshot?.Previous.Summary() ?? "unknown"} new={target.Summary()}");
        var rollbackHint = snapshot is null ? "" : Environment.NewLine + Environment.NewLine + "Previous configuration was saved; use 'Rollback last network configuration' to undo.";
        return OperationResult.Ok($"Static IPv4 applied to '{adapterName}': {target.Summary()}.{rollbackHint}", snapshot?.Render());
    }

    public async Task<OperationResult> RenameComputerAsync(string newName, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");
        if (!StrictInput.IsValidComputerName(newName))
            return OperationResult.Fail($"'{newName}' is not a valid computer name.", "Up to 15 characters; letters, digits and hyphens only; no spaces or dots.");
        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.", $"1. Rename computer '{Environment.MachineName}' to '{newName}'. Restart required afterwards.");
        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to rename the computer.");
        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        try
        {
            using var cs = new System.Management.ManagementObject(new System.Management.ManagementPath("Win32_ComputerSystem.Name='" + Environment.MachineName + "'"));
            var rc = Convert.ToUInt32(cs.InvokeMethod("Rename", new object?[] { newName, null, null }));
            if (rc == 0)
            {
                _log.Success("computer.rename", Environment.MachineName, $"new name {newName}; restart required");
                return OperationResult.Ok($"Computer renamed to '{newName}'. A restart is required for the new name to take effect.");
            }
            var msg = rc switch
            {
                5 => "Access denied - run Nanaininai as Administrator.",
                267 => "The computer name is invalid or already in use on the domain/workgroup.",
                269 => "The change was rejected (e.g. domain-joined computer requires a domain account).",
                _ => $"The system returned code {rc}."
            };
            return OperationResult.Fail("Could not rename the computer.", msg, (int)rc);
        }
        catch (Exception ex)
        {
            _log.Error("computer.rename", Environment.MachineName, ex.Message);
            return OperationResult.Fail("Could not rename the computer.", ex.Message);
        }
    }

    public async Task<OperationResult> SetNetworkCategoryAsync(string category, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");
        var allowed = new[] { "Private", "Public", "DomainAuthenticated" };
        if (!allowed.Contains(category, StringComparer.OrdinalIgnoreCase))
            return OperationResult.Fail($"Network category '{category}' is not valid. Expected Private, Public or DomainAuthenticated.");
        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.", $"1. Change the active network connection profile to {category}.");
        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to change the network profile.");
        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        var script = $"Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory {category}";
        var r = await ProcessRunner.RunPowerShellAsync(script).ConfigureAwait(false);
        if (!r.Success)
            return ToHuman("Could not change the network profile.", r);
        WindowsSystemInfoProvider.InvalidateRuntimeCache();
        _log.Success("network.profile", "active connection", $"category={category} confirmed={ctx.UserConfirmed}");
        return OperationResult.Ok($"Network profile changed to {category}. File sharing works best on Private networks.");
    }

    private static OperationResult ToHuman(string headline, ProcessRunner.ProcessResult r)
    {
        var code = r.ExitCode;
        var causes = code switch
        {
            5 => "Possible causes:\n- Nanaininai is not running as Administrator\n- Antivirus policy blocks configuration changes",
            87 => "Possible causes:\n- The adapter name does not match exactly (check spelling)\n- One of the values was rejected by Windows",
            -2 => "Possible causes:\n- The system was too busy to answer in time",
            _ => "Possible causes:\n- Adapter disconnected\n- Another configuration tool holds a lock\n- Insufficient privileges"
        };
        return OperationResult.Fail(headline, causes + $"\n\nTechnical details (exit code {code}):\n{r.Combined}".Trim(), code);
    }
}
