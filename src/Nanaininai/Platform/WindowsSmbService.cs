using System.ServiceProcess;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Platform;

public sealed class WindowsSmbService : ISmbService
{
    private readonly ILogService _log;
    public WindowsSmbService(ILogService log) => _log = log;

    public Task<SmbStatus> GetStatusAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var s = new SmbStatus();
            if (!OperatingSystem.IsWindows())
            {
                s.Detail = "SMB information is only available on Windows.";
                return s;
            }

            s.ClientRunning = GetServiceRunning("LanmanWorkstation");
            s.ServerRunning = GetServiceRunning("LanmanServer");

            var r = ProcessRunner.RunPowerShellAsync(
                "try { $c = Get-SmbServerConfiguration 2>$null; Write-Output ($c.EnableSMB2Protocol); Write-Output ($c.EnableSMB1Protocol); Write-Output ($c.ShareCompliantKeepAlive -ne $null) } catch { Write-Output $false; Write-Output $false; Write-Output $false } ; Write-Output $true").ConfigureAwait(false);
            var res = r.GetAwaiter().GetResult();
            if (res.Success)
            {
                var lines = res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length >= 3)
                {
                    s.Smb1Supported = lines[1].Equals("True", StringComparison.OrdinalIgnoreCase);
                }
                s.FileSharingEnabled = s.ServerRunning;
            }
            else
            {
                s.Detail = $"Could not read SMB server configuration: {res.Combined.Trim()}";
            }

            s.NetworkDiscoveryEnabled = GetServiceRunning("FDResPub") && GetServiceRunning("SSDPSRV");
            return s;
        }, ct);
    }

    private static bool GetServiceRunning(string serviceName)
    {
        try
        {
            var sc = new ServiceController(serviceName);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OperationResult> EnableFileSharingAsync(OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");

        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.",
                "1. Start the 'Server' (LanmanServer) service and set it to automatic\n2. Enable SMB2 protocol (SMB1 stays disabled)\n3. Enable the 'File and Printer Sharing' firewall rules");

        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to enable file sharing.");

        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        // Start the Server service.
        var script = "Set-Service -Name LanmanServer -StartupType Automatic; " +
                     "if ((Get-Service LanmanServer).Status -ne 'Running') { Start-Service LanmanServer }; " +
                     "Set-SmbServerConfiguration -EnableSMB2Protocol $true -Force 2>$null; " +
                     "Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force 2>$null; " +
                     "Write-Output OK";            var r = await ProcessRunner.RunPowerShellAsync(script).ConfigureAwait(false);
            if (!r.Success)
            {
                var isProtocolError = r.Combined.Contains("requested protocol", StringComparison.OrdinalIgnoreCase)
                    || r.Combined.Contains("has not been configured", StringComparison.OrdinalIgnoreCase);
                _log.Error("smb.enableSharing", "LanmanServer", r.Combined);
                return isProtocolError
                    ? OperationResult.Fail(
                        "Could not enable file sharing: the SMB/network protocol stack appears to be incomplete or not configured.",
                        "Possible causes:\n- Nanaininai is not running as Administrator\n- Domain policy controls the SMB configuration\n- The network protocol stack is in an incomplete state (no IP protocol bound, filter driver, transient state)\n\nTechnical details:\n" + r.Combined, r.ExitCode)
                    : OperationResult.Fail("Could not enable file sharing.",
                        "Possible causes:\n- Nanaininai is not running as Administrator\n- Domain policy controls the SMB configuration\n\nTechnical details:\n" + r.Combined, r.ExitCode);
            }

        _log.Success("smb.enableSharing", "LanmanServer", "SMB2 enabled, SMB1 disabled, confirmed=" + ctx.UserConfirmed);
        return OperationResult.Ok("File sharing enabled (SMB2 on, SMB1 off). Firewall rules may still need to be enabled in the Firewall section.");
    }
}
