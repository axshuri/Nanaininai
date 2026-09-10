using Nanaininai.Core.Network;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Platform;

public sealed class WindowsShareService : IShareService
{
    private readonly ILogService _log;
    public WindowsShareService(ILogService log) => _log = log;

    // ---------------------------------------------------------------- listing

    public Task<List<ShareInfo>> ListSharesAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        var list = new List<ShareInfo>();
        if (!OperatingSystem.IsWindows()) return list;

            var script = "Get-SmbShare -ErrorAction SilentlyContinue 2>$null | Where-Object { $_.Special -eq $false } | " +
                     "Select-Object Name,Path,Description | ConvertTo-Json -Compress";
            var r = await ProcessRunner.RunPowerShellAsync(script).ConfigureAwait(false);
            if (!r.Success || r.StdOut.Trim().Length == 0) return list;

            try
            {
                if (r.StdOut.Trim().StartsWith('['))
                {
                    var arr = JsonSerializer.Deserialize<ShareDto[]>(r.StdOut) ?? Array.Empty<ShareDto>();
                    foreach (var d in arr) list.Add(await ToShareInfo(d).ConfigureAwait(false));
                }
                else
                {
                    var d = JsonSerializer.Deserialize<ShareDto>(r.StdOut);
                    if (d is not null) list.Add(await ToShareInfo(d).ConfigureAwait(false));
                }
            }
            catch (Exception ex) when (
                ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
            {
                // SMB share enumeration failed because the protocol stack is incomplete; return empty list.
            }
            catch { }
            return list;
    }, ct);

    private async Task<ShareInfo> ToShareInfo(ShareDto d)
    {
        var info = new ShareInfo { Name = d.Name ?? "", LocalPath = d.Path ?? "", Description = d.Description ?? "" };
        info.SharePermissions = await GetSharePermissions(info.Name).ConfigureAwait(false);
        info.NtfsPermissions = GetNtfsPermissions(info.LocalPath);
        return info;
    }

    private static async Task<List<PermissionEntry>> GetSharePermissions(string shareName)
    {
        if (!StrictInput.IsValidShareName(shareName)) return new List<PermissionEntry>();            var script = $"Get-SmbShareAccess -Name {StrictInput.PsQuote(shareName)} 2>$null | Select-Object AccountName,AccessRight,AccessControlType | ConvertTo-Json -Compress";
            var r = await ProcessRunner.RunPowerShellAsync(script).ConfigureAwait(false);
            var entries = new List<PermissionEntry>();
            if (!r.Success || r.StdOut.Trim().Length == 0) return entries;
            try
            {
                IEnumerable<AccessDto>? dtos = r.StdOut.Trim().StartsWith('[')
                    ? JsonSerializer.Deserialize<AccessDto[]>(r.StdOut)
                    : JsonSerializer.Deserialize<AccessDto>(r.StdOut) is { } one ? new[] { one } : null;
                foreach (var d in dtos ?? Array.Empty<AccessDto>())
                    entries.Add(new PermissionEntry { Identity = d.AccountName ?? "", Rights = d.AccessRight ?? "", Type = d.AccessControlType ?? "Allow" });
            }
            catch (Exception ex) when (
                ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
            {
                // Share ACL read failed due to protocol stack; return empty list.
            }
            catch { }
            return entries;
    }

    private static List<PermissionEntry> GetNtfsPermissions(string localPath)
    {
        var entries = new List<PermissionEntry>();
        try
        {
            if (string.IsNullOrEmpty(localPath) || !Directory.Exists(localPath)) return entries;
            var dirInfo = new DirectoryInfo(localPath);
            var security = dirInfo.GetAccessControl();
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.NTAccount));
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
            {
                var rights = RightsSummary(rule.FileSystemRights);
                entries.Add(new PermissionEntry
                {
                    Identity = rule.IdentityReference.ToString(),
                    Rights = rights,
                    Type = rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny ? "Deny" : "Allow"
                });
            }
        }
        catch { /* ACL may be unreadable without elevation; show share permissions only. */ }
        return entries;
    }

    private static string RightsSummary(System.Security.AccessControl.FileSystemRights rights)
    {
        var parts = new List<string>();
        if (rights.HasFlag(System.Security.AccessControl.FileSystemRights.FullControl)) return "Full Control";
        if (rights.HasFlag(System.Security.AccessControl.FileSystemRights.Modify)) return "Modify";
        if (rights.HasFlag(System.Security.AccessControl.FileSystemRights.ReadAndExecute)) return "Read & Execute";
        if (rights.HasFlag(System.Security.AccessControl.FileSystemRights.Read)) parts.Add("Read");
        if (rights.HasFlag(System.Security.AccessControl.FileSystemRights.Write)) parts.Add("Write");
        if (rights.HasFlag(System.Security.AccessControl.FileSystemRights.Delete)) parts.Add("Delete");
        return parts.Count > 0 ? string.Join(" + ", parts) : rights.ToString();
    }

    public Task<ShareInfo?> GetShareAsync(string name, CancellationToken ct = default)
        => ListSharesAsync(ct).ContinueWith(t => t.Result.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)), ct);

    // ---------------------------------------------------------------- create

    public async Task<OperationResult> CreateShareAsync(CreateShareRequest request, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");
        if (!StrictInput.IsValidShareName(request.ShareName))
            return OperationResult.Fail($"Share name '{request.ShareName}' is invalid.", "Use 1-80 characters: letters, digits, dot, underscore or hyphen.");
        if (!StrictInput.IsValidLocalPath(request.LocalPath))
            return OperationResult.Fail($"Folder path '{request.LocalPath}' is not a valid local NTFS path.", "Example: D:\\SchoolShare");
        var access = request.AccessLevel switch { "Read" => "Read", "Change" => "Change", "Full" => "Full", _ => "" };
        if (access.Length == 0)
            return OperationResult.Fail($"Access level '{request.AccessLevel}' is not valid. Expected Read, Change or Full.");
        var identities = new List<string>();
        foreach (var id in request.AllowedIdentities)
        {
            if (!StrictInput.IsValidIdentity(id))
                return OperationResult.Fail($"Account '{id}' contains characters that are not allowed.");
            identities.Add(id);
        }
        if (identities.Count == 0) identities.Add("Authenticated Users");
        if (access == "Full" && identities.Contains("Everyone", StringComparer.OrdinalIgnoreCase))
            return OperationResult.Fail("Refusing to grant Everyone Full Control.", "Restrict the access level or the account list first.");

        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.", RenderSharePlan(request));

        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to create a share.");
        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        if (!Directory.Exists(request.LocalPath))
            return OperationResult.Fail($"Folder '{request.LocalPath}' does not exist.", "Create the folder first, then share it.");

        var ids = string.Join(",", identities.Select(i => StrictInput.PsQuote(i)));
        var script =
            $"New-SmbShare -Name {StrictInput.PsQuote(request.ShareName)} -Path {StrictInput.PsQuote(request.LocalPath)} " +
            $"-Description {StrictInput.PsQuote(request.Description)} -ErrorAction Stop; " +
            $"Grant-SmbShareAccess -Name {StrictInput.PsQuote(request.ShareName)} -AccountName *ID* -AccessRight {access} -Force -ErrorAction SilentlyContinue; " +
            "Write-Output CREATED";
        script = script.Replace("*ID*", ids.Length > 0 ? ids : "'Authenticated Users'");

        var r = await ProcessRunner.RunPowerShellAsync(script).ConfigureAwait(false);
        if (!r.Success || !r.StdOut.Contains("CREATED"))
        {
            _log.Error("share.create", request.ShareName, r.Combined);
            return OperationResult.Fail($"Could not create share '{request.ShareName}'.",
                "Possible causes:\n- A share with this name already exists\n- Nanaininai is not running as Administrator\n- The Server service is not running\n\nTechnical details:\n" + r.Combined, r.ExitCode);
        }

        // Remove the default Everyone Read grant that New-SmbShare creates, only if we granted explicit accounts.
        if (!identities.Contains("Everyone", StringComparer.OrdinalIgnoreCase))
        {
            var cleanup = $"Revoke-SmbShareAccess -Name {StrictInput.PsQuote(request.ShareName)} -AccountName 'Everyone' -Force -ErrorAction SilentlyContinue";
            await ProcessRunner.RunPowerShellAsync(cleanup).ConfigureAwait(false);
        }

        _log.Success("share.create", request.ShareName, $"path={request.LocalPath} access={access} identities={string.Join(",", identities)} confirmed={ctx.UserConfirmed}");
        var host = Environment.MachineName;
        return OperationResult.Ok("Share created.",
            $"Local Path:\n{request.LocalPath}\n\nNetwork Path:\n\\{host}\\{request.ShareName}\n\nShare Name:\n{request.ShareName}\n\nAccess: {access} for {string.Join(", ", identities)}");
    }

    private static string RenderSharePlan(CreateShareRequest request)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"1. Create SMB share '{request.ShareName}' pointing to '{request.LocalPath}'");
        sb.AppendLine($"2. Grant {request.AccessLevel} share access to {string.Join(", ", request.AllowedIdentities)}");
        sb.AppendLine("3. Remove the default 'Everyone: Read' grant");
        sb.AppendLine("Note: NTFS permissions of the folder are not modified automatically - check them in the Sharing section.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- remove

    public async Task<OperationResult> RemoveShareAsync(string shareName, OperationContext ctx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return OperationResult.Fail("This operation requires Windows.");
        if (!StrictInput.IsValidShareName(shareName))
            return OperationResult.Fail($"Share name '{shareName}' is invalid.");
        if (ctx.DryRun)
            return OperationResult.Ok("DRY RUN - no changes were made.", $"1. Remove SMB share '{shareName}' (the folder on disk is NOT deleted).");
        if (!Elevation.IsAdmin())
            return OperationResult.Fail("Administrator privileges are required to remove a share.");
        if (!ctx.UserConfirmed)
            return OperationResult.Fail("Change was not confirmed by the user; nothing was applied.");

        var r = await ProcessRunner.RunPowerShellAsync(
            $"Remove-SmbShare -Name {StrictInput.PsQuote(shareName)} -Force -ErrorAction Stop; Write-Output REMOVED").ConfigureAwait(false);
        if (!r.Success || !r.StdOut.Contains("REMOVED"))
            return OperationResult.Fail($"Could not remove share '{shareName}'.", r.Combined, r.ExitCode);
        _log.Success("share.remove", shareName, $"confirmed={ctx.UserConfirmed}");
        return OperationResult.Ok($"Share '{shareName}' removed. The folder on disk was not deleted.");
    }

    // ---------------------------------------------------------------- test

    public async Task<ShareTestResult> TestShareAsync(string target, bool testWrite, CancellationToken ct = default)
    {
        var result = new ShareTestResult { Target = target };

        void Step(string name, bool passed, string detail = "")
        {
            result.Steps.Add(new ShareTestStep { Name = name, Passed = passed, Detail = detail });
            if (!passed && !result.Success) result.Success = false;
        }

        // Parse \\host\share
        if (!StrictInput.IsValidUncPath(target) || target.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            result.Reason = $"'{target}' is not a valid UNC path. Expected \\\\server\\share.";
            result.Steps.Add(new ShareTestStep { Name = "Path validation", Passed = false, Detail = result.Reason });
            return result;
        }

        var parts = target.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var host = parts[0];
        var share = parts[1];
        var unc = $"\\\\{host}\\{share}";

        // 1. Host reachable (TCP is the reliable test; ICMP may be blocked).
        var tcpOk = false;
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(host, 445);
            var done = await Task.WhenAny(connect, Task.Delay(1500, ct)).ConfigureAwait(false);
            tcpOk = done == connect && tcp.Connected;
            Step("Host reachable (SMB port 445)", tcpOk, tcpOk ? "connected" : "no response on port 445");
        }
        catch (Exception ex) when (
            ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
        {
            Step("Host reachable (SMB port 445)", false, "SMB port 445 test skipped: the local network protocol stack is not configured.");
            result.Reason = "SMB connectivity test skipped because the local protocol stack is not configured.";
            result.Success = false;
            return result;
        }
        catch (Exception ex) { Step("Host reachable (SMB port 445)", false, ex.Message); }

        if (!tcpOk)
        {
            result.Reason = $"The host did not answer on port 445. Possible causes:\n- The computer is offline\n- 'Server' service not running on {host}\n- Firewall rule 'File and Printer Sharing (SMB-In)' disabled on {host}";
            result.Success = false;
            return result;
        }

        // 2. Share existence / read via filesystem on the UNC path (uses Windows authentication).
        var readOk = false;
        try
        {
            var dir = Directory.EnumerateFileSystemEntries(unc);
            readOk = dir.Any();
            Step("Share exists and is readable", true, readOk ? "entries listed" : "share exists but is empty");
        }
        catch (Exception ex) when (
            ex.Message.Contains("requested protocol", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("has not been configured", StringComparison.OrdinalIgnoreCase))
        {
            Step("Share exists", false, "Share test skipped: the network protocol stack is not configured on this PC.");
            result.Reason = "SMB share test skipped because the local protocol stack is not configured.";
            result.Success = false;
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            Step("Share exists and is readable", false, "access denied");
            result.Reason = "Authentication succeeded but access was denied.\nPossible causes:\n- Share permissions do not include your account\n- NTFS permissions deny read access to the folder\n- You may be connecting as a guest with no share access";
            result.Success = false;
            return result;
        }
        catch (IOException ioEx)
        {
            var msg = ioEx.Message;
            if (msg.Contains("name", StringComparison.OrdinalIgnoreCase))
            {
                Step("Share exists", false, "share not found");
                result.Reason = $"Share '{share}' was not found on {host}.\nPossible causes:\n- Share name misspelled\n- Share removed on {host}";
                result.Success = false;
                return result;
            }
            Step("Share reachable", false, msg);
            result.Reason = "Network error while opening the share.\nPossible causes:\n- SMB signing or authentication policy mismatch\n- Server offline mid-test";
            result.Success = false;
            return result;
        }
        catch (Exception ex)
        {
            Step("Share exists", false, ex.Message);
            result.Reason = ex.Message;
            result.Success = false;
            return result;
        }

        // 3. Write test (only when requested).
        if (testWrite)
        {
            try
            {
                var probe = Path.Combine(unc, $"nanaininai-probe-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(probe, "nanaininai write probe", ct).ConfigureAwait(false);
                File.Delete(probe);
                Step("Write access", true, "probe file created and removed");
            }
            catch (UnauthorizedAccessException)
            {
                Step("Write access", false, "denied");
                result.Reason = "NTFS or share permissions deny write access.\nThis is expected for a Read-only share; grant Change rights if write is required.";
            }
            catch (Exception ex)
            {
                Step("Write access", false, ex.Message);
                result.Reason = "Write test failed: " + ex.Message;
            }
        }

        result.Success = result.Steps.All(s => s.Passed || s.Name == "Write access" && !testWrite);
        if (result.Success) result.Success = result.Steps.Where(s => s.Name != "Write access").All(s => s.Passed);
        return result;
    }

    private sealed class ShareDto
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Description { get; set; }
    }

    private sealed class AccessDto
    {
        public string? AccountName { get; set; }
        public string? AccessRight { get; set; }
        public string? AccessControlType { get; set; }
    }
}
